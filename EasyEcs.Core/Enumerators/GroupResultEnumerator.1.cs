using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;
using EasyEcs.Core.Group;

namespace EasyEcs.Core.Enumerators;

/// <summary>
/// Zero-allocation enumerator for entities with a single component type.
/// Uses live iteration with tombstone handling for safe concurrent modifications.
/// </summary>
public struct GroupResultEnumerator<T> : IDisposable
    where T : struct, IComponent
{
    private readonly Context _context;
    private readonly List<Archetype> _matchingArchetypes;
    private readonly int _bitIdx;

    private int _archetypeIndex;
    private int _entityIndexInArchetype;

    public GroupResult<T> Current { get; private set; }

    private const int Tombstone = -1;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
        _bitIdx = -1;
        _matchingArchetypes = null;
        _archetypeIndex = 0;
        _entityIndexInArchetype = 0;
        Current = default;

        Console.WriteLine($"GroupResultEnumerator<{typeof(T).Name}>: Starting query");

        if (context.TagRegistry.TryGetTagBitIndex<T>(out var bitIdx))
        {
            Console.WriteLine($"GroupResultEnumerator<{typeof(T).Name}>: Found bitIdx = {bitIdx}");

            // Safely access Components array (may not be initialized yet)
            if (context.Components != null && bitIdx < context.Components.Length)
            {
                _bitIdx = bitIdx;

                // Build query tag
                var queryTag = new Tag();
                queryTag.SetBit(bitIdx);

                // Get matching archetypes from cache (O(1) after first access)
                _matchingArchetypes = context.GetMatchingArchetypes(queryTag);
                Console.WriteLine($"GroupResultEnumerator<{typeof(T).Name}>: Found {_matchingArchetypes.Count} matching archetypes");
            }
            else
            {
                Console.WriteLine($"GroupResultEnumerator<{typeof(T).Name}>: Components is null or bitIdx out of range. Components != null: {context.Components != null}, Components.Length: {context.Components?.Length ?? -1}, bitIdx: {bitIdx}");
            }
        }
        else
        {
            Console.WriteLine($"GroupResultEnumerator<{typeof(T).Name}>: TryGetTagBitIndex returned false");
        }
    }

    public GroupResultEnumerator<T> GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool MoveNext()
    {
        if (_matchingArchetypes == null || _bitIdx < 0)
            return false;

        // Iterate through archetypes
        while (_archetypeIndex < _matchingArchetypes.Count)
        {
            var archetype = _matchingArchetypes[_archetypeIndex];

            // Iterate entities in current archetype
            while (true)
            {
                // CRITICAL: Get fresh span on EACH iteration to handle concurrent modifications
                var entitySpan = archetype.GetEntitySpan();

                if (_entityIndexInArchetype >= entitySpan.Length)
                    break;

                int entityId = entitySpan[_entityIndexInArchetype++];

                // Skip tombstones (-1) - safe iteration during modifications
                if (entityId == Tombstone)
                    continue;

                Current = new GroupResult<T>(entityId, _context, _bitIdx);
                return true;
            }

            // Move to next archetype
            _archetypeIndex++;
            _entityIndexInArchetype = 0;
        }

        return false;
    }

    public void Dispose()
    {
        // Zero allocations - nothing to dispose
    }
}
