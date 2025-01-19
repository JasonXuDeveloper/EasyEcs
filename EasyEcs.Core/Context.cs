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
[SuppressMessage("ReSharper", "SuspiciousTypeConversion.Global")]
public class Context: IDisposable
{
    private readonly Random _random = new();
    private readonly List<Entity> _entities = new();
    private readonly List<Entity> _removeList = new();
    private readonly SortedList<int, IExecuteSystem> _executeSystems = new();
    private readonly SortedList<int, IInitSystem> _initSystems = new();
    private readonly SortedList<int, IEndSystem> _endSystems = new();
    private bool _started;
    private bool _disposed;

    private ReadOnlyCollection<Entity> _currentUpdateAllEntities;
    private readonly ConcurrentDictionary<long, List<Entity>> _groupsCache = new();
    private readonly ConcurrentQueue<List<Entity>> _pool = new();

    [ThreadStatic] private static List<Entity> _group;
    private static List<Entity> Group => _group ??= new();

    /// <summary>
    /// The shared context.
    /// </summary>
    public static Context Shared { get; } = new();

    /// <summary>
    /// Add a system to the context.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public Context AddSystem<T>() where T : SystemBase, new()
    {
        var system = new T();
        if (system is IExecuteSystem executeSystem)
            _executeSystems.Add(system.Priority, executeSystem);
        if (system is IInitSystem initSystem)
            _initSystems.Add(system.Priority, initSystem);
        if (system is IEndSystem endSystem)
            _endSystems.Add(system.Priority, endSystem);
        
        return this;
    }

    /// <summary>
    /// Initialize all systems. Use this when starting the context.
    /// </summary>
    public async ValueTask<Context> Init()
    {
        if(_started)
            throw new InvalidOperationException("Context already started.");

        // initialize all systems
        foreach (var system in _initSystems.Values)
        {
            await system.OnInit(this);
        }
        
        _started = true;
        
        return this;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        
        // dispose all systems
        foreach (var system in _endSystems.Values)
        {
            system.OnEnd(this).AsTask().Wait();
        }
        
        // clear all entities
        _entities.Clear();
        // clear all systems
        _executeSystems.Clear();
        _initSystems.Clear();
        _endSystems.Clear();
        // clear the cache of the groups
        InvalidateGroupCache();
        
        _disposed = true;
    }

    /// <summary>
    /// Update all systems.
    /// </summary>
    /// <param name="parallel"></param>
    public async ValueTask Update(bool parallel = true)
    {
        // check if the context is started
        if(!_started)
            throw new InvalidOperationException("Context not started.");
        
        // check if the context is disposed
        if(_disposed)
            throw new InvalidOperationException("Context disposed.");
        
        // all entities before executing all systems at this update
        _currentUpdateAllEntities = _entities.AsReadOnly();
        // clear the cache of the groups
        InvalidateGroupCache();
        // execute the systems
        if (!parallel)
        {
            foreach (var system in _executeSystems.Values)
            {
                if (((SystemBase)system).ShouldExecute())
                {
                    await system.OnExecute(this);
                }
            }
        }
        else
        {
            await Parallel.ForEachAsync(_executeSystems.Values, async (system, _) =>
            {
                if (((SystemBase)system).ShouldExecute())
                {
                    await system.OnExecute(this);
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
    /// Get all entities that have the specified components. Only use it in OnExecute
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

        Group.Clear();
        // if we have already looked up this group at this update, we simply copy the results
        if (_groupsCache.TryGetValue(group, out var entities))
        {
            Group.AddRange(entities);
            return Group.AsReadOnly();
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
        Group.AddRange(entities);
        return Group.AsReadOnly();
    }

    /// <summary>
    /// Invalidate the group cache. Happens when a component is added or removed from an entity. Or at a new update.
    /// </summary>
    internal void InvalidateGroupCache()
    {
        if (_groupsCache.Count == 0)
            return;

        // clear all groups
        foreach (var group in _groupsCache.Values)
        {
            // enqueue the hashset to the pool, so we can reuse it later
            group.Clear();
            _pool.Enqueue(group);
        }

        _groupsCache.Clear();
    }
}