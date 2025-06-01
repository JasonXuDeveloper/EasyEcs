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
            if (!_context.TagRegistry.TryGetTagBitIndex(type, out var bitIdx))
            {
                yield break;
            }

            tag.SetBit(bitIdx);
        }

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var id in group.Value)
                {
                    yield return new GroupResult(id, _context);
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T), out var bitIdx))
        {
            yield break;
        }

        tag.SetBit(bitIdx);

        Entity[] entities = _context.Entities;
        T[] components = _context.Components[bitIdx] as T[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);

        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T3), out var bitIdx3))
        {
            yield break;
        }

        tag.SetBit(bitIdx3);

        Entity[] entities = _context.Entities;
        T1[] components1 = _context.Components[bitIdx1] as T1[];
        T2[] components2 = _context.Components[bitIdx2] as T2[];
        T3[] components3 = _context.Components[bitIdx3] as T3[];

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T3), out var bitIdx3))
        {
            yield break;
        }

        tag.SetBit(bitIdx3);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T4), out var bitIdx4))
        {
            yield break;
        }

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
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T3), out var bitIdx3))
        {
            yield break;
        }

        tag.SetBit(bitIdx3);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T4), out var bitIdx4))
        {
            yield break;
        }

        tag.SetBit(bitIdx4);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T5), out var bitIdx5))
        {
            yield break;
        }

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
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T3), out var bitIdx3))
        {
            yield break;
        }

        tag.SetBit(bitIdx3);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T4), out var bitIdx4))
        {
            yield break;
        }

        tag.SetBit(bitIdx4);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T5), out var bitIdx5))
        {
            yield break;
        }

        tag.SetBit(bitIdx5);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T6), out var bitIdx6))
        {
            yield break;
        }

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
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T3), out var bitIdx3))
        {
            yield break;
        }

        tag.SetBit(bitIdx3);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T4), out var bitIdx4))
        {
            yield break;
        }

        tag.SetBit(bitIdx4);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T5), out var bitIdx5))
        {
            yield break;
        }

        tag.SetBit(bitIdx5);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T6), out var bitIdx6))
        {
            yield break;
        }

        tag.SetBit(bitIdx6);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T7), out var bitIdx7))
        {
            yield break;
        }

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
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T3), out var bitIdx3))
        {
            yield break;
        }

        tag.SetBit(bitIdx3);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T4), out var bitIdx4))
        {
            yield break;
        }

        tag.SetBit(bitIdx4);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T5), out var bitIdx5))
        {
            yield break;
        }

        tag.SetBit(bitIdx5);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T6), out var bitIdx6))
        {
            yield break;
        }

        tag.SetBit(bitIdx6);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T7), out var bitIdx7))
        {
            yield break;
        }

        tag.SetBit(bitIdx7);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T8), out var bitIdx8))
        {
            yield break;
        }

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
                foreach (var id in group.Value)
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

        Tag tag = new();
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T1), out var bitIdx1))
        {
            yield break;
        }

        tag.SetBit(bitIdx1);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T2), out var bitIdx2))
        {
            yield break;
        }

        tag.SetBit(bitIdx2);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T3), out var bitIdx3))
        {
            yield break;
        }

        tag.SetBit(bitIdx3);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T4), out var bitIdx4))
        {
            yield break;
        }

        tag.SetBit(bitIdx4);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T5), out var bitIdx5))
        {
            yield break;
        }

        tag.SetBit(bitIdx5);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T6), out var bitIdx6))
        {
            yield break;
        }

        tag.SetBit(bitIdx6);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T7), out var bitIdx7))
        {
            yield break;
        }

        tag.SetBit(bitIdx7);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T8), out var bitIdx8))
        {
            yield break;
        }

        tag.SetBit(bitIdx8);
        if (!_context.TagRegistry.TryGetTagBitIndex(typeof(T9), out var bitIdx9))
        {
            yield break;
        }

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
                foreach (var id in group.Value)
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