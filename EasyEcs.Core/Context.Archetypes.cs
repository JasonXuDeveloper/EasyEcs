using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

// Archetype registry, entity membership and the query cache.
//
// Thread-safety contract:
// - All mutation (archetype creation, membership changes, cache updates) happens under _structuralLock.
// - Lock-free readers (queries) only see immutable Archetype[] snapshots: _allArchetypes and every query cache entry
//   are replaced copy-on-write and published with Volatile.Write, never mutated in place.
public partial class Context
{
    // Lock for structural modifications (System.Threading.Lock: cheaper than Monitor, no object header sync block).
    private readonly Lock _structuralLock = new();

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
    /// Must be called inside _structuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveToArchetype(int entityId, Archetype target)
    {
        ref var meta = ref Meta[entityId];
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

    /// <summary>
    /// Remove an entity from its archetype, marking the id as not alive. Must be called inside _structuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DetachFromArchetype(int entityId)
    {
        ref var meta = ref Meta[entityId];
        var current = meta.Archetype;
        if (current == null)
            return;

        current.RemoveAt(meta.Slot);
        meta.Archetype = null;
    }

    /// <summary>
    /// Archetype reached from <paramref name="from"/> by adding one component bit (edge-cached, no Tag hashing).
    /// <paramref name="newTag"/> already contains the bit. Must be called inside _structuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Archetype ArchetypeAfterAdd(Archetype from, int bit, in Tag newTag)
    {
        var target = from.GetAddEdge(bit);
        if (target != null)
            return target;

        target = GetOrCreateArchetype(in newTag);
        from.SetAddEdge(bit, target);
        target.SetRemoveEdge(bit, from);
        return target;
    }

    /// <summary>
    /// Archetype reached from <paramref name="from"/> by removing one component bit (edge-cached).
    /// <paramref name="newTag"/> already has the bit cleared. Must be called inside _structuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Archetype ArchetypeAfterRemove(Archetype from, int bit, in Tag newTag)
    {
        var target = from.GetRemoveEdge(bit);
        if (target != null)
            return target;

        target = GetOrCreateArchetype(in newTag);
        from.SetRemoveEdge(bit, target);
        target.SetAddEdge(bit, from);
        return target;
    }

    /// <summary>
    /// Get or create the archetype for <paramref name="componentMask"/>. A new archetype is appended (copy-on-write)
    /// to every cached query it matches. Must be called inside _structuralLock.
    /// </summary>
    private Archetype GetOrCreateArchetype(in Tag componentMask)
    {
        if (_archetypes.TryGetValue(componentMask, out var archetype))
            return archetype;

        archetype = new Archetype(in componentMask);
        _archetypes[componentMask] = archetype;
        Volatile.Write(ref _allArchetypes, Append(_allArchetypes, archetype));

        foreach (var pair in _queryCache)
        {
            if (pair.Key.Matches(in archetype.ComponentMask))
                Volatile.Write(ref pair.Value.Archetypes, Append(pair.Value.Archetypes, archetype));
        }

        return archetype;
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
        var key = new QueryKey(bits, in filter);
        if (_queryCache.TryGetValue(key, out var entry))
            return Volatile.Read(ref entry.Archetypes);

        lock (_structuralLock)
        {
            if (_queryCache.TryGetValue(key, out entry))
                return entry.Archetypes;

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
    /// Compact all archetypes to remove tombstones.
    /// Call between frames (e.g. when <see cref="GetFragmentationStats"/> reports high fragmentation),
    /// never while a query is iterating.
    /// </summary>
    public void CompactArchetypes()
    {
        lock (_structuralLock)
        {
            var all = _allArchetypes;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AliveCount < all[i].Count)
                {
                    all[i].Compact(Meta);
                    StorageVersion++;
                }
            }
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
