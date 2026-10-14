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

    // Recycled entity ids (LIFO). Reusing ids keeps every per-entity / per-component array bounded by the
    // peak number of live entities instead of growing forever in long-running processes.
    private int[] _freeEntityIds = new int[64];
    private int _freeEntityIdCount;

    // Per-context singleton storage indexed by SingletonIndex<T>.Value.
    private object[] _singletons = Array.Empty<object>();
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

    // Cached task buffer for Update() to avoid per-bucket ArrayPool Rent/Return
    private UniTask[] _updateTaskBuffer;

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
        EntityArchetypes = new Archetype[initialCapacity];
        EntitySlots = new int[initialCapacity];
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
        Array.Resize(ref EntityArchetypes, newCapacity);
        Array.Resize(ref EntitySlots, newCapacity);

        StorageVersion++;

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
            // Prefer a recycled id; versions were bumped on destroy so stale refs stay invalid.
            int id;
            if (_freeEntityIdCount > 0)
            {
                id = _freeEntityIds[--_freeEntityIdCount];
            }
            else
            {
                id = _entityIdCounter++;
                if (id >= Entities.Length)
                {
                    int newCapacity = Math.Max(Entities.Length * 2, id + 1);
                    ResizeEntityArrays(newCapacity);
                }
            }

            Entities[id] = new Entity(this, id, EntityVersions[id]);
            _activeEntityIds[id] = true;
            _activeEntityCount++;

            // Add to empty archetype
            _emptyArchetype ??= GetOrCreateArchetype(in Tag.Empty);
            MoveToArchetype(id, _emptyArchetype);

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
            int id = entity.Id;
            if (!_activeEntityIds[id] || EntityVersions[id] != entity.Version)
                return;

            // Increment version to invalidate all existing references
            EntityVersions[id]++;

            DetachFromArchetype(id);

            // Release component data so managed references inside components do not outlive the entity
            ref var stored = ref Entities[id];
            var components = Components;
            if (components != null)
            {
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] != null && stored.Tag.HasBit(i))
                        Array.Clear(components[i], id, 1);
                }
            }

            stored = default;

            // Mark inactive and recycle the id
            _activeEntityIds[id] = false;
            _activeEntityCount--;

            if (_freeEntityIdCount >= _freeEntityIds.Length)
                Array.Resize(ref _freeEntityIds, _freeEntityIds.Length * 2);
            _freeEntityIds[_freeEntityIdCount++] = id;
        }
    }

    private Archetype _emptyArchetype;

    /// <summary>
    /// Throw when <paramref name="entity"/> is not alive or refers to an earlier occupant of a recycled id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfStale(in Entity entity)
    {
        int id = entity.Id;
        if ((uint)id >= (uint)_activeEntityIds.Length || !_activeEntityIds[id] || EntityVersions[id] != entity.Version)
            ThrowEntityNotFound();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEntityNotFound() => throw new InvalidOperationException("Entity not found");

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

        // Execute all systems by priority (zero allocation after first resize)
        for (int bucketIdx = 0; bucketIdx < _executeSystems.BucketCount; bucketIdx++)
        {
            var sequence = _executeSystems[bucketIdx];

            // Ensure task buffer is large enough for this bucket
            if (_updateTaskBuffer == null || _updateTaskBuffer.Length < sequence.Count)
                _updateTaskBuffer = new UniTask[sequence.Count * 2];

            for (int i = 0; i < sequence.Count; i++)
            {
                _updateTaskBuffer[i] = sequence[i].Update(this, OnError);
            }

            var task = ExecuteTasks(_updateTaskBuffer, 0, sequence.Count);
            if (task.Status != UniTaskStatus.Succeeded)
            {
                await task;
            }

            // Clear used entries to avoid holding references to completed tasks
            Array.Clear(_updateTaskBuffer, 0, sequence.Count);
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

        for (int i = 0; i < _allArchetypes.Count; i++)
            Array.Clear(_allArchetypes[i].EntityIds, 0, _allArchetypes[i].EntityIds.Length);

        Archetypes.Clear();
        _allArchetypes.Clear();
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
        _singletons = Array.Empty<object>();
        Array.Clear(EntityArchetypes, 0, EntityArchetypes.Length);
        _emptyArchetype = null;
        _freeEntityIdCount = 0;

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
    /// Singletons are scoped to this context (multiple contexts in one process do not share them).
    /// </summary>
    public SingletonComponentRef<T> AddSingletonComponent<T>() where T : struct, ISingletonComponent
    {
        lock (_structuralLock)
        {
            int idx = SingletonIndex<T>.Value;
            if (idx >= _singletons.Length)
                Array.Resize(ref _singletons, Math.Max(idx + 1, _singletons.Length * 2));

            var box = new SingletonBox<T>();
            // Volatile publish so lock-free readers on other threads observe a fully constructed box.
            System.Threading.Volatile.Write(ref _singletons[idx], box);
            return new SingletonComponentRef<T>(box);
        }
    }

    /// <summary>
    /// Get a singleton component from the context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SingletonComponentRef<T> GetSingletonComponent<T>() where T : struct, ISingletonComponent
    {
        if (!TryGetSingletonComponent<T>(out var value))
            ThrowSingletonNotInitialized();
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSingletonNotInitialized() =>
        throw new InvalidOperationException("Singleton component not initialized");

    /// <summary>
    /// Try to get a singleton component from the context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSingletonComponent<T>(out SingletonComponentRef<T> value) where T : struct, ISingletonComponent
    {
        int idx = SingletonIndex<T>.Value;
        var singletons = _singletons;
        if ((uint)idx < (uint)singletons.Length && singletons[idx] is { } box)
        {
            value = new SingletonComponentRef<T>(Unsafe.As<SingletonBox<T>>(box));
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Does the context have the singleton component?
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSingletonComponent<T>() where T : struct, ISingletonComponent =>
        TryGetSingletonComponent<T>(out _);

    /// <summary>
    /// Remove a singleton component from the context.
    /// </summary>
    public void RemoveSingletonComponent<T>() where T : struct, ISingletonComponent
    {
        lock (_structuralLock)
        {
            int idx = SingletonIndex<T>.Value;
            if (idx < _singletons.Length)
                _singletons[idx] = null;
        }
    }

    /// <summary>
    /// Get all active entities.
    /// </summary>
    public ActiveEntityEnumerator AllEntities => new ActiveEntityEnumerator(_activeEntityIds, EntityVersions, this);
}