using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class DynamicBufferTests
{
    public record struct Loot(int Item, int Count);
    public enum Tile : byte { Air, Stone }
    public enum Voxel : byte { Air, Stone }   // MaxPooledLength is process-wide: only this test sets it
    public enum Ore : byte { Air, Iron }      // and only the reload test sets this one's
    public record struct Mail(long Value);    // the pool of this element type serves only the tests below
    public record struct Rewind(long Value);  // and this one's only the restore test
    public record struct Stock(int Value);    // the lists shared until written (copy-on-write), below
    public record struct Spare(int Value);    // the pool of this one serves only the last-holder test
    public record struct Replayed(int Value); // and this one's only the replay test

    /// <summary>Counts, per entity, the runs in which its bag had changed since the system's previous run.</summary>
    [EcsSystem]
    public ref struct BagWatch
    {
        [ForEach, Changed<DynamicBuffer<Loot>>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>
    /// Stunned entities drop their first item: a write through a <c>ref</c> parameter, which stamps every row it visits
    /// (also an empty bag it leaves alone: the stamp this test checks; EECS053 points real code to a Mut&lt;T&gt;).
    /// </summary>
    [EcsSystem]
    public ref struct Drop
    {
#pragma warning disable EECS053
        [ForEach, With<Stun>]
        private static void Apply(ref DynamicBuffer<Loot> bag)
        {
            if (bag.Count > 0) bag.RemoveAt(0);
        }
#pragma warning restore EECS053
    }

    /// <summary>Reads every bag: an <c>in</c> parameter is not a write.</summary>
    [EcsSystem]
    public ref struct Weigh
    {
        [ForEach]
        private static void Sum(in DynamicBuffer<Loot> bag, ref Health weight)
        {
            int total = 0;
            foreach (var loot in bag) total += loot.Count;
            weight = new Health(total);
        }
    }

    /// <summary>Each stock's first element becomes its entity's health: the first write of lists the rows share.</summary>
    [EcsSystem]
    public ref struct Restock
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        private static void Apply(Mut<DynamicBuffer<Stock>> stock, in Health health) => stock.Write().AsSpan()[0] = new Stock(health.Value);
    }

    /// <summary>Each list's first element becomes its entity's health, in parallel chunks: a write of every row.</summary>
    [EcsSystem]
    public ref struct Replay
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        private static void Apply(Mut<DynamicBuffer<Replayed>> list, in Health health) => list.Write().AsSpan()[0] = new Replayed(health.Value);
    }

    /// <summary>Refills every bag with one item, in place: no allocation once the capacity is reached.</summary>
    [EcsSystem]
    public ref struct Refill
    {
        [ForEach]
        private static void Apply(ref DynamicBuffer<Loot> bag)
        {
            bag.Clear();
            bag.Add(new Loot(1, 1));
            bag.Add(new Loot(2, 2));
        }
    }

    [Test]
    public void ABufferIsAList()
    {
        using var world = new World();
        var e = world.CreateEntity(new DynamicBuffer<Loot>());              // no list until the first write
        ref var bag = ref e.Get<DynamicBuffer<Loot>>();
        Assert.That((bag.Count, bag.Capacity), Is.EqualTo((0, 0)));
        bag.EnsureCapacity(2);
        Assert.That((bag.Count, bag.Capacity), Is.EqualTo((0, 2)));

        bag.Add(new Loot(1, 1));
        bag.AddRange([new Loot(2, 2), new Loot(3, 3)]);
        bag.Insert(0, new Loot(0, 0));
        Assert.That(bag.AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Loot(0, 0), new Loot(1, 1), new Loot(2, 2), new Loot(3, 3) }));
        bag.AddRange(new Loot(4, 4), new Loot(5, 5));                       // params: a span on the stack
        bag.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(new System.Collections.Generic.List<Loot> { new(6, 6) })); // a list: its span
        Assert.That(bag.AsReadOnlySpan()[4..].ToArray(), Is.EqualTo(new[] { new Loot(4, 4), new Loot(5, 5), new Loot(6, 6) }));
        for (int i = 0; i < 3; i++) bag.RemoveAt(4);
        Assert.That(bag.Capacity, Is.GreaterThanOrEqualTo(4));

        bag.RemoveAt(1);                          // keeps the order
        bag.RemoveAtSwapBack(0);                  // the last moves in
        Assert.That(bag.AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Loot(3, 3), new Loot(2, 2) }));
        bag.AsSpan()[1].Count = 9;
        Assert.That(e.Read<DynamicBuffer<Loot>>()[1], Is.EqualTo(new Loot(2, 9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = e.Read<DynamicBuffer<Loot>>()[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => e.Get<DynamicBuffer<Loot>>().RemoveAt(2));

        int capacity = bag.Capacity;
        bag.Clear();
        Assert.That((bag.Count, bag.Capacity), Is.EqualTo((0, capacity)));
        bag.Add(new Loot(4, 4));
        bag.TrimExcess();                         // the peak capacity is released
        Assert.That((bag.Count, bag.Capacity, bag[0]), Is.EqualTo((1, 1, new Loot(4, 4))));
        bag.Add(new Loot(5, 5));
        bag.Add(new Loot(6, 6));
        int grown = bag.Capacity;
        bag.Clear();
        bag.TrimExcess();                         // empty: the list goes back to the pool
        Assert.That(bag.Capacity, Is.Zero);
        var other = world.CreateEntity(new DynamicBuffer<Loot>());
        ref var reused = ref other.Get<DynamicBuffer<Loot>>();
        reused.Add(new Loot(7, 7));               // the first write takes it back, with the capacity it grew to
        Assert.That((reused.Count, reused.Capacity, reused[0]), Is.EqualTo((1, grown, new Loot(7, 7))));

        var empty = world.CreateEntity(new DynamicBuffer<Loot>());
        Assert.That(empty.Read<DynamicBuffer<Loot>>().Count, Is.Zero);
        Assert.That(empty.Read<DynamicBuffer<Loot>>().AsReadOnlySpan().IsEmpty, Is.True);
        empty.Get<DynamicBuffer<Loot>>().Add(new Loot(5, 5));
        Assert.That(empty.Read<DynamicBuffer<Loot>>()[0], Is.EqualTo(new Loot(5, 5)));
    }

    [Test]
    public void WritesThroughRefAreChangesReadsAreNot()
    {
        using var world = new World();
        world.AddSystem<Drop>();
        world.AddSystem<Weigh>();
        world.AddSystem<BagWatch>();
        var quiet = world.CreateEntity(new DynamicBuffer<Loot>(), new Seen(), new Health());
        var stunned = world.CreateEntity(new DynamicBuffer<Loot>(), new Seen(), new Health(), new Stun(1));
        var handled = world.CreateEntity(new DynamicBuffer<Loot>(), new Seen(), new Health());
        foreach (var e in new[] { quiet, stunned, handled }) e.Get<DynamicBuffer<Loot>>().AddRange([new Loot(1, 1), new Loot(2, 2)]);

        world.Update();                           // the first run sees every entity (all were just written)
        handled.Get<DynamicBuffer<Loot>>().Add(new Loot(3, 3));
        _ = quiet.Read<DynamicBuffer<Loot>>().Count;     // a read: not a change
        world.Update();
        world.Update();

        Assert.That(quiet.Get<Seen>().Count, Is.EqualTo(1));
        Assert.That(handled.Get<Seen>().Count, Is.EqualTo(2));
        Assert.That(stunned.Get<Seen>().Count, Is.EqualTo(3)); // Drop writes it every run
        Assert.That(stunned.Read<DynamicBuffer<Loot>>().Count, Is.Zero);
        Assert.That(quiet.Get<Health>().Value, Is.EqualTo(3));
    }

    [Test]
    public void TheListMovesWithItsEntity()
    {
        using var world = new World();
        var e = world.CreateEntity(new DynamicBuffer<Loot>());
        e.Get<DynamicBuffer<Loot>>().Add(new Loot(7, 1));
        e.Set(new Position(1, 1));                 // another table
        e.Set(new Frozen());
        Assert.That(e.Read<DynamicBuffer<Loot>>()[0], Is.EqualTo(new Loot(7, 1)));

        var other = world.CreateEntity(new DynamicBuffer<Loot>(), new Position(), new Frozen());
        e.Destroy();                               // other may move into e's row
        Assert.That(other.Read<DynamicBuffer<Loot>>().Count, Is.Zero);
        other.Remove<DynamicBuffer<Loot>>();
        Assert.That(other.Has<DynamicBuffer<Loot>>(), Is.False);
    }

    [Test]
    public void SnapshotsCopyTheElements()
    {
        using var world = new World();
        var e = world.CreateEntity(new DynamicBuffer<Loot>());
        e.Get<DynamicBuffer<Loot>>().Add(new Loot(1, 1));
        var snapshot = world.Capture();

        e.Get<DynamicBuffer<Loot>>().AsSpan()[0].Count = 5;   // in place: the snapshot holds its own copy
        e.Get<DynamicBuffer<Loot>>().Add(new Loot(2, 2));
        world.Restore(snapshot);
        Assert.That(e.Read<DynamicBuffer<Loot>>().AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Loot(1, 1) }));

        e.Get<DynamicBuffer<Loot>>().AsSpan()[0].Count = 9;   // the restored list is not the snapshot's either
        world.Restore(snapshot);
        Assert.That(e.Read<DynamicBuffer<Loot>>()[0], Is.EqualTo(new Loot(1, 1)));
    }

    [Test]
    public void RestoreGivesTheListsOfTheRowsItDropsBackToThePool()
    {
        using var world = new World();
        world.CreateEntity(new DynamicBuffer<Rewind>()).Get<DynamicBuffer<Rewind>>().Add(new Rewind(1));
        var snapshot = world.Capture();
        var save = new Nino.Core.NinoArrayBufferWriter();
        world.Save(save);
        var created = new Entity[16];

        // A replay: entities made after the capture (each with its list), then the world put back without them.
        void Replay(bool load)
        {
            world.CreateEntities(created, new DynamicBuffer<Rewind>());
            foreach (var e in created) e.Get<DynamicBuffer<Rewind>>().Add(new Rewind(2));
            if (load) world.Load(save.WrittenSpan);
            else world.Restore(snapshot);
        }

        for (int i = 0; i < 3; i++)
        {
            Replay(false);
            Replay(true);
        }

        long before = Allocations.Baseline();
        for (int i = 0; i < 50; i++) Replay(false);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        // Restored rows share their lists with the snapshot, loaded rows have their own: the first loads after restores
        // take the lists that makes (once), Load's own copy one more.
        Replay(true);
        Replay(true);
        before = Allocations.Baseline();
        for (int i = 0; i < 50; i++) Replay(true);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(world.Count<DynamicBuffer<Rewind>>(), Is.EqualTo(1));
    }

    [Test]
    public void AParallelPassAfterARestoreWritesWithoutAllocating()
    {
        // A replay (rollback, a rehearsed tick): restore a snapshot, then a parallel pass writes every list. The restore
        // copies the snapshot's elements into each row written since, on the owner thread, so no chunk on a helper
        // thread copies a shared list into one from its own pool (which the lists the owner thread gives back do not
        // reach).
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4, MeasureSystems = true });
        var rows = new Entity[64 * 7 + 13];   // chunks of 64 rows, the last one short
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = world.CreateEntity(new DynamicBuffer<Replayed>(), new Health(i));
            for (int j = 0; j < 16; j++) rows[i].WriteReplayed().Add(new Replayed(-j));
        }

        var replay = world.AddSystem<Replay>();
        var snapshot = world.Capture();
        void Cycle()
        {
            world.Restore(snapshot);
            world.Update();
        }

        for (int i = 0; i < 20; i++) Cycle();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 200; i++) Cycle(); },
            () => GC.GetAllocatedBytesForCurrentThread() + replay.Stats.TotalAllocated), Is.Zero);
        for (int i = 0; i < rows.Length; i++) Assert.That(rows[i].Replayed[0], Is.EqualTo(new Replayed(i)));
        world.Restore(snapshot);
        Assert.That(rows[5].Replayed[0], Is.EqualTo(new Replayed(0)));   // the snapshot kept its own elements
    }

    [Test]
    public void WritesWithinTheCapacityDoNotAllocate()
    {
        using var world = new World();
        var bags = new Entity[100];
        world.CreateEntities(bags, new DynamicBuffer<Loot>(), new Health());       // one value, a list per entity
        bags[0].Get<DynamicBuffer<Loot>>().Add(new Loot(9, 9));
        Assert.That(bags[1].Read<DynamicBuffer<Loot>>().Count, Is.Zero);
        world.AddSystem<Refill>();
        world.AddSystem<Weigh>();
        for (int i = 0; i < 5; i++) world.Update();   // the lists reach their capacity

        long before = Allocations.Baseline();
        for (int i = 0; i < 100; i++) world.Update();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void BuffersThatComeAndGoDoNotAllocate()
    {
        using var world = new World();
        var bags = new Entity[64];
        var held = world.CreateEntity(new Health());

        void Churn()
        {
            for (int i = 0; i < bags.Length; i++)
            {
                bags[i] = world.CreateEntity(new DynamicBuffer<Loot>(), new Position());
                bags[i].Get<DynamicBuffer<Loot>>().Add(new Loot(i, 1));            // a loaded actor fills its mailbox
            }

            foreach (var bag in bags) bag.Destroy();                               // and unloads
            world.CreateEntities(bags, new DynamicBuffer<Loot>(), new Frozen());
            foreach (var bag in bags) bag.Get<DynamicBuffer<Loot>>().Add(new Loot(1, 1));
            world.DestroyAll<Frozen>();                                      // a whole table at once
            held.Set(new DynamicBuffer<Loot>());
            held.Get<DynamicBuffer<Loot>>().Add(new Loot(2, 2));
            held.Remove<DynamicBuffer<Loot>>();                                   // a component that goes
        }

        for (int i = 0; i < 5; i++) Churn();
        long before = Allocations.Baseline();
        for (int i = 0; i < 100; i++) Churn();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void AListThatServedADeadBufferStartsEmptyAndBelongsToOneEntity()
    {
        using var world = new World();
        var first = world.CreateEntity(new DynamicBuffer<string>());
        first.Get<DynamicBuffer<string>>().Add("mail");
        var kept = world.CreateEntity(new DynamicBuffer<string>());
        kept.Get<DynamicBuffer<string>>().Add("kept");
        first.Destroy();

        var next = world.CreateEntity(new DynamicBuffer<string>());
        var other = world.CreateEntity(new DynamicBuffer<string>());
        Assert.That(next.Read<DynamicBuffer<string>>().Count, Is.Zero);
        next.Get<DynamicBuffer<string>>().Add("a");
        other.Get<DynamicBuffer<string>>().Add("b");
        Assert.That((next.Read<DynamicBuffer<string>>()[0], other.Read<DynamicBuffer<string>>()[0], kept.Read<DynamicBuffer<string>>()[0]),
            Is.EqualTo(("a", "b", "kept")));

        kept.Disable<DynamicBuffer<string>>();                // a disabled buffer keeps its list
        kept.Set(new Position());                             // and moves with its row
        kept.Enable<DynamicBuffer<string>>();
        Assert.That(kept.Read<DynamicBuffer<string>>()[0], Is.EqualTo("kept"));
    }

    [Test]
    public void NamedMembersReadWriteAddAndRemoveTheBuffer()
    {
        using var world = new World();
        world.AddSystem<BagWatch>();
        var e = world.CreateEntity(new Seen());
        Assert.That(e.HasLoot, Is.False);

        e.GetOrAddLoot().Add(new Loot(1, 2));                   // added on first use
        Assert.That(e.HasLoot, Is.True);
        Assert.That(e.Loot[0], Is.EqualTo(new Loot(1, 2)));     // a read: not stamped
        world.Update();
        Assert.That(e.Read<Seen>().Count, Is.EqualTo(1));

        _ = e.Loot.Count;
        world.Update();
        Assert.That(e.Read<Seen>().Count, Is.EqualTo(1));

        e.WriteLoot().Add(new Loot(3, 4));                      // a write: stamped
        world.Update();
        Assert.That(e.Read<Seen>().Count, Is.EqualTo(2));
        Assert.That(e.Loot.Count, Is.EqualTo(2));

        e.RemoveLoot();
        Assert.That(e.HasLoot, Is.False);
    }

    [Test]
    public void AnEnumElementGetsTheNamedMembersToo()
    {
        using var world = new World();
        var e = world.CreateEntity(new DynamicBuffer<Tile>());   // names the buffer: its element gets members
        e.WriteTile().Add(Tile.Stone);
        e.RemoveTile().GetOrAddTile().Add(Tile.Stone);
        e.WriteTile().Add(Tile.Air);
        Assert.That(e.HasTile, Is.True);
        Assert.That((e.Tile[0], e.Tile[1]), Is.EqualTo((Tile.Stone, Tile.Air)));

        e.WriteTile().AsSpan()[0] = Tile.Air;
        Assert.That(e.Tile[0], Is.EqualTo(Tile.Air));
        var column = new Prefab().SetTile(Tile.Stone, Tile.Stone);
        Assert.That(world.Instantiate(column).Tile.Count, Is.EqualTo(2));
        e.RemoveTile();
        Assert.That(e.HasTile, Is.False);
    }

    [Test]
    public void MaxPooledLengthKeepsLongerListsForTheNextBuffer()
    {
        Assert.That(DynamicBuffer<Voxel>.MaxPooledLength, Is.EqualTo(4096));   // 4,096 bytes of one-byte elements
        Assert.That(DynamicBuffer<Loot>.MaxPooledLength, Is.EqualTo(512));     // of eight-byte ones
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicBuffer<Voxel>.MaxPooledLength = -1);

        using var world = new World();
        var chunk = new Voxel[8192];
        try
        {
            DynamicBuffer<Voxel>.MaxPooledLength = chunk.Length;
            Cycle(world, chunk);
            Cycle(world, chunk);                                             // warm: the table, the pool
            long before = Allocations.Baseline();
            Cycle(world, chunk);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);   // the list came back

            DynamicBuffer<Voxel>.MaxPooledLength = 4096;
            Cycle(world, chunk);                                             // takes the pooled list, keeps it not
            before = Allocations.Baseline();
            Cycle(world, chunk);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.GreaterThanOrEqualTo(chunk.Length));
        }
        finally
        {
            DynamicBuffer<Voxel>.MaxPooledLength = 4096;
        }
    }

    [Test]
    public void SetCountClearsTheElementsItAddsEvenInAListFromThePool()
    {
        using var world = new World();
        var used = world.CreateEntity(new DynamicBuffer<Ore>());
        ref var first = ref used.Get<DynamicBuffer<Ore>>();
        first.AddRange(Ore.Iron, Ore.Iron, Ore.Iron);
        used.Destroy();                                       // its list, holding Iron, goes back to the pool

        var chunk = world.CreateEntity(new DynamicBuffer<Ore>());
        ref var ores = ref chunk.Get<DynamicBuffer<Ore>>();
        ores.SetCount(0);
        Assert.That(ores.Count, Is.Zero);
        var written = ores.SetCount(4);                       // the pooled list: its old values must not show
        Assert.That(ores.AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { Ore.Air, Ore.Air, Ore.Air, Ore.Air }));
        written[3] = Ore.Iron;                                // the span returned is the buffer's
        Assert.That(ores[3], Is.EqualTo(Ore.Iron));
        ores.SetCount(2);
        ores.SetCount(4);                                     // grown again: the element dropped comes back cleared
        Assert.That(ores.AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { Ore.Air, Ore.Air, Ore.Air, Ore.Air }));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.Get<DynamicBuffer<Ore>>().SetCount(-1));
    }

    [Test]
    public void AnEmptyBufferOfAPrefabTakesAListFromThePool()
    {
        using var world = new World();
        var actor = new Prefab().Set(new Seen()).SetBuffer<Loot>();
        for (int i = 0; i < 3; i++) Spawn(world, actor);                     // warm: the table, the pool
        long before = Allocations.Baseline();
        Spawn(world, actor);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);  // no list of its own, then a pooled one

        var stocked = world.Instantiate(new Prefab().SetBuffer(new Loot(1, 2)));
        Assert.That(stocked.Loot[0], Is.EqualTo(new Loot(1, 2)));           // a prefab with elements still copies them
    }

    private static void Spawn(World world, Prefab actor)
    {
        var e = world.Instantiate(actor);
        e.WriteLoot().Add(new Loot(1, 1));
        e.Destroy();
    }

    [Test]
    public void ListsGivenBackOnOneThreadServeTheFirstWritesOfAnother()
    {
        using var world = new World();
        var mailboxes = new Entity[200];
        world.CreateEntities(mailboxes, new DynamicBuffer<Mail>());
        foreach (var mailbox in mailboxes) mailbox.Get<DynamicBuffer<Mail>>().Add(new Mail(1));
        world.DestroyAll<DynamicBuffer<Mail>>();                              // 200 lists back, most to the shared pool

        int reused = 0;
        var helper = new System.Threading.Thread(() =>
        {
            for (int i = 0; i < 100; i++)
                if (BufferStore<Mail>.Rent().Items.Length > 0) reused++;        // a list that held mail
        });
        helper.Start();
        helper.Join();
        Assert.That(reused, Is.EqualTo(100));
    }

    [Test]
    public void LoadEntitiesTakesTheListsItReadsInsteadOfCopyingThem()
    {
        var blocks = new Ore[8192];
        var bytes = new Nino.Core.NinoArrayBufferWriter();
        var loaded = new Entity[4];
        var ids = new EntityId[4];
        try
        {
            DynamicBuffer<Ore>.MaxPooledLength = blocks.Length;
            using var world = new World();
            world.CreateEntities(loaded, new DynamicBuffer<Ore>());
            for (int i = 0; i < loaded.Length; i++)
            {
                loaded[i].Get<DynamicBuffer<Ore>>().AddRange(blocks);
                ids[i] = loaded[i].Id;
            }

            for (int i = 0; i < 3; i++) Reload(world, bytes, ids, loaded);    // warm: the tables, the pool, the load world
            long before = Allocations.Baseline();
            Reload(world, bytes, ids, loaded);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(blocks.Length));   // no list copied
            Assert.That(loaded[0].Read<DynamicBuffer<Ore>>().Count, Is.EqualTo(blocks.Length));
        }
        finally
        {
            DynamicBuffer<Ore>.MaxPooledLength = 4096;
        }
    }

    private static BufferStore<Stock> StoreOf(Entity e) => e.Get<DynamicBuffer<Stock>>().Store;

    private static int[] Values(Entity e)
    {
        var values = new System.Collections.Generic.List<int>();
        foreach (var stock in e.Read<DynamicBuffer<Stock>>()) values.Add(stock.Value);
        return values.ToArray();
    }

    private static Entity Stocked(World world, params ReadOnlySpan<Stock> items)
    {
        var e = world.CreateEntity(new DynamicBuffer<Stock>(), new Frozen());
        e.Get<DynamicBuffer<Stock>>().AddRange(items);
        return e;
    }

    [Test]
    public void PrefabInstancesShareItsListUntilTheyWriteIt()
    {
        using var world = new World();
        var crate = new Prefab().SetBuffer(new Stock(1), new Stock(2), new Stock(3));
        var a = world.Instantiate(crate);
        var b = world.Instantiate(crate);
        var shared = StoreOf(a);
        Assert.That(StoreOf(b), Is.SameAs(shared));
        Assert.That(shared.Shares, Is.EqualTo(BufferStore.Uncounted));   // a prefab's list: never counted

        a.Get<DynamicBuffer<Stock>>().AsSpan()[0] = new Stock(10);       // the first write copies
        Assert.That(StoreOf(a), Is.Not.SameAs(shared));
        Assert.That(StoreOf(a).Shares, Is.Zero);
        Assert.That(Values(a), Is.EqualTo(new[] { 10, 2, 3 }));
        Assert.That(Values(b), Is.EqualTo(new[] { 1, 2, 3 }));
        b.Get<DynamicBuffer<Stock>>().Add(new Stock(4));
        Assert.That(Values(b), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(Values(world.Instantiate(crate)), Is.EqualTo(new[] { 1, 2, 3 }));

        world.DestroyAll<DynamicBuffer<Stock>>();                         // instances let the prefab's list go: not pooled
        var rented = BufferStore<Stock>.Rent();
        Assert.That(rented, Is.Not.SameAs(shared));
        rented.Recycle();
        Assert.That(crate.Read<DynamicBuffer<Stock>>().AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Stock(1), new Stock(2), new Stock(3) }));
    }

    [Test]
    public void EveryWriteCopiesASharedListFirst()
    {
        var crate = new Prefab().SetBuffer(new Stock(1), new Stock(2), new Stock(3));
        var writes = new (string Name, Action<Entity> Write, int[] Expected)[]
        {
            ("AsSpan", e => e.Get<DynamicBuffer<Stock>>().AsSpan()[1] = new Stock(9), [1, 9, 3]),
            ("Add", e => e.Get<DynamicBuffer<Stock>>().Add(new Stock(4)), [1, 2, 3, 4]),
            ("AddRange", e => e.Get<DynamicBuffer<Stock>>().AddRange(new Stock(4), new Stock(5)), [1, 2, 3, 4, 5]),
            ("Insert", e => e.Get<DynamicBuffer<Stock>>().Insert(0, new Stock(0)), [0, 1, 2, 3]),
            ("RemoveAt", e => e.Get<DynamicBuffer<Stock>>().RemoveAt(0), [2, 3]),
            ("RemoveAtSwapBack", e => e.Get<DynamicBuffer<Stock>>().RemoveAtSwapBack(0), [3, 2]),
            ("Clear", e => e.Get<DynamicBuffer<Stock>>().Clear(), []),
            ("TrimExcess", e => e.Get<DynamicBuffer<Stock>>().TrimExcess(), [1, 2, 3]),
            ("SetCount", e => e.Get<DynamicBuffer<Stock>>().SetCount(2)[1] = new Stock(7), [1, 7]),
            ("EnsureCapacity", e => e.Get<DynamicBuffer<Stock>>().EnsureCapacity(64), [1, 2, 3]),
        };

        foreach (var (name, write, expected) in writes)
        {
            using var world = new World();
            var original = Stocked(world, new Stock(1), new Stock(2), new Stock(3));
            var clone = original.Clone();
            var instance = world.Instantiate(crate);
            var snapshot = world.Capture();
            write(clone);
            write(instance);
            Assert.That(Values(clone), Is.EqualTo(expected), name);
            Assert.That(Values(instance), Is.EqualTo(expected), name);
            Assert.That(Values(original), Is.EqualTo(new[] { 1, 2, 3 }), name);
            Assert.That(crate.Read<DynamicBuffer<Stock>>().Count, Is.EqualTo(3), name);
            write(original);
            Assert.That(Values(original), Is.EqualTo(expected), name);
            world.Restore(snapshot);
            Assert.That(Values(original), Is.EqualTo(new[] { 1, 2, 3 }), name);
            Assert.That(Values(clone), Is.EqualTo(new[] { 1, 2, 3 }), name);
            Assert.That(Values(instance), Is.EqualTo(new[] { 1, 2, 3 }), name);
        }
    }

    [Test]
    public void ClonesShareTheListUntilEitherSideWrites()
    {
        using var world = new World();
        var original = Stocked(world, new Stock(1), new Stock(2));
        var list = StoreOf(original);
        var clone = original.Clone();
        Assert.That(StoreOf(clone), Is.SameAs(list));
        Assert.That(list.Shares, Is.EqualTo(1));

        original.Get<DynamicBuffer<Stock>>().Add(new Stock(3));          // the original writes: it copies, the clone keeps
        Assert.That(StoreOf(clone), Is.SameAs(list));
        Assert.That(list.Shares, Is.Zero);
        clone.Get<DynamicBuffer<Stock>>().AsSpan()[0] = new Stock(5);    // the clone alone holds it now: in place
        Assert.That(StoreOf(clone), Is.SameAs(list));
        Assert.That(Values(original), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(Values(clone), Is.EqualTo(new[] { 5, 2 }));

        var second = clone.Clone();                                       // a destroyed holder counts down too
        clone.Destroy();
        Assert.That(StoreOf(second), Is.SameAs(list));
        Assert.That(list.Shares, Is.Zero);
        second.Remove<DynamicBuffer<Stock>>();                            // and so does a removed component
        Assert.That(list.Count, Is.Zero);                                 // the last holder's: emptied for the pool
    }

    [Test]
    public void OnlyTheLastHolderGivesTheListBackToThePool()
    {
        using var world = new World();
        var original = world.CreateEntity(new DynamicBuffer<Spare>());
        original.Get<DynamicBuffer<Spare>>().Add(new Spare(1));
        var list = original.Get<DynamicBuffer<Spare>>().Store;
        var clone = original.Clone();
        var snapshot = world.Capture();                                   // its two rows hold the list too
        Assert.That(list.Shares, Is.EqualTo(3));

        clone.Destroy();
        original.Destroy();
        Assert.That(list.Shares, Is.EqualTo(1));                          // the snapshot's rows hold it
        Assert.That(list.Count, Is.EqualTo(1));
        var rented = BufferStore<Spare>.Rent();
        Assert.That(rented, Is.Not.SameAs(list));
        rented.Recycle();

        world.Restore(snapshot);                                          // destroyed since: the rows take copies
        Assert.That(list.Shares, Is.EqualTo(1));
        Assert.That(original.Get<DynamicBuffer<Spare>>().Store, Is.Not.SameAs(list));
        world.DestroyAll<DynamicBuffer<Spare>>();
        Assert.That(list.Shares, Is.EqualTo(1));
        world.Capture(snapshot);                                          // the snapshot lets it go: the last holder
        Assert.That(list.Count, Is.Zero);
        Assert.That(BufferStore<Spare>.Rent(), Is.SameAs(list));
    }

    [Test]
    public void SnapshotsRestoredTwiceKeepTheirElements()
    {
        using var world = new World();
        var e = Stocked(world, new Stock(1), new Stock(2));
        var snapshot = world.Capture();
        Assert.That(StoreOf(e).Shares, Is.EqualTo(1));

        world.Restore(snapshot);                                          // nothing written: still the one list
        world.Restore(snapshot);
        Assert.That(StoreOf(e).Shares, Is.EqualTo(1));
        BufferStore own = null;
        for (int i = 0; i < 3; i++)
        {
            e.Get<DynamicBuffer<Stock>>().AsSpan()[0] = new Stock(9 + i);
            e.Get<DynamicBuffer<Stock>>().Add(new Stock(3));
            world.Restore(snapshot);
            Assert.That(Values(e), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(StoreOf(e).Shares, Is.Zero);                      // written since: its own list takes the elements back
            if (i > 0) Assert.That(StoreOf(e), Is.SameAs(own));          // in place
            own = StoreOf(e);
        }

        world.Capture(snapshot);                                          // captured again into the same copy
        world.Capture(snapshot);
        Assert.That(StoreOf(e).Shares, Is.EqualTo(1));
        var second = world.Capture();
        Assert.That(StoreOf(e).Shares, Is.EqualTo(2));
        e.Get<DynamicBuffer<Stock>>().Clear();                            // a shared list is let go, not emptied
        Assert.That(e.Read<DynamicBuffer<Stock>>().Count, Is.Zero);
        world.Restore(second);
        Assert.That(Values(e), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void LoadNeverRefillsAListASnapshotHolds()
    {
        using var world = new World();
        var e = Stocked(world, new Stock(1), new Stock(2));
        var save = new Nino.Core.NinoArrayBufferWriter();
        world.Save(save);
        e.Get<DynamicBuffer<Stock>>().AsSpan()[0] = new Stock(5);
        var snapshot = world.Capture();                                   // holds [5, 2]

        for (int i = 0; i < 3; i++)
        {
            world.Load(save.WrittenSpan);                                 // the world's old lists go to Load's own copy
            Assert.That(Values(e), Is.EqualTo(new[] { 1, 2 }));
            world.Load(save.WrittenSpan);
            Assert.That(Values(e), Is.EqualTo(new[] { 1, 2 }));
            world.Restore(snapshot);
            Assert.That(Values(e), Is.EqualTo(new[] { 5, 2 }));
        }
    }

    [Test]
    public void AMovedEntityTakesNoCountedListToItsNewWorld()
    {
        using var from = new World();
        using var to = new World();
        var crate = new Prefab().SetBuffer(new Stock(7));
        var e = Stocked(from, new Stock(1), new Stock(2));
        var instance = from.Instantiate(crate);
        var snapshot = from.Capture();
        var moved = new Entity[2];
        from.MoveEntities(to, [e.Id, instance.Id], moved);

        Assert.That(StoreOf(moved[0]).Shares, Is.Zero);                   // its own list: the source world counts none of it
        Assert.That(StoreOf(moved[1]), Is.SameAs(crate.Read<DynamicBuffer<Stock>>().Store));   // a prefab's stays shared
        moved[0].Get<DynamicBuffer<Stock>>().AsSpan()[0] = new Stock(9);
        moved[1].Get<DynamicBuffer<Stock>>().Add(new Stock(8));
        from.Restore(snapshot);
        Assert.That(Values(e), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(Values(instance), Is.EqualTo(new[] { 7 }));
        Assert.That(Values(moved[0]), Is.EqualTo(new[] { 9, 2 }));
        Assert.That(Values(moved[1]), Is.EqualTo(new[] { 7, 8 }));
    }

    [Test]
    public void AVariantPrefabSharesItsBasesListUncounted()
    {
        using var world = new World();
        var crate = new Prefab().SetBuffer(new Stock(1));
        var variant = new Prefab(crate);
        Assert.That(variant.Read<DynamicBuffer<Stock>>().Store, Is.SameAs(crate.Read<DynamicBuffer<Stock>>().Store));
        var first = world.Instantiate(crate);
        variant.SetBuffer(new Stock(2), new Stock(3));
        Assert.That(Values(world.Instantiate(variant)), Is.EqualTo(new[] { 2, 3 }));
        crate.SetBuffer(new Stock(4));                                   // the old list stays the instances'
        Assert.That(Values(first), Is.EqualTo(new[] { 1 }));
        Assert.That(Values(world.Instantiate(crate)), Is.EqualTo(new[] { 4 }));
    }

    [Test]
    public void ParallelFirstWritesOfSharedListsGiveEachRowItsOwnCopy([Values(1, 4)] int threads)
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        world.AddSystem<Restock>();
        var crate = new Prefab().SetBuffer(new Stock(-1), new Stock(-2));
        var rows = new Entity[4096];
        for (int round = 0; round < 3; round++)
        {
            // Half clones of one entity (a counted list), half prefab instances (uncounted), interleaved in one table.
            var original = Stocked(world, new Stock(-1), new Stock(-2));
            var shared = StoreOf(original);
            for (int i = 0; i < rows.Length; i++)
            {
#pragma warning disable EECS031 // each row its own health
                rows[i] = (i % 2 == 0 ? original.Clone() : world.Instantiate(crate)).Set(new Health(i));
#pragma warning restore EECS031
            }

            Assert.That(shared.Shares, Is.EqualTo(rows.Length / 2));
            world.Update();
            for (int i = 0; i < rows.Length; i++) Assert.That(Values(rows[i]), Is.EqualTo(new[] { i, -2 }));
            Assert.That(Values(original), Is.EqualTo(new[] { -1, -2 }));
            Assert.That(Values(world.Instantiate(crate)), Is.EqualTo(new[] { -1, -2 }));
            Assert.That(shared.Shares, Is.LessThanOrEqualTo(0));         // every clone let it go (two may count it at once)
            world.DestroyAll<DynamicBuffer<Stock>>();
        }
    }

    [Test]
    public void APrefabServesWorldsOnSeveralThreadsAtOnce()
    {
        var crate = new Prefab().SetBuffer(new Stock(1), new Stock(2));
        var list = crate.Read<DynamicBuffer<Stock>>().Store;
        int failures = 0;
        var threads = new System.Threading.Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new System.Threading.Thread(() =>
            {
                using var world = new World();
                var spawned = new Entity[256];
                for (int round = 0; round < 50; round++)
                {
                    world.Instantiate(crate, spawned);
                    for (int i = 0; i < spawned.Length; i += 2) spawned[i].Get<DynamicBuffer<Stock>>().AsSpan()[0] = new Stock(i);
                    var clone = spawned[1].Clone();
                    if (Values(clone)[0] != 1 || Values(spawned[2])[0] != 2) System.Threading.Interlocked.Increment(ref failures);
                    world.DestroyAll<DynamicBuffer<Stock>>();
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
        Assert.That(failures, Is.Zero);
        Assert.That(list.Shares, Is.EqualTo(BufferStore.Uncounted));
        Assert.That(crate.Read<DynamicBuffer<Stock>>().AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Stock(1), new Stock(2) }));
    }

    [Test]
    public void ForParallelOverASharedBufferWritesItsOwnCopy()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var original = Stocked(world);
        original.Get<DynamicBuffer<Stock>>().SetCount(4096);
        var clone = original.Clone();
        var job = new Negate();
        world.ForParallel(ref clone.Get<DynamicBuffer<Stock>>(), ref job, 256);
        Assert.That(clone.Read<DynamicBuffer<Stock>>()[100], Is.EqualTo(new Stock(-1)));
        Assert.That(original.Read<DynamicBuffer<Stock>>()[100], Is.EqualTo(new Stock(0)));
    }

    private struct Negate : IForSpan<Stock>
    {
        public void Execute(int start, Span<Stock> items)
        {
            for (int i = 0; i < items.Length; i++) items[i] = new Stock(-1);
        }
    }

    [Test]
    public void SharedListsWrittenAndLetGoDoNotAllocate()
    {
        using var world = new World();
        var crate = new Prefab().SetBuffer(new Stock(1), new Stock(2), new Stock(3), new Stock(4));
        var spawned = new Entity[64];
        var snapshot = world.Capture();

        void Cycle()
        {
            var original = Stocked(world, new Stock(1), new Stock(2));
            world.Instantiate(crate, spawned);                                    // shared, then half written
            for (int i = 0; i < spawned.Length; i += 2) spawned[i].Get<DynamicBuffer<Stock>>().Add(new Stock(i));
            for (int i = 0; i < spawned.Length; i++)
            {
#pragma warning disable EECS031 // clones one by one, half of them written
                var clone = original.Clone();
#pragma warning restore EECS031
                if (i % 2 == 0) clone.Get<DynamicBuffer<Stock>>().AsSpan()[0] = new Stock(i);
            }

            world.Capture(snapshot);                                              // a rollback ring's step
            original.Get<DynamicBuffer<Stock>>().AsSpan()[1] = new Stock(9);
            spawned[1].Get<DynamicBuffer<Stock>>().Add(new Stock(9));
            world.Restore(snapshot);
            world.Restore(snapshot);
            world.DestroyAll<DynamicBuffer<Stock>>();
        }

        for (int i = 0; i < 5; i++) Cycle();
        long before = Allocations.Baseline();
        for (int i = 0; i < 50; i++) Cycle();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    // Chunks unloaded to bytes and loaded back.
    private static void Reload(World world, Nino.Core.NinoArrayBufferWriter bytes, EntityId[] ids, Entity[] loaded)
    {
        bytes.ResetWrittenCount();
        world.SaveEntities(bytes, ids);
        world.DestroyEntities(ids);
        world.LoadEntities(bytes.WrittenSpan, loaded);
        for (int i = 0; i < loaded.Length; i++) ids[i] = loaded[i].Id;
    }

    // A chunk loaded and unloaded: a buffer filled to its size, then destroyed with its entity.
    private static void Cycle(World world, Voxel[] blocks)
    {
        var e = world.CreateEntity(new DynamicBuffer<Voxel>());
        e.Get<DynamicBuffer<Voxel>>().AddRange(blocks);
        e.Destroy();
    }
}
