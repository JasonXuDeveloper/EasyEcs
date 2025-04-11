using System;
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
    internal Entity(Context context, int id)
    {
        Context = context;
        Id = id;
    }

    /// <summary>
    /// Add a component to the entity. Returns a reference to the component. If the component already exists, returns a reference to the existing component.
    /// </summary>
    /// <param name="callback"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    ///
    public void AddComponent<T>(Action<ComponentRef<T>> callback = null) where T : struct, IComponent
    {
        Context.AddComponent(this, callback);
    }

    /// <summary>
    /// Remove a component from the entity. Returns true if the component was removed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public void RemoveComponent<T>() where T : struct, IComponent
    {
        Context.RemoveComponent<T>(this);
    }

    /// <summary>
    /// Get a component from the entity.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public ComponentRef<T> GetComponent<T>() where T : struct, IComponent
    {
        var idx = Context.TagRegistry.GetTagBitIndex(typeof(T));
        if (!Tag.HasBit(idx))
            throw new InvalidOperationException($"Component {typeof(T)} not found.");

        return new ComponentRef<T>(Id, idx, Context);
    }

    /// <summary>
    /// Try to get a component from the entity.
    /// </summary>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool TryGetComponent<T>(out ComponentRef<T> value) where T : struct, IComponent
    {
        value = default;
        if (!Context.TagRegistry.TryGetTagBitIndex(typeof(T), out var idx))
            return false;
        if (!Tag.HasBit(idx))
            return false;

        value = new ComponentRef<T>(Id, idx, Context);
        return true;
    }

    /// <summary>
    /// Does the entity have a component?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool HasComponent<T>() where T : struct, IComponent
    {
        if (Context.TagRegistry.TryGetTagBitIndex(typeof(T), out var idx))
            return Tag.HasBit(idx);
        return false;
    }

    /// <summary>
    /// Does the entity have all the components?
    /// </summary>
    /// <param name="types"></param>
    /// <returns></returns>
    public bool HasComponents(params Type[] types)
    {
        Tag tag = new();
        foreach (var type in types)
        {
            if (!Context.TagRegistry.TryGetTagBitIndex(type, out var idx))
                return false;
            tag.SetBit(idx);
        }

        return Tag == tag;
    }

    /// <summary>
    /// Get the hash code of the entity.
    /// </summary>
    /// <returns></returns>
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