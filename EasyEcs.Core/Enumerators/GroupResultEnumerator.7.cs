using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EasyEcs.Core.Components;
using EasyEcs.Core.Group;

namespace EasyEcs.Core.Enumerators;

public struct GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7> : IDisposable
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
    where T6 : struct
    where T7 : struct
{
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly T5[] _components5;
    private readonly T6[] _components6;
    private readonly T7[] _components7;
    private readonly Tag _tag;
    private readonly Dictionary<Tag, List<int>> _contextGroups;
    private List<List<int>> _groups;
    private int _groupIdx;
    private int _elementIdx;
    public GroupResult<T1, T2, T3, T4, T5, T6, T7> Current { get; private set; }

    public GroupResultEnumerator(Context context)
    {
        _entities = Array.Empty<Entity>();
        _components1 = Array.Empty<T1>();
        _components2 = Array.Empty<T2>();
        _components3 = Array.Empty<T3>();
        _components4 = Array.Empty<T4>();
        _components5 = Array.Empty<T5>();
        _components6 = Array.Empty<T6>();
        _components7 = Array.Empty<T7>();
        _groups = null;
        _groupIdx = 0;
        _elementIdx = 0;
        Current = default;
        _tag = default;
        _contextGroups = null;

        if (context.Groups.Count == 0) return;

        Tag tmp = new();
        if (!context.TagRegistry.TryGetTagBitIndex<T1>(out var bitIdx1)) return;
        tmp.SetBit(bitIdx1);
        if (!context.TagRegistry.TryGetTagBitIndex<T2>(out var bitIdx2)) return;
        tmp.SetBit(bitIdx2);
        if (!context.TagRegistry.TryGetTagBitIndex<T3>(out var bitIdx3)) return;
        tmp.SetBit(bitIdx3);
        if (!context.TagRegistry.TryGetTagBitIndex<T4>(out var bitIdx4)) return;
        tmp.SetBit(bitIdx4);
        if (!context.TagRegistry.TryGetTagBitIndex<T5>(out var bitIdx5)) return;
        tmp.SetBit(bitIdx5);
        if (!context.TagRegistry.TryGetTagBitIndex<T6>(out var bitIdx6)) return;
        tmp.SetBit(bitIdx6);
        if (!context.TagRegistry.TryGetTagBitIndex<T7>(out var bitIdx7)) return;
        tmp.SetBit(bitIdx7);

        _tag = tmp;
        _entities = context.Entities;
        _components1 = context.Components[bitIdx1] as T1[];
        _components2 = context.Components[bitIdx2] as T2[];
        _components3 = context.Components[bitIdx3] as T3[];
        _components4 = context.Components[bitIdx4] as T4[];
        _components5 = context.Components[bitIdx5] as T5[];
        _components6 = context.Components[bitIdx6] as T6[];
        _components7 = context.Components[bitIdx7] as T7[];
        _contextGroups = context.Groups;
        _groupIdx = 0;
        _elementIdx = 0;
        Current = default;
    }

    private GroupResultEnumerator(Entity[] entities, T1[] components1, T2[] components2, T3[] components3,
        T4[] components4, T5[] components5, T6[] components6, T7[] components7, Tag tag, int groupIdx, int elementIdx,
        Dictionary<Tag, List<int>> contextGroups)
    {
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
        _components4 = components4;
        _components5 = components5;
        _components6 = components6;
        _components7 = components7;
        _tag = tag;
        _groupIdx = groupIdx;
        _elementIdx = elementIdx;
        Current = default;
        _contextGroups = contextGroups;

        if (_contextGroups == null)
        {
            _groups = null;
            return;
        }

        _groups = Pool<List<List<int>>>.Rent();
        _groups.Clear();
        foreach (var kvp in contextGroups)
        {
            if ((kvp.Key & tag) == tag)
            {
                _groups.Add(kvp.Value);
            }
        }
    }

    public GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7> GetEnumerator() =>
        new(_entities, _components1, _components2, _components3, _components4, _components5, _components6, _components7,
            _tag, _groupIdx, _elementIdx, _contextGroups);

    public bool MoveNext()
    {
        if (_groups == null || _groups.Count == 0) return false;

        Span<List<int>> span = CollectionsMarshal.AsSpan(_groups);
        while (_groupIdx < _groups.Count)
        {
            Span<int> group = CollectionsMarshal.AsSpan(span[_groupIdx]);
            if (_elementIdx < group.Length)
            {
                Current = new GroupResult<T1, T2, T3, T4, T5, T6, T7>(group[_elementIdx], _entities, _components1,
                    _components2, _components3, _components4, _components5, _components6, _components7);
                _elementIdx++;
                return true;
            }

            _groupIdx++;
            _elementIdx = 0;
        }

        Current = default;
        _groups.Clear();
        Pool<List<List<int>>>.Return(_groups);
        _groups = null;
        return false;
    }

    public void Dispose()
    {
        if (_groups != null)
        {
            _groups.Clear();
            Pool<List<List<int>>>.Return(_groups);
            _groups = null;
        }
    }
}