using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SystemTests
{
    private static readonly List<string> Log = [];

    private class Logged(string name, int priority = 0, int interval = 1) : EcsSystem
    {
        public override int Priority => priority;
        public override int UpdateInterval => interval;
        protected override void OnCreate() => Log.Add($"{name}.create");
        protected override void OnUpdate() => Log.Add($"{name}.update");
        protected override void OnDestroy() => Log.Add($"{name}.destroy");
    }

    [SetUp]
    public void SetUp() => Log.Clear();

    [Test]
    public void LifecycleAndPriorityOrder()
    {
        var world = new World();
        world.AddSystem(new Logged("late", priority: 10));
        world.AddSystem(new Logged("early", priority: -1));
        world.AddSystem(new Logged("mid"));
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
    public void ErrorsPropagateUnlessHandled()
    {
        using var world = new World();
        world.AddSystem<Throwing>();
        Assert.Throws<InvalidOperationException>(world.Update);

        var errors = new List<Exception>();
        world.SystemError += (_, e) => errors.Add(e);
        world.Update();
        Assert.That(errors, Has.Count.EqualTo(1));
    }

    [Test]
    public void ParallelBucketsRunEverySystem()
    {
        using var world = new World(new WorldOptions { Parallel = true });
        var updates = new int[3];
        for (int i = 0; i < updates.Length; i++) world.AddSystem(new Counting(updates, i));
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(updates, Is.All.EqualTo(10));
    }

    /// <summary>Systems of one parallel bucket must write disjoint data: each owns one slot.</summary>
    private sealed class Counting(int[] updates, int slot) : EcsSystem
    {
        protected override void OnUpdate() => updates[slot]++;
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
}
