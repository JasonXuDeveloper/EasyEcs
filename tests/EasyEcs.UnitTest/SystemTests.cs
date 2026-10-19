using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SystemTests
{
    private static readonly List<string> Log = [];

#pragma warning disable EECS018, EECS028 // one test system, many schedules: each instance's values never change; its log is the test's record
    private class Logged(string name, int interval = 1, int phase = 0) : EcsSystem
    {
        public override int UpdateInterval => interval;
        public override int UpdatePhase => phase;
        protected string Name => name;
        protected override void OnCreate() => Log.Add($"{name}.create");
        protected override void OnUpdate() => Log.Add($"{name}.update");
        protected override void OnDestroy() => Log.Add($"{name}.destroy");
    }
#pragma warning restore EECS018, EECS028

    [Test]
    public void PhasesSpreadSystemsOfOneInterval()
    {
        using var world = new World();
        world.AddSystem(new Logged("a", interval: 3));
        world.AddSystem(new Logged("b", interval: 3, phase: 1));
        world.AddSystem(new Logged("c", interval: 3, phase: 2));
        Log.Clear();
        for (int i = 0; i < 6; i++) world.Update();                           // updates 1..6
        Assert.That(Log, Is.EqualTo(new[] { "b.update", "c.update", "a.update", "b.update", "c.update", "a.update" }));
    }

    [SetUp]
    public void SetUp() => Log.Clear();

    [Test]
    public void SystemsListsEveryGroupWithoutAllocating()
    {
        using var world = new World();
        world.AddSystem(new InInputGroup());
        world.AddSystem(new Logged("tick"));
        var names = new List<string>();
        foreach (var system in world.Systems) names.Add(system.GetType().Name);
        Assert.That(names, Is.EqualTo(new[] { nameof(Logged), nameof(InInputGroup) }));   // default group first
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
        var a = new Logged("a", interval: 2);
        measured.AddSystem(a);
        for (int i = 0; i < 4; i++) measured.Update();
        Assert.That(a.Stats.Runs, Is.EqualTo(2));
        Assert.That(a.Stats.Total, Is.GreaterThanOrEqualTo(a.Stats.Last));
        Assert.That(a.Stats.Average, Is.GreaterThan(System.TimeSpan.Zero));

        using var plain = new World();
        var b = new Logged("b");
        plain.AddSystem(b);
        plain.Update();
        Assert.That(b.Stats.Runs, Is.Zero);
    }

    [UpdateAfter<Mid>] private sealed class Late() : Logged("late");
    [UpdateBefore<Mid>] private sealed class Early() : Logged("early");
    private sealed class Mid() : Logged("mid");

#pragma warning disable EECS019 // the cycle under test
    [UpdateBefore<CycleB>] private sealed class CycleA() : Logged("a");
    [UpdateBefore<CycleA>] private sealed class CycleB() : Logged("b");
#pragma warning restore EECS019

    [Test]
    public void ACycleOfOrderingsIsRefusedAndTheSystemStaysOut()
    {
        using var world = new World();
        world.AddSystem(new CycleA());
        var b = new CycleB();
        var error = Assert.Throws<InvalidOperationException>(() => world.AddSystem(b));
        Assert.That(error!.Message, Does.Contain("CycleA").And.Contain("CycleB"));
        Assert.That(b.World, Is.Null);
        Assert.That(world.GetSystem<CycleB>(), Is.Null);
        Assert.That(world.Systems.Count, Is.EqualTo(1));
        Assert.That(Log, Is.EqualTo(new[] { "a.create" }));                   // OnCreate only for the system that joined
    }

    [Test]
    public void AnOrderingMovesOnlyItsOwnSystem()
    {
        using var world = new World();
        world.AddSystem(new Late());                                          // after Mid
        world.AddSystem(new Logged("x"));
        world.AddSystem(new Mid());
        world.AddSystem(new Logged("y"));
        Log.Clear();
        world.Update();
        Assert.That(Log, Is.EqualTo(new[] { "x.update", "mid.update", "late.update", "y.update" }));
    }

    [Test]
    public void LifecycleAndDependencyOrder()
    {
        var world = new World();
        world.AddSystem(new Late());
        world.AddSystem(new Early());
        world.AddSystem(new Mid());
        world.Update();
        world.Dispose();

        Assert.That(Log, Is.EqualTo(new[]
        {
            "late.create", "early.create", "mid.create",
            "early.update", "mid.update", "late.update",
            "late.destroy", "mid.destroy", "early.destroy",
        }));
    }

    [Test]
    public void IntervalEnabledAndRemoval()
    {
        using var world = new World();
        var every3 = new Logged("every3", interval: 3);
        world.AddSystem(every3);
        for (int i = 0; i < 6; i++) world.Update();
        every3.Enabled = false;
        world.Update();
        world.Update();
        world.Update();

        Assert.That(Log.FindAll(l => l == "every3.update"), Has.Count.EqualTo(2));
        Assert.That(world.RemoveSystem<Logged>(), Is.True);
        Assert.That(world.GetSystem<Logged>(), Is.Null);
        Assert.That(Log[^1], Is.EqualTo("every3.destroy"));
        Assert.That(world.UpdateCount, Is.EqualTo(9));
    }

    private sealed class Throwing : EcsSystem
    {
        protected override void OnUpdate() => throw new InvalidOperationException("boom");
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

    [UpdateInGroup<InputGroup>]
    private class InInputGroup() : Logged("input");

    [Test]
    public void GroupsUpdateIndependently()
    {
        using var world = new World();
        world.AddSystem(new Logged("tick"));
        world.AddSystem(new InInputGroup());
        world.Update<InputGroup>();
        world.Update<InputGroup>();
        world.Update();

        Assert.That(Log.FindAll(l => l.EndsWith(".update")), Is.EqualTo(new[] { "input.update", "input.update", "tick.update" }));
        Assert.That(world.UpdateCount, Is.EqualTo(1), "UpdateCount counts the default group");
        Assert.That(world.GetSystem<InInputGroup>(), Is.Not.Null);
        Assert.That(world.RemoveSystem<InInputGroup>(), Is.True);
        world.Update<InputGroup>();
        Assert.That(Log.FindAll(l => l == "input.update"), Has.Count.EqualTo(2));
    }

    [UpdateInGroup<InputGroup>]
    private class InputBase() : Logged("base");

    private class InheritsInput : InputBase;                                    // the group attribute is inherited

    private struct EmptyGroup : ISystemGroup;

    private sealed class FailsEverywhere(string name) : Logged(name)
    {
        protected override void OnUpdate() => throw new InvalidOperationException(Name);
        protected override void OnDestroy() => throw new InvalidOperationException(Name + ".destroy");
    }

    [Test]
    public void GroupMembershipIsInheritedAndAnUnusedGroupIsANoOp()
    {
        using var world = new World();
        world.AddSystem(new InheritsInput());
        world.Update();
        Assert.That(Log, Has.No.Member("base.update"));
        world.Update<InputGroup>();
        Assert.That(Log, Has.Member("base.update"));
        Assert.DoesNotThrow(() => world.Update<EmptyGroup>());
    }

    [Test]
    public void ASystemBelongsToOneWorldAndADisposedWorldDoesNotUpdate()
    {
        var system = new Logged("once");
        var world = new World();
        world.AddSystem(system);
        Assert.Throws<InvalidOperationException>(() => world.AddSystem(system));
        using (var other = new World()) Assert.Throws<InvalidOperationException>(() => other.AddSystem(system));
        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => world.Update());
    }

    [Test]
    public void AFailedRunStillCountsAsRunAndDestroyErrorsAreRecorded()
    {
        var world = new World(new WorldOptions { SystemErrors = SystemErrorPolicy.Disable });
        var failing = new FailsEverywhere("bad");
        world.AddSystem(failing);
        world.Update();
        Assert.That(failing.Error.Message, Is.EqualTo("bad"));
        Assert.That(failing.LastRunTick, Is.Positive);                          // its changes are not delivered again
        world.Dispose();
        Assert.That(failing.Error.Message, Is.EqualTo("bad.destroy"));          // OnDestroy during Dispose
    }
}
