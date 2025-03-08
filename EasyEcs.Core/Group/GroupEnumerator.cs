using System;
using System.Collections;
using System.Collections.Generic;
using EasyEcs.Core.Components;

namespace EasyEcs.Core.Group;

public readonly struct GroupResultEnumerator : IEnumerable<GroupResult>
{
    private readonly Type[] _types;
    private readonly Context _context;

    public GroupResultEnumerator(Type[] types, Context context)
    {
        _types = types;
        _context = context;
    }

    public IEnumerator<GroupResult> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        Tag tag = new();
        foreach (var type in _types)
        {
            tag.SetBit(_context.TagRegistry.GetTagBitIndex(type));
        }

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult(entity);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct GroupResultEnumerator<T> : IEnumerable<GroupResult<T>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx = _context.TagRegistry.GetTagBitIndex(typeof(T));
        Tag tag = new();
        tag.SetBit(bitIdx);
        Entity[] entities = _context.Entities;
        T[] components = _context.Components[bitIdx] as T[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T>(id, entities, components);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct GroupResultEnumerator<T1, T2> : IEnumerable<GroupResult<T1, T2>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2>(id, entities, components1, components2);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct GroupResultEnumerator<T1, T2, T3> : IEnumerable<GroupResult<T1, T2, T3>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2, T3>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        var bitIdx3 = _context.TagRegistry.GetTagBitIndex(typeof(T3));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        tag.SetBit(bitIdx3);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3>(id, entities, components1, components2, components3);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct GroupResultEnumerator<T1, T2, T3, T4> : IEnumerable<GroupResult<T1, T2, T3, T4>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2, T3, T4>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        var bitIdx3 = _context.TagRegistry.GetTagBitIndex(typeof(T3));
        var bitIdx4 = _context.TagRegistry.GetTagBitIndex(typeof(T4));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        tag.SetBit(bitIdx3);
        tag.SetBit(bitIdx4);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];
        T4[] components4 = _context.Components[bitIdx4] as T4[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4>(id, entities, components1, components2, components3,
                        components4);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct GroupResultEnumerator<T1, T2, T3, T4, T5> : IEnumerable<GroupResult<T1, T2, T3, T4, T5>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2, T3, T4, T5>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        var bitIdx3 = _context.TagRegistry.GetTagBitIndex(typeof(T3));
        var bitIdx4 = _context.TagRegistry.GetTagBitIndex(typeof(T4));
        var bitIdx5 = _context.TagRegistry.GetTagBitIndex(typeof(T5));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        tag.SetBit(bitIdx3);
        tag.SetBit(bitIdx4);
        tag.SetBit(bitIdx5);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];
        T4[] components4 = _context.Components[bitIdx4] as T4[];
        T5[] components5 = _context.Components[bitIdx5] as T5[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5>(id, entities, components1, components2,
                        components3, components4, components5);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct GroupResultEnumerator<T1, T2, T3, T4, T5, T6> : IEnumerable<GroupResult<T1, T2, T3, T4, T5, T6>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2, T3, T4, T5, T6>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        var bitIdx3 = _context.TagRegistry.GetTagBitIndex(typeof(T3));
        var bitIdx4 = _context.TagRegistry.GetTagBitIndex(typeof(T4));
        var bitIdx5 = _context.TagRegistry.GetTagBitIndex(typeof(T5));
        var bitIdx6 = _context.TagRegistry.GetTagBitIndex(typeof(T6));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        tag.SetBit(bitIdx3);
        tag.SetBit(bitIdx4);
        tag.SetBit(bitIdx5);
        tag.SetBit(bitIdx6);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];
        T4[] components4 = _context.Components[bitIdx4] as T4[];
        T5[] components5 = _context.Components[bitIdx5] as T5[];
        T6[] components6 = _context.Components[bitIdx6] as T6[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6>(id, entities, components1, components2,
                        components3, components4, components5, components6);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct
    GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7> : IEnumerable<GroupResult<T1, T2, T3, T4, T5, T6, T7>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2, T3, T4, T5, T6, T7>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        var bitIdx3 = _context.TagRegistry.GetTagBitIndex(typeof(T3));
        var bitIdx4 = _context.TagRegistry.GetTagBitIndex(typeof(T4));
        var bitIdx5 = _context.TagRegistry.GetTagBitIndex(typeof(T5));
        var bitIdx6 = _context.TagRegistry.GetTagBitIndex(typeof(T6));
        var bitIdx7 = _context.TagRegistry.GetTagBitIndex(typeof(T7));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        tag.SetBit(bitIdx3);
        tag.SetBit(bitIdx4);
        tag.SetBit(bitIdx5);
        tag.SetBit(bitIdx6);
        tag.SetBit(bitIdx7);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];
        T4[] components4 = _context.Components[bitIdx4] as T4[];
        T5[] components5 = _context.Components[bitIdx5] as T5[];
        T6[] components6 = _context.Components[bitIdx6] as T6[];
        T7[] components7 = _context.Components[bitIdx7] as T7[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6, T7>(id, entities, components1, components2,
                        components3, components4, components5, components6, components7);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct
    GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8> : IEnumerable<GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        var bitIdx3 = _context.TagRegistry.GetTagBitIndex(typeof(T3));
        var bitIdx4 = _context.TagRegistry.GetTagBitIndex(typeof(T4));
        var bitIdx5 = _context.TagRegistry.GetTagBitIndex(typeof(T5));
        var bitIdx6 = _context.TagRegistry.GetTagBitIndex(typeof(T6));
        var bitIdx7 = _context.TagRegistry.GetTagBitIndex(typeof(T7));
        var bitIdx8 = _context.TagRegistry.GetTagBitIndex(typeof(T8));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        tag.SetBit(bitIdx3);
        tag.SetBit(bitIdx4);
        tag.SetBit(bitIdx5);
        tag.SetBit(bitIdx6);
        tag.SetBit(bitIdx7);
        tag.SetBit(bitIdx8);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];
        T4[] components4 = _context.Components[bitIdx4] as T4[];
        T5[] components5 = _context.Components[bitIdx5] as T5[];
        T6[] components6 = _context.Components[bitIdx6] as T6[];
        T7[] components7 = _context.Components[bitIdx7] as T7[];
        T8[] components8 = _context.Components[bitIdx8] as T8[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>(id, entities, components1, components2,
                        components3, components4, components5, components6, components7, components8);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public readonly struct
    GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8, T9> : IEnumerable<
    GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>>
{
    private readonly Context _context;

    public GroupResultEnumerator(Context context)
    {
        _context = context;
    }

    public IEnumerator<GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>> GetEnumerator()
    {
        if (_context.Groups.Count == 0)
        {
            yield break;
        }

        var bitIdx1 = _context.TagRegistry.GetTagBitIndex(typeof(T1));
        var bitIdx2 = _context.TagRegistry.GetTagBitIndex(typeof(T2));
        var bitIdx3 = _context.TagRegistry.GetTagBitIndex(typeof(T3));
        var bitIdx4 = _context.TagRegistry.GetTagBitIndex(typeof(T4));
        var bitIdx5 = _context.TagRegistry.GetTagBitIndex(typeof(T5));
        var bitIdx6 = _context.TagRegistry.GetTagBitIndex(typeof(T6));
        var bitIdx7 = _context.TagRegistry.GetTagBitIndex(typeof(T7));
        var bitIdx8 = _context.TagRegistry.GetTagBitIndex(typeof(T8));
        var bitIdx9 = _context.TagRegistry.GetTagBitIndex(typeof(T9));
        Tag tag = new();
        tag.SetBit(bitIdx1);
        tag.SetBit(bitIdx2);
        tag.SetBit(bitIdx3);
        tag.SetBit(bitIdx4);
        tag.SetBit(bitIdx5);
        tag.SetBit(bitIdx6);
        tag.SetBit(bitIdx7);
        tag.SetBit(bitIdx8);
        tag.SetBit(bitIdx9);
        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];
        T4[] components4 = _context.Components[bitIdx4] as T4[];
        T5[] components5 = _context.Components[bitIdx5] as T5[];
        T6[] components6 = _context.Components[bitIdx6] as T6[];
        T7[] components7 = _context.Components[bitIdx7] as T7[];
        T8[] components8 = _context.Components[bitIdx8] as T8[];
        T9[] components9 = _context.Components[bitIdx9] as T9[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (id, _) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>(id, entities, components1,
                        components2,
                        components3, components4, components5, components6, components7, components8, components9);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}