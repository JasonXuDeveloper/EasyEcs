using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace EasyEcs.Core;

/// <summary>
/// A context holds many entities and many systems.
/// <br/>
/// You should create a context and add systems to it. Then you should call <see cref="Update"/> in a loop, at certain intervals.
/// You can create entities and add components to them. Systems will then process these entities.
/// </summary>
public class Context
{
    private readonly Random _random = new();
    private readonly List<Entity> _entities = new();
    private readonly List<Entity> _removeList = new();
    private readonly SortedList<int, IExecuteSystem> _executeSystems = new();

    private ReadOnlyCollection<Entity> _currentUpdateAllEntities;
    private readonly ConcurrentDictionary<long, List<Entity>> _groupsCache = new();
    private readonly ConcurrentQueue<List<Entity>> _pool = new();

    /// <summary>
    /// The shared context.
    /// </summary>
    public static Context Shared { get; } = new();

    /// <summary>
    /// Add a system to the context.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddSystem<T>() where T : SystemBase, new()
    {
        var system = new T();
        if (system is IExecuteSystem executeSystem)
            _executeSystems.Add(system.Priority, executeSystem);
        //TODO other systems as well
    }

    /// <summary>
    /// Update all systems.
    /// </summary>
    /// <param name="parallel"></param>
    [SuppressMessage("ReSharper", "SuspiciousTypeConversion.Global")]
    public async ValueTask Update(bool parallel = true)
    {
        // all entities before executing all systems at this update
        _currentUpdateAllEntities = _entities.AsReadOnly();
        // clear all groups
        foreach (var group in _groupsCache.Values)
        {
            // enqueue the hashset to the pool, so we can reuse it later
            group.Clear();
            _pool.Enqueue(group);
        }

        // clear the cache of the groups
        _groupsCache.Clear();
        // execute the systems
        if (!parallel)
        {
            foreach (var system in _executeSystems.Values)
            {
                if (((SystemBase)system).ShouldUpdate())
                {
                    await system.Execute(this);
                }
            }
        }
        else
        {
            await Parallel.ForEachAsync(_executeSystems.Values, async (system, _) =>
            {
                if (((SystemBase)system).ShouldUpdate())
                {
                    await system.Execute(this);
                }
            });
        }

        // remove entities
        foreach (var entity in _removeList)
        {
            _entities.Remove(entity);
        }

        _removeList.Clear();
    }

    /// <summary>
    /// Create a new entity.
    /// </summary>
    /// <returns></returns>
    public Entity CreateEntity()
    {
        var entity = new Entity(this, _random.Next());
        _entities.Add(entity);
        return entity;
    }

    /// <summary>
    /// Destroy an entity.
    /// </summary>
    /// <param name="entity"></param>
    public void DestroyEntity(Entity entity)
    {
        _removeList.Add(entity);
    }

    /// <summary>
    /// Get all entities.
    /// <br/>
    /// Note: Newly created entities from the current update will not be included.
    /// </summary>
    public ReadOnlyCollection<Entity> AllEntities => _currentUpdateAllEntities;

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: You should save the result (via LINQ) if you want to call <see cref="GroupOf"/> again, or to call <see cref="AllEntities"/>
    /// </summary>
    /// <param name="components"></param>
    /// <returns></returns>
    public ReadOnlyCollection<Entity> GroupOf(params Type[] components)
    {
        // compute an id for these components
        long group = 0;
        foreach (var component in components)
        {
            group += component.GetHashCode();
        }

        // if we have already looked up this group at this update, we simply copy the results
        if (_groupsCache.TryGetValue(group, out var entities))
        {
            return entities.AsReadOnly();
        }

        // attempt to reuse a list
        if (!_pool.TryDequeue(out entities))
        {
            entities = new List<Entity>();
        }

        // iterate over all entities and check if they have the components, then cache it
        foreach (var entity in _currentUpdateAllEntities)
        {
            if (entity.HasComponents(components))
            {
                entities.Add(entity);
            }
        }

        // try cache it
        _groupsCache.TryAdd(group, entities);
        return entities.AsReadOnly();
    }
}