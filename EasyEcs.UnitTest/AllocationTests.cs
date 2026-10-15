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
        world.AddSystem<ChangedTests.Wear>();
        world.AddSystem<ChangedTests.ArmorWatch>();
        world.CreateEntities(new Entity[100], new Armor(1_000_000), new Seen(), new Stun(1)); // tracked changes
        var probe = world.CreateEntity(new Health(1));
        var callsigns = world.Index<Callsign, string>();
        var named = world.CreateEntity(new Callsign("a"));

        void Frame()
        {
            world.Update();
            foreach (var item in world.Query<Position, Velocity>()) item.Component1.X += item.Component2.X;
            _ = world.Count<Position, Velocity>(QueryFilter.Create());
            _ = world.Count<Armor>(QueryFilter.Create().Changed<Armor>().ChangedSince(world.ChangeTick));
            named.Get<Callsign>() = new Callsign(named.Get<Callsign>().Value == "a" ? "b" : "a"); // re-keyed every frame
            _ = callsigns.Count("a") + callsigns.Count("b");
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
