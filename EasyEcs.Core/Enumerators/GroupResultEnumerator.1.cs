using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EasyEcs.Core.Components;
using EasyEcs.Core.Group;

namespace EasyEcs.Core.Enumerators;

public struct GroupResultEnumerator<T> : IDisposable
    where T : struct
{
    private readonly Entity[] _entities;
    private readonly T[] _components;
    private readonly Tag _tag;
    private readonly Dictionary<Tag, List<int>> _contextGroups;
    private List<List<int>> _groups;
    private int _groupIdx;
    private int _elementIdx;
    public GroupResult<T> Current { get; private set; }

    public GroupResultEnumerator(Context context)
    {
        _entities = Array.Empty<Entity>();
        _components = Array.Empty<T>();
        _groups = null;
        _groupIdx = 0;
        _elementIdx = 0;
        Current = default;
        _tag = default;
        _contextGroups = null;

        if (context.Groups.Count == 0)
        {
            return;
        }

        _tag = new();
        if (!context.TagRegistry.TryGetTagBitIndex<T>(out var bitIdx)) return;
        _tag.SetBit(bitIdx);

        _entities = context.Entities;
        _components = context.Components[bitIdx] as T[];
        _contextGroups = context.Groups;
        _groupIdx = 0;
        _elementIdx = 0;
        Current = default;
    }

    private GroupResultEnumerator(Entity[] entities, T[] components, Tag tag, int groupIdx,
        int elementIdx, Dictionary<Tag, List<int>> contextGroups)
    {
        _entities = entities;
        _components = components;
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

        foreach (var group in contextGroups)
        {
            if ((group.Key & _tag) == _tag)
            {
                _groups.Add(group.Value);
            }
        }
    }

    public GroupResultEnumerator<T> GetEnumerator() =>
        new(_entities, _components, _tag, _groupIdx, _elementIdx, _contextGroups);

    public bool MoveNext()
    {
        if (_groups == null || _groups.Count == 0)
        {
            return false;
        }

        Span<List<int>> groupsSpan = CollectionsMarshal.AsSpan(_groups);
        while (_groupIdx < _groups.Count)
        {
            Span<int> group = CollectionsMarshal.AsSpan(groupsSpan[_groupIdx]);
            if (_elementIdx < group.Length)
            {
                Current = new GroupResult<T>(group[_elementIdx], _entities, _components);
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