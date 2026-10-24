using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Structural changes belong to the world's owner thread: one made while the world runs work on several threads throws
/// instead of tearing the tables. The cold paths those threads reach on their own (a query's first match, a missing
/// singleton) stay safe under contention.
/// </summary>
public class ConcurrencyTests
{
#pragma warning disable EECS017 // the handle is the point: a way to reach the world from a parallel method
    public record struct Spawner(Entity Owner) : IComponent;
#pragma warning restore EECS017

    /// <summary>Changes structure through a handle a component holds: wrong in a system that shares a parallel stage.</summary>
    [EcsSystem]
    public ref struct SpawnInStage
    {
#pragma warning disable EECS013 // the misuse under test: the runtime must catch what the analyzer reports
        [ForEach]
        static void Spawn(in Spawner spawner) => spawner.Owner.Set(new Marked());
#pragma warning restore EECS013
    }

    [EcsSystem]
    public ref struct Heal
    {
        [ForEach]
        static void Run(ref Health health) => health.Value++;
    }

    private struct SpawnFromChunk(World world) : IForEach<Health>, INoStructuralChanges
    {
        public void Execute(ref Health health) => world.CreateEntity(); // breaks the promise INoStructuralChanges makes
    }

    [Test]
    public void StructuralChangeInAParallelStageThrowsAndLeavesTheWorldUsable()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        var owner = world.CreateEntity(new Health(0));
        world.CreateEntity(new Spawner(owner));
        world.AddSystem<SpawnInStage>();
        world.AddSystem<Heal>();

        var error = Assert.Catch(() => world.Update());
        Assert.That(Flatten(error), Is.InstanceOf<InvalidOperationException>());
        Assert.That(owner.Has<Marked>(), Is.False);

        world.RemoveSystem<SpawnInStage>();
        world.Update();
        owner.Set(new Marked()); // the gate is open again
        Assert.That(owner.Get<Health>().Value, Is.EqualTo(2)); // Heal ran in both updates
    }

    [Test]
    public void StructuralChangeInAParallelForEachThrowsAndLeavesTheWorldUsable()
    {
        using var world = new World();
        var entities = new Entity[World.ParallelChunkRows * 2 + 1];
        world.CreateEntities(entities, new Health(0));
        int count = world.EntityCount;

        var job = new SpawnFromChunk(world);
        var error = Assert.Catch(() => world.ForEachParallel<Health, SpawnFromChunk>(ref job));
        Assert.That(Flatten(error), Is.InstanceOf<InvalidOperationException>());
        Assert.That(world.EntityCount, Is.EqualTo(count));

        world.CreateEntity();
        Assert.That(world.EntityCount, Is.EqualTo(count + 1));
    }

    [Test]
    public void FirstQueriesAndSingletonsFromManyThreadsStayConsistent()
    {
        using var world = new World();
        world.CreateEntity(new C1(1), new C2(2), new C3(3));
        world.CreateEntity(new C1(1), new C2(2));
        world.CreateEntity(new C1(1));

        // Every thread races to make the first match of the same queries and to add the same singleton.
        Parallel.For(0, 8, _ =>
        {
            Assert.That(world.Count<C1>(), Is.EqualTo(3));
            Assert.That(world.Count<C1, C2>(), Is.EqualTo(2));
            Assert.That(world.Count<C1, C2, C3>(), Is.EqualTo(1));
            world.GetOrAddSingleton<Clock>();
        });

        Assert.That(world.HasSingleton<Clock>(), Is.True);
    }

    // The memo of filters per call signature is published lock-free (CAS copy-on-write) and reset by the owner
    // (RemoveTypes, Dispose): threads that make first matches of new filters while it is reset again and again still
    // get every matching table, through the memo or the query cache, and the memo serves them after.
    [Test]
    public void QueriesFromManyThreadsStayRightWhileTheMemoIsReset()
    {
        using var world = new World();
        world.CreateEntity(new C1(1));
        world.CreateEntity(new C1(1), new C2(2));
        world.CreateEntity(new C1(1), new C2(2), new C3(3));
        // More filters than a signature's ways: some take the memo, the others the query cache.
        var filters = new[]
        {
            QueryFilter.Create(), QueryFilter.Create().With<C2>(), QueryFilter.Create().Without<C2>(), QueryFilter.Create().With<C3>(),
            QueryFilter.Create().Without<C3>(), QueryFilter.Create().With<C2>().Without<C3>(),
        };
        int[] expected = [3, 2, 1, 1, 2, 1];
        int[] pairs = [2, 2, 0, 1, 1, 1];  // Count<C1, C2> under the same filters

        int running = 4;
        var readers = new Task[running];
        for (int t = 0; t < readers.Length; t++)
        {
            int seed = t;
            readers[t] = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++)
                    {
                        int f = (i + seed) % filters.Length;
                        if (world.Count<C1>(filters[f]) != expected[f]) throw new InvalidOperationException($"Count<C1> of filter {f}");
                        if (world.Count<C1, C2>(filters[f]) != pairs[f]) throw new InvalidOperationException($"Count<C1, C2> of filter {f}");
                    }
                }
                finally { Interlocked.Decrement(ref running); }
            });
        }

        int resets = 0;
        while (Volatile.Read(ref running) > 0)
        {
            world.ResetQueryMemo();
            resets++;
        }

        Task.WaitAll(readers);
        Assert.That(resets, Is.GreaterThan(0));
        for (int f = 0; f < filters.Length; f++)
            Assert.That((world.Count<C1>(filters[f]), world.Count<C1, C2>(filters[f])), Is.EqualTo((expected[f], pairs[f])), $"filter {f}");
    }

    private static Exception Flatten(Exception error) =>
        error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;

    private sealed class Held
    {
        public SpinGate Gate;
        public volatile bool Entered, Released;
    }

    [Test]
    public void AThreadThatWaitsOnAContendedGateAllocatesNothing()
    {
        // The helpers' queue and the pools a parallel pass rents from are entered by several threads at once. A Lock
        // creates its wait event the first time a thread waits on it past its spin (56 B on that thread, any time after
        // warm-up when a holder is preempted); the gate spins and yields instead, and still lets one thread in at a time.
        var held = new Held();
        Allocations.Baseline(); // no background collection runs into the measurement below
        var holder = new Thread(() =>
        {
            using (new SpinScope(ref held.Gate))
            {
                held.Entered = true;
                Thread.Sleep(50); // long enough for the waiter to stop spinning
                held.Released = true;
            }
        });
        holder.Start();
        while (!held.Entered) Thread.Yield();
        bool releasedFirst;
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (new SpinScope(ref held.Gate)) releasedFirst = held.Released;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        holder.Join();
        Assert.That((allocated, releasedFirst), Is.EqualTo((0L, true)));
    }
}
