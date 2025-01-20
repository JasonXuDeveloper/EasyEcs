using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace EasyEcs.Core;

/// <summary>
/// A context holds many entities and many systems.
/// <br/>
/// You should create a context and add systems to it. Then you should call <see cref="Update"/> in a loop, at certain intervals.
/// You can create entities and add components to them. Systems will then process these entities.
/// <br/>
/// You should not have multiple contexts in the same thread.
/// </summary>
[SuppressMessage("ReSharper", "SuspiciousTypeConversion.Global")]
public partial class Context : IAsyncDisposable
{
    private readonly Random _random = new();
    private readonly List<Entity> _entities = new();
    private readonly ReaderWriterLockSlim _entitiesLock = new();
    private readonly ConcurrentQueue<Entity> _removeList = new();
    private readonly List<Task> _executeTasks = new();
    private readonly SortedList<int, ExecuteSystemWrapper> _executeSystems = new();
    private readonly SortedList<int, IInitSystem> _initSystems = new();
    private readonly SortedList<int, IEndSystem> _endSystems = new();
    private bool _started;
    private bool _disposed;

    private readonly ConcurrentDictionary<long, List<Entity>> _groupsCache = new();
    private static readonly ConcurrentQueue<List<Entity>> Pool = new();

    /// <summary>
    /// Called when an error occurs.
    /// </summary>
    public event Action<Exception> OnError;

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
            _executeSystems.Add(system.Priority, new ExecuteSystemWrapper(executeSystem));
        if (system is IInitSystem initSystem)
            _initSystems.Add(system.Priority, initSystem);
        if (system is IEndSystem endSystem)
            _endSystems.Add(system.Priority, endSystem);

        return this;
    }

    /// <summary>
    /// Create a new entity.
    /// </summary>
    /// <returns></returns>
    public Entity CreateEntity()
    {
        var entity = new Entity(this, _random.Next());
        _entitiesLock.EnterWriteLock();
        try
        {
            _entities.Add(entity);
        }
        finally
        {
            _entitiesLock.ExitWriteLock();
        }

        return entity;
    }

    /// <summary>
    /// Destroy an entity.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="immediate"></param>
    public void DestroyEntity(Entity entity, bool immediate = false)
    {
        if (immediate)
        {
            _entitiesLock.EnterWriteLock();
            try
            {
                _entities.Remove(entity);
            }
            finally
            {
                _entitiesLock.ExitWriteLock();
            }

            InvalidateGroupCache();
            return;
        }

        _removeList.Enqueue(entity);
    }

    /// <summary>
    /// Get all entities.
    /// <br/>
    /// Note: Newly created entities from the current update will not be included.
    /// </summary>
    public PooledCollection<List<Entity>, Entity> AllEntities
    {
        get
        {
            var ret = PooledCollection<List<Entity>, Entity>.Create();
            var lst = ret.Collection;
            lst.Clear();
            _entitiesLock.EnterReadLock();
            try
            {
                lst.AddRange(_entities);
            }
            finally
            {
                _entitiesLock.ExitReadLock();
            }

            return ret;
        }
    }

    /// <summary>
    /// Initialize all systems. Use this when starting the context.
    /// </summary>
    public async ValueTask<Context> Init()
    {
        if (_started)
            throw new InvalidOperationException("Context already started.");

        // clear the cache of the groups
        InvalidateGroupCache();

        // initialize all systems
        foreach (var system in _initSystems.Values)
        {
            await system.OnInit(this);
        }

        _started = true;

        return this;
    }

    /// <summary>
    /// Dispose the context.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // clear the cache of the groups
        InvalidateGroupCache();

        // dispose all systems
        foreach (var system in _endSystems.Values)
        {
            await system.OnEnd(this);
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
        if (!_started)
            throw new InvalidOperationException("Context not started.");

        // check if the context is disposed
        if (_disposed)
            throw new InvalidOperationException("Context disposed.");

        // clear the cache of the groups
        InvalidateGroupCache();
        // execute the systems
        _executeTasks.Clear();
        // sort by priority
        foreach (var system in _executeSystems.Values)
        {
            _executeTasks.Add(parallel ? Task.Run(() => system.Update(this)) : system.Update(this));
        }

        try
        {
            await Task.WhenAll(_executeTasks);
        }
        catch (Exception e)
        {
            OnError?.Invoke(e);
        }

        while (_removeList.TryDequeue(out var entity))
        {
            _entitiesLock.EnterWriteLock();
            try
            {
                _entities.Remove(entity);
            }
            finally
            {
                _entitiesLock.ExitWriteLock();
            }
        }
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf(typeof(Component1), typeof(Component2));
    /// </code>
    /// </summary>
    /// <param name="components"></param>
    /// <returns></returns>
    public PooledCollection<List<Entity>, Entity> GroupOf(params Type[] components)
    {
        // compute an id for these components
        long group = 0;
        foreach (var component in components)
        {
            group += component.GetHashCode();
        }

        // request a pooled enumerable
        var ret = PooledCollection<List<Entity>, Entity>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // if we have already looked up this group at this update, we simply copy the results
        if (_groupsCache.TryGetValue(group, out var entities))
        {
            lst.AddRange(entities);
            return ret;
        }

        // attempt to reuse a list
        if (!Pool.TryDequeue(out entities))
        {
            entities = new List<Entity>();
        }

        // iterate over all entities and check if they have the components, then cache it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.HasComponents(components))
                {
                    entities.Add(entity);
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        // try cache it
        _groupsCache.TryAdd(group, entities);
        lst.AddRange(entities);
        return ret;
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
            Pool.Enqueue(group);
        }

        _groupsCache.Clear();
    }
}