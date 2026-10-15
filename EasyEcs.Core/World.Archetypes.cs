using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
namespace EasyEcs;

// Archetype registry, entity membership and the query cache.
//
// Thread-safety contract:
// - All mutation (archetype creation, membership changes, cache updates) happens under StructuralLock.
// - Lock-free readers (queries) only see immutable Archetype[] snapshots: _allArchetypes and every query cache entry
//   are replaced copy-on-write and published with Volatile.Write, never mutated in place.
public sealed partial class World
{
    // Lock for structural modifications (System.Threading.Lock: cheaper than Monitor, no object header sync block).
    internal readonly Lock StructuralLock = new();

    // Bumped whenever storage arrays may be reallocated (component columns, entity arrays, archetype id arrays,
    // compaction) so in-flight ForEach passes refresh their cached array references.
    internal int StorageVersion;

    // Tag -> archetype (only touched under the lock).
    private readonly Dictionary<Tag, Archetype> _archetypes = new();

    // Copy-on-write snapshot of all archetypes (archetype creation is rare, reads are frequent).
    private Archetype[] _allArchetypes = Array.Empty<Archetype>();

    // Query -> matching archetypes. Entries are created once and their array is replaced copy-on-write.
    private readonly ConcurrentDictionary<QueryKey, QueryCacheEntry> _queryCache = new();

    private sealed class QueryCacheEntry(Archetype[] archetypes)
    {
        public Archetype[] Archetypes = archetypes;
    }

    /// <summary>
    /// Move an entity into <paramref name="target"/>, leaving its current archetype (if any).
    /// Must be called inside StructuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveToArchetype(int entityId, Archetype target)
    {
        ref var meta = ref MetaRef(entityId);
        var current = meta.Archetype;
        if (ReferenceEquals(current, target))
            return;

        current?.RemoveAt(meta.Slot);
        var ids = target.EntityIds;
        meta.Slot = target.Add(entityId);
        meta.Archetype = target;

        // Archetype storage reallocated: invalidate snapshots held by in-flight ForEach passes
        if (!ReferenceEquals(ids, target.EntityIds))
            StorageVersion++;
    }

    /// <summary>Archetype reached from <paramref name="from"/> by adding <paramref name="bit"/> (edge-cached). Inside the lock.</summary>
    private Archetype ArchetypeAfterAdd(Archetype from, ushort bit) => from.GetAddEdge(bit) ?? CreateEdge(from, bit, add: true);

    /// <summary>Archetype reached from <paramref name="from"/> by removing <paramref name="bit"/> (edge-cached). Inside the lock.</summary>
    private Archetype ArchetypeAfterRemove(Archetype from, ushort bit) => from.GetRemoveEdge(bit) ?? CreateEdge(from, bit, add: false);

    /// <summary>Resolve a transition once (mask hashing) and cache it in both directions.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Archetype CreateEdge(Archetype from, ushort bit, bool add)
    {
        var mask = from.ComponentMask.Clone();
        if (add) mask.SetBit(bit);
        else mask.ClearBit(bit);

        var target = GetOrCreateArchetype(in mask);
        var (without, with) = add ? (from, target) : (target, from);
        without.SetAddEdge(bit, with);
        with.SetRemoveEdge(bit, without);
        return target;
    }

    /// <summary>
    /// Resolve a multi-component transition once (mask hashing) and cache it on <paramref name="from"/> under
    /// <paramref name="signature"/>. The target may be <paramref name="from"/> itself (nothing to add or remove).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Archetype CreateSetEdge(Archetype from, ReadOnlySpan<ushort> bits, int signature, bool add)
    {
        var mask = from.ComponentMask.Clone();
        for (int i = 0; i < bits.Length; i++)
        {
            if (add) mask.SetBit(bits[i]);
            else mask.ClearBit(bits[i]);
        }

        var target = GetOrCreateArchetype(in mask);
        if (add) from.SetAddSetEdge(signature, target);
        else from.SetRemoveSetEdge(signature, target);
        return target;
    }

    /// <summary>
    /// Get or create the archetype for <paramref name="componentMask"/>. A new archetype is appended (copy-on-write)
    /// to every cached query it matches. Must be called inside StructuralLock.
    /// </summary>
    private Archetype GetOrCreateArchetype(in Tag componentMask)
    {
        if (_archetypes.TryGetValue(componentMask, out var archetype))
            return archetype;

        archetype = new Archetype(in componentMask);
        archetype.ManagedComponents = ManagedOf(archetype.Components);
        _archetypes[componentMask] = archetype;
        Volatile.Write(ref _allArchetypes, Append(_allArchetypes, archetype));

        foreach (var pair in _queryCache)
        {
            if (pair.Key.Matches(in archetype.ComponentMask))
                Volatile.Write(ref pair.Value.Archetypes, Append(pair.Value.Archetypes, archetype));
        }

        return archetype;
    }

    /// <summary>The bits of <paramref name="components"/> whose column holds managed references (cold path).</summary>
    private ushort[] ManagedOf(ushort[] components)
    {
        var managed = new List<ushort>();
        var hasReferences = _columnHasReferences;
        foreach (ushort bit in components)
            if (bit < hasReferences.Length && hasReferences[bit])
                managed.Add(bit);
        return managed.ToArray();
    }

    private static Archetype[] Append(Archetype[] source, Archetype item)
    {
        var result = new Archetype[source.Length + 1];
        Array.Copy(source, result, source.Length);
        result[source.Length] = item;
        return result;
    }

    /// <summary>
    /// Archetypes containing all <paramref name="bits"/> and matching <paramref name="filter"/>.
    /// Lock-free on cache hits; the returned array is an immutable snapshot.
    /// </summary>
    internal Archetype[] GetMatchingArchetypes(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        var key = QueryKey.ForLookup(bits, in filter);
        if (_queryCache.TryGetValue(key, out var entry))
            return Volatile.Read(ref entry.Archetypes);

        lock (StructuralLock)
        {
            if (_queryCache.TryGetValue(key, out entry))
                return entry.Archetypes;

            key = key.Detached();

            var all = _allArchetypes;
            var matching = new List<Archetype>();
            for (int i = 0; i < all.Length; i++)
            {
                if (key.Matches(in all[i].ComponentMask))
                    matching.Add(all[i]);
            }

            var result = matching.ToArray();
            _queryCache[key] = new QueryCacheEntry(result);
            return result;
        }
    }

    /// <summary>
    /// Remove tombstones and sort every archetype's entities by id (bitmap sort: O(alive + id range / 64) per
    /// archetype). Iteration then reads each column front to back. Call between updates (after mass despawns, or when
    /// <see cref="GetFragmentationStats"/> reports fragmentation), never while a query is iterating.
    /// <see cref="WorldOptions.SortArchetypesOnUpdate"/> does this automatically for archetypes that need it.
    /// </summary>
    public void CompactArchetypes()
    {
        lock (StructuralLock)
        {
            var all = _allArchetypes;
            for (int i = 0; i < all.Length; i++)
                all[i].Compact(Meta, _sortBits);
            StorageVersion++;
        }
    }

    /// <summary>
    /// Re-sort the archetypes whose id order degraded (<see cref="Archetype.NeedsSort"/>). Runs at the start of every
    /// update, when no query is iterating: O(archetypes) when nothing needs sorting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SortDisorderedArchetypes()
    {
        var all = _allArchetypes;
        for (int i = 0; i < all.Length; i++)
        {
            if (!all[i].NeedsSort) continue;
            SortDisorderedArchetypesLocked();
            return;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SortDisorderedArchetypesLocked()
    {
        lock (StructuralLock)
        {
            var all = _allArchetypes;
            for (int i = 0; i < all.Length; i++)
                if (all[i].NeedsSort)
                    all[i].Compact(Meta, _sortBits);
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
