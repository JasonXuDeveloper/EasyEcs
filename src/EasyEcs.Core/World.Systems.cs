using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
namespace EasyEcs;

// System scheduling. A system is a type ([EcsSystem] ref struct) and a record in the world (SystemRow); its code is a
// case of its assembly's system set (ISystemSet). Systems run synchronously, in registration order as
// [UpdateBefore<T>] / [UpdateAfter<T>] reorder it, in stages: a system's stage follows the stages of the systems it
// must run after and of the earlier systems whose SystemAccess conflicts with its own, so a stage holds systems with
// no order and no conflict between them; with WorldOptions.ParallelSystems a stage runs at the same time
// (World.Parallel.cs). A stage is cut into segments, consecutive systems of one set, each run by one call of its set.
public sealed partial class World
{
    // Group 0 is the default group (World.Update()); others are [UpdateInGroup<T>] groups (World.Update<T>()).
    private SystemSchedule[] _groups = [new()];

    /// <summary>The records of the added systems, by index (<see cref="SystemHandle"/>); free records have no code.</summary>
    internal SystemRow[] SystemRows = [];

    private int[] _freeSystems = [];
    private int _freeSystemCount;

    // TypeIndex<SystemCode.Entry, T> -> index + 1 of its record (0: not added). O(1) for HasSystem / System<T>.
    private int[] _systemOfType = [];

    /// <summary>A system of a schedule: its record and the record's version when it was scheduled.</summary>
    internal readonly record struct SystemSlot(int Index, int Version);

    /// <summary>
    /// Number of <see cref="Update()"/> calls so far, the one running included (0 before the first): updates of the default
    /// group only (<see cref="Update{TGroup}"/> runs are counted by <see cref="UpdateCount{TGroup}"/>). The count an
    /// <see cref="UpdateIntervalAttribute">[UpdateInterval]</see> of the default group's systems is taken from. O(1).
    /// </summary>
    public long DefaultUpdateCount => _groups[0].Updates;

    /// <summary>
    /// Number of <see cref="Update{TGroup}"/> calls of group <typeparamref name="TGroup"/> so far, the one running
    /// included (0 before the first, and for a group no system was ever added to): the count an
    /// <see cref="UpdateIntervalAttribute">[UpdateInterval]</see> of the group's systems is taken from (a system runs
    /// when it modulo the interval is its phase). O(1).
    /// </summary>
    public long UpdateCount<TGroup>() where TGroup : struct, ISystemGroup
    {
        var groups = _groups;
        int index = SystemGroup<TGroup>.Index;
        return index < groups.Length && groups[index] is { } group ? group.Updates : 0;
    }

    /// <summary>Systems of every group, each group in execution order (default group first). Allocation-free.</summary>
    public SystemList Systems => new(this, _groups);

    /// <summary>
    /// Add system <typeparamref name="T"/> (an <see cref="EcsSystemAttribute">[EcsSystem]</see> ref struct) to its group
    /// (<see cref="UpdateInGroupAttribute{TGroup}"/>, default otherwise) and run its
    /// <c>OnCreate</c> method. A system is added once per world: adding it again throws.
    /// Cold: allocates its record and the group's new schedule, O(systems²) for the stages.
    /// </summary>
    /// <param name="changesFromNow">
    /// False (the default): the first run of its <c>[Changed&lt;T&gt;]</c> and <c>[Removed&lt;T&gt;]</c> methods visits
    /// every row written or removed since the world began. True: only what is written or removed after this call, as if
    /// the system had run just now (<see cref="SystemHandle.LastRunTick"/>); for a system added to a world that already
    /// holds its state (a restart after <see cref="Load(System.ReadOnlySpan{byte}, bool)"/>, a system switched on later).
    /// </param>
    /// <returns>The system's handle.</returns>
    /// <exception cref="NotSupportedException">No assembly registers code for <typeparamref name="T"/>: it is no
    /// [EcsSystem], or a closed generic system no assembly names (made by reflection).</exception>
    public unsafe SystemHandle AddSystem<T>(bool changesFromNow = false) where T : struct, allows ref struct
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        int typeSlot = TypeIndex<SystemCode.Entry, T>.Value;
        if (Find(typeSlot) >= 0)
            throw new InvalidOperationException($"{ScheduleInfo.NameOf(typeof(T))} is already a system of this world");
        var code = SystemCode.Of(typeof(T));
        code.Schedule();
        _ = code.Access; // declared before the schedule reads it
        int index;
        using (Structural())
        {
            index = TakeSystemRow();
            ref var row = ref SystemRows[index];
            row.Code = code;
            row.Id = code.Id;
            row.Enabled = true;
            row.Interval = code.Interval;
            row.Phase = code.Phase;
            row.Group = code.Group;
            row.TypeSlot = typeSlot;
            if (code.Group >= _groups.Length) Array.Resize(ref _groups, code.Group + 1);
            var group = _groups[code.Group] ??= new SystemSchedule { Name = code.GroupName };
            try
            {
                group.Insert(this, new SystemSlot(index, row.Version)); // throws on a cycle: the system stays out
            }
            catch
            {
                FreeSystemRow(index);
                throw;
            }

            if (changesFromNow) SystemRows[index].LastRunTick = _changeTick++; // writes from here on are newer than its "last run"
        }

        if (typeSlot >= _systemOfType.Length) Array.Resize(ref _systemOfType, Math.Max(typeSlot + 1, _systemOfType.Length * 2));
        _systemOfType[typeSlot] = index + 1;
        var handle = new SystemHandle(this, index, SystemRows[index].Version);
        code.Set.Create(this, code.Id, handle);
        return handle;
    }

    /// <summary>Remove system <typeparamref name="T"/> (runs its <c>OnDestroy</c> method): false when the world has none. Its handles turn stale.</summary>
    public bool RemoveSystem<T>() where T : struct, allows ref struct
    {
        int index = Find(TypeIndex<SystemCode.Entry, T>.Value);
        if (index < 0) return false;
        RemoveSystem(index);
        return true;
    }

    /// <summary>Whether system <typeparamref name="T"/> is in this world. O(1).</summary>
    public bool HasSystem<T>() where T : struct, allows ref struct => Find(TypeIndex<SystemCode.Entry, T>.Value) >= 0;

    /// <summary>
    /// The handle of system <typeparamref name="T"/> (for tools, tests and the code that hosts the world, never from
    /// another system). Throws <see cref="InvalidOperationException"/> when the world has none. O(1).
    /// </summary>
    public SystemHandle System<T>() where T : struct, allows ref struct
    {
        if (!TryGetSystem<T>(out var system))
            throw new InvalidOperationException($"{ScheduleInfo.NameOf(typeof(T))} is no system of this world");
        return system;
    }

    /// <summary>Find system <typeparamref name="T"/>: false and a default handle when the world has none. O(1).</summary>
    public bool TryGetSystem<T>(out SystemHandle system) where T : struct, allows ref struct
    {
        int index = Find(TypeIndex<SystemCode.Entry, T>.Value);
        system = index < 0 ? default : new SystemHandle(this, index, SystemRows[index].Version);
        return index >= 0;
    }

    private int Find(int typeSlot) => typeSlot < _systemOfType.Length ? _systemOfType[typeSlot] - 1 : -1;

    private unsafe void RemoveSystem(int index)
    {
        ref var row = ref SystemRows[index];
        var code = row.Code;
        var handle = new SystemHandle(this, index, row.Version);
        using (Structural()) _groups[row.Group].Remove(this, index);
        _systemOfType[row.TypeSlot] = 0;
        try
        {
            code.Set.Destroy(this, code.Id, handle); // its handle still reads the record
        }
        finally
        {
            FreeSystemRow(index);
        }
    }

    private int TakeSystemRow()
    {
        if (_freeSystemCount > 0) return _freeSystems[--_freeSystemCount];
        int index = SystemRows.Length;
        Array.Resize(ref SystemRows, Math.Max(8, index * 2));
        for (int i = SystemRows.Length - 1; i > index; i--) PushFree(i); // the lowest first
        return index;
    }

    /// <summary>Clear record <paramref name="index"/>: its version moves on, so every handle of its system is stale.</summary>
    private void FreeSystemRow(int index)
    {
        SystemRows[index] = new SystemRow { Version = SystemRows[index].Version + 1 }; // drops the code, its set and its error
        PushFree(index);
    }

    private void PushFree(int index)
    {
        if (_freeSystemCount == _freeSystems.Length) Array.Resize(ref _freeSystems, Math.Max(8, _freeSystemCount * 2));
        _freeSystems[_freeSystemCount++] = index;
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

    private unsafe void Run(SystemSchedule group)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        ThreadSetup.CatchUp(); // per-thread state another thread set up (a system may run here what ran on a helper)
        // No pass can run across an update: one still counted was interrupted by an exception. Close its tombstones.
        if (Volatile.Read(ref PassDepth) != 0) CompactArchetypes();

        long update = ++group.Updates;
        // EntityRandom's tick counts the Update() calls: new numbers each update, none for an Update<TGroup>().
        if (ReferenceEquals(group, _groups[0]) && Box<EntityRandom>() is { } random) random.Value.Tick++;
        if (!_options.ParallelSystems)
        {
            // One thread: the whole group in runs of one set across stages, the tick moved on where a stage starts.
            var serial = group.Serial;
            var runs = serial.Runs;
            for (int r = 0; r < runs.Length; r++)
            {
                var run = runs[r];
                run.Set.Run(this, serial.Systems.AsSpan(run.Start, run.Count), update, serial.Starts.AsSpan(run.Start, run.Count),
                    serial.Fused.AsSpan(run.Start, run.Count));
            }
        }
        else
        {
            var stages = group.Stages;
            for (int b = 0; b < stages.Length; b++)
            {
                var stage = stages[b];
                // One change tick per stage: its systems never write what another one of them reads (Changed<T> reads T).
                _changeTick++;
                if (stage.Systems.Length > 1)
                {
                    RunStage(stage, update);
                    continue;
                }

                var segments = stage.Segments;
                for (int s = 0; s < segments.Length; s++)
                {
                    var segment = segments[s];
                    segment.Set.Run(this, stage.Systems.AsSpan(segment.Start, segment.Count), update, default, default);
                }
            }
        }

        _changeTick++; // writes between updates are newer than every system's last run
    }

    /// <summary>
    /// Run <paramref name="systems"/>, systems of set <typeparamref name="TSet"/>, for update <paramref name="update"/>
    /// of their group: the enabled ones due on it (<see cref="UpdateIntervalAttribute">[UpdateInterval]</see>), each
    /// through the set's switch, so a system's code is a direct call. A segment of a stage runs with one call, a system
    /// of a parallel stage with a span of one. Bound once per set (<see cref="SystemCode.Register{TSet}"/>).
    /// <paramref name="starts"/>, when not empty, marks the systems that start a stage (a run of the serial plan, across
    /// stages): the change tick moves on before each, as it does before every stage, run or not.
    /// <paramref name="fused"/>, when not empty, holds for each system the fusion of the set it shares one walk with the
    /// next by (pass fusion, <see cref="ISystemSet.RunFused"/>), or -1: used when both run on this update.
    /// </summary>
    internal static void RunSystems<TSet>(World world, ReadOnlySpan<SystemSlot> systems, long update, ReadOnlySpan<bool> starts,
        ReadOnlySpan<short> fused) where TSet : struct, ISystemSet
    {
        for (int i = 0; i < systems.Length; i++)
        {
            if ((uint)i < (uint)starts.Length && starts[i]) world._changeTick++; // one change tick per stage
            var slot = systems[i];
            // Re-read per system: one may add systems (a new array) or remove a later one (a new version) as it runs.
            ref var row = ref world.SystemRows[slot.Index];
            if (row.Version != slot.Version || !row.Enabled) continue;
            int interval = row.Interval;
            if (interval > 1 && update % interval != row.Phase) continue;
            var handle = new SystemHandle(world, slot.Index, slot.Version);
            int id = row.Id;
            if (world._options.MeasureSystems)
            {
                world.RunMeasured<TSet>(id, handle);
                continue;
            }

            if ((uint)(i + 1) < (uint)fused.Length && fused[i] >= 0 && world.Runs(systems[i + 1], update) is { IsAlive: true } next)
            {
                // The next system shares this one's walk (it is due and enabled too): one pass for both.
                world.RunFused<TSet>(fused[i], handle, next, (uint)(i + 1) < (uint)starts.Length && starts[i + 1]);
                i++;
                continue;
            }

            try
            {
                TSet.Update(world, id, handle);
            }
            catch (Exception e) when (world._options.SystemErrors == SystemErrorPolicy.Disable)
            {
                world.Fail(handle, e);
            }
            finally
            {
                world.Ran(handle);
            }
        }
    }

    /// <summary>The handle of <paramref name="slot"/> when it runs on update <paramref name="update"/> (enabled and due), else a default one.</summary>
    private SystemHandle Runs(SystemSlot slot, long update)
    {
        ref var row = ref SystemRows[slot.Index];
        if (row.Version != slot.Version || !row.Enabled || row.Interval > 1 && update % row.Interval != row.Phase) return default;
        return new SystemHandle(this, slot.Index, slot.Version);
    }

    /// <summary>
    /// Two systems in one walk (fusion <paramref name="fusion"/> of set <typeparamref name="TSet"/>): the second one's
    /// writes are stamped with the tick of its own stage (<paramref name="nextStage"/>: the tick moves on between them),
    /// an exception disables the system whose method threw (the other keeps what it did on the rows walked so far), and
    /// each counts a run.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunFused<TSet>(int fusion, SystemHandle first, SystemHandle second, bool nextStage) where TSet : struct, ISystemSet
    {
        try
        {
            TSet.RunFused(this, fusion, first, second, nextStage ? _changeTick + 1 : _changeTick);
        }
        catch (Exception e) when (_options.SystemErrors == SystemErrorPolicy.Disable)
        {
            Fail(SystemCode.Culprit(e) == 1 ? second : first, e);
        }
        finally
        {
            Ran(first);
            if (nextStage) _changeTick++;
            Ran(second);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunMeasured<TSet>(int id, SystemHandle handle) where TSet : struct, ISystemSet
    {
        // The thread's own allocations, plus those of the threads that ran chunks of its parallel [ForEach] passes
        // (TryRunChunks adds them to HelperAllocated of this thread).
        SystemRows[handle.Index].Visited = 0;
        long helpers = HelperAllocated;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        try
        {
            TSet.Update(this, id, handle);
        }
        catch (Exception e) when (_options.SystemErrors == SystemErrorPolicy.Disable)
        {
            Fail(handle, e);
        }
        finally
        {
            long ticks = Stopwatch.GetTimestamp() - start;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated + (HelperAllocated - helpers);
            if (handle.IsAlive)
            {
                ref var row = ref SystemRows[handle.Index];
                row.Stats = row.Stats.Add(ticks, allocated, row.Visited);
            }

            Ran(handle);
        }
    }

    /// <summary>After a run: its own writes are not changes for its next run, and the next run visits the next slice.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ran(SystemHandle handle)
    {
        ref var row = ref SystemRows[handle.Index]; // the array as it is now: the run may have grown it
        if (row.Version != handle.Version) return; // it removed itself
        row.LastRunTick = _changeTick;
        row.RunCount++;
    }

    /// <summary>Switch the system off for <paramref name="error"/> (<see cref="SystemErrorPolicy.Disable"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Fail(SystemHandle handle, Exception error)
    {
        if (!handle.IsAlive) return;
        ref var row = ref SystemRows[handle.Index];
        row.Enabled = false;
        row.Error = error;
    }

    /// <summary>Whether this world measures its systems (<see cref="WorldOptions.MeasureSystems"/>).</summary>
    internal bool MeasuresSystems => _options.MeasureSystems;

    /// <summary>
    /// Group index of a system type (its [UpdateInGroup] attribute, or the default group), the group's name and the
    /// system types it runs before and after. Cold: once per system type.
    /// </summary>
    internal static (int Group, string Name, Type[] Before, Type[] After) GroupOf(Type type)
    {
        // The attribute instance knows its group index, so no MakeGenericType.
        int group = 0;
        string name = SystemSchedule.DefaultName;
        List<Type> runsBefore = null, runsAfter = null;
        foreach (var attribute in Attribute.GetCustomAttributes(type, typeof(Attribute), inherit: false))
        {
            if (attribute is ISystemGroupAttribute grouping) (group, name) = (grouping.Index, grouping.Name);
            else if (attribute is ISystemOrder order) ((order.Before ? runsBefore ??= [] : runsAfter ??= [])).Add(order.Other);
        }

        return (group, name, runsBefore?.ToArray() ?? [], runsAfter?.ToArray() ?? []);
    }

    /// <summary>
    /// Destroy all systems (OnDestroy in reverse order), then release all storage. Every system's OnDestroy runs and
    /// the storage is released even when one throws; under <see cref="SystemErrorPolicy.Throw"/> the first exception is
    /// rethrown at the end. Afterwards every handle is stale and every structural change throws
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Exception first = null;
        var groups = _groups;
        for (int g = groups.Length - 1; g >= 0; g--)
        {
            var systems = groups[g]?.Systems ?? [];
            for (int i = systems.Length - 1; i >= 0; i--)
            {
                var slot = systems[i];
                ref var row = ref SystemRows[slot.Index];
                if (row.Version != slot.Version || row.Code is not { } code) continue;
                var handle = new SystemHandle(this, slot.Index, slot.Version);
                try
                {
                    code.Set.Destroy(this, code.Id, handle);
                }
                catch (Exception e) when (_options.SystemErrors == SystemErrorPolicy.Disable)
                {
                    Fail(handle, e);
                }
                catch (Exception e)
                {
                    first ??= e; // the other systems still get their OnDestroy
                }
            }
        }

        _groups = [new()];
        for (int i = 0; i < SystemRows.Length; i++) SystemRows[i] = new SystemRow { Version = SystemRows[i].Version + 1 };
        _systemOfType = [];

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
        Gate.Close(); // the cleared tables take no rows: every later structural change throws
        if (first != null) global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
    }

    /// <summary>Consecutive systems of a stage that belong to one set: run by one call of the set.</summary>
    internal readonly record struct Segment(SystemCode.Set Set, int Start, int Count);

    /// <summary>
    /// A group's stages as one thread runs them: every system in execution order (stage after stage), cut into runs of
    /// consecutive systems of one set whatever the stages, and which systems start a stage. Sixteen systems of one
    /// assembly in sixteen stages are one call of their set, not sixteen; the change tick still moves on once per stage.
    /// </summary>
    internal sealed class SerialPlan(SystemSlot[] systems, bool[] starts, Segment[] runs, short[] fused)
    {
        public static readonly SerialPlan Empty = new([], [], [], []);

        public readonly SystemSlot[] Systems = systems;
        public readonly bool[] Starts = starts;
        public readonly Segment[] Runs = runs;

        /// <summary>
        /// Per system, the fusion of its set it shares one walk with the next system by (pass fusion: the two are next to
        /// each other in one run of the set, and the set declares the pair), or -1.
        /// </summary>
        public readonly short[] Fused = fused;

        /// <summary>The plan of <paramref name="stages"/>, whose systems are <paramref name="systems"/> in order, their codes <paramref name="codes"/>.</summary>
        public static SerialPlan Of(SystemStage[] stages, SystemSlot[] systems, SystemCode.Entry[] codes)
        {
            var starts = new bool[systems.Length];
            var runs = new List<Segment>();
            for (int b = 0, at = 0; b < stages.Length; b++)
            {
                starts[at] = true;
                foreach (var segment in stages[b].Segments)
                {
                    if (runs.Count > 0 && runs[^1].Set == segment.Set) runs[^1] = runs[^1] with { Count = runs[^1].Count + segment.Count };
                    else runs.Add(segment with { Start = at + segment.Start });
                }

                at += stages[b].Systems.Length;
            }

            var fused = new short[systems.Length];
            for (int i = 0; i < fused.Length; i++)
                fused[i] = (short)(i + 1 < codes.Length && codes[i].Set == codes[i + 1].Set ? codes[i].Set.FusionOf(codes[i].Id, codes[i + 1].Id) : -1);
            return new SerialPlan(systems, starts, runs.ToArray(), fused);
        }
    }

    /// <summary>
    /// Systems of one group in execution order (registration order as [UpdateBefore] / [UpdateAfter] reorder it), with
    /// their parallel stages. Mutated under the structural gate by publishing new arrays (copy-on-write), so a running
    /// update never sees a torn list.
    /// </summary>
    internal sealed class SystemSchedule
    {
        public SystemSlot[] Systems = []; // execution order
        public SystemStage[] Stages = [];
        public SerialPlan Serial = SerialPlan.Empty; // the stages as one thread runs them (no ParallelSystems)
        public long Updates;
        public string Name = DefaultName; // the group type's (World.DescribeSchedule)
        private SystemSlot[] _registered = [];

        public const string DefaultName = "Default";

        public void Insert(World world, SystemSlot system) => Publish(world, [.. _registered, system]);

        public void Remove(World world, int index)
        {
            int at = Array.FindIndex(_registered, s => s.Index == index);
            Publish(world, [.. _registered.AsSpan(0, at), .. _registered.AsSpan(at + 1)]);
        }

        /// <summary>
        /// Order <paramref name="registered"/> (registration order, then [UpdateBefore] / [UpdateAfter]: a stable
        /// topological sort) and split it into stages: each system goes to the stage after the latest stage of a
        /// system it must follow, by an ordering or by an earlier system whose access conflicts with its own. Of two
        /// conflicting systems the earlier one always finishes first; unordered, non-conflicting systems share a stage.
        /// Each stage is cut into segments of one set. Throws on a cycle, before anything changes. O(systems²), once per
        /// AddSystem / RemoveSystem.
        /// </summary>
        private void Publish(World world, SystemSlot[] registered)
        {
            var codes = new SystemCode.Entry[registered.Length];
            for (int i = 0; i < codes.Length; i++) codes[i] = world.SystemRows[registered[i].Index].Code;
            var order = Sort(codes);
            var levels = new int[order.Length];
            int stageCount = 0;
            for (int i = 0; i < order.Length; i++)
            {
                for (int j = 0; j < i; j++)
                    if (levels[j] >= levels[i] && (Precedes(codes[order[j]], codes[order[i]]) || codes[order[j]].Access.ConflictsWith(codes[order[i]].Access)))
                        levels[i] = levels[j] + 1;
                stageCount = Math.Max(stageCount, levels[i] + 1);
            }

            var stages = new SystemStage[stageCount];
            var systems = new SystemSlot[order.Length];
            var ordered = new SystemCode.Entry[order.Length]; // the codes of systems, in their order
            for (int level = 0, next = 0; level < stageCount; level++)
            {
                int start = next;
                var segments = new List<Segment>();
                List<Action<World>> prepares = null;
                for (int i = 0; i < order.Length; i++)
                {
                    if (levels[i] != level) continue;
                    if (codes[order[i]].Access.Indexes is { } indexes)
                        foreach (var prepare in indexes)
                            if (!(prepares ??= []).Contains(prepare)) prepares.Add(prepare);
                    var set = codes[order[i]].Set;
                    int at = next - start;
                    if (segments.Count > 0 && segments[^1].Set == set) segments[^1] = segments[^1] with { Count = segments[^1].Count + 1 };
                    else segments.Add(new Segment(set, at, 1));
                    ordered[next] = codes[order[i]];
                    systems[next++] = registered[order[i]];
                }

                stages[level] = new SystemStage(systems[start..next], segments.ToArray(), prepares?.ToArray() ?? []);
            }

            // An update reads only Stages, or only Serial: one reference, so it never mixes two versions of the list.
            _registered = registered;
            Systems = systems;
            Serial = SerialPlan.Of(stages, systems, ordered);
            Stages = stages;
        }

        /// <summary>
        /// Registration order (indexes into <paramref name="codes"/>), with each system that names others moved: an
        /// [UpdateBefore&lt;X&gt;] system goes just before X, an [UpdateAfter&lt;X&gt;] one just after X, repeated until
        /// every ordering holds; the other systems keep their relative order. Orderings that cannot all hold (a cycle)
        /// throw; orderings that hold only if systems that name nobody move too (X before Y, Y registered first, and a
        /// system between them) take the earliest-registered-first topological order instead.
        /// </summary>
        private static int[] Sort(SystemCode.Entry[] codes)
        {
            var sorted = TopologicalOrder(codes);
            var order = new List<int>(codes.Length);
            for (int i = 0; i < codes.Length; i++) order.Add(i);
            int rounds = codes.Length * codes.Length + 1; // a bound: moves that settle need far fewer rounds
            for (bool moved = true; moved;)
            {
                if (--rounds < 0) return sorted; // the moves keep undoing each other
                moved = false;
                for (int b = 0; b < order.Count; b++)
                for (int a = b + 1; a < order.Count; a++) // a runs after b, but must it run before?
                {
                    int first = order[a];
                    int second = order[b];
                    if (Names(codes[first].Before, codes[second])) // first moves: just before second
                    {
                        order.RemoveAt(a);
                        order.Insert(b, first);
                    }
                    else if (Names(codes[second].After, codes[first])) // second moves: just after first
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
        private static int[] TopologicalOrder(SystemCode.Entry[] codes)
        {
            int n = codes.Length;
            var placed = new bool[n];
            var order = new int[n];
            for (int count = 0; count < n; count++)
            {
                int next = -1;
                for (int i = 0; i < n && next < 0; i++)
                {
                    if (placed[i]) continue;
                    bool ready = true;
                    for (int j = 0; j < n && ready; j++) ready = placed[j] || j == i || !Precedes(codes[j], codes[i]);
                    if (ready) next = i;
                }

                if (next < 0) throw Cycle(codes, placed);
                placed[next] = true;
                order[count] = next;
            }

            return order;
        }

        /// <summary>Whether <paramref name="first"/> must run before <paramref name="second"/> by an ordering of either.</summary>
        internal static bool Precedes(SystemCode.Entry first, SystemCode.Entry second) => Names(first.Before, second) || Names(second.After, first);

        private static bool Names(Type[] types, SystemCode.Entry system) => Array.IndexOf(types, system.Type) >= 0;

        private static InvalidOperationException Cycle(SystemCode.Entry[] codes, bool[] placed)
        {
            var names = new List<string>();
            for (int i = 0; i < codes.Length; i++)
                if (!placed[i]) names.Add(ScheduleInfo.NameOf(codes[i].Type));
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
    private readonly World _world;
    private readonly World.SystemSchedule[] _groups;

    internal SystemList(World world, World.SystemSchedule[] groups)
    {
        _world = world;
        _groups = groups;
    }

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

    /// <summary>The enumerator of <c>foreach</c>: every group's systems in execution order, allocation-free.</summary>
    public Enumerator GetEnumerator() => new(_world, _groups);

    /// <summary>Walks the groups and their systems.</summary>
    public struct Enumerator
    {
        private readonly World _world;
        private readonly World.SystemSchedule[] _groups;
        private World.SystemSlot[] _systems;
        private int _group;
        private int _index;

        internal Enumerator(World world, World.SystemSchedule[] groups)
        {
            _world = world;
            _groups = groups;
            _systems = [];
            _group = -1;
            _index = -1;
        }

        /// <summary>The current system.</summary>
        public readonly SystemHandle Current => new(_world, _systems[_index].Index, _systems[_index].Version);

        /// <summary>Move to the next system. O(1) amortized.</summary>
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
