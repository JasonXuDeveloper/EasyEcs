using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
namespace EasyEcs;

// Archetype registry, entity membership and the query cache.
//
// Thread-safety contract:
// - All mutation (archetype creation, membership changes, cache updates) happens under the structural gate (Structural()).
// - Lock-free readers (queries) only see immutable Archetype[] snapshots: _allArchetypes and every query cache entry
//   are replaced copy-on-write and published with Volatile.Write, never mutated in place.
public sealed partial class World
{
    // Lock for structural modifications (see StructuralGate: one CAS in, one store out).
    internal StructuralGate Gate;

    /// <summary>Hold the structural gate until the returned scope is disposed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StructuralScope Structural() => new(ref Gate);

    // Bumped by every structural change (and every storage reallocation or compaction), so an in-flight ForEach pass
    // knows when to refresh its cached array references and check whether its job moved the visited entity.
    internal int StorageVersion;

    // Tag -> archetype (only touched under the gate).
    private readonly Dictionary<Tag, Archetype> _archetypes = new();

    // Copy-on-write snapshot of all archetypes (archetype creation is rare, reads are frequent).
    private Archetype[] _allArchetypes = Array.Empty<Archetype>();

    // Query -> matching archetypes. Entries are created once and their array is replaced copy-on-write.
    private readonly ConcurrentDictionary<QueryKey, QueryCacheEntry> _queryCache = new();

    internal sealed class QueryCacheEntry(QueryMatch match)
    {
        public QueryMatch Match = match;
    }

    /// <summary>
    /// Put a new entity (no archetype yet) into <paramref name="target"/> and return its row. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Place(int id, Archetype target)
    {
        ref var meta = ref MetaRef(id);
        int slot = target.Add(id, meta.Version);
        meta.Slot = slot;
        meta.Archetype = target;
        StorageVersion++;
        return slot;
    }

    /// <summary>Resolve an add transition once (mask hashing) and cache it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Transition CreateEdge(Archetype from, ushort bit)
    {
        var mask = from.ComponentMask.Clone();
        mask.SetBit(bit);
        var transition = new Transition(from, GetOrCreateArchetype(in mask));
        from.SetAddEdge(bit, transition);
        return transition;
    }

    /// <summary>
    /// Resolve a multi-component add transition once (mask hashing) and cache it on <paramref name="from"/> under
    /// <paramref name="signature"/>. The target is <paramref name="from"/> itself when it has every column already.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Transition CreateSetEdge(Archetype from, ReadOnlySpan<ushort> bits, int signature)
    {
        var mask = from.ComponentMask.Clone();
        for (int i = 0; i < bits.Length; i++) mask.SetBit(bits[i]);
        var transition = new Transition(from, GetOrCreateArchetype(in mask));
        from.SetAddSetEdge(signature, transition);
        return transition;
    }

    /// <summary>
    /// Get or create the archetype for <paramref name="componentMask"/>. A new archetype is appended (copy-on-write)
    /// to every cached query it matches. Must be called inside the structural gate.
    /// </summary>
    private Archetype GetOrCreateArchetype(in Tag mask)
    {
        var componentMask = ComponentInfo.AnyTracked ? WithCompanions(in mask) : mask;
        if (_archetypes.TryGetValue(componentMask, out var archetype))
            return archetype;

        archetype = new Archetype(in componentMask);
        _archetypes[componentMask] = archetype;
        Volatile.Write(ref _allArchetypes, Append(_allArchetypes, archetype));

        foreach (var pair in _queryCache)
        {
            if (pair.Key.Matches(in archetype.ComponentMask))
                Volatile.Write(ref pair.Value.Match, pair.Value.Match.Append(archetype, pair.Key.FilterFor(archetype)));
        }

        return archetype;
    }

    /// <summary>
    /// <paramref name="mask"/> with the change-tick column of every tracked component in it, and without those whose
    /// component it lacks (a transition adds or removes the component alone). Cold path: new transitions only.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Tag WithCompanions(in Tag mask)
    {
        var result = mask;
        bool cloned = false;
        foreach (ushort bit in mask.ToBitArray())
        {
            var info = ComponentInfo.Of(bit);
            int add = info.Companion >= 0 && !mask.HasBit(info.Companion) ? info.Companion : -1;
            bool drop = info.Owner >= 0 && !mask.HasBit(info.Owner);
            if (add < 0 && !drop) continue;
            if (!cloned)
            {
                result = mask.Clone();
                cloned = true;
            }

            if (add >= 0) result.SetBit(add);
            if (drop) result.ClearBit(bit);
        }

        return result;
    }

    private static Archetype[] Append(Archetype[] source, Archetype item)
    {
        var result = new Archetype[source.Length + 1];
        Array.Copy(source, result, source.Length);
        result[source.Length] = item;
        return result;
    }

    /// <summary>
    /// Archetypes containing all <paramref name="bits"/> and matching <paramref name="filter"/>, with their per-row
    /// presence tests. Lock-free on cache hits; the result is an immutable snapshot.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // once per pass: keeps dictionary and gate code out of the iteration loops
    internal QueryMatch GetMatchingArchetypes(ReadOnlySpan<ushort> bits, in QueryFilter filter) =>
        Volatile.Read(ref GetQueryEntry(bits, in filter).Match);

    /// <summary>
    /// The cache entry of a query: its <see cref="QueryCacheEntry.Match"/> is replaced (volatile) whenever a new
    /// archetype matches, so holders of the entry always see every matching table without another lookup.
    /// </summary>
    internal QueryCacheEntry GetQueryEntry(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        var key = QueryKey.ForLookup(bits, in filter);
        if (_queryCache.TryGetValue(key, out var entry))
            return entry;

        using (Structural())
        {
            if (_queryCache.TryGetValue(key, out entry))
                return entry;

            key = key.Detached();

            var all = _allArchetypes;
            var archetypes = new List<Archetype>();
            var filters = new List<RowPlan>();
            for (int i = 0; i < all.Length; i++)
            {
                if (!key.Matches(in all[i].ComponentMask)) continue;
                archetypes.Add(all[i]);
                filters.Add(key.FilterFor(all[i]));
            }

            entry = new QueryCacheEntry(new QueryMatch(archetypes.ToArray(), filters.ToArray()));
            _queryCache[key] = entry;
            return entry;
        }
    }

    /// <summary>
    /// Close the tombstone rows of every archetype (moving the last rows into them: O(tombstones x columns)).
    /// Structural changes already keep tables dense: outside passes a removed row is filled at once, and tombstones left
    /// by changes during passes are closed when the last pass ends. This is only needed after a pass was interrupted by
    /// an exception. Never call it while a query is iterating.
    /// </summary>
    public void CompactArchetypes()
    {
        using (Structural())
        {
            Passes = 0;
            var all = _allArchetypes;
            for (int i = 0; i < all.Length; i++)
                all[i].FillHoles(Meta);
            Array.Clear(_dirty, 0, _dirtyCount);
            _dirtyCount = 0;
            StorageVersion++;
        }
    }

    /// <summary>
    /// Archetype fragmentation statistics for monitoring (lock-free, approximate under concurrent changes).
    /// </summary>
    public (int TotalSlots, int AliveEntities, float FragmentationRatio) GetFragmentationStats()
    {
        var all = Volatile.Read(ref _allArchetypes);
        int totalSlots = 0;
        int aliveEntities = 0;
        for (int i = 0; i < all.Length; i++)
        {
            totalSlots += all[i].Count;
            aliveEntities += all[i].AliveCount;
        }

        float fragmentation = totalSlots > 0 ? 1.0f - (float)aliveEntities / totalSlots : 0f;
        return (totalSlots, aliveEntities, fragmentation);
    }

    private void ClearArchetypes()
    {
        var all = _allArchetypes;
        for (int i = 0; i < all.Length; i++)
            Array.Clear(all[i].EntityIds, 0, all[i].EntityIds.Length);

        _archetypes.Clear();
        _allArchetypes = Array.Empty<Archetype>();
        _queryCache.Clear();
    }
}
