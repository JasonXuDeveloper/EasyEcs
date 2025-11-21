using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EasyEcs.Core.Components;
using EasyEcs.Core.Enumerators;
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
    internal int[] EntityVersions; // Version tracking for destroyed entities
    private bool[] _activeEntityIds;
    private int _activeEntityCount;

    // Component storage
    internal Array[] Components;

    // System storage (zero-allocation priority lists)
    private readonly PrioritySystemList<ExecuteSystemWrapper> _executeSystems = new();
    private readonly PrioritySystemList<IInitSystem> _initSystems = new();
    private readonly PrioritySystemList<IEndSystem> _endSystems = new();

    // Configuration
    private readonly Options _options;

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

        lock (_structuralLock)
        {
            if (Entities.Length >= capacity)
                return;

            ResizeEntityArrays(capacity);
        }
    }

    /// <summary>
    /// Resize all entity and component arrays to the specified capacity.
    /// Must be called inside _structuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResizeEntityArrays(int newCapacity)
    {
        Array.Resize(ref Entities, newCapacity);
        Array.Resize(ref EntityVersions, newCapacity);
        Array.Resize(ref _activeEntityIds, newCapacity);

        // Resize component arrays
        if (Components != null)
        {
            for (int i = 0; i < Components.Length; i++)
            {
                var arr = Components[i];
                if (arr != null && arr.Length < newCapacity)
                {
                    var newArr = Array.CreateInstance(arr.GetType().GetElementType()!, newCapacity);
                    Array.Copy(arr, newArr, arr.Length);
                    Components[i] = newArr;
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

        throw new InvalidOperationException("Entity not found");
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

        throw new IndexOutOfRangeException("Entity index out of range");
    }

    /// <summary>
    /// Create a new entity immediately.
    /// Thread-safe and returns entity directly (no callbacks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public EntityRef CreateEntity()
    {
        lock (_structuralLock)
        {
            // ID allocation inside lock to ensure capacity check is atomic
            int id = _entityIdCounter++;

            // Ensure capacity inside lock
            if (id >= Entities.Length)
            {
                int newCapacity = Math.Max(Entities.Length * 2, id + 1);
                ResizeEntityArrays(newCapacity);
            }

            var entity = new Entity(this, id, EntityVersions[id]);
            Entities[id] = entity;
            _activeEntityIds[id] = true;
            _activeEntityCount++;

            // Add to empty archetype
            var archetype = GetOrCreateArchetype(in Tag.Empty);
            archetype.Add(id);

            return new EntityRef(id, EntityVersions[id], this);
        }
    }

    /// <summary>
    /// Destroy an entity immediately.
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void DestroyEntity(Entity entity)
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                return;

            // Increment version to invalidate all existing references
            EntityVersions[entity.Id]++;

            // Remove from archetype
            var archetype = GetOrCreateArchetype(in entity.Tag);
            archetype.Remove(entity.Id);

            // Mark inactive
            _activeEntityIds[entity.Id] = false;
            _activeEntityCount--;
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
    /// Add a system to the context immediately.
    /// </summary>
    public void AddSystem<T>() where T : SystemBase, new()
    {
        lock (_structuralLock)
        {
            var system = new T();
            AddSystemInternal(system);
        }
    }

    private void AddSystemInternal(SystemBase system)
    {
        if (system is IExecuteSystem executeSystem)
        {
            _executeSystems.Add(system.Priority, new ExecuteSystemWrapper(executeSystem));
        }

        if (system is IInitSystem initSystem)
        {
            _initSystems.Add(system.Priority, initSystem);
        }

        if (system is IEndSystem endSystem)
        {
            _endSystems.Add(system.Priority, endSystem);
        }
    }

    /// <summary>
    /// Remove a system from the context immediately.
    /// </summary>
    public void RemoveSystem<T>() where T : SystemBase
    {
        lock (_structuralLock)
        {
            var systemType = typeof(T);

            // Manual iteration to avoid closure allocation - iterate backwards to safely remove
            for (int i = 0; i < _initSystems.BucketCount; i++)
            {
                var list = _initSystems[i];
                for (int j = list.Count - 1; j >= 0; j--)
                {
                    if (list[j].GetType() == systemType)
                        list.RemoveAt(j);
                }
            }

            for (int i = 0; i < _executeSystems.BucketCount; i++)
            {
                var list = _executeSystems[i];
                for (int j = list.Count - 1; j >= 0; j--)
                {
                    if (list[j].System.GetType() == systemType)
                        list.RemoveAt(j);
                }
            }

            for (int i = 0; i < _endSystems.BucketCount; i++)
            {
                var list = _endSystems[i];
                for (int j = list.Count - 1; j >= 0; j--)
                {
                    if (list[j].GetType() == systemType)
                        list.RemoveAt(j);
                }
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

        // Only initialize Components if it hasn't been initialized yet
        // (AddComponent may have already created it before Init was called)
        if (Components == null)
            Components = Array.Empty<Array>();

        // Initialize all systems by priority (zero allocation iteration)
        for (int bucketIdx = 0; bucketIdx < _initSystems.BucketCount; bucketIdx++)
        {
            var sequence = _initSystems[bucketIdx];
            UniTask[] arr = ArrayPool<UniTask>.Shared.Rent(sequence.Count);
            Array.Fill(arr, default);

            for (int i = 0; i < sequence.Count; i++)
            {
                arr[i] = sequence[i].OnInit(this);
            }

            // Execute all systems at this priority level before moving to next priority
            await ExecuteTasks(arr, 0, sequence.Count);
            ArrayPool<UniTask>.Shared.Return(arr);
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
        if (_initSystems.BucketCount > 0)
        {
            for (int bucketIdx = 0; bucketIdx < _initSystems.BucketCount; bucketIdx++)
            {
                var sequence = _initSystems[bucketIdx];
                var arr = ArrayPool<UniTask>.Shared.Rent(sequence.Count);
                Array.Fill(arr, default);

                for (int i = 0; i < sequence.Count; i++)
                {
                    arr[i] = sequence[i].OnInit(this);
                }

                var task = ExecuteTasks(arr, 0, sequence.Count);
                if (task.Status != UniTaskStatus.Succeeded)
                {
                    await task;
                }

                ArrayPool<UniTask>.Shared.Return(arr);
            }

            _initSystems.Clear();
        }

        // Execute all systems by priority (zero allocation iteration)
        for (int bucketIdx = 0; bucketIdx < _executeSystems.BucketCount; bucketIdx++)
        {
            var sequence = _executeSystems[bucketIdx];
            var arr = ArrayPool<UniTask>.Shared.Rent(sequence.Count);
            Array.Fill(arr, default);

            for (int i = 0; i < sequence.Count; i++)
            {
                arr[i] = sequence[i].Update(this, OnError);
            }

            var task = ExecuteTasks(arr, 0, sequence.Count);
            if (task.Status != UniTaskStatus.Succeeded)
            {
                await task;
            }

            ArrayPool<UniTask>.Shared.Return(arr);
        }
    }

    /// <summary>
    /// Execute tasks either sequentially or in parallel based on options.
    /// Zero allocation after warmup (when using pre-allocated lists).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async UniTask ExecuteTasks(UniTask[] tasks, int actualStartIndex, int actualCount)
    {
        if (tasks.Length == 0)
            return;

        if (_options.Parallel && actualCount > 4)
        {
            // Parallel execution using UniTask.WhenAll
            await UniTask.WhenAll(tasks);
        }
        // If count is 1, just await directly to avoid overhead
        else if (actualCount == 1)
        {
            ref var task = ref tasks[actualStartIndex];
            if (task.Status != UniTaskStatus.Succeeded)
            {
                await task;
            }
        }
        // Else if count is 2, unroll the loop for slight performance gain
        else if (actualCount == 2)
        {
            ref var task0 = ref tasks[actualStartIndex];
            if (task0.Status != UniTaskStatus.Succeeded)
            {
                await task0;
            }
            ref var task1 = ref tasks[actualStartIndex + 1];
            if (task1.Status != UniTaskStatus.Succeeded)
            {
                await task1;
            }
        }
        // Else if count is 3, unroll the loop for slight performance gain
        else if (actualCount == 3)
        {
            ref var task0 = ref tasks[actualStartIndex];
            if (task0.Status != UniTaskStatus.Succeeded)
            {
                await task0;
            }
            ref var task1 = ref tasks[actualStartIndex + 1];
            if (task1.Status != UniTaskStatus.Succeeded)
            {
                await task1;
            }
            ref var task2 = ref tasks[actualStartIndex + 2];
            if (task2.Status != UniTaskStatus.Succeeded)
            {
                await task2;
            }
        }
        // Else if count is 4, unroll the loop for slight performance gain
        else if (actualCount == 4)
        {
            ref var task0 = ref tasks[actualStartIndex];
            if (task0.Status != UniTaskStatus.Succeeded)
            {
                await task0;
            }
            ref var task1 = ref tasks[actualStartIndex + 1];
            if (task1.Status != UniTaskStatus.Succeeded)
            {
                await task1;
            }
            ref var task2 = ref tasks[actualStartIndex + 2];
            if (task2.Status != UniTaskStatus.Succeeded)
            {
                await task2;
            }
            ref var task3 = ref tasks[actualStartIndex + 3];
            if (task3.Status != UniTaskStatus.Succeeded)
            {
                await task3;
            }
        }
        else
        {
            // Sequential execution (zero allocation)
            for (int i = actualStartIndex; i < actualStartIndex + actualCount; i++)
            {
                ref var task = ref tasks[i];
                if (task.Status != UniTaskStatus.Succeeded)
                {
                    await task;
                }
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

        // Execute all end systems (zero allocation iteration)
        for (int bucketIdx = 0; bucketIdx < _endSystems.BucketCount; bucketIdx++)
        {
            var sequence = _endSystems[bucketIdx];
            var arr = ArrayPool<UniTask>.Shared.Rent(sequence.Count);
            Array.Fill(arr, default);

            for (int i = 0; i < sequence.Count; i++)
            {
                arr[i] = sequence[i].OnEnd(this);
            }

            await ExecuteTasks(arr, 0, sequence.Count);
            ArrayPool<UniTask>.Shared.Return(arr);
        }

        // Clear all data
        Array.Clear(_activeEntityIds, 0, _activeEntityIds.Length);
        Array.Clear(Entities, 0, Entities.Length);

        // Use direct enumeration to avoid allocating Dictionary.Values collection
        foreach (var kvp in Archetypes)
        {
            Array.Clear(kvp.Value.EntityIds, 0, kvp.Value.EntityIds.Length);
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

        // Note: TagRegistry is global and shared across all contexts.
        // It is NOT cleared here to avoid breaking other Context instances.
        // Only clear TagRegistry in isolated testing scenarios via TagRegistry.Clear().

        _activeEntityCount = 0;
        _disposed = true;
    }

    /// <summary>
    /// Add a singleton component to the context immediately.
    /// </summary>
    public SingletonComponentRef<T> AddSingletonComponent<T>() where T : struct, ISingletonComponent
    {
        lock (_structuralLock)
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
            throw new InvalidOperationException("Singleton component not initialized");
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
    public ActiveEntityEnumerator AllEntities => new ActiveEntityEnumerator(_activeEntityIds, EntityVersions, this);
}