using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SystemTests
{
    private static readonly List<string> Log = [];

    /// <summary>The test's record of a hook: the system's type name and the hook.</summary>
    private static void Note(SystemHandle system, string hook) => Log.Add(system.Type.Name + "." + hook);

    /// <summary>A system that logs every hook.</summary>
    [EcsSystem]
    internal ref struct Tick
    {
        static void OnCreate(SystemHandle system) => Note(system, "create");
        static void OnUpdate(SystemHandle system) => Note(system, "update");
        static void OnDestroy(SystemHandle system) => Note(system, "destroy");
    }

    [EcsSystem] internal ref struct X { static void OnUpdate(SystemHandle system) => Note(system, "update"); }
    [EcsSystem] internal ref struct Y { static void OnUpdate(SystemHandle system) => Note(system, "update"); }

    [EcsSystem, UpdateInterval(3)]
    internal ref struct Every3 { static void OnUpdate(SystemHandle system) => Note(system, "update"); }

    [EcsSystem, UpdateInterval(3, Phase = 1)]
    internal ref struct Every3Phase1 { static void OnUpdate(SystemHandle system) => Note(system, "update"); }

    [EcsSystem, UpdateInterval(3, Phase = 2)]
    internal ref struct Every3Phase2 { static void OnUpdate(SystemHandle system) => Note(system, "update"); }

    // A run as short as Tick's can take less than a TimeSpan tick (100 ns), so its measured time may round to zero:
    // this one lasts at least a microsecond.
    [EcsSystem, UpdateInterval(2)]
    internal ref struct Slow
    {
        static void OnUpdate()
        {
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromMicroseconds(1)) { }
        }
    }

    [Test]
    public void PhasesSpreadSystemsOfOneInterval()
    {
        using var world = new World();
        world.AddSystem<Every3>();
        world.AddSystem<Every3Phase1>();
        world.AddSystem<Every3Phase2>();
        Log.Clear();
        for (int i = 0; i < 6; i++) world.Update();                           // updates 1..6
        Assert.That(Log, Is.EqualTo(new[]
        {
            "Every3Phase1.update", "Every3Phase2.update", "Every3.update", "Every3Phase1.update", "Every3Phase2.update", "Every3.update",
        }));
    }

    [SetUp]
    public void SetUp() => Log.Clear();

    [Test]
    public void SystemsListsEveryGroupWithoutAllocating()
    {
        using var world = new World();
        world.AddSystem<InInputGroup>();
        world.AddSystem<Tick>();
        var names = new List<string>();
        foreach (var system in world.Systems) names.Add(system.Type.Name);
        Assert.That(names, Is.EqualTo(new[] { nameof(Tick), nameof(InInputGroup) }));   // default group first
        Assert.That(world.Systems.Count, Is.EqualTo(2));

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        for (int i = 0; i < 100; i++)
            foreach (var _ in world.Systems) count++;
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(count, Is.EqualTo(200));
    }

    [Test]
    public void MeasureSystemsRecordsRunsOnlyWhenEnabled()
    {
        using var measured = new World(new WorldOptions { MeasureSystems = true });
        var a = measured.AddSystem<Slow>();
        for (int i = 0; i < 4; i++) measured.Update();
        Assert.That(a.Stats.Runs, Is.EqualTo(2));
        Assert.That(a.Stats.TotalTime, Is.GreaterThanOrEqualTo(a.Stats.LastTime));
        Assert.That(a.Stats.AverageTime, Is.GreaterThan(TimeSpan.Zero));

        using var plain = new World();
        var b = plain.AddSystem<Tick>();
        plain.Update();
        Assert.That(b.Stats.Runs, Is.Zero);
    }

    [EcsSystem, UpdateAfter<Mid>]
    internal ref struct Late
    {
        static void OnCreate(SystemHandle system) => Note(system, "create");
        static void OnUpdate(SystemHandle system) => Note(system, "update");
        static void OnDestroy(SystemHandle system) => Note(system, "destroy");
    }

    [EcsSystem, UpdateBefore<Mid>]
    internal ref struct Early
    {
        static void OnCreate(SystemHandle system) => Note(system, "create");
        static void OnUpdate(SystemHandle system) => Note(system, "update");
        static void OnDestroy(SystemHandle system) => Note(system, "destroy");
    }

    [EcsSystem]
    internal ref struct Mid
    {
        static void OnCreate(SystemHandle system) => Note(system, "create");
        static void OnUpdate(SystemHandle system) => Note(system, "update");
        static void OnDestroy(SystemHandle system) => Note(system, "destroy");
    }

#pragma warning disable EECS019 // the cycle under test
    [EcsSystem, UpdateBefore<CycleB>]
    internal ref struct CycleA { static void OnCreate(SystemHandle system) => Note(system, "create"); }

    [EcsSystem, UpdateBefore<CycleA>]
    internal ref struct CycleB { static void OnCreate(SystemHandle system) => Note(system, "create"); }
#pragma warning restore EECS019

    [Test]
    public void ACycleOfOrderingsIsRefusedAndTheSystemStaysOut()
    {
        using var world = new World();
        world.AddSystem<CycleA>();
        var error = Assert.Throws<InvalidOperationException>(() => world.AddSystem<CycleB>());
        Assert.That(error!.Message, Does.Contain("CycleA").And.Contain("CycleB"));
        Assert.That(world.HasSystem<CycleB>(), Is.False);
        Assert.That(world.TryGetSystem<CycleB>(out var found), Is.False);
        Assert.That(found, Is.EqualTo(default(SystemHandle)));
        Assert.That(world.Systems.Count, Is.EqualTo(1));
        Assert.That(Log, Is.EqualTo(new[] { "CycleA.create" }));               // OnCreate only for the system that joined
    }

    [Test]
    public void AnOrderingMovesOnlyItsOwnSystem()
    {
        using var world = new World();
        world.AddSystem<Late>();                                              // after Mid
        world.AddSystem<X>();
        world.AddSystem<Mid>();
        world.AddSystem<Y>();
        Log.Clear();
        world.Update();
        Assert.That(Log, Is.EqualTo(new[] { "X.update", "Mid.update", "Late.update", "Y.update" }));
    }

    [Test]
    public void LifecycleAndDependencyOrder()
    {
        var world = new World();
        world.AddSystem<Late>();
        world.AddSystem<Early>();
        world.AddSystem<Mid>();
        world.Update();
        world.Dispose();

        Assert.That(Log, Is.EqualTo(new[]
        {
            "Late.create", "Early.create", "Mid.create",
            "Early.update", "Mid.update", "Late.update",
            "Late.destroy", "Mid.destroy", "Early.destroy",
        }));
    }

    [Test]
    public void IntervalEnabledAndRemoval()
    {
        using var world = new World();
        var every3 = world.AddSystem<Every3>();
        for (int i = 0; i < 6; i++) world.Update();
        every3.Enabled = false;
        world.Update();
        world.Update();
        world.Update();

        Assert.That(Log.FindAll(l => l == "Every3.update"), Has.Count.EqualTo(2));
        Assert.That(every3.RunCount, Is.EqualTo(2));
        Assert.That(world.RemoveSystem<Every3>(), Is.True);
        Assert.That(world.RemoveSystem<Every3>(), Is.False);
        Assert.That(world.TryGetSystem<Every3>(out _), Is.False);
        Assert.That(world.DefaultUpdateCount, Is.EqualTo(9));
    }

    [Test]
    public void AHandleTurnsStaleWhenItsSystemIsRemoved()
    {
        using var world = new World();
        var tick = world.AddSystem<Tick>();
        Assert.That((tick.IsAlive, tick.World, tick.Type, tick.Name), Is.EqualTo((true, world, typeof(Tick), "Tick")));
        Assert.That(world.System<Tick>(), Is.EqualTo(tick));
        Assert.That(world.HasSystem<Tick>(), Is.True);

        world.RemoveSystem<Tick>();
        Assert.That(Log, Is.EqualTo(new[] { "Tick.create", "Tick.destroy" }));
        Assert.That(tick.IsAlive, Is.False);
        Assert.Throws<InvalidOperationException>(() => _ = tick.Enabled);
        Assert.Throws<InvalidOperationException>(() => _ = tick.RunCount);
        Assert.Throws<InvalidOperationException>(() => _ = tick.World);
        Assert.Throws<InvalidOperationException>(() => world.System<Tick>());
        Assert.Throws<InvalidOperationException>(() => _ = default(SystemHandle).Stats);

        // The record is reused for the next system: the old handle stays stale.
        var x = world.AddSystem<X>();
        var again = world.AddSystem<Tick>();
        Assert.That((tick.IsAlive, x.IsAlive, again.IsAlive, again == tick), Is.EqualTo((false, true, true, false)));
    }

    [Test]
    public void AddingASystemTwiceThrowsAndEachWorldHasItsOwn()
    {
        var world = new World();
        var first = world.AddSystem<Tick>();
        Assert.Throws<InvalidOperationException>(() => world.AddSystem<Tick>());
        Assert.That(world.Systems.Count, Is.EqualTo(1));
        using (var other = new World())
        {
            var second = other.AddSystem<Tick>();
            Assert.That((second.World, second == first), Is.EqualTo((other, false)));
        }

        world.Dispose();
        Assert.That(first.IsAlive, Is.False, "a disposed world's systems are gone");
        Assert.Throws<ObjectDisposedException>(() => world.Update());
        Assert.Throws<ObjectDisposedException>(() => world.AddSystem<X>());
    }

#pragma warning disable EECS028 // the test's record
    /// <summary>The hooks' parameters in either order, or none.</summary>
    [EcsSystem]
    internal ref struct Parameters
    {
        static void OnCreate() => Log.Add("create");
        static void OnUpdate(SystemHandle system, World world) => Log.Add($"update {system.RunCount} {system.World == world}");
        static void OnDestroy(World world, SystemHandle system) => Log.Add($"destroy {system.RunCount} {world.DefaultUpdateCount}");
    }
#pragma warning restore EECS028

    [Test]
    public void HooksTakeTheWorldAndTheHandleInEitherOrder()
    {
        using var world = new World();
        world.AddSystem<Parameters>();
        world.Update();
        world.Update();
        world.RemoveSystem<Parameters>();
        Assert.That(Log, Is.EqualTo(new[] { "create", "update 0 True", "update 1 True", "destroy 2 2" }));
    }

    [EcsSystem]
    internal ref struct Throwing
    {
        static void OnUpdate() => throw new InvalidOperationException("boom");
    }

    [Test]
    public void ErrorsPropagateOrSwitchTheSystemOff()
    {
        using (var world = new World())
        {
            world.AddSystem<Throwing>();
            Assert.Throws<InvalidOperationException>(world.Update);
        }

        using var isolating = new World(new WorldOptions { SystemErrors = SystemErrorPolicy.Disable });
        var throwing = isolating.AddSystem<Throwing>();
        isolating.Update();
        Assert.That(throwing.Enabled, Is.False);
        Assert.That(throwing.Error, Is.InstanceOf<InvalidOperationException>());
        isolating.Update();                                                    // off: not run again
        throwing.Enabled = true;                                               // back on: the error is cleared
        Assert.That(throwing.Error, Is.Null);
    }

    public struct InputGroup : ISystemGroup;

    [EcsSystem, UpdateInGroup<InputGroup>]
    internal ref struct InInputGroup { static void OnUpdate(SystemHandle system) => Note(system, "update"); }

    [Test]
    public void GroupsUpdateIndependently()
    {
        using var world = new World();
        world.AddSystem<Tick>();
        world.AddSystem<InInputGroup>();
        world.Update<InputGroup>();
        world.Update<InputGroup>();
        world.Update();

        Assert.That(Log.FindAll(l => l.EndsWith(".update")), Is.EqualTo(new[] { "InInputGroup.update", "InInputGroup.update", "Tick.update" }));
        Assert.That(world.DefaultUpdateCount, Is.EqualTo(1), "DefaultUpdateCount counts the default group");
        Assert.That((world.UpdateCount<InputGroup>(), world.UpdateCount<EmptyGroup>()), Is.EqualTo((2L, 0L)));
        Assert.That(world.TryGetSystem<InInputGroup>(out var input) && input.World == world, Is.True);
        Assert.That(world.RemoveSystem<InInputGroup>(), Is.True);
        world.Update<InputGroup>();
        Assert.That(Log.FindAll(l => l == "InInputGroup.update"), Has.Count.EqualTo(2));
    }

    private struct EmptyGroup : ISystemGroup;

    [Test]
    public void AGroupsSystemSkipsTheDefaultUpdateAndAnUnusedGroupIsANoOp()
    {
        using var world = new World();
        world.AddSystem<InInputGroup>();
        world.Update();
        Assert.That(Log, Has.No.Member("InInputGroup.update"));
        world.Update<InputGroup>();
        Assert.That(Log, Has.Member("InInputGroup.update"));
        Assert.DoesNotThrow(() => world.Update<EmptyGroup>());
    }

    [EcsSystem]
    internal ref struct FailsEverywhere
    {
        static void OnUpdate(SystemHandle system) => throw new InvalidOperationException(system.Name);
        static void OnDestroy(SystemHandle system) => throw new InvalidOperationException(system.Name + ".destroy");
    }

    [Test]
    public void AFailedRunStillCountsAsRun()
    {
        using var world = new World(new WorldOptions { SystemErrors = SystemErrorPolicy.Disable });
        var failing = world.AddSystem<FailsEverywhere>();
        world.Update();
        Assert.That(failing.Error.Message, Is.EqualTo("FailsEverywhere"));
        Assert.That(failing.LastRunTick, Is.Positive);                          // its changes are not delivered again
        Assert.That(failing.RunCount, Is.EqualTo(1));
        Assert.DoesNotThrow(world.Dispose, "under Disable, an OnDestroy error does not escape Dispose");
    }

    [Test]
    public void AThrowingOnDestroyStillDestroysTheOtherSystemsAndReleasesTheWorld()
    {
        var world = new World();
        world.AddSystem<Tick>();
        world.AddSystem<FailsEverywhere>();
        var entity = world.CreateEntity(new Position());
        var error = Assert.Throws<InvalidOperationException>(world.Dispose);
        Assert.That(error.Message, Is.EqualTo("FailsEverywhere.destroy"));      // rethrown after the rest ran
        Assert.That(Log, Has.Member("Tick.destroy"));
        Assert.That(entity.IsAlive, Is.False);                                   // the storage was released
        Assert.DoesNotThrow(world.Dispose);                                      // once
        Assert.Throws<ObjectDisposedException>(() => world.CreateEntity(new Position()));
        Assert.Throws<ObjectDisposedException>(() => world.GetOrAddSingleton<Clock>());
    }

    [Test]
    public void AThrowingOnDestroyOfARemovedSystemStillRemovesIt()
    {
        using var world = new World();
        var failing = world.AddSystem<FailsEverywhere>();
        Assert.Throws<InvalidOperationException>(() => world.RemoveSystem<FailsEverywhere>());
        Assert.That((world.HasSystem<FailsEverywhere>(), failing.IsAlive, world.Systems.Count), Is.EqualTo((false, false, 0)));
    }

    // ---------------- SystemStats: allocated bytes and visited entities ----------------

    [EcsSystem]
    internal ref struct Moves
    {
        [ForEach]
        private static void Move(ref Position position, in Velocity velocity) => position.X += velocity.X;
    }

#pragma warning disable EECS028 // the allocation the stats measure
    [EcsSystem]
    internal ref struct Renames
    {
        [ForEach]
        private static void Rename(ref Name name) => name.Value = new string('x', 1000);
    }

    [EcsSystem]
    internal ref struct RenamesInParallel
    {
        [ForEach(Parallel = true)]
        private static void Rename(ref Name name) => name.Value = new string('x', 64);
    }
#pragma warning restore EECS028

    [EcsSystem]
    internal ref struct SeesArmor
    {
        [ForEach, Changed<Armor>]
        private static void See(ref Seen seen) => seen.Count++;
    }

    [Test]
    public void StatsCountTheBytesARunAllocatesAndTheEntitiesItVisits()
    {
        using var world = new World(new WorldOptions { MeasureSystems = true });
        for (int i = 0; i < 10; i++) world.CreateEntity(new Position(0, 0), new Velocity(1, 0));
        for (int i = 0; i < 5; i++) world.CreateEntity(new Position(0, 0));                  // no Velocity: not visited
        for (int i = 0; i < 4; i++) world.CreateEntity(new Name("a"));
        var moves = world.AddSystem<Moves>();
        var renames = world.AddSystem<Renames>();
        for (int i = 0; i < 3; i++) world.Update();

        Assert.That(moves.Stats.LastEntities, Is.EqualTo(10));
        Assert.That(moves.Stats.TotalEntities, Is.EqualTo(30));
        Assert.That(moves.Stats.LastAllocated, Is.Zero);                                     // warmed up: queries cached
        Assert.That(renames.Stats.LastEntities, Is.EqualTo(4));
        Assert.That(renames.Stats.LastAllocated, Is.GreaterThanOrEqualTo(4 * 2000));         // four 1,000-char strings
        Assert.That(renames.Stats.TotalAllocated, Is.GreaterThanOrEqualTo(3 * 4 * 2000));
        Assert.That(renames.Stats.TotalAllocated, Is.GreaterThanOrEqualTo(renames.Stats.LastAllocated * 3));
    }

    [Test]
    public void StatsCountOnlyTheChangedEntitiesAChangedMethodVisits()
    {
        using var world = new World(new WorldOptions { MeasureSystems = true });
        var entities = new Entity[10];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity(new Armor(1), new Seen(0));
        var sees = world.AddSystem<SeesArmor>();
        world.Update();
        Assert.That(sees.Stats.LastEntities, Is.EqualTo(10));                               // all written since the start
        for (int i = 0; i < 3; i++) entities[i].Set(new Armor(2));
        world.Update();
        Assert.That(sees.Stats.LastEntities, Is.EqualTo(3));
        world.Update();
        Assert.That(sees.Stats.LastEntities, Is.Zero);
        Assert.That(sees.Stats.TotalEntities, Is.EqualTo(13));
    }

    [Test]
    public void StatsOfAParallelForEachSumTheBytesOfEveryThread()
    {
        using var world = new World(new WorldOptions { MeasureSystems = true, MaxDegreeOfParallelism = 4 });
        const int count = 40_000;
        world.CreateEntities(new Entity[count], new Name(""));
        var renames = world.AddSystem<RenamesInParallel>();
        for (int i = 0; i < 4; i++) world.Update();
        Assert.That(renames.Stats.LastEntities, Is.EqualTo(count));
        // A 64-char string is at least 128 bytes of characters, whichever thread ran its chunk.
        Assert.That(renames.Stats.LastAllocated, Is.GreaterThanOrEqualTo(count * 128L));
        Assert.That(renames.Stats.TotalAllocated, Is.GreaterThanOrEqualTo(4 * count * 128L));
    }

    [Test]
    public void StatsOfSystemsInAParallelStageAreTheirOwn()
    {
        using var world = new World(new WorldOptions { MeasureSystems = true, ParallelSystems = true, MaxDegreeOfParallelism = 4 });
        for (int i = 0; i < 6; i++) world.CreateEntity(new Position(0, 0), new Velocity(1, 0));
        for (int i = 0; i < 3; i++) world.CreateEntity(new Name(""));
        var moves = world.AddSystem<Moves>();
        var renames = world.AddSystem<Renames>();
        Assert.That(world.DescribeSchedule().Groups[0].Stages, Has.Length.EqualTo(1));       // one stage: no conflict
        for (int i = 0; i < 3; i++) world.Update();
        Assert.That((moves.Stats.Runs, renames.Stats.Runs), Is.EqualTo((3L, 3L)));
        Assert.That((moves.Stats.LastEntities, renames.Stats.LastEntities), Is.EqualTo((6L, 3L)));
        Assert.That(moves.Stats.LastAllocated, Is.Zero);                                     // the other one's bytes are not its own
        Assert.That(renames.Stats.LastAllocated, Is.GreaterThanOrEqualTo(3 * 2000));
    }

    [Test]
    public void StatsStayEmptyWithoutMeasureSystems()
    {
        using var world = new World();
        for (int i = 0; i < 4; i++) world.CreateEntity(new Name("a"));
        var renames = world.AddSystem<Renames>();
        world.Update();
        Assert.That((renames.Stats.Runs, renames.Stats.TotalEntities, renames.Stats.TotalAllocated), Is.EqualTo((0L, 0L, 0L)));
    }
}
