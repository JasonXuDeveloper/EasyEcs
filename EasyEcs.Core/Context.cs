using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EasyEcs.Core.Components;
using EasyEcs.Core.Systems;

namespace EasyEcs.Core;

/// <summary>
/// High-performance ECS context with immediate execution and zero-allocation design.
/// Optimized for server applications with multi-core CPUs.
/// </summary>
public partial class Context : IAsyncDisposable
{
    // Entity storage
    private int _entityIdCounter;
    internal Entity[] Entities;
    internal int[] EntityVersions;  // Version tracking for destroyed entities
    private bool[] _activeEntityIds;
    private readonly Queue<int> _reusableIds = new();
    private int _activeEntityCount;

    // Component storage
    internal Array[] Components;
    internal readonly TagRegistry TagRegistry = new();

    // System storage
    private readonly SortedList<int, List<ExecuteSystemWrapper>> _executeSystems = new();
    private readonly SortedList<int, List<IInitSystem>> _initSystems = new();
    private readonly SortedList<int, List<IEndSystem>> _endSystems = new();

    // System execution (pre-allocated arrays for zero allocation)
    private UniTask[] _initTasks = new UniTask[32];
    private UniTask[] _updateTasks = new UniTask[32];
    private UniTask[] _endTasks = new UniTask[32];
    private int _initTaskCount;
    private int _updateTaskCount;
    private int _endTaskCount;

    // Configuration
    private readonly Options _options;
    private readonly ParallelOptions _parallelOptions;

    // State
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Called when an error occurs in a system.
    /// </summary>
    public event Action<Exception> OnError;

    public Context(Options options = null)
    {
        _options = options ?? new Options();

        if (_options.Parallel)
        {
            _parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.LevelOfParallelism
            };
        }

        // Pre-allocate entity storage
        int initialCapacity = _options.InitialEntityCapacity;
        Entities = new Entity[initialCapacity];
        EntityVersions = new int[initialCapacity];
        _activeEntityIds = new bool[initialCapacity];
    }

    public class Options
    {
        public readonly bool Parallel;
        public readonly int LevelOfParallelism;
        public readonly int InitialEntityCapacity;

        public Options(bool parallel = true, int levelOfParallelism = -1, int initialEntityCapacity = 1024)
        {
            Parallel = parallel;
            LevelOfParallelism = levelOfParallelism == -1 ? Environment.ProcessorCount : levelOfParallelism;
            InitialEntityCapacity = initialEntityCapacity;
        }
    }

    /// <summary>
    /// Get count of active entities.
    /// </summary>
    public int EntityCount => _activeEntityCount;

    /// <summary>
    /// Ensure entity arrays can hold at least the specified capacity.
    /// </summary>
    public void EnsureEntityCapacity(int capacity)
    {
        if (Entities.Length >= capacity)
            return;

        using (_structuralLock.EnterScope())
        {
            if (Entities.Length >= capacity)
                return;

            Array.Resize(ref Entities, capacity);
            Array.Resize(ref EntityVersions, capacity);
            Array.Resize(ref _activeEntityIds, capacity);

            // Resize component arrays
            if (Components != null)
            {
                for (int i = 0; i < Components.Length; i++)
                {
                    var arr = Components[i];
                    if (arr != null && arr.Length < capacity)
                    {
                        var newArr = Array.CreateInstance(arr.GetType().GetElementType()!, capacity);
                        Array.Copy(arr, newArr, arr.Length);
                        Components[i] = newArr;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Try to get an entity by ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetEntityById(int id, out EntityRef entityRef)
    {
        if (id >= 0 && id < _activeEntityIds.Length && _activeEntityIds[id])
        {
            entityRef = new EntityRef(id, EntityVersions[id], this);
            return true;
        }

        entityRef = default;
        return false;
    }

    /// <summary>
    /// Get an entity by ID (throws if not found).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRef GetEntityById(int id)
    {
        if (id >= 0 && id < _activeEntityIds.Length && _activeEntityIds[id])
            return new EntityRef(id, EntityVersions[id], this);

        throw new InvalidOperationException($"Entity with id {id} not found.");
    }

    /// <summary>
    /// Get the Nth active entity (0-indexed).
    /// </summary>
    public EntityRef EntityAt(int index)
    {
        var span = _activeEntityIds.AsSpan();
        int currentIndex = 0;

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i])
            {
                if (currentIndex == index)
                    return new EntityRef(i, EntityVersions[i], this);
                currentIndex++;
            }
        }

        throw new IndexOutOfRangeException($"Entity at index {index} not found.");
    }

    /// <summary>
    /// Create a new entity immediately.
    /// Thread-safe and returns entity directly (no callbacks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public EntityRef CreateEntity()
    {
        // Lock-free ID allocation
        int id = Interlocked.Increment(ref _entityIdCounter) - 1;

        // Ensure capacity
        if (id >= Entities.Length)
        {
            int newCapacity = Math.Max(Entities.Length * 2, id + 1);
            EnsureEntityCapacity(newCapacity);
        }

        var entity = new Entity(this, id, EntityVersions[id]);
        Entities[id] = entity;

        using (_structuralLock.EnterScope())
        {
            _activeEntityIds[id] = true;
            _activeEntityCount++;

            // Add to empty archetype
            var archetype = GetOrCreateArchetype(new Tag());
            archetype.Add(id);
        }

        return new EntityRef(id, EntityVersions[id], this);
    }

    /// <summary>
    /// Destroy an entity immediately.
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void DestroyEntity(Entity entity)
    {
        using (_structuralLock.EnterScope())
        {
            if (!_activeEntityIds[entity.Id])
                return;

            // Increment version to invalidate all existing references
            EntityVersions[entity.Id]++;

            // Remove from archetype
            var archetype = GetOrCreateArchetype(entity.Tag);
            archetype.Remove(entity.Id);

            // Mark inactive
            _activeEntityIds[entity.Id] = false;
            _activeEntityCount--;

            // Reuse ID
            _reusableIds.Enqueue(entity.Id);
        }
    }

    /// <summary>
    /// Destroy an entity immediately (EntityRef overload).
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DestroyEntity(EntityRef entityRef)
    {
        DestroyEntity(entityRef.Value);
    }

    /// <summary>
    /// Add a component to an entity immediately (returns ComponentRef directly).
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ComponentRef<T> AddComponent<T>(Entity entity) where T : struct, IComponent
    {
        using (_structuralLock.EnterScope())
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException($"Entity {entity.Id} not found.");

            // Get or register component type
            byte componentIdx = TagRegistry.GetOrRegisterTag<T>();

            // Ensure component array exists
            if (Components == null || componentIdx >= Components.Length)
            {
                int newSize = Math.Max((Components?.Length ?? 0) * 2, componentIdx + 1);
                Array.Resize(ref Components, newSize);
            }

            if (Components[componentIdx] == null)
            {
                Components[componentIdx] = new T[Entities.Length];
            }
            else if (Components[componentIdx].Length < Entities.Length)
            {
                Array.Resize(ref (T[])Components[componentIdx], Entities.Length);
            }

            var componentArray = (T[])Components[componentIdx];

            // Update entity archetype
            ref var entityRef = ref Entities[entity.Id];
            var oldTag = entityRef.Tag;
            entityRef.Tag.SetBit(componentIdx);

            // Move to new archetype
            var oldArchetype = GetOrCreateArchetype(oldTag);
            oldArchetype.Remove(entity.Id);

            var newArchetype = GetOrCreateArchetype(entityRef.Tag);
            newArchetype.Add(entity.Id);

            // Initialize component
            componentArray[entity.Id] = new T();

            return new ComponentRef<T>(entity.Id, EntityVersions[entity.Id], componentIdx, this);
        }
    }

    /// <summary>
    /// Remove a component from an entity immediately.
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponent<T>(Entity entity) where T : struct, IComponent
    {
        using (_structuralLock.EnterScope())
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException($"Entity {entity.Id} not found.");

            if (!TagRegistry.TryGetTagBitIndex<T>(out var componentIdx))
                return;

            // Update entity archetype
            ref var entityRef = ref Entities[entity.Id];
            var oldTag = entityRef.Tag;
            entityRef.Tag.ClearBit(componentIdx);

            // Move to new archetype
            var oldArchetype = GetOrCreateArchetype(oldTag);
            oldArchetype.Remove(entity.Id);

            var newArchetype = GetOrCreateArchetype(entityRef.Tag);
            newArchetype.Add(entity.Id);

            // Clear component data
            if (Components[componentIdx] is T[] componentArray)
            {
                componentArray[entity.Id] = default;
            }
        }
    }

    /// <summary>
    /// Add a system to the context immediately.
    /// </summary>
    public void AddSystem<T>() where T : SystemBase, new()
    {
        using (_structuralLock.EnterScope())
        {
            var system = new T();
            AddSystemInternal(system);
        }
    }

    private void AddSystemInternal(SystemBase system)
    {
        if (system is IExecuteSystem executeSystem)
        {
            if (!_executeSystems.TryGetValue(system.Priority, out var list))
            {
                list = new List<ExecuteSystemWrapper>();
                _executeSystems.Add(system.Priority, list);
            }
            list.Add(new ExecuteSystemWrapper(executeSystem));
        }

        if (system is IInitSystem initSystem)
        {
            if (!_initSystems.TryGetValue(system.Priority, out var list))
            {
                list = new List<IInitSystem>();
                _initSystems.Add(system.Priority, list);
            }
            list.Add(initSystem);
        }

        if (system is IEndSystem endSystem)
        {
            if (!_endSystems.TryGetValue(system.Priority, out var list))
            {
                list = new List<IEndSystem>();
                _endSystems.Add(system.Priority, list);
            }
            list.Add(endSystem);
        }
    }

    /// <summary>
    /// Remove a system from the context immediately.
    /// </summary>
    public void RemoveSystem<T>() where T : SystemBase
    {
        using (_structuralLock.EnterScope())
        {
            var systemType = typeof(T);

            foreach (var list in _initSystems.Values)
            {
                list.RemoveAll(s => s.GetType() == systemType);
            }

            foreach (var list in _executeSystems.Values)
            {
                list.RemoveAll(s => s.System.GetType() == systemType);
            }

            foreach (var list in _endSystems.Values)
            {
                list.RemoveAll(s => s.GetType() == systemType);
            }
        }
    }

    /// <summary>
    /// Initialize all systems.
    /// </summary>
    public async UniTask<Context> Init()
    {
        if (_started)
            throw new InvalidOperationException("Context already started.");

        _started = true;
        Components = Array.Empty<Array>();

        // Initialize all systems by priority
        foreach (var sequence in _initSystems.Values)
        {
            _initTaskCount = 0;
            foreach (var system in sequence)
            {
                if (_initTaskCount >= _initTasks.Length)
                    Array.Resize(ref _initTasks, _initTasks.Length * 2);

                _initTasks[_initTaskCount++] = system.OnInit(this);
            }

            await ExecuteTasks(_initTasks, _initTaskCount);
        }

        _initSystems.Clear();
        return this;
    }

    /// <summary>
    /// Update all systems.
    /// </summary>
    public async UniTask Update()
    {
        if (!_started)
            throw new InvalidOperationException("Context not started. Call Init() first.");

        if (_disposed)
            throw new InvalidOperationException("Context disposed.");

        // Initialize newly added systems
        if (_initSystems.Count > 0)
        {
            foreach (var sequence in _initSystems.Values)
            {
                _initTaskCount = 0;
                foreach (var system in sequence)
                {
                    if (_initTaskCount >= _initTasks.Length)
                        Array.Resize(ref _initTasks, _initTasks.Length * 2);

                    _initTasks[_initTaskCount++] = system.OnInit(this);
                }

                await ExecuteTasks(_initTasks, _initTaskCount);
            }

            _initSystems.Clear();
        }

        // Execute all systems by priority
        foreach (var sequence in _executeSystems.Values)
        {
            _updateTaskCount = 0;
            foreach (var wrapper in sequence)
            {
                if (_updateTaskCount >= _updateTasks.Length)
                    Array.Resize(ref _updateTasks, _updateTasks.Length * 2);

                _updateTasks[_updateTaskCount++] = wrapper.Update(this, OnError);
            }

            await ExecuteTasks(_updateTasks, _updateTaskCount);
        }
    }

    /// <summary>
    /// Execute tasks either sequentially or in parallel based on options.
    /// Zero allocation after warmup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async UniTask ExecuteTasks(UniTask[] tasks, int count)
    {
        if (count == 0)
            return;

        if (_options.Parallel)
        {
            // Parallel execution using Parallel.ForEachAsync
            await Parallel.ForEachAsync(
                tasks.AsMemory(0, count),
                _parallelOptions,
                async (task, ct) => await task
            ).AsUniTask(false);
        }
        else
        {
            // Sequential execution (zero allocation)
            for (int i = 0; i < count; i++)
            {
                await tasks[i];
            }
        }
    }

    /// <summary>
    /// Dispose the context and clean up all resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // Execute all end systems
        foreach (var sequence in _endSystems.Values)
        {
            _endTaskCount = 0;
            foreach (var system in sequence)
            {
                if (_endTaskCount >= _endTasks.Length)
                    Array.Resize(ref _endTasks, _endTasks.Length * 2);

                _endTasks[_endTaskCount++] = system.Execute(this, OnError);
            }

            await ExecuteTasks(_endTasks, _endTaskCount);
        }

        // Clear all data
        Array.Clear(_activeEntityIds, 0, _activeEntityIds.Length);
        Array.Clear(Entities, 0, Entities.Length);
        _reusableIds.Clear();

        foreach (var archetype in Archetypes.Values)
        {
            Array.Clear(archetype.EntityIds, 0, archetype.EntityIds.Length);
        }

        Archetypes.Clear();
        _queryCache.Clear();

        if (Components != null)
        {
            foreach (var arr in Components)
            {
                if (arr != null)
                    Array.Clear(arr, 0, arr.Length);
            }
        }

        Components = null;

        _executeSystems.Clear();
        _initSystems.Clear();
        _endSystems.Clear();

        TagRegistry.Clear();

        _activeEntityCount = 0;
        _disposed = true;
    }

    /// <summary>
    /// Add a singleton component to the context immediately.
    /// </summary>
    public SingletonComponentRef<T> AddSingletonComponent<T>() where T : struct, ISingletonComponent
    {
        using (_structuralLock.EnterScope())
        {
            Singleton<T>.Instance.Value = new T();
            Singleton<T>.Instance.Initialized = true;
            return new SingletonComponentRef<T>();
        }
    }

    /// <summary>
    /// Get a singleton component from the context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SingletonComponentRef<T> GetSingletonComponent<T>() where T : struct, ISingletonComponent
    {
        if (!Singleton<T>.Instance.Initialized)
            throw new InvalidOperationException($"Singleton component {typeof(T)} not initialized.");
        return new SingletonComponentRef<T>();
    }

    /// <summary>
    /// Try to get a singleton component from the context.
    /// </summary>
    public bool TryGetSingletonComponent<T>(out SingletonComponentRef<T> value) where T : struct, ISingletonComponent
    {
        if (Singleton<T>.Instance.Initialized)
        {
            value = new SingletonComponentRef<T>();
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Get all active entities.
    /// </summary>
    public ActiveEntityEnumerator AllEntities => new ActiveEntityEnumerator(_activeEntityIds, this);
}
