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
    private readonly Context _context;
    private readonly int _bitIdx1;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
    }
}

public readonly struct GroupResult<T1, T2>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
    }
}

public readonly struct GroupResult<T1, T2, T3>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public ref T3 Component3 => ref Unsafe.As<T3[]>(_context.Components[_bitIdx3]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2, int bitIdx3)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
        _bitIdx3 = bitIdx3;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;
    private readonly int _bitIdx4;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public ref T3 Component3 => ref Unsafe.As<T3[]>(_context.Components[_bitIdx3]).AsSpan()[_entityId];

    public ref T4 Component4 => ref Unsafe.As<T4[]>(_context.Components[_bitIdx4]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2, int bitIdx3, int bitIdx4)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
        _bitIdx3 = bitIdx3;
        _bitIdx4 = bitIdx4;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;
    private readonly int _bitIdx4;
    private readonly int _bitIdx5;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public ref T3 Component3 => ref Unsafe.As<T3[]>(_context.Components[_bitIdx3]).AsSpan()[_entityId];

    public ref T4 Component4 => ref Unsafe.As<T4[]>(_context.Components[_bitIdx4]).AsSpan()[_entityId];

    public ref T5 Component5 => ref Unsafe.As<T5[]>(_context.Components[_bitIdx5]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2, int bitIdx3, int bitIdx4, int bitIdx5)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
        _bitIdx3 = bitIdx3;
        _bitIdx4 = bitIdx4;
        _bitIdx5 = bitIdx5;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;
    private readonly int _bitIdx4;
    private readonly int _bitIdx5;
    private readonly int _bitIdx6;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public ref T3 Component3 => ref Unsafe.As<T3[]>(_context.Components[_bitIdx3]).AsSpan()[_entityId];

    public ref T4 Component4 => ref Unsafe.As<T4[]>(_context.Components[_bitIdx4]).AsSpan()[_entityId];

    public ref T5 Component5 => ref Unsafe.As<T5[]>(_context.Components[_bitIdx5]).AsSpan()[_entityId];

    public ref T6 Component6 => ref Unsafe.As<T6[]>(_context.Components[_bitIdx6]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2, int bitIdx3, int bitIdx4, int bitIdx5, int bitIdx6)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
        _bitIdx3 = bitIdx3;
        _bitIdx4 = bitIdx4;
        _bitIdx5 = bitIdx5;
        _bitIdx6 = bitIdx6;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6, T7>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;
    private readonly int _bitIdx4;
    private readonly int _bitIdx5;
    private readonly int _bitIdx6;
    private readonly int _bitIdx7;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public ref T3 Component3 => ref Unsafe.As<T3[]>(_context.Components[_bitIdx3]).AsSpan()[_entityId];

    public ref T4 Component4 => ref Unsafe.As<T4[]>(_context.Components[_bitIdx4]).AsSpan()[_entityId];

    public ref T5 Component5 => ref Unsafe.As<T5[]>(_context.Components[_bitIdx5]).AsSpan()[_entityId];

    public ref T6 Component6 => ref Unsafe.As<T6[]>(_context.Components[_bitIdx6]).AsSpan()[_entityId];

    public ref T7 Component7 => ref Unsafe.As<T7[]>(_context.Components[_bitIdx7]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2, int bitIdx3, int bitIdx4, int bitIdx5, int bitIdx6, int bitIdx7)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
        _bitIdx3 = bitIdx3;
        _bitIdx4 = bitIdx4;
        _bitIdx5 = bitIdx5;
        _bitIdx6 = bitIdx6;
        _bitIdx7 = bitIdx7;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;
    private readonly int _bitIdx4;
    private readonly int _bitIdx5;
    private readonly int _bitIdx6;
    private readonly int _bitIdx7;
    private readonly int _bitIdx8;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public ref T3 Component3 => ref Unsafe.As<T3[]>(_context.Components[_bitIdx3]).AsSpan()[_entityId];

    public ref T4 Component4 => ref Unsafe.As<T4[]>(_context.Components[_bitIdx4]).AsSpan()[_entityId];

    public ref T5 Component5 => ref Unsafe.As<T5[]>(_context.Components[_bitIdx5]).AsSpan()[_entityId];

    public ref T6 Component6 => ref Unsafe.As<T6[]>(_context.Components[_bitIdx6]).AsSpan()[_entityId];

    public ref T7 Component7 => ref Unsafe.As<T7[]>(_context.Components[_bitIdx7]).AsSpan()[_entityId];

    public ref T8 Component8 => ref Unsafe.As<T8[]>(_context.Components[_bitIdx8]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2, int bitIdx3, int bitIdx4, int bitIdx5, int bitIdx6, int bitIdx7, int bitIdx8)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
        _bitIdx3 = bitIdx3;
        _bitIdx4 = bitIdx4;
        _bitIdx5 = bitIdx5;
        _bitIdx6 = bitIdx6;
        _bitIdx7 = bitIdx7;
        _bitIdx8 = bitIdx8;
    }
}

public readonly struct GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>
{
    private readonly int _entityId;
    private readonly Context _context;
    private readonly int _bitIdx1;
    private readonly int _bitIdx2;
    private readonly int _bitIdx3;
    private readonly int _bitIdx4;
    private readonly int _bitIdx5;
    private readonly int _bitIdx6;
    private readonly int _bitIdx7;
    private readonly int _bitIdx8;
    private readonly int _bitIdx9;

    public ref Entity Entity => ref _context.Entities.AsSpan()[_entityId];

    public ref T1 Component1 => ref Unsafe.As<T1[]>(_context.Components[_bitIdx1]).AsSpan()[_entityId];

    public ref T2 Component2 => ref Unsafe.As<T2[]>(_context.Components[_bitIdx2]).AsSpan()[_entityId];

    public ref T3 Component3 => ref Unsafe.As<T3[]>(_context.Components[_bitIdx3]).AsSpan()[_entityId];

    public ref T4 Component4 => ref Unsafe.As<T4[]>(_context.Components[_bitIdx4]).AsSpan()[_entityId];

    public ref T5 Component5 => ref Unsafe.As<T5[]>(_context.Components[_bitIdx5]).AsSpan()[_entityId];

    public ref T6 Component6 => ref Unsafe.As<T6[]>(_context.Components[_bitIdx6]).AsSpan()[_entityId];

    public ref T7 Component7 => ref Unsafe.As<T7[]>(_context.Components[_bitIdx7]).AsSpan()[_entityId];

    public ref T8 Component8 => ref Unsafe.As<T8[]>(_context.Components[_bitIdx8]).AsSpan()[_entityId];

    public ref T9 Component9 => ref Unsafe.As<T9[]>(_context.Components[_bitIdx9]).AsSpan()[_entityId];

    public GroupResult(int id, Context context, int bitIdx1, int bitIdx2, int bitIdx3, int bitIdx4, int bitIdx5, int bitIdx6, int bitIdx7, int bitIdx8, int bitIdx9)
    {
        _entityId = id;
        _context = context;
        _bitIdx1 = bitIdx1;
        _bitIdx2 = bitIdx2;
        _bitIdx3 = bitIdx3;
        _bitIdx4 = bitIdx4;
        _bitIdx5 = bitIdx5;
        _bitIdx6 = bitIdx6;
        _bitIdx7 = bitIdx7;
        _bitIdx8 = bitIdx8;
        _bitIdx9 = bitIdx9;
    }
}