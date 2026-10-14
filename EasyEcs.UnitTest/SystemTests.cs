using System;
using System.Collections.Generic;
using System.Threading;
using EasyEcs.Core;
using EasyEcs.Core.Systems;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SystemTests
{
    private static readonly List<string> Log = new();

    private abstract class LoggingSystem : SystemBase
    {
        protected override void OnCreate() => Log.Add(GetType().Name + ".Create");
        protected override void OnUpdate() => Log.Add(GetType().Name + ".Update");
        protected override void OnDestroy() => Log.Add(GetType().Name + ".Destroy");
    }

    private sealed class Late : LoggingSystem
    {
        public override int Priority => 10;
    }

    private sealed class EarlyA : LoggingSystem
    {
        public override int Priority => -1;
    }

    private sealed class EarlyB : LoggingSystem
    {
        public override int Priority => -1;
    }

    private sealed class EveryThird : SystemBase
    {
        public int Runs;
        public override int UpdateInterval => 3;
        protected override void OnUpdate() => Runs++;
    }

    private sealed class Throwing : SystemBase
    {
        protected override void OnUpdate() => throw new InvalidOperationException("boom");
    }

    private sealed class Counting : SystemBase
    {
        public int Runs;
        public int ThreadId;

        protected override void OnUpdate()
        {
            Interlocked.Increment(ref Runs);
            ThreadId = Environment.CurrentManagedThreadId;
        }
    }

    [SetUp]
    public void Reset() => Log.Clear();

    [Test]
    public void LifecycleAndPriorityOrder()
    {
        var world = new World();
        world.AddSystem<Late>();
        world.AddSystem<EarlyA>();
        world.AddSystem<EarlyB>();
        Assert.That(Log, Is.EqualTo(new[] { "Late.Create", "EarlyA.Create", "EarlyB.Create" }), "OnCreate runs on add");

        Log.Clear();
        world.Update();
        Assert.That(Log, Is.EqualTo(new[] { "EarlyA.Update", "EarlyB.Update", "Late.Update" }),
            "ascending priority, registration order within a priority");

        Log.Clear();
        world.Dispose();
        Assert.That(Log, Is.EqualTo(new[] { "Late.Destroy", "EarlyB.Destroy", "EarlyA.Destroy" }), "reverse order on dispose");
        Assert.Throws<ObjectDisposedException>(() => world.Update());
    }

    [Test]
    public void UpdateIntervalEnabledAndRemove()
    {
        using var world = new World();
        var system = world.AddSystem<EveryThird>();
        for (int i = 0; i < 9; i++) world.Update();
        Assert.That(system.Runs, Is.EqualTo(3));

        system.Enabled = false;
        for (int i = 0; i < 9; i++) world.Update();
        Assert.That(system.Runs, Is.EqualTo(3));

        Assert.That(world.GetSystem<EveryThird>(), Is.SameAs(system));
        Assert.That(world.RemoveSystem<EveryThird>(), Is.True);
        Assert.That(world.GetSystem<EveryThird>(), Is.Null);
        Assert.That(system.World, Is.Null);
        Assert.That(world.RemoveSystem<EveryThird>(), Is.False);
    }

    [Test]
    public void SystemCannotJoinTwoWorlds()
    {
        using var a = new World();
        using var b = new World();
        var system = a.AddSystem<Counting>();
        Assert.Throws<InvalidOperationException>(() => b.AddSystem(system));
    }

    [Test]
    public void ErrorsPropagateWithoutHandlerAndAreReportedWithOne()
    {
        using var world = new World();
        var after = world.AddSystem<Counting>();
        world.AddSystem<Throwing>();
        Assert.Throws<InvalidOperationException>(() => world.Update());

        SystemBase failed = null;
        world.SystemError += (system, _) => failed = system;
        world.Update();
        Assert.That(failed, Is.InstanceOf<Throwing>());
        Assert.That(after.Runs, Is.EqualTo(2), "other systems still run");
    }

    [Test]
    public void ParallelBucketRunsEverySystem()
    {
        using var world = new World(new WorldOptions { Parallel = true });
        var systems = new[] { new Counting(), new Counting(), new Counting(), new Counting() };
        foreach (var s in systems) world.AddSystem(s);
        for (int i = 0; i < 10; i++) world.Update();
        foreach (var s in systems) Assert.That(s.Runs, Is.EqualTo(10));
        Assert.That(world.UpdateCount, Is.EqualTo(10));
    }
}
