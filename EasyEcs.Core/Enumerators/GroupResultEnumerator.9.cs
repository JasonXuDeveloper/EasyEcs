using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;
using EasyEcs.Core.Group;

namespace EasyEcs.Core.Enumerators;

public struct GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8, T9> : IDisposable
    where T1 : struct, IComponent
    where T2 : struct, IComponent
    where T3 : struct, IComponent
    where T4 : struct, IComponent
    where T5 : struct, IComponent
    where T6 : struct, IComponent
    where T7 : struct, IComponent
    where T8 : struct, IComponent
    where T9 : struct, IComponent
{
    private readonly Context _context;
    private readonly List<Archetype> _matchingArchetypes;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly T5[] _components5;
    private readonly T6[] _components6;
    private readonly T7[] _components7;
    private readonly T8[] _components8;
    private readonly T9[] _components9;
    private readonly Entity[] _entities;

    private int _archetypeIndex;
    private int _entityIndexInArchetype;

    public GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9> Current { get; private set; }

    private const int Tombstone = -1;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
        _entities = context.Entities;
        _components1 = null;
        _components2 = null;
        _components3 = null;
        _components4 = null;
        _components5 = null;
        _components6 = null;
        _components7 = null;
        _components8 = null;
        _components9 = null;
        _matchingArchetypes = null;
        _archetypeIndex = 0;
        _entityIndexInArchetype = 0;
        Current = default;

        if (context.TagRegistry.TryGetTagBitIndex<T1>(out var bitIdx1) &&
            context.TagRegistry.TryGetTagBitIndex<T2>(out var bitIdx2) &&
            context.TagRegistry.TryGetTagBitIndex<T3>(out var bitIdx3) &&
            context.TagRegistry.TryGetTagBitIndex<T4>(out var bitIdx4) &&
            context.TagRegistry.TryGetTagBitIndex<T5>(out var bitIdx5) &&
            context.TagRegistry.TryGetTagBitIndex<T6>(out var bitIdx6) &&
            context.TagRegistry.TryGetTagBitIndex<T7>(out var bitIdx7) &&
            context.TagRegistry.TryGetTagBitIndex<T8>(out var bitIdx8) &&
            context.TagRegistry.TryGetTagBitIndex<T9>(out var bitIdx9))
        {
            if (context.Components != null &&
                bitIdx1 < context.Components.Length &&
                bitIdx2 < context.Components.Length &&
                bitIdx3 < context.Components.Length &&
                bitIdx4 < context.Components.Length &&
                bitIdx5 < context.Components.Length &&
                bitIdx6 < context.Components.Length &&
                bitIdx7 < context.Components.Length &&
                bitIdx8 < context.Components.Length &&
                bitIdx9 < context.Components.Length)
            {
                _components1 = Unsafe.As<T1[]>(context.Components[bitIdx1]);
                _components2 = Unsafe.As<T2[]>(context.Components[bitIdx2]);
                _components3 = Unsafe.As<T3[]>(context.Components[bitIdx3]);
                _components4 = Unsafe.As<T4[]>(context.Components[bitIdx4]);
                _components5 = Unsafe.As<T5[]>(context.Components[bitIdx5]);
                _components6 = Unsafe.As<T6[]>(context.Components[bitIdx6]);
                _components7 = Unsafe.As<T7[]>(context.Components[bitIdx7]);
                _components8 = Unsafe.As<T8[]>(context.Components[bitIdx8]);
                _components9 = Unsafe.As<T9[]>(context.Components[bitIdx9]);

                var queryTag = new Tag();
                queryTag.SetBit(bitIdx1);
                queryTag.SetBit(bitIdx2);
                queryTag.SetBit(bitIdx3);
                queryTag.SetBit(bitIdx4);
                queryTag.SetBit(bitIdx5);
                queryTag.SetBit(bitIdx6);
                queryTag.SetBit(bitIdx7);
                queryTag.SetBit(bitIdx8);
                queryTag.SetBit(bitIdx9);

                _matchingArchetypes = context.GetMatchingArchetypes(queryTag);
            }
        }
    }

    public GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8, T9> GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool MoveNext()
    {
        if (_matchingArchetypes == null || _components1 == null || _components2 == null || _components3 == null || _components4 == null || _components5 == null || _components6 == null || _components7 == null || _components8 == null || _components9 == null)
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

                Current = new GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>(entityId, _entities, _components1, _components2, _components3, _components4, _components5, _components6, _components7, _components8, _components9);
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
