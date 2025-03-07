using System;

namespace EasyEcs.Core.Components;

public readonly struct ComponentRef<T> where T : struct, IComponent
{
    private readonly int _index;
    private readonly Type _type;
    private readonly Context _context;

    public ComponentRef(int index, Context context)
    {
        _index = index;
        _type = typeof(T);
        _context = context;
    }

    public ref T Value => ref ((T[])_context.Components[_context.TagRegistry.GetTagBitIndex(_type)])[_index];

    public static implicit operator T(ComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}

public readonly struct SingletonComponentRef<T> where T : struct, ISingletonComponent
{
    private readonly int _index;
    private readonly Type _type;
    private readonly Context _context;

    public SingletonComponentRef(int index, Context context)
    {
        _index = index;
        _type = typeof(T);
        _context = context;
    }

    public ref T Value => ref ((T[])_context.Components[_context.TagRegistry.GetTagBitIndex(_type)])[_index];

    public static implicit operator T(SingletonComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}