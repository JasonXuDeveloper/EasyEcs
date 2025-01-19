using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace EasyEcs.Core;

/// <summary>
/// An entity holds several components.
/// </summary>
[SuppressMessage("ReSharper", "NotAccessedField.Local")]
public class Entity
{
    /// <summary>
    /// Unique identifier of the entity.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The context that holds the entity.
    /// </summary>
    private readonly Context _context;

    /// <summary>
    /// The components this entity holds.
    /// </summary>
    private readonly List<IComponent> _components = new();

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
    public T AddComponent<T>() where T : class, IComponent, new()
    {
        // Check if component already exists
        int idx = -1;
        for (var i = 0; i < _components.Count; i++)
        {
            if (_components[i] is T)
            {
                idx = i;
                break;
            }
        }

        if (idx == -1)
        {
            // Invalidate cache in the context
            _context.InvalidateGroupCache();
            // Add component
            _components.Add(new T());

            idx = _components.Count - 1;
        }

        return (T)_components[idx];
    }

    /// <summary>
    /// Get a component from the entity.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public T GetComponent<T>() where T : class, IComponent, new()
    {
        foreach (var component in _components)
        {
            if (component is T comp)
            {
                return comp;
            }
        }

        throw new InvalidOperationException($"Component {typeof(T)} not found.");
    }

    /// <summary>
    /// Does the entity have a component?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool HasComponent<T>() where T : class, IComponent, new()
    {
        foreach (var component in _components)
        {
            if (component is T)
            {
                return true;
            }
        }

        return false;
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
            var found = false;
            foreach (var component in _components)
            {
                if (component.GetType() != type) continue;
                found = true;
                break;
            }

            if (!found)
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
    public bool RemoveComponent<T>() where T : class, IComponent, new()
    {
        for (var i = 0; i < _components.Count; i++)
        {
            if (_components[i] is T)
            {
                // Invalidate cache in the context
                _context.InvalidateGroupCache();
                // Remove component
                _components.RemoveAt(i);

                return true;
            }
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