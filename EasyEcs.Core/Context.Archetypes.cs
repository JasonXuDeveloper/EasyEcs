using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

public partial class Context
{
    // Archetype storage: Tag -> Archetype
    internal readonly Dictionary<Tag, Archetype> Archetypes = new();

    // Flat list of all archetypes for lock-free iteration (only grows, never shrinks except on Dispose)
    private readonly List<Archetype> _allArchetypes = new();

    // Query cache: QueryTag -> List of matching archetypes (O(1) lookup after first query)
    // Using ConcurrentDictionary for lock-free reads on cache hits
    private readonly ConcurrentDictionary<Tag, List<Archetype>> _queryCache = new();

    // Lock for structural modifications (simple object lock for .NET 8.0 compatibility)
    private readonly object _structuralLock = new();

    /// <summary>
    /// Get or create an archetype for the given component mask.
    /// Thread-safe: always locks to prevent race conditions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Archetype GetOrCreateArchetype(in Tag componentMask)
    {
        lock (_structuralLock)
        {
            if (!Archetypes.TryGetValue(componentMask, out var archetype))
            {
                archetype = new Archetype(in componentMask, initialCapacity: 1024);
                Archetypes[componentMask] = archetype;
                _allArchetypes.Add(archetype);

                // Incrementally update query cache: add new archetype to matching queries
                // This is smarter than clearing the entire cache
                foreach (var kvp in _queryCache)
                {
                    var queryTag = kvp.Key;
                    var cachedList = kvp.Value;

                    // Check if new archetype matches this cached query
                    if (componentMask.ContainsAll(in queryTag))
                    {
                        cachedList.Add(archetype);
                    }
                }
            }

            return archetype;
        }
    }

    /// <summary>
    /// Get all archetypes matching the query tag.
    /// Uses O(1) cached lookup after first access with lock-free fast path.
    /// Thread-safe: uses double-checked locking for cache misses only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal List<Archetype> GetMatchingArchetypes(in Tag queryTag)
    {
        // Fast path: lock-free cache lookup (common case for GroupOf operations)
        // ConcurrentDictionary.TryGetValue is thread-safe and doesn't require locking
        if (_queryCache.TryGetValue(queryTag, out var cached))
            return cached;

        // Slow path: cache miss, need to build and cache the result
        // Use lock to ensure only one thread builds the cache entry and to safely read Archetypes
        lock (_structuralLock)
        {
            // Double-check after acquiring lock (another thread might have built it)
            if (_queryCache.TryGetValue(queryTag, out cached))
                return cached;

            // Build list of matching archetypes
            var matching = new List<Archetype>(16);

            for (int i = 0; i < _allArchetypes.Count; i++)
            {
                var archetype = _allArchetypes[i];
                // SIMD-accelerated ContainsAll check
                if (archetype.ComponentMask.ContainsAll(in queryTag))
                {
                    matching.Add(archetype);
                }
            }

            // Cache for future queries (ConcurrentDictionary handles concurrent access)
            _queryCache[queryTag] = matching;
            return matching;
        }
    }

    /// <summary>
    /// Compact all archetypes to remove tombstones.
    /// Call during loading screens or maintenance windows.
    /// </summary>
    public void CompactArchetypes()
    {
        lock (_structuralLock)
        {
            for (int i = 0; i < _allArchetypes.Count; i++)
            {
                var archetype = _allArchetypes[i];
                if (archetype.AliveCount < archetype.Count)
                {
                    archetype.Compact();
                }
            }
        }
    }

    /// <summary>
    /// Get archetype fragmentation statistics for monitoring.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
    public (int TotalSlots, int AliveEntities, float FragmentationRatio) GetFragmentationStats()
    {
        int totalSlots = 0;
        int aliveEntities = 0;

        // Lock-free: _allArchetypes only grows, index-based for loop is safe during concurrent append.
        // Individual int reads (Count, AliveCount) are atomic on all .NET platforms.
        for (int i = 0; i < _allArchetypes.Count; i++)
        {
            totalSlots += _allArchetypes[i].Count;
            aliveEntities += _allArchetypes[i].AliveCount;
        }

        float fragmentation = totalSlots > 0 ? 1.0f - (float)aliveEntities / totalSlots : 0f;
        return (totalSlots, aliveEntities, fragmentation);
    }
}