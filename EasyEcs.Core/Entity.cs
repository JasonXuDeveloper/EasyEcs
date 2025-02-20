using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace EasyEcs.Core;

/// <summary>
/// An entity holds several components.
/// <br/>
/// It is not recommended to hold a reference to an entity at any time, since it may escape from the context (when destroyed) and cause memory leaks.
/// Simply get the entity from the context when needed via <see cref="Context.GetEntityById"/> (This operation is roughly O(1)).
/// </summary>
public class Entity
{
    /// <summary>
    /// Unique identifier of the entity.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The context that holds the entity.
    /// </summary>
    public Context Context => _context;

    /// <summary>
    /// The context that holds the entity.
    /// </summary>
    private readonly Context _context;

    /// <summary>
    /// The components this entity holds.
    /// </summary>
    private readonly Dictionary<Type, Component> _components = new();

    /// <summary>
    /// Create a new entity.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="id"></param>
    public Entity(Context context, int id)
    {
        _context = context;
        Id = id;
    }

    /// <summary>
    /// Add a component to the entity. Returns a reference to the component. If the component already exists, returns a reference to the existing component.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T AddComponent<T>() where T : Component, new()
    {
        // Check if component already exists
        if (_components.TryGetValue(typeof(T), out var component))
        {
            return (T)component;
        }

        // Invalidate cache in the context
        _context.InvalidateGroupCache();
        // Add component
        var newComponent = new T();
        newComponent.EntityRef = this;
        _components.Add(typeof(T), newComponent);

        return newComponent;
    }

    /// <summary>
    /// Get a component from the entity.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public T GetComponent<T>() where T : Component, new()
    {
        if (TryGetComponent(out T component))
        {
            return component;
        }

        throw new InvalidOperationException($"Component {typeof(T)} not found.");
    }

    /// <summary>
    /// Try to get a component from the entity.
    /// </summary>
    /// <param name="component"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool TryGetComponent<T>([NotNullWhen(true)] out T component) where T : Component, new()
    {
        if (_components.TryGetValue(typeof(T), out var c))
        {
            component = (T)c;
            return true;
        }

        component = null;
        return false;
    }

    /// <summary>
    /// Does the entity have a component?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool HasComponent<T>() where T : Component, new()
    {
        return _components.ContainsKey(typeof(T));
    }

    /// <summary>
    /// Does the entity have all the components?
    /// </summary>
    /// <param name="types"></param>
    /// <returns></returns>
    public bool HasComponents(params Type[] types)
    {
        foreach (var type in types)
        {
            if (!_components.ContainsKey(type))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Remove a component from the entity. Returns true if the component was removed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool RemoveComponent<T>() where T : Component, new()
    {
        if (_components.Remove(typeof(T)))
        {
            _context.InvalidateGroupCache();
            return true;
        }

        return false;
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
}