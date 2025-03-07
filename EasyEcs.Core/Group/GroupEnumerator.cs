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

        Tag tag = new();
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T3)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T3)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T4)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T3)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T4)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T5)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T3)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T4)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T5)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T6)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T3)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T4)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T5)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T6)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T7)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6, T7>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T3)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T4)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T5)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T6)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T7)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T8)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>(entity);
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
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T1)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T2)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T3)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T4)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T5)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T6)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T7)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T8)));
        tag.SetBit(_context.TagRegistry.GetTagBitIndex(typeof(T9)));

        foreach (var group in _context.Groups)
        {
            if ((group.Key & tag) == tag)
            {
                foreach (var (_, entity) in group.Value)
                {
                    yield return new GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>(entity);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}