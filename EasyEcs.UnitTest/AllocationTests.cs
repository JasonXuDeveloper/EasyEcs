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
        var churned = new Entity[50];

        void Frame()
        {
            world.Update();
            foreach (var item in world.Query<Position, Velocity>()) item.Component1.X += item.Component2.X;
            _ = world.Count<Position, Velocity>(QueryFilter.Create());
            _ = world.Count<Armor>(QueryFilter.Create().Changed<Armor>(world.ChangeTick));
            named.Get<Callsign>() = new Callsign(named.Get<Callsign>().Value == "a" ? "b" : "a"); // re-keyed every frame
            _ = callsigns.Count("a") + callsigns.Count("b");
            probe.Get<Health>().Value++;
            probe.Remove<Frozen>();
            probe.Set(new Frozen()); // archetype transitions on cached edges
            for (int i = 0; i < churned.Length; i++) churned[i] = world.CreateEntity(new Position(), new Health(1));
            foreach (var e in churned) e.Destroy(); // recycled ids and rows
            world.CreateEntities(churned, new Churned(), new Health(1));
            world.DestroyEntities<Churned>();
        }

        for (int i = 0; i < 10; i++) Frame(); // warm-up: query caches, edges, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Frame();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void IndexLookupsChangedQueriesAndIndexedChurnDoNotAllocate()
    {
        using var world = new World();
        var children = world.Index<ChildOf, Entity>();
        var callsigns = world.Index<Callsign, string>();
        var parents = new Entity[8];
        world.CreateEntities(parents, new Health(1));
        var kids = new Entity[64];
        for (int i = 0; i < kids.Length; i++)
            kids[i] = world.CreateEntity(new ChildOf(parents[i % parents.Length]), new Callsign(i % 2 == 0 ? "even" : "odd"));
        var armored = new Entity[10_000];
        world.CreateEntities(armored, new Armor(1));
        int next = 0;

        void Frame()
        {
            // Churn: the oldest kid dies, a new one takes its place (ids reused, lists unlinked and relinked).
            int k = next++ % kids.Length;
            kids[k].Destroy();
            kids[k] = world.CreateEntity(new ChildOf(parents[k % parents.Length]), new Callsign(k % 2 == 0 ? "even" : "odd"));
            kids[(k + 1) % kids.Length].Remove<Callsign>();
            kids[(k + 1) % kids.Length].Set(new Callsign("odd"));
            kids[(k + 2) % kids.Length].Get<ChildOf>() = new ChildOf(parents[next % parents.Length]); // re-parent

            int found = 0;
            foreach (var parent in parents)
                foreach (var _ in children.Find(parent)) found++;
            _ = callsigns.TryFind("even", out _) && found == kids.Length;
            _ = callsigns.Count("odd");

            ulong mark = world.ChangeTick;
            armored[next % armored.Length].Get<Armor>().Value++;
            var changed = QueryFilter.Create().Changed<Armor>(mark);
            foreach (var item in world.Query<Armor>(changed)) item.Component1.Value++;
            _ = world.Any<Armor>(changed);
        }

        for (int i = 0; i < 200; i++) Frame(); // warm-up: every kid replaced, query caches, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) Frame();
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

    private struct Churned : IComponent;
}
