using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>The steady-state hot paths allocate nothing (performance numbers come from EasyEcs.Benchmark).</summary>
public class AllocationTests
{
    [Test]
    public void UpdatesQueriesAndStructuralChangesDoNotAllocate()
    {
        using var world = new World();
        for (int i = 0; i < 1_000; i++)
        {
            var e = world.CreateEntity(new Position(i, i), new Velocity(1, 1), new Health(1_000_000));
            if (i % 3 == 0) e.Set(new Frozen());
        }

        world.AddSystem<Movement>();
        world.AddSystem<Damage>();
        var probe = world.CreateEntity(new Health(1));

        void Frame()
        {
            world.Update();
            foreach (var item in world.Query<Position, Velocity>()) item.Component1.X += item.Component2.X;
            _ = world.Count<Position, Velocity>(QueryFilter.Create());
            probe.Get<Health>().Value++;
            probe.Remove<Frozen>();
            probe.Set(new Frozen()); // archetype transitions on cached edges
        }

        for (int i = 0; i < 10; i++) Frame(); // warm-up: query caches, edges, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Frame();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void ParallelForEachDoesNotAllocateAfterItsFirstCall()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[World.ChunkRows * 4], new ParallelTests.Counter(0), new ParallelTests.Seen(1));
        world.AddSystem<ParallelTests.ChunkedIncrement>();
        for (int i = 0; i < 10; i++) world.Update();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) world.Update();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
