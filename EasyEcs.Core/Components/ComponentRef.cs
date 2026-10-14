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
    private readonly World _world;

    public ComponentRef(int entityId, int version, ushort componentIndex, World world)
    {
        _packed = ((long)version << 32) | (uint)entityId;
        _componentIndex = componentIndex;
        _world = world;
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
            var world = _world;

            // Validate version without bounds checks (id < Meta.Length is an invariant of live refs)
            if (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(world.Meta), id).Version != Version)
                ThrowEntityDestroyed();

            // Invariant: a ComponentRef only exists for a component the entity has, and a component bit is only ever
            // set after its column was created in this world, so the column exists and covers the id.
            return ref world.ColumnRef<T>(_componentIndex, id);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEntityDestroyed() =>
        throw new InvalidOperationException("Entity has been destroyed");

    public static implicit operator T(ComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}

/// <summary>Handle to a world singleton (from <see cref="World.TryGetSingleton{T}"/>).</summary>
public readonly struct SingletonRef<T> where T : struct, ISingletonComponent
{
    private readonly SingletonBox<T> _box;

    internal SingletonRef(SingletonBox<T> box) => _box = box;

    public bool IsValid => _box != null;

    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _box.Value;
    }
}
