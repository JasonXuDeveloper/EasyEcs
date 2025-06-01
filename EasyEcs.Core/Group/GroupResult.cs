using System;

namespace EasyEcs.Core.Group;

public readonly struct GroupResult
{
    private readonly int _entityId;
    private readonly Context _context;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T GetComponent<T>() where T : struct
    {
        return ref ((T[])_context.Components[_context.TagRegistry.GetTagBitIndex<T>()]).AsSpan()[_entityId];
    }

    public GroupResult(int id, Context context)
    {
        _entityId = id;
        _context = context;
    }
}

public readonly struct GroupResult<T1>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
    }
}

public readonly struct GroupResult<T1, T2>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
    }
}

public readonly struct GroupResult<T1, T2, T3>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public ref T3 Component3 => ref _components3.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2, T3[] components3)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public ref T3 Component3 => ref _components3.AsSpan()[_entityId];

    public ref T4 Component4 => ref _components4.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2, T3[] components3,
        T4[] components4)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
        _components4 = components4;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly T5[] _components5;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public ref T3 Component3 => ref _components3.AsSpan()[_entityId];

    public ref T4 Component4 => ref _components4.AsSpan()[_entityId];

    public ref T5 Component5 => ref _components5.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2, T3[] components3,
        T4[] components4, T5[] components5)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
        _components4 = components4;
        _components5 = components5;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly T5[] _components5;
    private readonly T6[] _components6;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public ref T3 Component3 => ref _components3.AsSpan()[_entityId];

    public ref T4 Component4 => ref _components4.AsSpan()[_entityId];

    public ref T5 Component5 => ref _components5.AsSpan()[_entityId];

    public ref T6 Component6 => ref _components6.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2, T3[] components3,
        T4[] components4, T5[] components5, T6[] components6)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
        _components4 = components4;
        _components5 = components5;
        _components6 = components6;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6, T7>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly T5[] _components5;
    private readonly T6[] _components6;
    private readonly T7[] _components7;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public ref T3 Component3 => ref _components3.AsSpan()[_entityId];

    public ref T4 Component4 => ref _components4.AsSpan()[_entityId];

    public ref T5 Component5 => ref _components5.AsSpan()[_entityId];

    public ref T6 Component6 => ref _components6.AsSpan()[_entityId];

    public ref T7 Component7 => ref _components7.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2, T3[] components3,
        T4[] components4, T5[] components5, T6[] components6, T7[] components7)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
        _components4 = components4;
        _components5 = components5;
        _components6 = components6;
        _components7 = components7;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly T5[] _components5;
    private readonly T6[] _components6;
    private readonly T7[] _components7;
    private readonly T8[] _components8;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public ref T3 Component3 => ref _components3.AsSpan()[_entityId];

    public ref T4 Component4 => ref _components4.AsSpan()[_entityId];

    public ref T5 Component5 => ref _components5.AsSpan()[_entityId];

    public ref T6 Component6 => ref _components6.AsSpan()[_entityId];

    public ref T7 Component7 => ref _components7.AsSpan()[_entityId];

    public ref T8 Component8 => ref _components8.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2, T3[] components3,
        T4[] components4, T5[] components5, T6[] components6, T7[] components7, T8[] components8)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
        _components4 = components4;
        _components5 = components5;
        _components6 = components6;
        _components7 = components7;
        _components8 = components8;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>
{
    private readonly int _entityId;
    private readonly Entity[] _entities;
    private readonly T1[] _components1;
    private readonly T2[] _components2;
    private readonly T3[] _components3;
    private readonly T4[] _components4;
    private readonly T5[] _components5;
    private readonly T6[] _components6;
    private readonly T7[] _components7;
    private readonly T8[] _components8;
    private readonly T9[] _components9;

    public ref Entity Entity => ref _entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref _components1.AsSpan()[_entityId];

    public ref T2 Component2 => ref _components2.AsSpan()[_entityId];

    public ref T3 Component3 => ref _components3.AsSpan()[_entityId];

    public ref T4 Component4 => ref _components4.AsSpan()[_entityId];

    public ref T5 Component5 => ref _components5.AsSpan()[_entityId];

    public ref T6 Component6 => ref _components6.AsSpan()[_entityId];

    public ref T7 Component7 => ref _components7.AsSpan()[_entityId];

    public ref T8 Component8 => ref _components8.AsSpan()[_entityId];

    public ref T9 Component9 => ref _components9.AsSpan()[_entityId];

    public GroupResult(int id, Entity[] entities, T1[] components1, T2[] components2, T3[] components3,
        T4[] components4, T5[] components5, T6[] components6, T7[] components7, T8[] components8, T9[] components9)
    {
        _entityId = id;
        _entities = entities;
        _components1 = components1;
        _components2 = components2;
        _components3 = components3;
        _components4 = components4;
        _components5 = components5;
        _components6 = components6;
        _components7 = components7;
        _components8 = components8;
        _components9 = components9;
    }
}