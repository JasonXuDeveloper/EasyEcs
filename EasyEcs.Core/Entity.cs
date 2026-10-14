using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// Handle to an entity: id + version + world (16 bytes). Every access validates the version, so a handle to a
/// destroyed entity throws instead of touching the entity that reused its id. Safe to store and to compare.
/// </summary>
/// <remarks>
/// Refs returned by <see cref="Get{T}"/>/<see cref="Add{T}"/> point into component storage: use them immediately and
/// never keep them across a structural change or an <c>await</c>.
/// </remarks>
public readonly partial struct Entity : IEquatable<Entity>
{
    private readonly World _world;

    /// <summary>Entity id (dense, recycled after destruction).</summary>
    public readonly int Id;

    /// <summary>Incarnation of <see cref="Id"/>; bumped when the entity is destroyed.</summary>
    public readonly int Version;

    internal Entity(World world, int id, int version)
    {
        _world = world;
        Id = id;
        Version = version;
    }

    /// <summary>The world owning the entity (null for <c>default</c>).</summary>
    public World World => _world;

    /// <summary>Whether this incarnation is alive. O(1).</summary>
    public bool IsAlive => _world != null && (uint)Id < (uint)_world.Meta.Length && _world.Meta[Id].Version == Version &&
                           _world.Meta[Id].Archetype != null;

    /// <summary>Whether the entity has component <typeparamref name="T"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>() where T : struct, IComponent => Archetype.ComponentMask.HasBit(ComponentType<T>.Bit);

    /// <summary>Ref to component <typeparamref name="T"/>; throws if the entity does not have it. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>() where T : struct, IComponent
    {
        ushort bit = ComponentType<T>.Bit;
        if (!Archetype.ComponentMask.HasBit(bit)) ThrowMissing(typeof(T));
        return ref _world.ColumnRef<T>(bit, Id);
    }

    /// <summary>Copy of component <typeparamref name="T"/> if the entity has it. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<T>(out T value) where T : struct, IComponent
    {
        ushort bit = ComponentType<T>.Bit;
        if (Archetype.ComponentMask.HasBit(bit))
        {
            value = _world.ColumnRef<T>(bit, Id);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Add component <typeparamref name="T"/> (reset to default if present) and return a ref to it. O(1) amortized.</summary>
    public ref T Add<T>() where T : struct, IComponent
    {
        var world = _world;
        lock (world.StructuralLock)
        {
            world.ThrowIfStale(this);
            ushort bit = world.PrepareColumn<T>();
            world.AddBit(Id, bit);
            ref T value = ref world.ColumnRef<T>(bit, Id);
            value = default;
            return ref value;
        }
    }

    /// <summary>Ref to component <typeparamref name="T"/>, adding it (default) first if missing. O(1) amortized.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetOrAdd<T>() where T : struct, IComponent
    {
        ushort bit = ComponentType<T>.Bit;
        return ref Archetype.ComponentMask.HasBit(bit) ? ref _world.ColumnRef<T>(bit, Id) : ref Add<T>();
    }

    // Set(in T1..Tn) and Remove<T1..Tn>() are generated (Entity.Structural.g.cs).

    /// <summary>Destroy the entity; a destroyed handle is ignored. O(components).</summary>
    public void Destroy() => _world?.DestroyEntity(this);

    /// <summary>Archetype of a live entity; throws for a stale handle (one cache line: id -> meta).</summary>
    internal Archetype Archetype
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref var meta = ref _world.MetaRef(Id);
            if (meta.Version != Version || meta.Archetype == null) ThrowStale();
            return meta.Archetype;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowStale() => throw new InvalidOperationException("Entity has been destroyed");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissing(Type type) => throw new InvalidOperationException($"Entity has no {type.Name} component");

    public bool Equals(Entity other) => Id == other.Id && Version == other.Version && ReferenceEquals(_world, other._world);

    public override bool Equals(object obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, Version);

    public override string ToString() => $"Entity {Id}v{Version}";

    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
