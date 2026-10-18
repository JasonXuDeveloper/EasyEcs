using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class DynamicBufferTests
{
    public record struct Loot(int Item, int Count);

    /// <summary>Counts, per entity, the runs in which its bag had changed since the system's previous run.</summary>
    public class BagWatch : EcsSystem
    {
        [ForEach, Changed<DynamicBuffer<Loot>>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>Stunned entities drop their first item: a write through a <c>ref</c> parameter.</summary>
    public class Drop : EcsSystem
    {
        [ForEach, With<Stun>]
        private static void Apply(ref DynamicBuffer<Loot> bag)
        {
            if (bag.Count > 0) bag.RemoveAt(0);
        }
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
}
