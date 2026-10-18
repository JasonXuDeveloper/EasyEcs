using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
namespace EasyEcs;

// System scheduling. Systems run synchronously, in registration order as [UpdateBefore<T>] / [UpdateAfter<T>] reorder
// it, in stages: a system's stage follows the stages of the systems it must run after and of the earlier systems whose
// SystemAccess conflicts with its own, so a stage holds systems with no order and no conflict between them; with
// WorldOptions.ParallelSystems a stage runs at the same time (World.Parallel.cs). Each update calls OnUpdate() then the
// generated [ForEach] methods.
public sealed partial class World
{
    // Group 0 is the default group (World.Update()); others are [UpdateInGroup<T>] groups (World.Update<T>()).
    private SystemSchedule[] _groups = [new()];

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

        system.Group = GroupOf(system.GetType(), out system.RunsBefore, out system.RunsAfter);
        system.DeclareAccess();
        using (Structural())
        {
            if (system.Group >= _groups.Length) Array.Resize(ref _groups, system.Group + 1);
            (_groups[system.Group] ??= new SystemSchedule()).Insert(system); // throws on a cycle: the system stays out
        }

        system.World = this;

        system.Create();
    }

    /// <summary>Remove the system of type <typeparamref name="T"/> (calls its <see cref="EcsSystem.OnDestroy"/>).</summary>
    public bool RemoveSystem<T>() where T : EcsSystem
    {
        var removed = GetSystem<T>();
        if (removed == null) return false;
        RemoveSystem(removed);
        return true;
    }

    private void RemoveSystem(EcsSystem system)
    {
        using (Structural()) _groups[system.Group].Remove(system);
        system.Destroy();
        system.World = null;
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
        var stages = group.Stages;
        bool parallel = _options.ParallelSystems;
        for (int b = 0; b < stages.Length; b++)
        {
            var stage = stages[b];
            var systems = stage.Systems;
            // One change tick per stage: its systems never write what another one of them reads (Changed<T> reads T).
            _changeTick++;
            if (parallel && systems.Length > 1)
            {
                RunStage(stage, update);
                continue;
            }

            for (int i = 0; i < systems.Length; i++)
                RunSystem(systems[i], update);
        }

        _changeTick++; // writes between updates are newer than every system's last run
    }

    private void RunSystem(EcsSystem system, long update)
    {
        if (!system.Enabled) return;
        int interval = system.UpdateInterval;
        if (interval > 1 && update % interval != system.UpdatePhase % interval) return;

        try
        {
            RunMeasured(system);
        }
        catch (Exception e) when (_options.SystemErrors == SystemErrorPolicy.Disable)
        {
            system.Fail(e);
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

    /// <summary>
    /// Group index of a system type (its inherited [UpdateInGroup] attribute, or the default group) and the system types
    /// it runs before and after.
    /// </summary>
    private static int GroupOf(Type type, out Type[] before, out Type[] after)
    {
        // Cold path (once per AddSystem); the attribute instance knows its group index, so no MakeGenericType.
        int group = 0;
        List<Type> runsBefore = null, runsAfter = null;
        foreach (var attribute in Attribute.GetCustomAttributes(type, typeof(Attribute), inherit: true))
        {
            if (attribute is ISystemGroupAttribute grouping) group = grouping.Index;
            else if (attribute is ISystemOrder order) ((order.Before ? runsBefore ??= [] : runsAfter ??= [])).Add(order.Other);
        }

        before = runsBefore?.ToArray() ?? [];
        after = runsAfter?.ToArray() ?? [];
        return group;
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
                catch (Exception e) when (_options.SystemErrors == SystemErrorPolicy.Disable)
                {
                    systems[i].Fail(e);
                }

                systems[i].World = null;
            }
        }

        _groups = [new()];

        // Meta keeps its length (handles index it unchecked); every record moves past its version, so every handle
        // is stale (a handle matching its record's version is alive: Entity.LocateArchetype).
        foreach (ref var meta in Meta.AsSpan()) meta = new EntityMeta { Version = meta.Version + 1 };
        ClearArchetypes();
        ResetIndexes(false); // an index still held finds nothing
        ClearSingletons();
        _arrivals?.Dispose();
        _arrivals = null;
        _saveBuffer = null;
        _freeIdCount = 0;
        _entityCount = 0;
        Collectible.Untrack(this);
        ComponentInfo.Reclaim(); // bits of unloaded contexts this world still mentioned
    }

    /// <summary>
    /// Systems of one group sorted by priority (stable), with their parallel stages. Mutated under
    /// the structural gate by publishing new arrays (copy-on-write), so a running update never sees a torn list.
    /// </summary>
    internal sealed class SystemSchedule
    {
        public EcsSystem[] Systems = []; // execution order
        public SystemStage[] Stages = [];
        public long Updates;
        private EcsSystem[] _registered = [];

        public void Insert(EcsSystem system) => Publish([.. _registered, system]);

        public void Remove(EcsSystem system)
        {
            int index = Array.IndexOf(_registered, system);
            Publish([.. _registered.AsSpan(0, index), .. _registered.AsSpan(index + 1)]);
        }

        /// <summary>
        /// Order <paramref name="registered"/> (registration order, then [UpdateBefore] / [UpdateAfter]: a stable
        /// topological sort) and split it into stages: each system goes to the stage after the latest stage of a
        /// system it must follow, by an ordering or by an earlier system whose access conflicts with its own. Of two
        /// conflicting systems the earlier one always finishes first; unordered, non-conflicting systems share a stage.
        /// Throws on a cycle, before anything changes. O(systems²), once per AddSystem / RemoveSystem.
        /// </summary>
        private void Publish(EcsSystem[] registered)
        {
            var order = Sort(registered);
            var levels = new int[order.Length];
            int stageCount = 0;
            for (int i = 0; i < order.Length; i++)
            {
                for (int j = 0; j < i; j++)
                    if (levels[j] >= levels[i] && (Precedes(order[j], order[i]) || order[j].Access.ConflictsWith(order[i].Access)))
                        levels[i] = levels[j] + 1;
                stageCount = Math.Max(stageCount, levels[i] + 1);
            }

            var stages = new SystemStage[stageCount];
            var systems = new EcsSystem[order.Length];
            for (int level = 0, next = 0; level < stageCount; level++)
            {
                int start = next;
                for (int i = 0; i < order.Length; i++)
                    if (levels[i] == level) systems[next++] = order[i];
                stages[level] = new SystemStage(systems[start..next]);
            }

            // An update reads only Stages: one reference, so it never mixes two versions of the list.
            _registered = registered;
            Systems = systems;
            Stages = stages;
        }

        /// <summary>
        /// Registration order, with each system that names others moved: an [UpdateBefore&lt;X&gt;] system goes just
        /// before X, an [UpdateAfter&lt;X&gt;] one just after X, repeated until every ordering holds; the other systems
        /// keep their relative order. Orderings that cannot all hold (a cycle) throw; orderings that hold only if
        /// systems that name nobody move too (X before Y, Y registered first, and a system between them) take the
        /// earliest-registered-first topological order instead.
        /// </summary>
        private static EcsSystem[] Sort(EcsSystem[] registered)
        {
            var sorted = TopologicalOrder(registered);
            var order = new List<EcsSystem>(registered);
            int rounds = registered.Length * registered.Length + 1; // a bound: moves that settle need far fewer rounds
            for (bool moved = true; moved;)
            {
                if (--rounds < 0) return sorted; // the moves keep undoing each other
                moved = false;
                for (int b = 0; b < order.Count; b++)
                for (int a = b + 1; a < order.Count; a++) // a runs after b, but must it run before?
                {
                    var first = order[a];
                    var second = order[b];
                    if (Names(first.RunsBefore, second)) // first moves: just before second
                    {
                        order.RemoveAt(a);
                        order.Insert(b, first);
                    }
                    else if (Names(second.RunsAfter, first)) // second moves: just after first
                    {
                        order.RemoveAt(b);
                        order.Insert(a, second);
                    }
                    else continue;

                    moved = true;
                }
            }

            return order.ToArray();
        }

        /// <summary>
        /// Kahn's algorithm, the earliest registered free system first; throws when the orderings form a cycle (some
        /// systems never become free of predecessors).
        /// </summary>
        private static EcsSystem[] TopologicalOrder(EcsSystem[] registered)
        {
            int n = registered.Length;
            var placed = new bool[n];
            var order = new EcsSystem[n];
            for (int count = 0; count < n; count++)
            {
                int next = -1;
                for (int i = 0; i < n && next < 0; i++)
                {
                    if (placed[i]) continue;
                    bool ready = true;
                    for (int j = 0; j < n && ready; j++) ready = placed[j] || j == i || !Precedes(registered[j], registered[i]);
                    if (ready) next = i;
                }

                if (next < 0) throw Cycle(registered, placed);
                placed[next] = true;
                order[count] = registered[next];
            }

            return order;
        }

        /// <summary>Whether <paramref name="first"/> must run before <paramref name="second"/> by an ordering of either.</summary>
        private static bool Precedes(EcsSystem first, EcsSystem second) => Names(first.RunsBefore, second) || Names(second.RunsAfter, first);

        private static bool Names(Type[] types, EcsSystem system)
        {
            foreach (var type in types)
                if (type.IsInstanceOfType(system)) return true;
            return false;
        }

        private static InvalidOperationException Cycle(EcsSystem[] registered, bool[] placed)
        {
            var names = new List<string>();
            for (int i = 0; i < registered.Length; i++)
                if (!placed[i]) names.Add(registered[i].GetType().Name);
            return new InvalidOperationException("[UpdateBefore] / [UpdateAfter] form a cycle among " + string.Join(", ", names));
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
