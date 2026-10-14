using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace EasyEcs;

// System scheduling. Systems run synchronously, in ascending Priority; systems sharing a priority form a bucket
// (run in parallel only with WorldOptions.Parallel). Each update calls OnUpdate() then the generated [ForEach] methods.
public sealed partial class World
{
    // Group 0 is the default group (World.Update()); others are [UpdateInGroup<T>] groups (World.Update<T>()).
    private SystemSchedule[] _groups = [new()];
    private ParallelOptions _parallelOptions;

    /// <summary>
    /// Raised when a system throws during an update. With no subscriber the exception propagates.
    /// </summary>
    public event Action<EcsSystem, Exception> SystemError;

    /// <summary>Number of completed <see cref="Update()"/> calls (default group).</summary>
    public long UpdateCount => _groups[0].Updates;

    /// <summary>Systems of every group, each group in execution order (default group first).</summary>
    public IReadOnlyList<EcsSystem> Systems
    {
        get
        {
            var all = new List<EcsSystem>();
            foreach (var group in _groups)
                if (group != null) all.AddRange(group.Systems);
            return all;
        }
    }

    /// <summary>Create a system, attach it to this world and call its <see cref="EcsSystem.OnCreate"/>.</summary>
    public T AddSystem<T>() where T : EcsSystem, new()
    {
        var system = new T();
        AddSystem(system);
        return system;
    }

    /// <summary>
    /// Attach <paramref name="system"/> to its group (<see cref="UpdateInGroupAttribute{TGroup}"/>, default otherwise)
    /// and call its <see cref="EcsSystem.OnCreate"/>.
    /// </summary>
    public void AddSystem(EcsSystem system)
    {
        if (system.World != null)
            throw new InvalidOperationException($"{system.GetType().Name} already belongs to a world");

        system.World = this;
        system.Group = GroupOf(system.GetType());
        lock (StructuralLock)
        {
            if (system.Group >= _groups.Length) Array.Resize(ref _groups, system.Group + 1);
            (_groups[system.Group] ??= new SystemSchedule()).Insert(system);
        }

        system.Create();
    }

    /// <summary>Remove the system of type <typeparamref name="T"/> (calls its <see cref="EcsSystem.OnDestroy"/>).</summary>
    public bool RemoveSystem<T>() where T : EcsSystem
    {
        EcsSystem removed;
        lock (StructuralLock)
        {
            removed = GetSystem<T>();
            if (removed == null) return false;
            _groups[removed.Group].Remove(removed);
        }

        removed.Destroy();
        removed.World = null;
        return true;
    }

    /// <summary>Get the system of type <typeparamref name="T"/>, or null.</summary>
    public T GetSystem<T>() where T : EcsSystem
    {
        foreach (var group in _groups)
        {
            if (group == null) continue;
            var systems = group.Systems;
            for (int i = 0; i < systems.Length; i++)
                if (systems[i] is T match)
                    return match;
        }

        return null;
    }

    /// <summary>Run one update of every enabled system of the default group (synchronous). Allocation-free.</summary>
    public void Update() => Run(_groups[0]);

    /// <summary>
    /// Run one update of every enabled system in group <typeparamref name="TGroup"/> (synchronous, allocation-free).
    /// A group with no systems is a no-op.
    /// </summary>
    public void Update<TGroup>() where TGroup : struct, ISystemGroup
    {
        var groups = _groups;
        int index = SystemGroup<TGroup>.Index;
        if (index < groups.Length && groups[index] is { } group) Run(group);
    }

    private void Run(SystemSchedule group)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));

        long update = ++group.Updates;
        var systems = group.Systems;
        var buckets = group.Buckets;
        for (int b = 0; b < buckets.Length; b++)
        {
            var (start, count) = buckets[b];
            if (count > 1 && _options.Parallel)
            {
                RunParallel(systems, start, count, update);
            }
            else
            {
                for (int i = start; i < start + count; i++)
                    RunSystem(systems[i], update);
            }
        }
    }

    // Separate method: the lambda's closure would otherwise be allocated on every update, parallel or not.
    private void RunParallel(EcsSystem[] systems, int start, int count, long update)
    {
        _parallelOptions ??= new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism };
        Parallel.For(start, start + count, _parallelOptions, i => RunSystem(systems[i], update));
    }

    private void RunSystem(EcsSystem system, long update)
    {
        if (!system.Enabled) return;
        int interval = system.UpdateInterval;
        if (interval > 1 && update % interval != 0) return;

        try
        {
            system.Update();
        }
        catch (Exception e) when (SystemError != null)
        {
            SystemError(system, e);
        }
    }

    /// <summary>Group index of a system type: its (inherited) [UpdateInGroup] attribute, or the default group.</summary>
    private static int GroupOf(Type type)
    {
        // Cold path (once per AddSystem); the attribute instance knows its group index, so no MakeGenericType.
        foreach (var attribute in Attribute.GetCustomAttributes(type, typeof(Attribute), inherit: true))
            if (attribute is ISystemGroupAttribute group)
                return group.Index;
        return 0;
    }

    /// <summary>Destroy all systems (OnDestroy in reverse order), then release all storage.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var groups = _groups;
        for (int g = groups.Length - 1; g >= 0; g--)
        {
            var systems = groups[g]?.Systems ?? [];
            for (int i = systems.Length - 1; i >= 0; i--)
            {
                try
                {
                    systems[i].Destroy();
                }
                catch (Exception e) when (SystemError != null)
                {
                    SystemError(systems[i], e);
                }

                systems[i].World = null;
            }
        }

        _groups = [new()];

        // Meta keeps its length (handles index it unchecked): cleared entries make every handle stale.
        Array.Clear(Meta);
        ClearArchetypes();
        Components = [];
        _columnHasReferences = [];
        _channels = [];
        ClearSingletons();
        _freeIdCount = 0;
        _entityCount = 0;
    }

    /// <summary>
    /// Systems of one group sorted by priority (stable), with their equal-priority buckets. Mutated under
    /// <see cref="StructuralLock"/> by publishing new arrays (copy-on-write), so a running update never sees a torn list.
    /// </summary>
    private sealed class SystemSchedule
    {
        public EcsSystem[] Systems = [];
        public (int Start, int Count)[] Buckets = [];
        public long Updates;

        public void Insert(EcsSystem system)
        {
            // Stable insertion by priority: systems with equal priority keep their registration order.
            var systems = Systems;
            int index = systems.Length;
            while (index > 0 && systems[index - 1].Priority > system.Priority) index--;
            var next = new EcsSystem[systems.Length + 1];
            Array.Copy(systems, 0, next, 0, index);
            next[index] = system;
            Array.Copy(systems, index, next, index + 1, systems.Length - index);
            Publish(next);
        }

        public void Remove(EcsSystem system)
        {
            int index = Array.IndexOf(Systems, system);
            var next = new EcsSystem[Systems.Length - 1];
            Array.Copy(Systems, 0, next, 0, index);
            Array.Copy(Systems, index + 1, next, index, Systems.Length - index - 1);
            Publish(next);
        }

        private void Publish(EcsSystem[] systems)
        {
            var buckets = new List<(int, int)>();
            for (int i = 0; i < systems.Length;)
            {
                int start = i;
                int priority = systems[i].Priority;
                while (i < systems.Length && systems[i].Priority == priority) i++;
                buckets.Add((start, i - start));
            }

            // Buckets first: an update reads Systems then Buckets, and both are consistent with the new list's prefix.
            Buckets = buckets.ToArray();
            Systems = systems;
        }
    }
}
