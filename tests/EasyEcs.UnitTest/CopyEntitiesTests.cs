using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>A copy of a row of another world (a ghost of a neighbor's border): the tag of <c>CopyEntities</c>.</summary>
public struct Replica : IComponent;

/// <summary>A tracked tag: its copies are stamped like a created entity's components.</summary>
public struct TrackedReplica : IComponent, ITracked;

/// <summary>12 bytes: a gathered column of a size without its own load and store.</summary>
public record struct Point3(float X, float Y, float Z) : IComponent;

/// <summary>16 bytes: a gathered column copied as one 128-bit value per row.</summary>
public record struct Pair16(long A, long B) : IComponent;

public class CopyEntitiesTests
{
    private static readonly ComponentSet Positions = ComponentSet.Create().With<Position>();
    private static readonly ComponentSet Record = ComponentSet.Create().With<Position>().With<Health>().With<Follows>()
        .With<Callsign>().With<ChildOf>().With<DynamicBuffer<DynamicBufferTests.Loot>>().With<Lease>().With<Login>();

    [Test]
    public void ACopyHoldsTheSetsComponentsAndTheTagAndTheOriginalsStay()
    {
        using var from = new World();
        using var to = new World();
        to.CreateEntity(new Velocity(9, 9));                                  // ids taken there: the copies get others
        var a = from.CreateEntity(new Position(1, 2), new Velocity(3, 4), new Health(5));
        var b = from.CreateEntity(new Position(6, 7), new Velocity(8, 9));
        var copied = new Entity[2];
        Assert.That(from.CopyEntities<Replica>(to, [a.Id, b.Id], in Positions, copied), Is.EqualTo(2));

        Assert.That(copied[0].Read<Position>(), Is.EqualTo(new Position(1, 2)));
        Assert.That(copied[1].Read<Position>(), Is.EqualTo(new Position(6, 7)));
        foreach (var copy in copied)
            Assert.That((copy.Has<Replica>(), copy.Has<Velocity>(), copy.Has<Health>()), Is.EqualTo((true, false, false)));
        Assert.That((a.IsAlive, b.IsAlive, a.Read<Health>().Value, b.Read<Velocity>()), Is.EqualTo((true, true, 5, new Velocity(8, 9))));
        Assert.That((from.EntityCount, to.EntityCount, to.Count<Replica>()), Is.EqualTo((2, 3, 2)));
    }

    [Test]
    public void ACopyIsTheEntityACreateWouldMake()
    {
        using var from = new World();
        using var made = new World();
        using var copiedTo = new World();
        var ids = new List<EntityId>();
        var given = new List<Position>();
        for (int i = 0; i < 300; i++)
        {
            var e = i % 3 == 0 ? from.CreateEntity(new Position(i, -i), new Velocity(1, 1))
                : i % 3 == 1 ? from.CreateEntity(new Position(i, -i), new Health(i))
                : from.CreateEntity(new Position(i, -i));
            if (i % 5 == 0) continue;                                        // runs of rows with gaps
            ids.Add(e.Id);
            given.Add(new Position(i, -i));
        }

        foreach (var position in given) made.CreateEntity(position, new Replica());
        int count = from.CopyEntities<Replica>(copiedTo, CollectionsMarshal.AsSpan(ids), in Positions);

        Assert.That(count, Is.EqualTo(ids.Count));
        var expected = new List<Position>();
        var actual = new List<Position>();
        foreach (var item in made.Read<Position>(QueryFilter.Create().With<Replica>())) expected.Add(item.Component1);
        foreach (var item in copiedTo.Read<Position>(QueryFilter.Create().With<Replica>())) actual.Add(item.Component1);
        Assert.That(actual, Is.EqualTo(expected));                           // one table, in the order given
        Assert.That(copiedTo.DescribeTables().Length, Is.EqualTo(made.DescribeTables().Length));
    }

    [Test]
    public void RowsScatteredOverOneTableAreGatheredWithTheirValuesPresenceAndTicks()
    {
        using var from = new World();
        using var to = new World();
        var all = new Entity[300];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = from.CreateEntity(new Position(i, -i), new Armor(i), new Name("n" + i), new Health(i), new Point3(i, i + 1, i + 2), new Pair16(i, 2 * i));
            if (i % 4 == 0) all[i].Disable<Health>();
            if (i % 5 == 0) all[i].Remove<Armor>(); // moves to another table: two tables, scattered rows of each
        }

        to.Update();
        ulong mark = to.ChangeTick;
        var ids = new List<EntityId>();
        for (int i = all.Length - 1; i >= 0; i -= 3) ids.Add(all[i].Id);  // backwards, every third row: gathers
        var set = ComponentSet.Create().With<Position>().With<Armor>().With<Name>().With<Health>().With<Point3>().With<Pair16>();
        var copied = new Entity[ids.Count];
        Assert.That(from.CopyEntities<Replica>(to, CollectionsMarshal.AsSpan(ids), in set, copied), Is.EqualTo(ids.Count));

        for (int k = 0; k < ids.Count; k++)
        {
            int i = all.Length - 1 - 3 * k;
            var copy = copied[k];
            Assert.That((copy.Read<Position>(), copy.Read<Name>().Value, copy.Read<Point3>(), copy.Read<Pair16>()),
                Is.EqualTo((new Position(i, -i), "n" + i, new Point3(i, i + 1, i + 2), new Pair16(i, 2 * i))));
            Assert.That((copy.Has<Armor>(), copy.IsDisabled<Health>()), Is.EqualTo((i % 5 != 0, i % 4 == 0)));
            if (i % 5 != 0) Assert.That(copy.Read<Armor>().Value, Is.EqualTo(i));
            if (i % 4 != 0) Assert.That(copy.Read<Health>().Value, Is.EqualTo(i));
        }

        int armored = 0;
        foreach (var id in ids) armored += from.GetEntity(id).Has<Armor>() ? 1 : 0;
        Assert.That(to.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.EqualTo(armored));
    }

    [Test]
    public void LinksInsideTheCopyPointAtTheCopiesAndOthersAreNull()
    {
        using var from = new World();
        using var to = new World();
        var guild = from.CreateEntity(new Health(0));                           // not copied
        var player = from.CreateEntity(new Health(10), new Follows(guild));
        var sword = from.CreateEntity(new ChildOf(player), new Follows(player), new Callsign("sword"));
        var orphan = from.CreateEntity(new ChildOf(guild), new Health(1));       // its parent stays behind
        player.Set(new DynamicBuffer<DynamicBufferTests.Loot>());
        var copied = new Entity[3];
        Assert.That(from.CopyEntities<Replica>(to, [player.Id, sword.Id, orphan.Id], in Record, copied), Is.EqualTo(3));

        var (p, s, o) = (copied[0], copied[1], copied[2]);
        Assert.That((p.Read<Follows>().Target, s.Read<Follows>().Target, s.Read<ChildOf>().Parent), Is.EqualTo((EntityId.Null, p.Id, p.Id)));
        Assert.That((o.Has<ChildOf>(), o.Read<Health>().Value), Is.EqualTo((false, 1)));  // a root here, as a load makes it
        Assert.That(to.Index<Follows, EntityId>().Count(p), Is.EqualTo(1));
        Assert.That(to.Index<Callsign, string>().Count("sword"), Is.EqualTo(1));
        // Without handles asked for, the links are rewritten all the same.
        using var third = new World();
        from.CopyEntities<Replica>(third, [player.Id, sword.Id], in Record);
        Assert.That(third.Index<Callsign, string>().TryFind("sword", out var swordThere), Is.True);
        var playerThere = third.GetEntity(swordThere.Read<Follows>().Target);
        Assert.That((playerThere.Read<Health>().Value, swordThere.Read<ChildOf>().Parent), Is.EqualTo((10, playerThere.Id)));

        // The originals keep their links.
        Assert.That((player.Read<Follows>().Target, sword.Read<ChildOf>().Parent, orphan.Read<ChildOf>().Parent),
            Is.EqualTo((guild.Id, player.Id, guild.Id)));
    }

    [Test]
    public void RelationsAndBufferElementsAreRemappedWithoutTouchingTheOriginals()
    {
        using var from = new World();
        using var to = new World();
        var red = from.CreateEntity(new Health(1));
        var blue = from.CreateEntity(new Health(2));
        var member = from.CreateEntity(new Health(3)).Link(new MemberOf(red)).Link(new MemberOf(blue));
        var bag = from.CreateEntity(new DynamicBuffer<DynamicBufferTests.Loot>());
        bag.Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(7, 2));
        var set = ComponentSet.Create().With<Health>().With<DynamicBuffer<MemberOf>>().With<DynamicBuffer<DynamicBufferTests.Loot>>();
        var copied = new Entity[3];
        from.CopyEntities<Replica>(to, [red.Id, member.Id, bag.Id], in set, copied);

        ref readonly var links = ref copied[1].Read<DynamicBuffer<MemberOf>>();
        Assert.That((links.Count, links[0].Target, links[1].Target), Is.EqualTo((2, copied[0].Id, EntityId.Null)));
        Assert.That((member.Links<MemberOf>(red), member.Links<MemberOf>(blue)), Is.EqualTo((true, true)));

        // A copied buffer is the copy's own: writing it leaves the original's list as it was.
        copied[2].Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(8, 1));
        Assert.That((bag.Read<DynamicBuffer<DynamicBufferTests.Loot>>().Count, copied[2].Read<DynamicBuffer<DynamicBufferTests.Loot>>().Count),
            Is.EqualTo((1, 2)));
    }

    [Test]
    public void StaleRecycledRepeatedAndForeignIdsAreSkipped()
    {
        using var from = new World();
        using var other = new World();
        using var to = new World();
        var dead = from.CreateEntity(new Position(0, 0));
        var deadId = dead.Id;
        dead.Destroy();
        var recycled = from.CreateEntity(new Position(1, 1));                    // reuses dead's index, a new version
        for (int i = 0; i < 50; i++) other.CreateEntity(new Position(i, i));
        var foreign = other.CreateEntity(new Position(5, 5));                    // an index this world never had
        var copied = new Entity[5];

        int count = from.CopyEntities<Replica>(to, [deadId, recycled.Id, recycled.Id, foreign.Id, default], in Positions, copied);

        Assert.That((count, to.EntityCount), Is.EqualTo((1, 1)));
        Assert.That(copied, Is.EqualTo(new[] { default, copied[1], copied[1], default, default(Entity) }));
        Assert.That(copied[1].Read<Position>(), Is.EqualTo(new Position(1, 1)));
        Assert.That(from.CopyEntities<Replica>(to, [], in Positions), Is.Zero);
        Assert.That(to.EntityCount, Is.EqualTo(1));

        // Without handles and links nothing maps the ids: repeats are found by the order of the ids, then by stamps.
        var a = from.CreateEntity(new Position(2, 2));
        var b = from.CreateEntity(new Position(3, 3));
        Assert.That(from.CopyEntities<Replica>(to, [recycled.Id, a.Id, b.Id, b.Id], in Positions), Is.EqualTo(3));
        Assert.That(from.CopyEntities<Replica>(to, [a.Id, b.Id, a.Id, recycled.Id, b.Id], in Positions), Is.EqualTo(3));
        Assert.That(from.CopyEntities<Replica>(to, [b.Id, a.Id, b.Id, deadId], in Positions), Is.EqualTo(2));
        Assert.That(to.Count<Replica>(), Is.EqualTo(1 + 3 + 3 + 2));
    }

    [Test]
    public void AComponentOfTheSetTheRowLacksIsNotGivenToTheCopy()
    {
        using var from = new World();
        using var to = new World();
        var bare = from.CreateEntity(new Velocity(1, 1));
        var full = from.CreateEntity(new Velocity(1, 1), new Health(4));
        var copied = new Entity[2];
        from.CopyEntities<Replica>(to, [bare.Id, full.Id], in Record, copied);
        Assert.That((copied[0].Has<Replica>(), copied[0].Has<Health>(), copied[0].Has<Velocity>()), Is.EqualTo((true, false, false)));
        Assert.That(copied[1].Read<Health>().Value, Is.EqualTo(4));
    }

    [Test]
    public void ADisabledComponentArrivesDisabledWithItsValue()
    {
        using var from = new World();
        using var to = new World();
        var e = from.CreateEntity(new Position(1, 1), new Health(42));
        e.Disable<Health>();
        var copied = new Entity[1];
        from.CopyEntities<Replica>(to, [e.Id], in Record, copied);
        Assert.That((copied[0].Has<Health>(), copied[0].IsDisabled<Health>()), Is.EqualTo((false, true)));
        copied[0].Enable<Health>();
        Assert.That(copied[0].Read<Health>().Value, Is.EqualTo(42));

        // Not in the set: neither the component nor its tag comes along.
        var onlyPositions = new Entity[1];
        from.CopyEntities<Replica>(to, [e.Id], in Positions, onlyPositions);
        Assert.That((onlyPositions[0].IsDisabled<Health>(), onlyPositions[0].Has<Disabled<Health>>()), Is.EqualTo((false, false)));
    }

    [Test]
    public void CleanupComponentsAndRetiredEntitiesAreNotCopied()
    {
        using var from = new World();
        using var to = new World();
        var leased = from.CreateEntity(new Position(1, 1), new Lease(3));
        var retired = from.CreateEntity(new Position(2, 2), new Lease(4));
        retired.Destroy();                                                       // kept alive for its lease: retired
        var copied = new Entity[2];
        Assert.That(from.CopyEntities<Replica>(to, [leased.Id, retired.Id], in Record, copied), Is.EqualTo(1));
        Assert.That((copied[0].Has<Lease>(), copied[0].Read<Position>(), copied[1]), Is.EqualTo((false, new Position(1, 1), default(Entity))));
        Assert.That(leased.Read<Lease>().Slot, Is.EqualTo(3));
    }

    [Test]
    public void AUniqueKeyTheTargetHoldsCopiesNothing()
    {
        using var from = new World();
        using var to = new World();
        var a = from.CreateEntity(new Login(1), new Position(1, 1));
        var b = from.CreateEntity(new Login(2), new Position(2, 2));
        to.CreateEntity(new Login(2));
        Assert.Throws<InvalidOperationException>(() => from.CopyEntities<Replica>(to, [a.Id, b.Id], in Record));
        Assert.That(to.EntityCount, Is.EqualTo(1));

        // A unique component outside the set is no clash: it does not come along.
        Assert.That(from.CopyEntities<Replica>(to, [a.Id, b.Id], in Positions), Is.EqualTo(2));
        // Copied keys are indexed: copying the same rows again clashes with the first copies.
        using var fresh = new World();
        from.CopyEntities<Replica>(fresh, [a.Id, b.Id], in Record);
        Assert.That(fresh.Index<Login, int>().Get(2).Read<Position>(), Is.EqualTo(new Position(2, 2)));
        Assert.Throws<InvalidOperationException>(() => from.CopyEntities<Replica>(fresh, [a.Id], in Record));
    }

    [Test]
    public void TheCopiesCountAsWrittenLikeACreate()
    {
        using var from = new World();
        using var to = new World();
        var e = from.CreateEntity(new Armor(3), new Position(1, 1));
        to.Update();
        ulong mark = to.ChangeTick;
        var set = ComponentSet.Create().With<Armor>();
        var copied = new Entity[1];
        from.CopyEntities<TrackedReplica>(to, [e.Id], in set, copied);
        Assert.That(to.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.EqualTo(1));
        Assert.That(to.Count<TrackedReplica>(QueryFilter.Create().Changed<TrackedReplica>(mark)), Is.EqualTo(1));
        Assert.That(copied[0].Read<Armor>().Value, Is.EqualTo(3));
    }

    [Test]
    public void GroupsAndIndexesOfTheTargetLearnTheCopies()
    {
        using var from = new World();
        using var to = new World();
        to.GroupBy<Zone>();
        var a = from.CreateEntity(new Zone(1), new Callsign("a"));
        var b = from.CreateEntity(new Zone(2), new Callsign("b"));
        var set = ComponentSet.Create().With<Zone>().With<Callsign>();
        var copied = new Entity[2];
        from.CopyEntities<Replica>(to, [a.Id, b.Id], in set, copied);
        Assert.That(to.Index<Callsign, string>().Count("b"), Is.EqualTo(1));
        Assert.That(to.Count<Replica>(QueryFilter.Create().InGroup<Zone>(2)), Is.EqualTo(1));
        Assert.That(copied[1].Read<Zone>().Id, Is.EqualTo(2));
    }

    [Test]
    public void APassIteratingTheTargetDoesNotVisitTheCopies()
    {
        using var from = new World();
        using var to = new World();
        var e = from.CreateEntity(new Position(1, 1));
        to.CreateEntity(new Position(0, 0));
        int visited = 0;
        foreach (var _ in to.Query<Position>())
        {
            visited++;
            from.CopyEntities<Replica>(to, [e.Id], in Positions);
        }

        Assert.That((visited, to.Count<Replica>()), Is.EqualTo((1, 1)));

        // The source may be iterated too: it is only read.
        int copies = 0;
        foreach (var _ in from.Query<Position>()) copies += from.CopyEntities<Replica>(to, [e.Id], in Positions);
        Assert.That((copies, to.Count<Replica>()), Is.EqualTo((1, 2)));
    }

    [Test]
    public void APrefabsInstancesCopyTheirSharedBuffersAsTheirOwn()
    {
        using var from = new World();
        using var to = new World();
        var chest = new Prefab().Set(new Position(0, 0)).Set(new DynamicBuffer<DynamicBufferTests.Loot>());
        var instance = from.Instantiate(chest);
        instance.Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(1, 1));
        var plain = from.Instantiate(chest);                                     // still shares the prefab's list
        var copied = new Entity[2];
        from.CopyEntities<Replica>(to, [instance.Id, plain.Id], in Record, copied);
        copied[1].Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(2, 2));
        Assert.That((copied[0].Read<DynamicBuffer<DynamicBufferTests.Loot>>().Count, copied[1].Read<DynamicBuffer<DynamicBufferTests.Loot>>().Count),
            Is.EqualTo((1, 1)));
        Assert.That((plain.Read<DynamicBuffer<DynamicBufferTests.Loot>>().Count, from.Instantiate(chest).Read<DynamicBuffer<DynamicBufferTests.Loot>>().Count),
            Is.EqualTo((0, 0)));

        // The target's prefab tables and its copy tables are cached apart.
        var instanceThere = to.Instantiate(chest);
        Assert.That((instanceThere.Has<Replica>(), copied[0].Has<Replica>()), Is.EqualTo((false, true)));
    }

    [Test]
    public void ManagedComponentsAreCopied()
    {
        using var from = new World();
        using var to = new World();
        var e = from.CreateEntity(new Name("unit"), new Position(1, 1));
        var copied = new Entity[1];
        from.CopyEntities<Replica>(to, [e.Id], ComponentSet.Create().With<Name>(), copied);
        Assert.That(copied[0].Read<Name>().Value, Is.EqualTo("unit"));
    }

    [Test]
    public void TheTagHoldsItsDefaultEvenWhereAnOldRowHeldAValue()
    {
        using var from = new World();
        using var to = new World();
        var e = from.CreateEntity(new Position(1, 1));
        for (int i = 0; i < 3; i++)
        {
            to.DestroyAll<Health>();
            var copied = new Entity[1];
            from.CopyEntities<Health>(to, [e.Id], in Positions, copied);         // a tag with a field: its default
            Assert.That(copied[0].Read<Health>().Value, Is.Zero);
            copied[0].Get<Health>().Value = 99;                                  // left in the column for the next copy
        }
    }

    [Test]
    public void WrongCallsThrow()
    {
        using var from = new World();
        using var to = new World();
        var e = from.CreateEntity(new Position(1, 1));
        Assert.Throws<ArgumentException>(() => from.CopyEntities<Replica>(from, [e.Id], in Positions));
        Assert.Throws<ArgumentNullException>(() => from.CopyEntities<Replica>(null!, [e.Id], in Positions));
        Assert.Throws<ArgumentException>(() => from.CopyEntities<Replica>(to, [e.Id], ComponentSet.Create().WithSingleton<Score>()));
        Assert.Throws<ArgumentException>(() => from.CopyEntities<Login>(to, [e.Id], in Positions));
        Assert.That(to.EntityCount, Is.Zero);
    }

    [Test]
    public void AGhostExchangeEveryTickAllocatesNothing()
    {
        using var from = new World();
        using var to = new World();
        var ids = new List<EntityId>();
        for (int i = 0; i < 1_000; i++)
        {
            var e = from.CreateEntity(new Position(i, 0), new Velocity(1, 0), new Health(i));
            if (i % 4 != 0) ids.Add(e.Id);
        }

        var copied = new Entity[ids.Count];
        var set = ComponentSet.Create().With<Position>().With<Health>();
        void Exchange()
        {
            to.DestroyAll<Replica>();
            from.CopyEntities<Replica>(to, CollectionsMarshal.AsSpan(ids), in set, copied);
        }

        Allocations.WarmUp(Exchange, 10);
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 50; i++) Exchange(); }), Is.Zero);
        Assert.That((to.Count<Replica>(), copied[^1].Read<Health>().Value), Is.EqualTo((ids.Count, 999)));
    }
}
