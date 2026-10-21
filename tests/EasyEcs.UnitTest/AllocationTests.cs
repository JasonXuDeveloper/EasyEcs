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
        world.AddSingleton<Score>();
        world.AddSystem<Damage>();
        world.AddSystem<ChangedTests.Wear>();
        world.AddSystem<ChangedTests.ArmorWatch>();
        world.AddSystem<ChangedTests.CapArmor>();          // Mut<T>: stamps per write
        world.AddSystem<ChangedTests.CapArmorParallel>();
        world.AddSystem<ChangedTests.PeekAndHit>();        // Mut<T> beside a Maybe<T>: runs of rows
        world.CreateEntities(new Entity[100], new Armor(1_000_000), new Seen(), new Stun(1)); // tracked changes
        var probe = world.CreateEntity(new Health(1));
        var callsigns = world.Index<Callsign, string>();
        var named = world.CreateEntity(new Callsign("a"));
        var churned = new Entity[50];
        var churnedIds = new EntityId[50];
        var prefab = new Prefab().Set(new Churned()).Set(new Health(1)).Set(new Armor(2));

        void Frame()
        {
            world.Update();
            foreach (var item in world.Query<Position, Velocity>()) item.Position.X += item.Velocity.X;
            foreach (var item in world.Read<Armor, Seen>()) _ = item.Armor.Value + item.Seen.Count;
            _ = world.Count<Position, Velocity>(QueryFilter.Create());
            _ = world.Count<Armor>(QueryFilter.Create().Changed<Armor>(world.ChangeTick));
            named.Set(new Callsign(named.Callsign.Value == "a" ? "b" : "a")); // re-keyed every frame
            _ = callsigns.Count("a") + callsigns.Count("b");
            probe.Get<Health>().Value++;
            _ = probe.SetIfChanged(new Health(7)) | probe.SetIfChanged(new Health(7)); // a new value, then an equal one
            probe.Remove<Frozen>();
            probe.Set(new Frozen()); // archetype transitions on cached edges
            for (int i = 0; i < churned.Length; i++) churned[i] = world.CreateEntity(new Position(), new Health(1));
            foreach (var e in churned) e.Destroy(); // recycled ids and rows
            world.CreateEntities(churned, new Churned(), new Health(1));
            world.DestroyAll<Churned>();
            world.Instantiate(prefab, churned);                               // prefab copies, cached table
            for (int i = 0; i < churned.Length; i++) churnedIds[i] = churned[i].Id;
            world.DestroyEntities(churnedIds);                                // a list of ids, one gate pass
            probe.Clone().Destroy();                                          // a copy of a live entity
            probe.Set(new Armor(1));
            probe.Remove<Armor>();                                            // a tracked removal
            _ = world.Count<Health>(QueryFilter.Create().Removed<Armor>(world.ChangeTick));
            var heal = new Heal();
            _ = probe.Run<Health, Heal>(ref heal) && probe.Run<Health, Heal>(ref heal, QueryFilter.Create().With<Frozen>());
        }

        for (int i = 0; i < 10; i++) Frame(); // warm-up: query caches, edges, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Frame();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void TicksAfterTrimExcessDoNotAllocate()
    {
        using var world = new World();
        world.AddSystem<Movement>();
        world.AddSystem<ChangedTests.Wear>();
        var crowd = new Entity[20_000];
        world.CreateEntities(crowd, new Position(), new Velocity(1, 1), new Armor(1_000_000), new Seen());
        for (int i = 0; i < crowd.Length; i++)
            if (i % 10 != 0) crowd[i].Destroy();
        var churned = new Entity[50];
        var probe = world.CreateEntity(new Health(1));

        void Frame()
        {
            world.Update();
            _ = world.Count<Armor>(QueryFilter.Create().Changed<Armor>(world.ChangeTick));
            world.CreateEntities(churned, new Churned(), new Health(1));
            world.DestroyAll<Churned>();
            probe.Set(new Frozen());
            probe.Remove<Frozen>();
        }

        for (int i = 0; i < 10; i++) Frame();
        world.TrimExcess();
        for (int i = 0; i < 10; i++) Frame(); // the dropped tables and edges come back once
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
            foreach (var _ in world.Descendants(root.Id)) seen++;              // the subtree a save would collect
            root.Destroy();
            leaf.Remove<Lease>();
        }

        for (int i = 0; i < 3000; i++) Frame(); // warm-up: keys sorted, buffers, links, stacks and tables grown, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) Frame();
        Assert.That((GC.GetAllocatedBytesForCurrentThread() - before, world.EntityCount), Is.EqualTo((0L, players.Length + walkers.Length)));
    }

    [Test]
    public void PerClientChangedAnyFiltersDoNotAllocate()
    {
        using var world = new World();
        world.AddSystem<ChangedTests.ArmorOrShieldWatch>();
        world.AddSystem<ChangedTests.ArmorOrShieldWatchBatch>();
        world.AddSystem<RemovedTests.ArmorOrShieldLoss>();
        var entities = new Entity[5_000];
        world.CreateEntities(entities, new Armor(1), new Shield(1), new ChangedTests.Ward(1), new Seen());
        var dirty = QueryFilter.Create().ChangedAny<Armor, Shield, ChangedTests.Ward>(0).Without<Dead>();
        var lost = QueryFilter.Create().RemovedAny<Armor, Shield>(0);
        var acked = new ulong[8]; // one acknowledged tick per client
        int next = 0;

        void Frame()
        {
            int k = next++;
            entities[k * 7 % entities.Length].Get<Armor>().Value++;
            ref var shield = ref entities[k * 13 % entities.Length].TryGetRef<Shield>(out bool has);
            if (has) shield.Value++;
            var e = entities[k * 17 % entities.Length];
            if (e.Has<Shield>()) e.Remove<Shield>();
            else e.Set(new Shield(1));
            world.Update();
            for (int c = 0; c < acked.Length; c++)
            {
                if ((k + c) % 3 != 0) continue; // clients acknowledge at different ticks
                foreach (var item in world.Query<Seen>(dirty.Since(acked[c]))) item.Component1.Count++;
                _ = world.Count<Seen>(lost.Since(acked[c]));
                _ = world.Count<Seen>(QueryFilter.Create().ChangedAny<Armor, Shield>(acked[c])); // built at the call: interned
                acked[c] = world.ChangeTick;
            }
        }

        for (int i = 0; i < 50; i++) Frame(); // warm-up: query caches, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) Frame();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    // The fewest bytes one of three windows of `updates` updates allocated on this thread. Waking helpers queues thread
    // pool work, and the pool may start a worker thread from the queuing thread (its Thread object counts here) when an
    // idle one retired: an allocation of the runtime, not of the world, which a window without it leaves out. An
    // allocation per update shows in every window.
    private static long LeastAllocated(World world, int updates)
    {
        long least = long.MaxValue;
        for (int window = 0; window < 3 && least != 0; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < updates; i++) world.Update();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    [Test]
    public void ParallelForEachDoesNotAllocateAfterItsFirstCall()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[World.ParallelChunkRows * 4], new ParallelTests.Counter(0), new ParallelTests.Seen(1));
        world.AddSystem<ParallelTests.ChunkedIncrement>();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(LeastAllocated(world, 500), Is.Zero);
    }

    [Test]
    public void MeasuredSystemsDoNotAllocate()
    {
        // Timing, bytes and entity counts of every run, in parallel stages and parallel [ForEach] passes too.
        using var world = new World(new WorldOptions { MeasureSystems = true, ParallelSystems = true, MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[World.ParallelChunkRows * 4], new ParallelTests.Counter(0), new ParallelTests.Seen(1));
        world.CreateEntities(new Entity[100], new Position(0, 0), new Velocity(1, 1));
        world.AddSystem<ParallelTests.ChunkedIncrement>();
        world.AddSystem<SystemTests.Moves>();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(LeastAllocated(world, 500), Is.Zero);
        Assert.That(world.GetSystem<SystemTests.Moves>().Stats.LastEntities, Is.EqualTo(100));
    }

    private struct Churned : IComponent;

    // Toggles a tag on the entity it runs on: entity.Run with a structural change inside.
    private struct Heal : IForEachEntity<Health>
    {
        public void Execute(Entity entity, ref Health health)
        {
            health.Value++;
            if (entity.Has<Stun>()) entity.Remove<Stun>();
            else entity.Set(new Stun(1));
        }
    }
}
