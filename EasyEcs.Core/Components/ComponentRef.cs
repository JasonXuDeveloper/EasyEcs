namespace EasyEcs.Core.Components;

public readonly struct ComponentRef<T> where T : struct, IComponent
{
    private readonly int _index;
    private readonly byte _bitIndex;
    private readonly Context _context;

    public ComponentRef(int index, byte bitIndex, Context context)
    {
        _index = index;
        _bitIndex = bitIndex;
        _context = context;
    }

    public ref T Value => ref ((T[])_context.Components[_bitIndex])[_index];

    public static implicit operator T(ComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}

public readonly struct SingletonComponentRef<T> where T : struct, ISingletonComponent
{
    private readonly byte _bitIndex;
    private readonly Context _context;

    public ref T Value => ref ((T[])_context.Components[_bitIndex])[0];

    public SingletonComponentRef(byte bitIndex, Context context)
    {
        _bitIndex = bitIndex;
        _context = context;
    }

    public static implicit operator T(SingletonComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}