using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs.Core.Components;

/// <summary>
/// Optimized component reference with packed ID+version and Unsafe access.
/// 24 bytes total (down from 32+).
/// </summary>
public readonly struct ComponentRef<T> where T : struct, IComponent
{
    // Pack entity ID (lower 32 bits) + version (upper 32 bits)
    private readonly long _packed;
    private readonly ushort _componentIndex; // Supports up to 65536 component types
    private readonly Context _context;

    public ComponentRef(int entityId, int version, ushort componentIndex, Context context)
    {
        _packed = ((long)version << 32) | (uint)entityId;
        _componentIndex = componentIndex;
        _context = context;
    }

    private int EntityId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)_packed;
    }

    private int Version
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(_packed >> 32);
    }

    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int id = EntityId;
            var context = _context;

            // Validate version without bounds checks (id < Meta.Length is an invariant of live refs)
            if (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(context.Meta), id).Version != Version)
                ThrowEntityDestroyed();

            // Fast path inline: the column exists whenever this context added the component
            var columns = context.Components;
            if (columns == null || _componentIndex >= (uint)columns.Length || columns[_componentIndex] == null)
                return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(EnsureComponentArraySlow()), id);

            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Unsafe.As<T[]>(columns[_componentIndex])), id);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T[] EnsureComponentArraySlow() => _context.EnsureComponentArrayInitialized<T>(_componentIndex);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEntityDestroyed() =>
        throw new InvalidOperationException("Entity has been destroyed");

    public static implicit operator T(ComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}

public readonly struct SingletonComponentRef<T> where T : struct, ISingletonComponent
{
    private readonly SingletonBox<T> _box;

    internal SingletonComponentRef(SingletonBox<T> box) => _box = box;

    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _box.Value;
    }

    public static implicit operator T(SingletonComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}
