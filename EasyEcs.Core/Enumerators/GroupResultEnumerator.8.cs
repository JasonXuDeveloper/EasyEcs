using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;
using EasyEcs.Core.Group;

namespace EasyEcs.Core.Enumerators;

public struct GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8> : IDisposable
    where T1 : struct, IComponent
    where T2 : struct, IComponent
    where T3 : struct, IComponent
    where T4 : struct, IComponent
    where T5 : struct, IComponent
    where T6 : struct, IComponent
    where T7 : struct, IComponent
    where T8 : struct, IComponent
{
    private readonly Context _context;
    private readonly List<Archetype> _matchingArchetypes;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;
    private readonly int _bitIdx4;
    private readonly int _bitIdx5;
    private readonly int _bitIdx6;
    private readonly int _bitIdx7;
    private readonly int _bitIdx8;

    private int _archetypeIndex;
    private int _entityIndexInArchetype;

    public GroupResult<T1, T2, T3, T4, T5, T6, T7, T8> Current { get; private set; }

    private const int Tombstone = -1;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
        _bitIdx1 = -1;
        _bitIdx2 = -1;
        _bitIdx3 = -1;
        _bitIdx4 = -1;
        _bitIdx5 = -1;
        _bitIdx6 = -1;
        _bitIdx7 = -1;
        _bitIdx8 = -1;
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
            context.TagRegistry.TryGetTagBitIndex<T8>(out var bitIdx8))
        {
            if (context.Components != null &&
                bitIdx1 < context.Components.Length &&
                bitIdx2 < context.Components.Length &&
                bitIdx3 < context.Components.Length &&
                bitIdx4 < context.Components.Length &&
                bitIdx5 < context.Components.Length &&
                bitIdx6 < context.Components.Length &&
                bitIdx7 < context.Components.Length &&
                bitIdx8 < context.Components.Length)
            {
                _bitIdx1 = bitIdx1;
                _bitIdx2 = bitIdx2;
                _bitIdx3 = bitIdx3;
                _bitIdx4 = bitIdx4;
                _bitIdx5 = bitIdx5;
                _bitIdx6 = bitIdx6;
                _bitIdx7 = bitIdx7;
                _bitIdx8 = bitIdx8;

                var queryTag = new Tag();
                queryTag.SetBit(bitIdx1);
                queryTag.SetBit(bitIdx2);
                queryTag.SetBit(bitIdx3);
                queryTag.SetBit(bitIdx4);
                queryTag.SetBit(bitIdx5);
                queryTag.SetBit(bitIdx6);
                queryTag.SetBit(bitIdx7);
                queryTag.SetBit(bitIdx8);

                _matchingArchetypes = context.GetMatchingArchetypes(queryTag);
            }
        }
    }

    public GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8> GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool MoveNext()
    {
        if (_matchingArchetypes == null || _bitIdx1 < 0 || _bitIdx2 < 0 || _bitIdx3 < 0 || _bitIdx4 < 0 || _bitIdx5 < 0 || _bitIdx6 < 0 || _bitIdx7 < 0 || _bitIdx8 < 0)
            return false;

        while (_archetypeIndex < _matchingArchetypes.Count)
        {
            var archetype = _matchingArchetypes[_archetypeIndex];

            while (true)
            {
                // Get fresh span on each iteration to handle concurrent modifications
                var entitySpan = archetype.GetEntitySpan();

                if (_entityIndexInArchetype >= entitySpan.Length)
                    break;

                int entityId = entitySpan[_entityIndexInArchetype++];

                if (entityId == Tombstone)
                    continue;

                Current = new GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>(entityId, _context, _bitIdx1, _bitIdx2, _bitIdx3, _bitIdx4, _bitIdx5, _bitIdx6, _bitIdx7, _bitIdx8);
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
