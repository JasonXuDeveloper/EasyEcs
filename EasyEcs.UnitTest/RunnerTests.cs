using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [ForEach] methods outside systems: the generator writes an entry point per method that takes the world and the
/// caller's values, for tools, tests and one-off passes that need no system.
/// </summary>
public static class Reports
{
    [ForEach, Without<Dead>]
    private static void CountAlive(in Health hp, ref int alive) => alive++;

    [ForEach]
    private static void Heal(ref Health hp, int amount) => hp.Value += amount;

    [ForEach]
    private static void Floor(ref Health hp, in int floor) => hp.Value = Math.Max(hp.Value, floor);  // in: a readonly reference

    [ForEach]
    private static void Tick(ref Seen seen, ref Clock clock) => seen.Count = (int)clock.Tick++;

    [ForEach, Changed<Armor>]
    private static void Changed(Entity entity, ref int seen) => seen++;

    [ForEach]
    private static void Total(in Health hp, Maybe<Armor> armor, ref int total) => total += hp.Value + (armor.HasValue ? armor.Value.Value : 0);

    [ForEach]
    private static void Sum(ReadOnlySpan<Health> hp, ref int total)
    {
        for (int i = 0; i < hp.Length; i++) total += hp[i].Value;
    }

    [ForEach(Parallel = true)]
    private static void Scale(ref Health hp, int factor) => hp.Value *= factor;

    [ForEach]
    private static void Kill(Entity entity, in Health hp, int below)
    {
        if (hp.Value < below) entity.SetDead();
    }
}

public class Tools                  // an instance class may hold runners too
{
    [ForEach]
    private static void Count(in Health hp, ref int count) => count++;
}

public class RunnerTests
{
    private World _world = null!;

    [SetUp]
    public void SetUp() => _world = new World();

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void TheEntryPointRunsTheMethodWithTheCallersValues()
    {
        _world.CreateEntity(new Health(10));
        _world.CreateEntity(new Health(20), new Armor(5));
        _world.CreateEntity(new Health(30), new Dead());

        int alive = 0;
        Reports.CountAlive(_world, ref alive);
        Assert.That(alive, Is.EqualTo(2), "a ref value is the caller's variable; filters apply");

        Reports.Heal(_world, 1);
        Reports.Floor(_world, 15);
        int total = 0;
        Reports.Total(_world, ref total);
        Assert.That(total, Is.EqualTo(15 + 21 + 31 + 5), "by value and in; Maybe for the optional component");

        total = 0;
        Reports.Sum(_world, ref total);
        Assert.That(total, Is.EqualTo(15 + 21 + 31), "spans");

        int count = 0;
        Tools.Count(_world, ref count);
        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public void SingletonsResolveOncePerRun()
    {
        _world.SetClock(7);
        for (int i = 0; i < 3; i++) _world.CreateEntity(new Seen());
        Reports.Tick(_world);
        Assert.That(_world.Clock.Tick, Is.EqualTo(10));
    }

    [Test]
    public void ChangedFiltersTakeTheTick()
    {
        var a = _world.CreateEntity(new Armor(1));
        _world.CreateEntity(new Armor(2));
        _world.Update();
        ulong since = _world.ChangeTick;
        a.SetArmor(3);

        int seen = 0;
        Reports.Changed(_world, since, ref seen);
        Assert.That(seen, Is.EqualTo(1));
        seen = 0;
        Reports.Changed(_world, 0, ref seen);
        Assert.That(seen, Is.EqualTo(2));
    }

    [Test]
    public void ParallelAndStructuralRunners()
    {
        for (int i = 0; i < 10_000; i++) _world.CreateEntity(new Health(i));
        Reports.Scale(_world, 2);
        Reports.Kill(_world, 100);
        Assert.That(_world.Count<Dead>(), Is.EqualTo(50));
    }

    [Test]
    public void RunnersAllocateNothing()
    {
        for (int i = 0; i < 100; i++) _world.CreateEntity(new Health(i));
        int alive = 0;
        Reports.CountAlive(_world, ref alive);
        Reports.Heal(_world, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            Reports.CountAlive(_world, ref alive);
            Reports.Heal(_world, 1);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
