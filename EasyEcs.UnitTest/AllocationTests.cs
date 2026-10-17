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
        var prefab = new Prefab().Set(new Churned()).Set(new Health(1)).Set(new Armor(2));

        void Frame()
        {
            world.Update();
            foreach (var item in world.Query<Position, Velocity>()) item.Component1.X += item.Component2.X;
            _ = world.Count<Position, Velocity>(QueryFilter.Create());
            _ = world.Count<Armor>(QueryFilter.Create().Changed<Armor>(world.ChangeTick));
            named.Set(new Callsign(named.Callsign.Value == "a" ? "b" : "a")); // re-keyed every frame
            _ = callsigns.Count("a") + callsigns.Count("b");
            probe.Get<Health>().Value++;
            probe.Remove<Frozen>();
            probe.Set(new Frozen()); // archetype transitions on cached edges
            for (int i = 0; i < churned.Length; i++) churned[i] = world.CreateEntity(new Position(), new Health(1));
            foreach (var e in churned) e.Destroy(); // recycled ids and rows
            world.CreateEntities(churned, new Churned(), new Health(1));
            world.DestroyEntities<Churned>();
            world.Instantiate(prefab, churned);                               // prefab copies, cached table
            world.DestroyEntities<Churned>();
            probe.Clone().Destroy();                                          // a copy of a live entity
            probe.Set(new Armor(1));
            probe.Remove<Armor>();                                            // a tracked removal
            _ = world.Count<Health>(QueryFilter.Create().Removed<Armor>(world.ChangeTick));
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
        var children = world.Index<ChildOf, EntityId>();
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
            kids[(k + 2) % kids.Length].Set(new ChildOf(parents[next % parents.Length])); // re-parent

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
    public void OrderedWalksRelationsCascadesAndCleanupDoNotAllocate()
    {
        using var world = new World();
        var ratings = world.Index<Rating, int>();
        var parties = world.Relations<MemberOf>();
        world.GroupBy<Zone>();
        var walkers = new Entity[64];
        for (int i = 0; i < walkers.Length; i++) walkers[i] = world.CreateEntity(new Zone(i % 4), new Health(i));
        var players = new Entity[256];
        for (int i = 0; i < players.Length; i++) players[i] = world.CreateEntity(new Rating(i % 32));
        int next = 0;

        void Frame()
        {
            // Ratings move (keys appear and empty), a leaderboard reads the top, a bracket reads a range.
            var player = players[next++ % players.Length];
            player.Set(new Rating((player.Read<Rating>().Points + 37) % 64));
            int seen = 0;
            foreach (var _ in ratings.Descending()) if (++seen == 10) break;
            foreach (var _ in ratings.Range(10, 20)) seen++;

            // Multi-target links: a player joins a party and leaves another, a party lists its members.
            var party = players[next * 7 % players.Length];
            player.AddRelation(new MemberOf(party));
            players[(next + 128) % players.Length].RemoveRelation<MemberOf>(players[(next + 128) * 7 % players.Length]);
            foreach (var _ in parties.Find(party)) seen++;

            // A party disbands while linked and a new one takes its id: both incarnations have links for a while.
            var guild = world.CreateEntity(new Health(3));
            var founder = players[next % players.Length];
            founder.AddRelation(new MemberOf(guild));
            var disbanded = guild.Id;
            guild.Destroy();
            var successor = world.CreateEntity(new Health(3));            // the recycled id
            var joiner = players[(next + 1) % players.Length];
            joiner.AddRelation(new MemberOf(successor));
            founder.RemoveRelation<MemberOf>(disbanded);
            joiner.RemoveRelation<MemberOf>(successor);
            successor.Destroy();

            // Groups: an entity changes zone (a row move between group tables), a zone's rows are walked.
            var walker = walkers[next % walkers.Length];
            walker.Set(new Zone((walker.Read<Zone>().Id + 1) % 4));
            foreach (var item in world.Query<Health>(QueryFilter.Create().InGroup<Zone>(next % 4))) seen += item.Component1.Value;

            // Bulk changes: a zone is stunned, the stun lifted, a status disabled and given back.
            world.SetAll(new Stun(2), QueryFilter.Create().InGroup<Zone>(next % 4));
            world.RemoveAll<Stun>();
            world.DisableAll<Health>(QueryFilter.Create().InGroup<Zone>(next % 4));
            world.EnableAll<Health>();

            // A tree with a retiring leaf: destroy cascades, the cleanup component is released.
            var root = world.CreateEntity(new Health(1));
            var child = world.CreateEntity(new ChildOf(root));
            var leaf = world.CreateEntity(new ChildOf(child), new Lease(1));
            root.Destroy();
            leaf.Remove<Lease>();
        }

        for (int i = 0; i < 3000; i++) Frame(); // warm-up: keys sorted, buffers, links, stacks and tables grown, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) Frame();
        Assert.That((GC.GetAllocatedBytesForCurrentThread() - before, world.EntityCount), Is.EqualTo((0L, players.Length + walkers.Length)));
    }

    [Test]
    public void ParallelForEachDoesNotAllocateAfterItsFirstCall()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[World.ParallelChunkRows * 4], new ParallelTests.Counter(0), new ParallelTests.Seen(1));
        world.AddSystem<ParallelTests.ChunkedIncrement>();
        for (int i = 0; i < 10; i++) world.Update();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) world.Update();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private struct Churned : IComponent;
}
