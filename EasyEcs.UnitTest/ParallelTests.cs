using System;
using System.Threading;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="WorldOptions.Parallel"/>: systems of one priority run together exactly when their declared access
/// (generated from the [ForEach] parameters) does not conflict.
/// </summary>
public partial class ParallelTests
{
    // Each system waits at the rendezvous: it reports Met only if another system arrived while it was running.
    public record struct Rendezvous(Barrier Barrier, int TimeoutMs) : ISingletonComponent;
    public record struct Left(bool Met) : IComponent;
    public record struct Right(bool Met) : IComponent;
    public record struct Counter(int N) : IComponent;
    public record struct Seen(int N) : IComponent;
    public record struct Fault(bool Throw) : IComponent;

    public partial class WriteLeft : EcsSystem
    {
        [ForEach]
        static void Meet(ref Left left, in Rendezvous rendezvous) =>
            left.Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
    }

    public partial class WriteRight : EcsSystem
    {
        [ForEach]
        static void Meet(ref Right right, in Rendezvous rendezvous) =>
            right.Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
    }

    /// <summary>An Entity parameter can reach any component (and make structural changes): it runs alone.</summary>
    public partial class WriteRightThroughEntity : EcsSystem
    {
        [ForEach, With<Left>]
        static void Meet(Entity entity, in Rendezvous rendezvous) =>
            entity.Get<Right>().Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
    }

    public partial class Count : EcsSystem
    {
        [ForEach]
        static void Increment(ref Counter counter) => counter.N++;
    }

    public partial class Observe : EcsSystem
    {
        [ForEach]
        static void Copy(in Counter counter, ref Seen seen) => seen.N = counter.N;
    }

    public partial class Throwing : EcsSystem
    {
        [ForEach]
        static void Fail(in Fault fault)
        {
            if (fault.Throw) throw new InvalidOperationException("boom");
        }
    }

    private static World ParallelWorld(int timeoutMs)
    {
        var world = new World(new WorldOptions { Parallel = true, MaxDegreeOfParallelism = 2 });
        world.AddSingleton<Rendezvous>() = new Rendezvous(new Barrier(2), timeoutMs);
        world.CreateEntity(new Left(), new Right());
        return world;
    }

    [Test]
    public void SystemsWithDisjointWritesRunTogether()
    {
        using var world = ParallelWorld(timeoutMs: 10_000);
        world.AddSystem<WriteLeft>();
        world.AddSystem<WriteRight>();
        world.Update();

        foreach (var item in world.Query<Left, Right>())
            Assert.That((item.Component1.Met, item.Component2.Met), Is.EqualTo((true, true)), "both were running at once");
    }

    [Test]
    public void SystemsThatReachTheWorldRunAlone()
    {
        using var world = ParallelWorld(timeoutMs: 100);
        world.AddSystem<WriteLeft>();
        world.AddSystem<WriteRightThroughEntity>();
        world.Update();

        foreach (var item in world.Query<Left, Right>())
            Assert.That((item.Component1.Met, item.Component2.Met), Is.EqualTo((false, false)), "never at the same time");
    }

    [Test]
    public void ConflictingSystemsKeepRegistrationOrder()
    {
        using var world = new World(new WorldOptions { Parallel = true });
        for (int i = 0; i < 64; i++) world.CreateEntity(new Counter(), new Seen());
        world.AddSystem<Count>();
        world.AddSystem<Observe>(); // reads what Count writes: runs after it, every update

        for (int update = 1; update <= 200; update++)
        {
            world.Update();
            foreach (var item in world.Query<Counter, Seen>())
                Assert.That(item.Component2.N, Is.EqualTo(update));
        }
    }

    [Test]
    public void ErrorsOnHelperThreadsReachTheCaller()
    {
        using var world = ParallelWorld(timeoutMs: 0);
        world.CreateEntity(new Fault(true));
        world.AddSystem<WriteLeft>();
        world.AddSystem<Throwing>();
        Assert.Throws<InvalidOperationException>(world.Update);
        Assert.DoesNotThrow(() =>
        {
            foreach (var item in world.Query<Fault>()) item.Component1.Throw = false;
            world.Update();
        }, "the next update runs normally");
    }

    [Test]
    public void ParallelUpdatesDoNotAllocate()
    {
        using var world = new World(new WorldOptions { Parallel = true });
        for (int i = 0; i < 1_000; i++) world.CreateEntity(new Position(i, i), new Velocity(1, 1), new Counter());
        world.AddSystem<Count>();
        world.AddSystem<Movement>();

        // Warm-up: helpers, JIT tiers, and the thread pool's own worker threads (started from the queuing thread).
        for (int i = 0; i < 500; i++) world.Update();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) world.Update();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
