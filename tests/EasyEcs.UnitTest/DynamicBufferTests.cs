using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class DynamicBufferTests
{
    public record struct Loot(int Item, int Count);
    public enum Tile : byte { Air, Stone }
    public enum Voxel : byte { Air, Stone }   // PoolCapacity is process-wide: only this test sets it
    public enum Ore : byte { Air, Iron }      // and only the reload test sets this one's
    public record struct Mail(long Value);    // the pool of this element type serves only the tests below

    /// <summary>Counts, per entity, the runs in which its bag had changed since the system's previous run.</summary>
    public class BagWatch : EcsSystem
    {
        [ForEach, Changed<DynamicBuffer<Loot>>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>
    /// Stunned entities drop their first item: a write through a <c>ref</c> parameter, which stamps every row it visits
    /// (also an empty bag it leaves alone: the stamp this test checks; EECS053 points real code to a Mut&lt;T&gt;).
    /// </summary>
    public class Drop : EcsSystem
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
    public class Weigh : EcsSystem
    {
        [ForEach]
        private static void Sum(in DynamicBuffer<Loot> bag, ref Health weight)
        {
            int total = 0;
            foreach (var loot in bag) total += loot.Count;
            weight = new Health(total);
        }
    }

    /// <summary>Refills every bag with one item, in place: no allocation once the capacity is reached.</summary>
    public class Refill : EcsSystem
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
        bag.Clear();
        bag.TrimExcess();
        Assert.That(bag.Capacity, Is.Zero);

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

        long before = GC.GetAllocatedBytesForCurrentThread();
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
        long before = GC.GetAllocatedBytesForCurrentThread();
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
    public void PoolCapacityKeepsLongerListsForTheNextBuffer()
    {
        Assert.That(DynamicBuffer<Voxel>.PoolCapacity, Is.EqualTo(4096));   // 4,096 bytes of one-byte elements
        Assert.That(DynamicBuffer<Loot>.PoolCapacity, Is.EqualTo(512));     // of eight-byte ones
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicBuffer<Voxel>.PoolCapacity = -1);

        using var world = new World();
        var chunk = new Voxel[8192];
        try
        {
            DynamicBuffer<Voxel>.PoolCapacity = chunk.Length;
            Cycle(world, chunk);
            Cycle(world, chunk);                                             // warm: the table, the pool
            long before = GC.GetAllocatedBytesForCurrentThread();
            Cycle(world, chunk);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);   // the list came back

            DynamicBuffer<Voxel>.PoolCapacity = 4096;
            Cycle(world, chunk);                                             // takes the pooled list, keeps it not
            before = GC.GetAllocatedBytesForCurrentThread();
            Cycle(world, chunk);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.GreaterThanOrEqualTo(chunk.Length));
        }
        finally
        {
            DynamicBuffer<Voxel>.PoolCapacity = 4096;
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
        ores.SetCount(4);                                     // the pooled list: its old values must not show
        Assert.That(ores.AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { Ore.Air, Ore.Air, Ore.Air, Ore.Air }));
        ores.AsSpan()[3] = Ore.Iron;
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
        long before = GC.GetAllocatedBytesForCurrentThread();
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
            DynamicBuffer<Ore>.PoolCapacity = blocks.Length;
            using var world = new World();
            world.CreateEntities(loaded, new DynamicBuffer<Ore>());
            for (int i = 0; i < loaded.Length; i++)
            {
                loaded[i].Get<DynamicBuffer<Ore>>().AddRange(blocks);
                ids[i] = loaded[i].Id;
            }

            for (int i = 0; i < 3; i++) Reload(world, bytes, ids, loaded);    // warm: the tables, the pool, the load world
            long before = GC.GetAllocatedBytesForCurrentThread();
            Reload(world, bytes, ids, loaded);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(blocks.Length));   // no list copied
            Assert.That(loaded[0].Get<DynamicBuffer<Ore>>().Count, Is.EqualTo(blocks.Length));
        }
        finally
        {
            DynamicBuffer<Ore>.PoolCapacity = 4096;
        }
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
