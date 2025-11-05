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
    private readonly T[] _components;
    private readonly Entity[] _entities;

    private int _archetypeIndex;
    private int _entityIndexInArchetype;

    public GroupResult<T> Current { get; private set; }

    private const int Tombstone = -1;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
        _entities = context.Entities;
        _components = null;
        _matchingArchetypes = null;
        _archetypeIndex = 0;
        _entityIndexInArchetype = 0;
        Current = default;

        if (context.TagRegistry.TryGetTagBitIndex<T>(out var bitIdx))
        {
            // Safely access Components array (may not be initialized yet)
            if (context.Components != null && bitIdx < context.Components.Length)
            {
                _components = Unsafe.As<T[]>(context.Components[bitIdx]);

                // Build query tag
                var queryTag = new Tag();
                queryTag.SetBit(bitIdx);

                // Get matching archetypes from cache (O(1) after first access)
                _matchingArchetypes = context.GetMatchingArchetypes(queryTag);
            }
        }
    }

    public GroupResultEnumerator<T> GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool MoveNext()
    {
        if (_matchingArchetypes == null || _components == null)
            return false;

        // Iterate through archetypes
        while (_archetypeIndex < _matchingArchetypes.Count)
        {
            var archetype = _matchingArchetypes[_archetypeIndex];

            // CRITICAL: Get fresh span on EACH MoveNext() to handle array resize
            var entitySpan = archetype.GetEntitySpan();

            // Iterate entities in current archetype
            while (_entityIndexInArchetype < entitySpan.Length)
            {
                int entityId = entitySpan[_entityIndexInArchetype++];

                // Skip tombstones (-1) - safe iteration during modifications
                if (entityId == Tombstone)
                    continue;

                Current = new GroupResult<T>(entityId, _entities, _components);
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
