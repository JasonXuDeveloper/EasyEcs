using System;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

/// <summary>
/// An entity holds several components.
/// <br/>
/// It is not recommended to hold a reference to an entity at any time, since it may escape from the context (when destroyed) and cause memory leaks.
/// Simply get the entity from the context when needed via <see cref="Context.GetEntityById"/> (This operation is roughly O(1)).
/// </summary>
public struct Entity : IEquatable<Entity>
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
    /// The context that holds the entity.
    /// </summary>
    public Context Context { get; }

    /// <summary>
    /// Create a new entity.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="id"></param>
    /// <param name="version"></param>
    internal Entity(Context context, int id, int version)
    {
        Context = context;
        Id = id;
        Version = version;
    }

    /// <summary>
    /// Add a component to the entity immediately and return a reference to it.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns>Reference to the newly added component</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentRef<T> AddComponent<T>() where T : struct, IComponent
    {
        return Context.AddComponent<T>(this);
    }

    /// <summary>
    /// Remove a component from the entity. Returns true if the component was removed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponent<T>() where T : struct, IComponent
    {
        Context.RemoveComponent<T>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>) AddComponents<T1, T2>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        return Context.AddComponents<T1, T2>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>) AddComponents<T1, T2, T3>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        return Context.AddComponents<T1, T2, T3>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>) AddComponents<T1, T2, T3, T4>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        return Context.AddComponents<T1, T2, T3, T4>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>) AddComponents<T1, T2, T3, T4, T5>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        return Context.AddComponents<T1, T2, T3, T4, T5>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>) AddComponents<T1, T2, T3, T4, T5, T6>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
    {
        return Context.AddComponents<T1, T2, T3, T4, T5, T6>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>, ComponentRef<T7>) AddComponents<T1, T2, T3, T4, T5, T6, T7>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        return Context.AddComponents<T1, T2, T3, T4, T5, T6, T7>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>, ComponentRef<T7>, ComponentRef<T8>) AddComponents<T1, T2, T3, T4, T5, T6, T7, T8>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
    {
        return Context.AddComponents<T1, T2, T3, T4, T5, T6, T7, T8>(this);
    }

    /// <summary>
    /// Add multiple components to the entity with a single archetype transition.
    /// More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>, ComponentRef<T7>, ComponentRef<T8>, ComponentRef<T9>) AddComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
        where T9 : struct, IComponent
    {
        return Context.AddComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2, T3>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2, T3>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2, T3, T4>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2, T3, T4>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2, T3, T4, T5>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2, T3, T4, T5>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2, T3, T4, T5, T6>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6, T7>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2, T3, T4, T5, T6, T7>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6, T7, T8>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2, T3, T4, T5, T6, T7, T8>(this);
    }

    /// <summary>
    /// Remove multiple components from the entity with a single archetype transition.
    /// More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
        where T9 : struct, IComponent
    {
        Context.RemoveComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>(this);
    }

    /// <summary>
    /// Get a component from the entity.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentRef<T> GetComponent<T>() where T : struct, IComponent
    {
        var idx = Context.TagRegistry.GetTagBitIndex<T>();
        if (!Tag.HasBit(idx))
            throw new InvalidOperationException("Component not found");

        return new ComponentRef<T>(Id, Version, idx, Context);
    }

    /// <summary>
    /// Try to get a component from the entity.
    /// </summary>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetComponent<T>(out ComponentRef<T> value) where T : struct, IComponent
    {
        value = default;
        if (!Context.TagRegistry.TryGetTagBitIndex<T>(out var idx))
            return false;
        if (!Tag.HasBit(idx))
            return false;

        value = new ComponentRef<T>(Id, Version, idx, Context);
        return true;
    }

    /// <summary>
    /// Does the entity have a component?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasComponent<T>() where T : struct, IComponent
    {
        if (Context.TagRegistry.TryGetTagBitIndex<T>(out var idx))
            return Tag.HasBit(idx);
        return false;
    }

    /// <summary>
    /// Get the hash code of the entity.
    /// </summary>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return Id;
    }

    /// <summary>
    /// Is the entity equal to another entity?
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(Entity other)
    {
        return Id == other.Id;
    }

    /// <summary>
    /// Is the entity equal to another object?
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public override bool Equals(object obj)
    {
        if (obj is Entity entity)
        {
            return entity.Id == Id;
        }

        return false;
    }

    /// <summary>
    /// Get the string representation of the entity.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return $"Entity {Id}";
    }

    public static bool operator ==(Entity left, Entity right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Entity left, Entity right)
    {
        return !(left == right);
    }
}