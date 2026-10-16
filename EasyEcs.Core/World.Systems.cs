using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
namespace EasyEcs;

// System scheduling. Systems run synchronously, in ascending Priority (registration order breaks ties). Systems of one
// priority are split into batches of consecutive systems whose SystemAccess does not conflict; with
// WorldOptions.Parallel a batch runs at the same time (World.Parallel.cs). Each update calls OnUpdate() then the
// generated [ForEach] methods.
public sealed partial class World
{
    // Group 0 is the default group (World.Update()); others are [UpdateInGroup<T>] groups (World.Update<T>()).
    private SystemSchedule[] _groups = [new()];

    /// <summary>
    /// Raised when a system throws during an update. With no subscriber the exception propagates.
    /// </summary>
    public event Action<EcsSystem, Exception> SystemError;

    /// <summary>Number of completed <see cref="Update()"/> calls (default group).</summary>
    public long UpdateCount => _groups[0].Updates;

    /// <summary>Systems of every group, each group in execution order (default group first). Allocation-free.</summary>
    public SystemList Systems => new(_groups);

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
        system.DeclareAccess();
        using (Structural())
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
        using (Structural())
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
        // No pass can run across an update: one still counted was interrupted by an exception. Close its tombstones.
        if (Volatile.Read(ref Passes) != 0) CompactArchetypes();

        long update = ++group.Updates;
        var batches = group.Batches;
        bool parallel = _options.Parallel;
        for (int b = 0; b < batches.Length; b++)
        {
            var batch = batches[b];
            // One change tick per batch: its systems never write what another one of them reads (Changed<T> reads T).
            _changeTick++;
            if (parallel && batch.Length > 1)
            {
                RunBatch(batch, update);
                continue;
            }

            for (int i = 0; i < batch.Length; i++)
                RunSystem(batch[i], update);
        }

        _changeTick++; // writes between updates are newer than every system's last run
    }

    private void RunSystem(EcsSystem system, long update)
    {
        if (!system.Enabled) return;
        int interval = system.UpdateInterval;
        if (interval > 1 && update % interval != 0) return;

        try
        {
            RunMeasured(system);
        }
        catch (Exception e) when (SystemError != null)
        {
            SystemError(system, e);
        }
        finally
        {
            system.LastRunTick = _changeTick; // its own writes of this run are not changes for its next run
        }
    }

    private void RunMeasured(EcsSystem system)
    {
        if (!_options.MeasureSystems)
        {
            system.Update();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            system.Update();
        }
        finally
        {
            system.Stats = system.Stats.Add(Stopwatch.GetTimestamp() - start);
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
        ResetIndexes(); // an index still held finds nothing
        ClearSingletons();
        _freeIdCount = 0;
        _entityCount = 0;
    }

    /// <summary>
    /// Systems of one group sorted by priority (stable), with their parallel batches. Mutated under
    /// the structural gate by publishing new arrays (copy-on-write), so a running update never sees a torn list.
    /// </summary>
    internal sealed class SystemSchedule
    {
        public EcsSystem[] Systems = [];
        public EcsSystem[][] Batches = [];
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

        /// <summary>
        /// Batches are runs of consecutive systems of one priority with pairwise non-conflicting access. Consecutive
        /// keeps the meaning of registration order: of two conflicting systems, the earlier one always finishes first.
        /// </summary>
        private void Publish(EcsSystem[] systems)
        {
            var batches = new List<EcsSystem[]>();
            for (int i = 0; i < systems.Length;)
            {
                int start = i++;
                while (i < systems.Length && systems[i].Priority == systems[start].Priority && Fits(systems, start, i)) i++;
                batches.Add(systems[start..i]);
            }

            // An update reads only Batches: one reference, so it never mixes two versions of the list.
            Systems = systems;
            Batches = batches.ToArray();
        }

        private static bool Fits(EcsSystem[] systems, int start, int next)
        {
            for (int i = start; i < next; i++)
                if (systems[i].Access.ConflictsWith(systems[next].Access))
                    return false;
            return true;
        }
    }
}

/// <summary>
/// The systems of a world, every group in execution order (default group first): <c>foreach</c> it, or read
/// <see cref="Count"/>. A view, not a copy: allocation-free, it sees the systems registered when each group is reached.
/// </summary>
public readonly struct SystemList
{
    private readonly World.SystemSchedule[] _groups;

    internal SystemList(World.SystemSchedule[] groups) => _groups = groups;

    /// <summary>Number of systems in every group. O(groups).</summary>
    public int Count
    {
        get
        {
            int count = 0;
            foreach (var group in _groups)
                if (group != null) count += group.Systems.Length;
            return count;
        }
    }

    public Enumerator GetEnumerator() => new(_groups);

    /// <summary>Walks the groups and their systems.</summary>
    public struct Enumerator
    {
        private readonly World.SystemSchedule[] _groups;
        private EcsSystem[] _systems;
        private int _group;
        private int _index;

        internal Enumerator(World.SystemSchedule[] groups)
        {
            _groups = groups;
            _systems = [];
            _group = -1;
            _index = -1;
        }

        public readonly EcsSystem Current => _systems[_index];

        public bool MoveNext()
        {
            while (++_index >= _systems.Length)
            {
                if (++_group >= _groups.Length) return false;
                _systems = _groups[_group]?.Systems ?? [];
                _index = -1;
            }

            return true;
        }
    }
}
