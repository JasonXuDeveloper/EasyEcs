using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;
using EasyEcs.Core.Group;

namespace EasyEcs.Core.Enumerators;

public struct GroupResultEnumerator<T1, T2, T3, T4> : IDisposable
    where T1 : struct, IComponent
    where T2 : struct, IComponent
    where T3 : struct, IComponent
    where T4 : struct, IComponent
{
    private readonly Context _context;
    private readonly List<Archetype> _matchingArchetypes;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly Entity[] _entities;

    private int _archetypeIndex;
    private int _entityIndexInArchetype;

    public GroupResult<T1, T2, T3, T4> Current { get; private set; }

    private const int Tombstone = -1;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
        _entities = context.Entities;
        _components1 = null;
        _components2 = null;
        _components3 = null;
        _components4 = null;
        _matchingArchetypes = null;
        _archetypeIndex = 0;
        _entityIndexInArchetype = 0;
        Current = default;

        if (context.TagRegistry.TryGetTagBitIndex<T1>(out var bitIdx1) &&
            context.TagRegistry.TryGetTagBitIndex<T2>(out var bitIdx2) &&
            context.TagRegistry.TryGetTagBitIndex<T3>(out var bitIdx3) &&
            context.TagRegistry.TryGetTagBitIndex<T4>(out var bitIdx4))
        {
            _components1 = context.Components[bitIdx1] as T1[];
            _components2 = context.Components[bitIdx2] as T2[];
            _components3 = context.Components[bitIdx3] as T3[];
            _components4 = context.Components[bitIdx4] as T4[];

            var queryTag = new Tag();
            queryTag.SetBit(bitIdx1);
            queryTag.SetBit(bitIdx2);
            queryTag.SetBit(bitIdx3);
            queryTag.SetBit(bitIdx4);

            _matchingArchetypes = context.GetMatchingArchetypes(queryTag);
        }
    }

    public GroupResultEnumerator<T1, T2, T3, T4> GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool MoveNext()
    {
        if (_matchingArchetypes == null || _components1 == null || _components2 == null || _components3 == null || _components4 == null)
            return false;

        while (_archetypeIndex < _matchingArchetypes.Count)
        {
            var archetype = _matchingArchetypes[_archetypeIndex];
            var entitySpan = archetype.GetEntitySpan();

            while (_entityIndexInArchetype < entitySpan.Length)
            {
                int entityId = entitySpan[_entityIndexInArchetype++];

                if (entityId == Tombstone)
                    continue;

                Current = new GroupResult<T1, T2, T3, T4>(entityId, _entities, _components1, _components2, _components3, _components4);
                return true;
            }

            _archetypeIndex++;
            _entityIndexInArchetype = 0;
        }

        return false;
    }

    public void Dispose()
    {
    }
}
