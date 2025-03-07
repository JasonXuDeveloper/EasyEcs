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
        if (!Tag.HasBit(Context.TagRegistry.GetTagBitIndex(typeof(T))))
            throw new InvalidOperationException($"Component {typeof(T)} not found.");

        return new ComponentRef<T>(Id, Context);
    }

    /// <summary>
    /// Try to get a component from the entity.
    /// </summary>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool TryGetComponent<T>(out ComponentRef<T> value) where T : struct, IComponent
    {
        value = new ComponentRef<T>(Id, Context);
        return Tag.HasBit(Context.TagRegistry.GetTagBitIndex(typeof(T)));
    }

    /// <summary>
    /// Does the entity have a component?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool HasComponent<T>() where T : struct, IComponent
    {
        return Tag.HasBit(Context.TagRegistry.GetTagBitIndex(typeof(T)));
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
            tag.SetBit(Context.TagRegistry.GetTagBitIndex(type));
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