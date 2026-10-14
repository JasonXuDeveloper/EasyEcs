using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs.Core;

/// <summary>
/// Optimized entity reference with packed ID+version and Unsafe access.
/// 16 bytes total (down from 24).
/// </summary>
public readonly struct EntityRef : IEquatable<EntityRef>
{
    // Pack ID (lower 32 bits) + version (upper 32 bits) into single long
    private readonly long _packed;
    private readonly World _world;

    public EntityRef(int id, int version, World world)
    {
        _packed = ((long)version << 32) | (uint)id;
        _world = world;
    }

    internal int Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)_packed;
    }

    internal int Version
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(_packed >> 32);
    }

    public ref Entity Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            int id = Id;

            // Validate version using Unsafe (zero bounds checks)
            ref int actualVersion = ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(_world.Meta), id).Version;

            if (actualVersion != Version)
                ThrowEntityDestroyed();

            return ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(_world.Entities),
                id);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEntityDestroyed() =>
        throw new InvalidOperationException("Entity has been destroyed");

    public static implicit operator EntityRef(Entity entity)
    {
        return new EntityRef(entity.Id, entity.Version, entity.World);
    }

    public static implicit operator Entity(EntityRef entityRef)
    {
        return entityRef.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(EntityRef left, EntityRef right)
    {
        return left._packed == right._packed && left._world == right._world;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(EntityRef left, EntityRef right)
    {
        return left._packed != right._packed || left._world != right._world;
    }

    public override bool Equals(object obj)
    {
        return obj is EntityRef other && this == other;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(EntityRef other)
    {
        return _packed == other._packed && ReferenceEquals(_world, other._world);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return _packed.GetHashCode();
    }
}