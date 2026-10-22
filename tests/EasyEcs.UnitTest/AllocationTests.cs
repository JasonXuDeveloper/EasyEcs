using System;
using Nino.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>The steady-state hot paths allocate nothing (performance numbers come from EasyEcs.Benchmark).</summary>
public class AllocationTests
{
    [Test]
    public void ParallelReductionsScratchAndLookupsDoNotAllocate()
    {
        // Several tables of chunks, several threads: the job copies kept per chunk, the thread's scratch, an index built
        // before the stage. Warm-up lets every helper thread meet every method once.
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4, ParallelSystems = true });
        world.CreateEntities(new Entity[World.ParallelChunkRows * 3], new ReduceTests.Points(3));
        world.CreateEntities(new Entity[World.ParallelChunkRows], new ReduceTests.Points(1), new ReduceTests.Spot(1, 2));
        world.CreateEntities(new Entity[World.ParallelChunkRows * 2], new ScratchTests.Number(12345), new ScratchTests.DigitSum());
        var leader = world.CreateEntity(new LookupTests.Found());
        leader.Found = new LookupTests.Found(leader.Id, 0);
        world.CreateEntities(new Entity[World.ParallelChunkRows], new Follows(leader.Id), new LookupTests.Place(1, 0));
        world.AddSystem<ReduceTests.ParallelScores>();
        world.AddSystem<ReduceTests.ParallelBounds>();
        world.AddSystem<ReduceTests.ParallelCensus>();
        world.AddSystem<ScratchTests.SumDigits>();
        world.AddSystem<LookupTests.SumFollowers>();
        for (int i = 0; i < 30; i++) world.Update();
        long least = long.MaxValue;
        for (int window = 0; window < 3 && least != 0; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) world.Update();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.That(least, Is.Zero);
        Assert.That(leader.Found.Followers, Is.EqualTo(World.ParallelChunkRows));
    }

    [Test]
    public void CompactionAndFilteredCollectIdsDoNotAllocate()
    {
        using var world = new World();
        var all = new Entity[1000];
        world.CreateEntities(all, new Health(1));
        for (int i = 0; i < all.Length; i += 3) all[i].Set(new Frozen());
        var awake = QueryFilter.Create().Without<Frozen>();
        var ids = new EntityId[all.Length];
        var values = new long[64];
        var picked = new long[64];
        world.CollectIds<Health>(ids, awake);
        Lanes.Compact<long>(values, 0xF0F0_F0F0_F0F0_F0F0UL, picked);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int collected = world.CollectIds<Health>(ids, awake);
        int compacted = Lanes.Compact<long>(values, 0xF0F0_F0F0_F0F0_F0F0UL, picked);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That((collected, compacted), Is.EqualTo((666, 32)));
    }

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
            player.Link(new MemberOf(party));
            players[(next + 128) % players.Length].Unlink<MemberOf>(players[(next + 128) * 7 % players.Length]);
            foreach (var _ in parties.Find(party)) seen++;

            // A party disbands while linked and a new one takes its id: both incarnations have links for a while.
            var guild = world.CreateEntity(new Health(3));
            var founder = players[next % players.Length];
            founder.Link(new MemberOf(guild));
            var disbanded = guild.Id;
            guild.Destroy();
            var successor = world.CreateEntity(new Health(3));            // the recycled id
            var joiner = players[(next + 1) % players.Length];
            joiner.Link(new MemberOf(successor));
            founder.Unlink<MemberOf>(disbanded);
            joiner.Unlink<MemberOf>(successor);
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

    /// <summary>A region of a map, one per key, with a list of cells: what a server unloads and loads back.</summary>
    public record struct Region(int Key) : IComponent, IUniqueIndexed<int>
    {
        readonly int IIndexed<int>.Key => Key;
    }

    public record struct Cell(byte Value); // the pool of this element type serves only the test below

    [Test]
    public void UnloadingAndLoadingBackEntitiesDoesNotAllocateOnceWarm()
    {
        DynamicBuffer<Cell>.PoolCapacity = 1024;
        using var world = new World();
        var others = new Entity[3000];
        for (int i = 0; i < others.Length; i++) others[i] = world.CreateEntity(new Health(i));
        var output = new NinoArrayBufferWriter(64 * 1024);
        var loaded = new Entity[1];
        var ids = new EntityId[1];

        // A region is saved and destroyed (every other one without its Position), another entity of the world goes and comes back (so the next region takes
        // another id, as in a world where other entities come and go), and the region is loaded back.
        void Cycle(int round)
        {
            var region = world.CreateEntity(new Region(round % 16), new Position(round, 0));
            region.GetOrAdd<DynamicBuffer<Cell>>().SetCount(1024).Fill(new Cell((byte)round));
            if (round % 2 == 0) region.Remove<Position>(); // a row without a component of its table: presence bits saved
            ids[0] = region.Id;
            world.SaveEntities(output, ids);
            region.Destroy();
            int other = round * 37 % others.Length;
            others[other].Destroy();
            others[other] = world.CreateEntity(new Health(other));
            world.LoadEntities(output.WrittenSpan, loaded);
            loaded[0].Destroy();
            output.ResetWrittenCount();
        }

        for (int round = 0; round < 2000; round++) Cycle(round);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 2000; round < 2500; round++) Cycle(round);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void LoadingAGrowingWorldGrowsItsArraysAFewTimes()
    {
        // Each round the source world gains an entity (and frees another id) and is loaded into the target: the load's
        // records of the ids grow with the world by doubling, not by one id per load.
        using var source = new World();
        using var target = new World();
        var output = new NinoArrayBufferWriter(256 * 1024);
        for (int i = 0; i < 4000; i++) source.CreateEntity(new Health(i));

        void Cycle(int round)
        {
            source.CreateEntity(new Health(round));
            source.CreateEntity(new Health(-round)).Destroy();
            source.Save(output);
            target.Load(output.WrittenSpan);
            output.ResetWrittenCount();
        }

        for (int round = 0; round < 100; round++) Cycle(round);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 100; round < 2100; round++) Cycle(round);
        // 4,000 more ids: a growth or two of a few arrays of 8,000-16,000 ids (the worlds' own and the load's), where
        // growing by one id per load would copy them 2,000 times (hundreds of MB).
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(2 << 20));
    }

    [Test]
    public void DestroyingAReservedWorldDoesNotAllocate()
    {
        // The ids of a reservation's entities, all destroyed one by one, go to the free list without growing it.
        using var world = new World();
        world.EnsureEntityCapacity(20_000);
        var entities = new Entity[20_000];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity(new Health(i));
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var entity in entities) entity.Destroy();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    public record struct Grain(byte Value); // the pool of this element type serves only the test below

    [Test]
    public void ReservedPoolListsServeTheFirstBuffers()
    {
        // ReservePool pays at startup for the lists the world's buffers take when it first grows to its peak.
        DynamicBuffer<Grain>.PoolCapacity = 256;
        DynamicBuffer<Grain>.ReservePool(500);
        using var world = new World();
        world.Reserve<Position, DynamicBuffer<Grain>>(500);
        var entities = new Entity[500];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(new Position(i, 0), new DynamicBuffer<Grain>());
            entities[i].Get<DynamicBuffer<Grain>>().SetCount(256).Fill(new Grain((byte)i));
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicBuffer<Grain>.ReservePool(-1));
    }

    [Test]
    public void ReservingAPrefabCoversItsIndexKeysAndItsRetiredTable()
    {
        // Each instance takes a key of its own in a value index and a unique index, and holds a cleanup component, so
        // its destruction moves it to the retired table: the reservation covers all of it, and the free ids.
        using var world = new World();
        world.Index<Rating, int>();
        var prefab = new Prefab().Set(new Rating(0)).Set(new Region(-1)).Set(new Lease(0));
        const int count = 2000;
        var spawned = new Entity[count];
        // One instance lives its whole life first: the tables and the caches of the calls exist from then on.
        var first = world.Instantiate(prefab, new Rating(-1), new Region(-2));
        first.Destroy();
        first.Remove<Lease>();
        world.Reserve(prefab, count);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) spawned[i] = world.Instantiate(prefab, new Rating(i), new Region(i));
        long created = GC.GetAllocatedBytesForCurrentThread();
        foreach (var entity in spawned) entity.Destroy();
        long retired = GC.GetAllocatedBytesForCurrentThread();
        foreach (var entity in spawned) entity.Remove<Lease>(); // released: freed
        long freed = GC.GetAllocatedBytesForCurrentThread();
        Assert.That((created - start, retired - created, freed - retired), Is.EqualTo((0L, 0L, 0L)));
        Assert.That(world.EntityCount, Is.Zero);
    }

    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    [TestCase(true, false, true, false)]
    [TestCase(true, false, false, true)]
    [TestCase(false, true, false, true)]
    [TestCase(true, true, true, true)]
    public void RestoringAndLoadingAWorldWithIndexesDoesNotAllocateOnceWarm(bool restore, bool load, bool unique, bool links)
    {
        // Value, unique and relation indexes are emptied by each restore and load and filled again from the copy: their
        // lists come back from the arrays of the last one. Links are buffers: a restore copies them into the lists the
        // rows hold, a capture into the snapshot's.
        using var world = new World();
        var ratings = world.Index<Rating, int>();
        var parties = world.Relations<MemberOf>();
        var party = world.CreateEntity(new Position(0, 0));
        for (int i = 0; i < 300; i++)
        {
            var e = world.CreateEntity(new Rating(i % 7));
            if (unique) e.Set(new Region(i));
            if (links) e.Link(new MemberOf(party.Id));
        }
        var snapshot = world.Capture();
        var output = new NinoArrayBufferWriter(64 * 1024);
        world.Save(output);
        var bytes = output.WrittenSpan.ToArray();

        void Cycle()
        {
            if (restore)
            {
                world.Restore(snapshot);
                world.Capture(snapshot);
            }

            if (load) world.Load(bytes);
        }

        for (int round = 0; round < 50; round++) Cycle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 200; round++) Cycle();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(ratings.Find(3).Count, Is.EqualTo(43));
        if (links) Assert.That(parties.Find(party.Id).Count, Is.EqualTo(300));
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
        var moves = world.AddSystem<SystemTests.Moves>();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(LeastAllocated(world, 500), Is.Zero);
        Assert.That(moves.Stats.LastEntities, Is.EqualTo(100));
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
