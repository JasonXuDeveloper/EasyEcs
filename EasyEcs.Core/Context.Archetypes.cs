using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

public partial class Context
{
    // Archetype storage: Tag -> Archetype
    internal readonly Dictionary<Tag, Archetype> Archetypes = new();

    // Query cache: QueryTag -> List of matching archetypes (O(1) lookup after first query)
    private readonly Dictionary<Tag, List<Archetype>> _queryCache = new();

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

                // Invalidate query cache (new archetype may match existing queries)
                _queryCache.Clear();
            }

            return archetype;
        }
    }

    /// <summary>
    /// Get all archetypes matching the query tag.
    /// Uses O(1) cached lookup after first access.
    /// Thread-safe: always locks to prevent race conditions with cache invalidation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal List<Archetype> GetMatchingArchetypes(in Tag queryTag)
    {
        lock (_structuralLock)
        {
            Console.WriteLine($"GetMatchingArchetypes: Searching for queryTag, Total archetypes: {Archetypes.Count}");

            // Check cache
            if (_queryCache.TryGetValue(queryTag, out var cached))
            {
                Console.WriteLine($"GetMatchingArchetypes: Found in cache, {cached.Count} archetypes");
                return cached;
            }

            // Build list of matching archetypes
            var matching = new List<Archetype>(16);

            // Use direct enumeration to avoid allocating Dictionary.Values collection
            foreach (var kvp in Archetypes)
            {
                var archetype = kvp.Value;
                var result = (archetype.ComponentMask & queryTag);
                bool matches = result == queryTag;
                Console.WriteLine($"GetMatchingArchetypes: Archetype with {archetype.AliveCount} entities, matches={matches}");

                // SIMD-accelerated bitwise AND
                // An archetype matches if it contains all required components
                if (matches)
                {
                    matching.Add(archetype);
                }
            }

            Console.WriteLine($"GetMatchingArchetypes: Found {matching.Count} matching archetypes (not from cache)");

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
