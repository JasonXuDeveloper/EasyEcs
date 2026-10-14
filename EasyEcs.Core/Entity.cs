using System;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

/// <summary>
/// An entity holds several components.
/// <br/>
/// It is not recommended to hold a reference to an entity at any time, since it may escape from the world (when destroyed) and cause memory leaks.
/// Simply get the entity from the world when needed via <see cref="World.GetEntityById"/> (This operation is roughly O(1)).
/// </summary>
public partial struct Entity : IEquatable<Entity>
{
    /// <summary>
    /// Unique identifier of the entity.
    /// </summary>
    public readonly int Id;

    /// <summary>
    /// Version number for detecting destroyed entities (increments on destroy).
    /// </summary>
    public readonly int Version;

    internal Tag Tag = new();

    /// <summary>
    /// The world that holds the entity.
    /// </summary>
    public World World { get; }

    /// <summary>
    /// Create a new entity.
    /// </summary>
    /// <param name="world"></param>
    /// <param name="id"></param>
    /// <param name="version"></param>
    internal Entity(World world, int id, int version)
    {
        World = world;
        Id = id;
        Version = version;
    }

    // AddComponent(s) / RemoveComponent(s) for 1..9 components are generated (Entity.Structural.g.cs).

    /// <summary>
    /// Get a component from the entity.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentRef<T> GetComponent<T>() where T : struct, IComponent
    {
        if (!TryGetComponent<T>(out var component))
            ThrowComponentMissing(typeof(T));
        return component;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowComponentMissing(Type type) =>
        throw new InvalidOperationException($"Entity has no {type.Name} component");

    /// <summary>
    /// Try to get a component from the entity.
    /// </summary>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetComponent<T>(out ComponentRef<T> value) where T : struct, IComponent
    {
        ushort bit = ComponentType<T>.Bit;
        bool has = Tag.HasBit(bit);
        value = has ? new ComponentRef<T>(Id, Version, bit, World) : default;
        return has;
    }

    /// <summary>
    /// Does the entity have a component?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasComponent<T>() where T : struct, IComponent
    {
        return Tag.HasBit(ComponentType<T>.Bit);
    }

    /// <summary>Entities are equal when they are the same incarnation (id and version) in the same world.</summary>
    public bool Equals(Entity other) => Id == other.Id && Version == other.Version && ReferenceEquals(World, other.World);

    public override bool Equals(object obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, Version);

    public override string ToString() => $"Entity {Id}v{Version}";

    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
