using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EasyEcs.Core.Commands;
using EasyEcs.Core.Components;
using EasyEcs.Core.Systems;

namespace EasyEcs.Core;

/// <summary>
/// A context holds many entities and many systems.
/// <br/>
/// <br/>
/// You should create a context and add systems to it. Then you should call <see cref="Update"/> in a loop, at certain intervals.
/// You can create entities and add components to them. Systems will then process these entities.
/// <br/>
/// <br/>
/// Note that adding/removing entities/components/systems are async operations, they will be processed at the end of the frame.
/// So that they will be ready for the next frame.
/// </summary>
public partial class Context : IAsyncDisposable
{
    private int _entityIdCounter = 1;
    private readonly Options _options;
    private readonly ParallelOptions _parallelOptions;

    private readonly CommandBuffer _commandBuffer = new();
    internal readonly TagRegistry TagRegistry = new();

    internal Array[] Components;
    internal readonly SortedDictionary<Tag, List<int>> Groups = new();

    internal Entity[] Entities = new Entity[1];
    private bool[] _activeEntityIds = new bool[1];
    private readonly Queue<int> _reusableIds = new();
    private int _activeEntityCount;

    private readonly SortedList<int, List<ExecuteSystemWrapper>> _executeSystems = new();
    private readonly SortedList<int, List<IInitSystem>> _initSystems = new();
    private readonly SortedList<int, List<IEndSystem>> _endSystems = new();

    private bool _started;
    private bool _disposed;

    private readonly List<Task> _executeTasks = new();

    /// <summary>
    /// Called when an error occurs.
    /// </summary>
    public event Action<Exception> OnError;

    /// <summary>
    /// Create a new context.
    /// </summary>
    /// <param name="options"></param>
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
    }

    /// <summary>
    /// Options for the context.
    /// </summary>
    public class Options
    {
        public readonly bool Parallel;
        public readonly int LevelOfParallelism;

        public Options(bool parallel = true, int levelOfParallelism = -1)
        {
            Parallel = parallel;
            LevelOfParallelism = levelOfParallelism == -1 ? Environment.ProcessorCount : levelOfParallelism;
        }
    }

    /// <summary>
    /// Get count of all entities.
    /// </summary>
    public int EntityCount => _activeEntityCount;

    /// <summary>
    /// Resize the entity array to ensure it can hold at least the specified capacity.
    /// </summary>
    /// <param name="capacity"></param>
    public void EnsureEntityCapacity(int capacity)
    {
        if (Entities.Length < capacity)
        {
            Array.Resize(ref Entities, capacity);
            Array.Resize(ref _activeEntityIds, capacity);
            if (Components == null || Components.Length == 0)
            {
                return;
            }

            for (var index = 0; index < Components.Length; index++)
            {
                var arr = Components[index];
                if (arr != null && arr.Length < capacity)
                {
                    var arr2 = Array.CreateInstance(arr.GetType()!.GetElementType()!, capacity);
                    Array.Copy(arr, arr2, arr.Length);
                    Components[index] = arr2;
                }
            }
        }
    }

    /// <summary>
    /// Get an entity by index.
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public ref Entity EntityAt(int index)
    {
        var span = _activeEntityIds.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            var isActive = span[i];
            if (isActive && index-- == 0)
            {
                return ref Entities.AsSpan()[i];
            }
        }

        throw new IndexOutOfRangeException($"Entity at index {index} not found.");
    }

    /// <summary>
    /// Try to get an entity by id.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="entityRef"></param>
    /// <returns></returns>
    public bool TryGetEntityById(int id, out EntityRef entityRef)
    {
        if (id > 0 && _activeEntityIds[id])
        {
            entityRef = new EntityRef(id, this);
            return true;
        }

        entityRef = default;
        return false;
    }

    /// <summary>
    /// Get an entity by id.
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public EntityRef GetEntityById(int id)
    {
        if (id > 0 && _activeEntityIds[id])
            return new EntityRef(id, this);

        throw new InvalidOperationException($"Entity with id {id} not found.");
    }


    /// <summary>
    /// Get all entities.
    /// </summary>
    public IEnumerable<EntityRef> AllEntities()
    {
        for (var i = 0; i < _activeEntityIds.Length; i++)
        {
            var isActive = _activeEntityIds[i];
            if (!isActive || i == 0)
                continue;
            yield return new EntityRef(i, this);
        }
    }

    /// <summary>
    /// Create a new entity.
    /// </summary>
    public void CreateEntity(Action<EntityRef> callback = null)
    {
        _commandBuffer.AddCommand(new CreateEntityCommand(entity =>
        {
            try
            {
                callback?.Invoke(entity);
            }
            catch (Exception e)
            {
                OnError?.Invoke(new AggregateException("Create Entity error", e));
            }
        }));
    }

    /// <summary>
    /// Destroy an entity.
    /// </summary>
    /// <param name="entity"></param>
    public void DestroyEntity(Entity entity)
    {
        _commandBuffer.AddCommand(new DeleteEntityCommand(entity.Id));
    }

    /// <summary>
    /// Add a system to the context.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddSystem<T>() where T : SystemBase, new()
    {
        _commandBuffer.AddCommand(new AddSystemCommand(typeof(T)));
    }

    /// <summary>
    /// Remove a system from the context.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void RemoveSystem<T>() where T : SystemBase, new()
    {
        _commandBuffer.AddCommand(new RemoveSystemCommand(typeof(T)));
    }

    /// <summary>
    /// Add a component to an entity.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="callback"></param>
    /// <typeparam name="T"></typeparam>
    public void AddComponent<T>(Entity entity, Action<ComponentRef<T>> callback = null) where T : struct, IComponent
    {
        _commandBuffer.AddCommand(new AddComponentCommand(entity.Id, typeof(T),
            () =>
            {
                var idx = TagRegistry.GetTagBitIndex<T>();
                var arr = (T[])Components[idx];
                ref var compRef = ref arr[entity.Id];
                compRef = new T();
                try
                {
                    callback?.Invoke(new ComponentRef<T>(entity.Id, idx, this));
                }
                catch (Exception e)
                {
                    OnError?.Invoke(new AggregateException($"Add Component {typeof(T).Name} error", e));
                }
            },
            () => TagRegistry.HasTag<T>(),
            () => TagRegistry.GetTagBitIndex<T>(),
            () => TagRegistry.RegisterTag<T>()
        ));
    }

    /// <summary>
    /// Remove a component from an entity.
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    public void RemoveComponent<T>(Entity entity) where T : struct, IComponent
    {
        _commandBuffer.AddCommand(new RemoveComponentCommand(entity.Id, typeof(T),
            () => TagRegistry.GetTagBitIndex<T>()));
    }

    /// <summary>
    /// Add a singleton component to the context.
    /// </summary>
    /// <param name="callback"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public void AddSingletonComponent<T>(Action<SingletonComponentRef<T>> callback = null)
        where T : struct, ISingletonComponent
    {
        _commandBuffer.AddCommand(new AddComponentCommand(0, typeof(T),
            () =>
            {
                var idx = TagRegistry.GetTagBitIndex<T>();
                var arr = (T[])Components[idx];
                var component = new T();
                arr[0] = component;
                try
                {
                    callback?.Invoke(new SingletonComponentRef<T>(idx, this));
                }
                catch (Exception e)
                {
                    OnError?.Invoke(new AggregateException($"Add SingletonComponent {typeof(T).Name} error", e));
                }
            },
            () => TagRegistry.HasTag<T>(),
            () => TagRegistry.GetTagBitIndex<T>(),
            () => TagRegistry.RegisterTag<T>()));
    }

    /// <summary>
    /// Get a singleton component from the context.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public SingletonComponentRef<T> GetSingletonComponent<T>() where T : struct, ISingletonComponent
    {
        ref var entity = ref Entities[0];
        var idx = TagRegistry.GetTagBitIndex<T>();
        if (!entity.Tag.HasBit(idx))
            throw new InvalidOperationException($"Component {typeof(T)} not found.");

        return new SingletonComponentRef<T>(idx, this);
    }

    /// <summary>
    /// Try to get a singleton component from the context.
    /// </summary>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool TryGetSingletonComponent<T>(out SingletonComponentRef<T> value) where T : struct, ISingletonComponent
    {
        value = default;
        ref var entity = ref Entities[0];
        if (!TagRegistry.TryGetTagBitIndex<T>(out var idx))
            return false;
        if (!entity.Tag.HasBit(idx))
            return false;

        value = new SingletonComponentRef<T>(idx, this);
        return true;
    }

    /// <summary>
    /// Add a system to the context.
    /// </summary>
    /// <param name="system"></param>
    private void AddSystem(SystemBase system)
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
    /// Query all tasks based on the options
    /// </summary>
    /// <param name="list"></param>
    /// <param name="action"></param>
    /// <param name="catchError"></param>
    /// <typeparam name="T"></typeparam>
    private async ValueTask QueryTasks<T>(List<T> list, Func<T, ValueTask> action, bool catchError = true)
    {
        if (_options.Parallel)
        {
            await Parallel.ForEachAsync(list, _parallelOptions, async (item, _) =>
            {
                if (!catchError)
                {
                    await action(item);
                    return;
                }

                try
                {
                    await action(item);
                }
                catch (Exception e)
                {
                    OnError?.Invoke(new AggregateException($"{item.GetType().Name} error", e));
                }
            });
        }
        else
        {
            // clear previous tasks
            _executeTasks.Clear();

            // collect tasks for the current priority
            foreach (var item in list)
            {
                _executeTasks.Add(action(item).AsTask());
            }

            // dispatch all tasks of the same priority
            if (catchError)
            {
                await Task.WhenAll(_executeTasks);
            }
            else
            {
                try
                {
                    await Task.WhenAll(_executeTasks);
                }
                catch (Exception e)
                {
                    OnError?.Invoke(e);
                }
            }

            _executeTasks.Clear();
        }
    }

    /// <summary>
    /// Initialize all systems. Use this when starting the context.
    /// </summary>
    public async ValueTask<Context> Init()
    {
        if (_started)
            throw new InvalidOperationException("Context already started.");

        _started = true;
        Components = Array.Empty<Array>();

        // dequeue all commands
        ProcessCommandBuffer();

        // initialize all systems
        foreach (var sequence in _initSystems.Values)
        {
            await QueryTasks(sequence, async system => await system.OnInit(this), false);
        }

        _initSystems.Clear();

        return this;
    }

    /// <summary>
    /// Dispose the context.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // dispose all systems
        foreach (var sequence in _endSystems.Values)
        {
            await QueryTasks(sequence, async system => await system.OnEnd(this));
        }

        // clear all entities
        _activeEntityIds.AsSpan().Clear();
        Entities.AsSpan().Clear();
        _reusableIds.Clear();
        // clear all groups
        foreach (var group in Groups.Values)
        {
            group.Clear();
        }

        Groups.Clear();
        // clear all components
        foreach (var arr in Components)
        {
            if (arr == null)
                continue;
            Array.Clear(arr, 0, arr.Length);
        }

        Components = null;
        // clear all systems
        _executeSystems.Clear();
        _initSystems.Clear();
        _endSystems.Clear();
        // clear the execute tasks
        _executeTasks.Clear();

        // clear the tag registry
        TagRegistry.Clear();

        _activeEntityCount = 0;
        _disposed = true;
    }

    /// <summary>
    /// Update all systems.
    /// </summary>
    public async ValueTask Update()
    {
        // check if the context is started
        if (!_started)
            throw new InvalidOperationException("Context not started.");

        // check if the context is disposed
        if (_disposed)
            throw new InvalidOperationException("Context disposed.");

        // are there any newly added systems to initialize?
        if (_initSystems.Count > 0)
        {
            foreach (var sequence in _initSystems.Values)
            {
                await QueryTasks(sequence, async system => await system.OnInit(this));
            }

            _initSystems.Clear();
        }

        // if empty, return
        if (_executeSystems.Count == 0)
            return;

        // group by priority
        foreach (var sequence in _executeSystems.Values)
        {
            await QueryTasks(sequence, async system => await system.Update(this));
        }

        // dequeue all commands
        ProcessCommandBuffer();
    }

    private void ProcessCommandBuffer()
    {
        while (_commandBuffer.TryGetCommand(out var command))
        {
            try
            {
                switch (command)
                {
                    case CreateEntityCommand createEntityCommand:
                    {
                        // create entity
                        var id = _reusableIds.Count > 0 ? _reusableIds.Dequeue() : _entityIdCounter++;
                        var entity = new Entity(this, id);
                        // ensure array size
                        int newLen = Math.Max(1, Entities.Length);
                        int targetLen = id + 1;
                        while (newLen < targetLen)
                        {
                            newLen *= 2;
                        }

                        if (Entities.Length < newLen)
                        {
                            Array.Resize(ref Entities, newLen);
                            Array.Resize(ref _activeEntityIds, newLen);
                        }

                        Entities[id] = entity;
                        // add to active entities
                        _activeEntityIds[id] = true;
                        if (!Groups.TryGetValue(entity.Tag, out var group))
                        {
                            group = new(Entities.Length);
                            Groups.Add(entity.Tag, group);
                        }

                        group.Add(entity.Id);
                        _activeEntityCount++;

                        createEntityCommand.Callback?.Invoke(entity);
                        break;
                    }
                    case DeleteEntityCommand deleteEntityCommand:
                    {
                        if (!_activeEntityIds[deleteEntityCommand.Id] || deleteEntityCommand.Id == 0)
                            throw new InvalidOperationException(
                                $"Entity with id {deleteEntityCommand.Id} not found.");

                        var entity = Entities[deleteEntityCommand.Id];
                        if (Groups.TryGetValue(entity.Tag, out var group))
                        {
                            group.Remove(entity.Id);
                        }

                        _activeEntityIds[deleteEntityCommand.Id] = false;
                        _activeEntityCount--;
                        _reusableIds.Enqueue(entity.Id);

                        break;
                    }
                    case AddSystemCommand addSystemCommand:
                        AddSystem(Activator.CreateInstance(addSystemCommand.SystemType) as SystemBase);
                        break;
                    case RemoveSystemCommand removeSystemCommand:
                        foreach (var list in _initSystems.Values)
                        {
                            list.RemoveAll(system => system.GetType() == removeSystemCommand.SystemType);
                        }

                        foreach (var list in _executeSystems.Values)
                        {
                            list.RemoveAll(system => system.System.GetType() == removeSystemCommand.SystemType);
                        }

                        foreach (var list in _endSystems.Values)
                        {
                            list.RemoveAll(system => system.GetType() == removeSystemCommand.SystemType);
                        }

                        break;
                    case AddComponentCommand addComponentCommand:
                    {
                        if (!_activeEntityIds[addComponentCommand.Id] && addComponentCommand.Id != 0)
                            throw new InvalidOperationException($"Entity with id {addComponentCommand.Id} not found.");
                        if (addComponentCommand.Id == 0)
                        {
                            // check if type is singleton
                            if (!addComponentCommand.ComponentType.IsAssignableTo(typeof(ISingletonComponent)))
                            {
                                throw new InvalidOperationException(
                                    $"Component {addComponentCommand.ComponentType} is not a singleton component.");
                            }
                        }

                        ref Entity entity = ref Entities[addComponentCommand.Id];
                        byte bitIdx;
                        Array arr;

                        // unregistered tag
                        if (!addComponentCommand.HasTag())
                        {
                            var type = addComponentCommand.ComponentType;
                            if (type == typeof(IComponent) || type == typeof(ISingletonComponent))
                                continue;
                            if (!type.IsValueType)
                                throw new InvalidOperationException($"Component {type.Name} must be a struct.");
                            // register
                            addComponentCommand.RegisterTag();
                            // ensure array size
                            if (Components.Length <= TagRegistry.TagCount)
                            {
                                Array.Resize(ref Components, TagRegistry.TagCount * 2);
                            }

                            // create array of components
                            arr = Array.CreateInstance(type, Entities.Length * 2);

                            // set component array to the correct index
                            bitIdx = addComponentCommand.GetTagBitIndex();
                            Components[bitIdx] = arr;
                        }
                        else
                        {
                            bitIdx = addComponentCommand.GetTagBitIndex();
                            arr = Components[bitIdx];
                            if (arr.Length < Entities.Length)
                            {
                                var arr2 = Array.CreateInstance(addComponentCommand.ComponentType, Entities.Length * 2);
                                Array.Copy(arr, arr2, arr.Length);
                                Components[bitIdx] = arr2;
                            }
                        }

                        // set tag
                        var oldTag = entity.Tag;
                        entity.Tag.SetBit(bitIdx);
                        // move group
                        if (Groups.TryGetValue(oldTag, out var oldGroup))
                        {
                            oldGroup.Remove(entity.Id);
                        }

                        if (!Groups.TryGetValue(entity.Tag, out var group))
                        {
                            group = new(Entities.Length);
                            Groups.Add(entity.Tag, group);
                        }

                        group.Add(entity.Id);

                        // callback
                        addComponentCommand.OnComponentAdded?.Invoke();
                        break;
                    }
                    case RemoveComponentCommand removeComponentCommand:
                    {
                        if (!_activeEntityIds[removeComponentCommand.Id] && removeComponentCommand.Id != 0)
                            throw new InvalidOperationException(
                                $"Entity with id {removeComponentCommand.Id} not found.");
                        if (removeComponentCommand.Id == 0)
                        {
                            // check if type is singleton
                            if (!removeComponentCommand.ComponentType.IsAssignableTo(typeof(ISingletonComponent)))
                            {
                                throw new InvalidOperationException(
                                    $"Component {removeComponentCommand.ComponentType} is not a singleton component.");
                            }
                        }

                        ref Entity entity = ref Entities[removeComponentCommand.Id];
                        int bitIdx = removeComponentCommand.GetTagBitIndex();

                        // remove from array
                        var arr = Components[bitIdx];
                        arr.SetValue(null, entity.Id);

                        // set tag
                        Tag oldCompTag = entity.Tag;
                        entity.Tag.ClearBit(bitIdx);
                        // remove from group 
                        if (Groups.TryGetValue(oldCompTag, out var oldGroup))
                        {
                            oldGroup.Remove(entity.Id);
                        }

                        // if the entity still has a tag, add it to the group
                        if (!Groups.TryGetValue(entity.Tag, out var group))
                        {
                            group = new(Entities.Length);
                            Groups.Add(entity.Tag, group);
                        }

                        group.Add(entity.Id);

                        break;
                    }
                }
            }
            catch (Exception e)
            {
                OnError?.Invoke(new AggregateException($"{command.GetType().Name} error", e));
            }
        }
    }
}