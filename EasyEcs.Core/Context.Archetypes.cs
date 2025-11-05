using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

public partial class Context
{
    // Archetype storage: Tag -> Archetype (thread-safe for concurrent reads and writes)
    internal readonly ConcurrentDictionary<Tag, Archetype> Archetypes = new();

    // Query cache: QueryTag -> List of matching archetypes (O(1) lookup after first query)
    private readonly Dictionary<Tag, List<Archetype>> _queryCache = new();

    // Lock for structural modifications (simple object lock for .NET 8.0 compatibility)
    private readonly object _structuralLock = new();

    /// <summary>
    /// Get or create an archetype for the given component mask.
    /// Thread-safe using ConcurrentDictionary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Archetype GetOrCreateArchetype(in Tag componentMask)
    {
        return Archetypes.GetOrAdd(componentMask, static (tag) =>
        {
            return new Archetype(in tag, initialCapacity: 1024);
        });
        // Note: Query cache invalidation happens in structural operations (add/remove component)
    }

    /// <summary>
    /// Get all archetypes matching the query tag.
    /// Uses O(1) cached lookup after first access.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal List<Archetype> GetMatchingArchetypes(in Tag queryTag)
    {
        // Fast path: check cache without lock (read-only access is safe)
        if (_queryCache.TryGetValue(queryTag, out var cached))
            return cached;

        lock (_structuralLock)
        {
            // Double-check after acquiring lock
            if (_queryCache.TryGetValue(queryTag, out cached))
                return cached;

            // Build list of matching archetypes
            var matching = new List<Archetype>(16);

            // Use direct enumeration to avoid allocating Dictionary.Values collection
            foreach (var kvp in Archetypes)
            {
                var archetype = kvp.Value;
                // SIMD-accelerated bitwise AND
                // An archetype matches if it contains all required components
                if ((archetype.ComponentMask & queryTag) == queryTag)
                {
                    matching.Add(archetype);
                }
            }

            // Cache for future queries
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
            // Use direct enumeration to avoid allocating Dictionary.Values collection
            foreach (var kvp in Archetypes)
            {
                var archetype = kvp.Value;
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
    public (int TotalSlots, int AliveEntities, float FragmentationRatio) GetFragmentationStats()
    {
        int totalSlots = 0;
        int aliveEntities = 0;

        lock (_structuralLock)
        {
            // Use direct enumeration to avoid allocating Dictionary.Values collection
            foreach (var kvp in Archetypes)
            {
                var archetype = kvp.Value;
                totalSlots += archetype.Count;
                aliveEntities += archetype.AliveCount;
            }
        }

        float fragmentation = totalSlots > 0 ? 1.0f - (float)aliveEntities / totalSlots : 0f;
        return (totalSlots, aliveEntities, fragmentation);
    }
}
