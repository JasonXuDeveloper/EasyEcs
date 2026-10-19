using System;
using NUnit.Framework;
using Loot = EasyEcs.UnitTest.DynamicBufferTests.Loot;

namespace EasyEcs.UnitTest;

public class InstantiateTests
{
    [Test]
    public void APrefabBelongsToNoWorld()
    {
        using var world = new World();
        var orc = new Prefab().Set(new Position(0, 0)).Set(new Health(30)).Set(new Armor(5));
        Assert.That(world.EntityCount, Is.Zero);                        // building it creates nothing anywhere
        var spawned = new Entity[100];
        world.Instantiate(orc, spawned);
        Assert.That(world.Count<Health>(), Is.EqualTo(100));
        Assert.That(spawned[42].World, Is.SameAs(world));
        Assert.That(spawned[42].Get<Health>(), Is.EqualTo(new Health(30)));
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(world.ChangeTick)), Is.EqualTo(100)); // written now
        Assert.That(world.Count<Health>(QueryFilter.Create().Removed<Armor>(1)), Is.Zero);
        var one = world.Instantiate(orc);                              // the table is found once, then cached
        Assert.That(one.Get<Armor>(), Is.EqualTo(new Armor(5)));
    }

    [Test]
    public void OnePrefabServesManyWorldsAndCanBeChanged()
    {
        using var a = new World();
        using var b = new World();
        var orc = new Prefab().Set(new Health(30)).Set(new Armor(5));
        Assert.That(a.Instantiate(orc).Get<Health>(), Is.EqualTo(new Health(30)));
        orc.Set(new Health(40)).Remove<Armor>().Remove<Armor>();      // removing what it lacks is a no-op
        Assert.That(orc.Has<Armor>(), Is.False);
        Assert.That(orc.Read<Health>(), Is.EqualTo(new Health(40)));
        var e = b.Instantiate(orc);
        Assert.That(e.Get<Health>(), Is.EqualTo(new Health(40)));
        Assert.That(e.Has<Armor>(), Is.False);
        Assert.That(b.Count<Health>(QueryFilter.Create().Removed<Armor>(1)), Is.Zero); // lacking is not losing
    }

    [Test]
    public void ACloneCopiesEveryComponentAndIsIndependent()
    {
        using var world = new World();
        var source = world.CreateEntity(new Position(1, 2), new Health(10), new Name("orc"));
        source.Remove<Health>();                                       // a component the row lacks stays lacking
        var clone = source.Clone();
        Assert.That(clone, Is.Not.EqualTo(source));
        Assert.That(clone.Get<Position>(), Is.EqualTo(new Position(1, 2)));
        Assert.That(clone.Get<Name>().Value, Is.EqualTo("orc"));
        Assert.That(clone.Has<Health>(), Is.False);
        clone.Get<Position>().X = 5;
        Assert.That(source.Get<Position>().X, Is.EqualTo(1));
        Assert.That(world.Count<Position>(), Is.EqualTo(2));
    }

    [Test]
    public void DynamicBuffersAreCopiedNotShared()
    {
        using var world = new World();
        var source = world.CreateEntity(new DynamicBuffer<Loot>());
        source.Get<DynamicBuffer<Loot>>().Add(new Loot(1, 2));
        var clone = source.Clone();
        clone.Get<DynamicBuffer<Loot>>().Add(new Loot(3, 4));
        Assert.That(source.Read<DynamicBuffer<Loot>>().Count, Is.EqualTo(1));
        Assert.That(clone.Read<DynamicBuffer<Loot>>().Count, Is.EqualTo(2));

        var chest = new Prefab().Set(new DynamicBuffer<Loot>());
        world.Instantiate(chest).Get<DynamicBuffer<Loot>>().Add(new Loot(5, 6));
        Assert.That(world.Instantiate(chest).Read<DynamicBuffer<Loot>>().Count, Is.Zero);
    }

    [Test]
    public void APrefabsBufferElementsReachEveryInstanceInAListOfItsOwn()
    {
        using var world = new World();
        var chest = new Prefab().SetBuffer(new Loot(1, 2), new Loot(3, 4));
        var first = world.Instantiate(chest);
        first.Get<DynamicBuffer<Loot>>().Add(new Loot(5, 6));
        var second = world.Instantiate(chest);
        Assert.That(second.Read<DynamicBuffer<Loot>>().AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Loot(1, 2), new Loot(3, 4) }));
        Assert.That(first.Read<DynamicBuffer<Loot>>().Count, Is.EqualTo(3));

        chest.SetBuffer(new Loot(7, 8));                                 // replaces the recipe's elements
        Assert.That(world.Instantiate(chest).Read<DynamicBuffer<Loot>>().AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Loot(7, 8) }));
        Assert.That(chest.Read<DynamicBuffer<Loot>>().Count, Is.EqualTo(1));
    }

    [Test]
    public void ADerivedPrefabCopiesItsBaseAndChangesOnItsOwn()
    {
        using var world = new World();
        var orc = new Prefab().Set(new Health(30)).Set(new Armor(5)).Set(new DynamicBuffer<Loot>());
        var chief = new Prefab(orc).Set(new Health(90)).Remove<Armor>();
        orc.Set(new Health(31));                                        // later changes of the base stay its own
        var e = world.Instantiate(chief);
        Assert.That((e.Get<Health>().Value, e.Has<Armor>(), e.Has<DynamicBuffer<Loot>>()), Is.EqualTo((90, false, true)));
        Assert.That((orc.Read<Health>().Value, orc.Has<Armor>()), Is.EqualTo((31, true)));
    }

    [Test]
    public void ReservingForAPrefabMakesItsInstancesGrowNothing()
    {
        var orc = new Prefab().Set(new Health(30)).Set(new Armor(5));
        using (var warm = new World()) warm.Instantiate(orc).Destroy(); // JIT and caches
        using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
        world.EnsureCapacity(orc, 5000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) world.Instantiate(orc);
        Assert.That((GC.GetAllocatedBytesForCurrentThread() - before, world.EntityCount), Is.EqualTo((0L, 5000)));
    }

    [Test]
    public void APassGoesOnInTheGrownColumnsAfterReservingForAPrefab()
    {
        var orc = new Prefab().Set(new Health(30));
        using var world = new World(new WorldOptions { InitialEntityCapacity = 20_000 }); // only the table grows below
        for (int i = 0; i < 100; i++) world.Instantiate(orc);
        int visited = 0;
        foreach (var item in world.Query<Health>())
        {
            item.Health.Value = 1; // before the reservation: a ref is valid until the next structural change
            if (visited++ == 10) world.EnsureCapacity(orc, 10_000); // the columns move under the pass
        }

        int written = 0;
        foreach (var item in world.Query<Health>()) written += item.Health.Value;
        Assert.That(written, Is.EqualTo(100));
    }

    [Test]
    public void IndexesLearnTheNewEntities()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var children = world.Index<ChildOf, EntityId>();
        var callsigns = world.Index<Callsign, string>();
        Assert.That(children.Count(parent), Is.Zero);                 // builds the indexes
        var child = world.CreateEntity(new ChildOf(parent), new Callsign("x"));
        child.Clone();
        world.Instantiate(new Prefab().Set(new Callsign("x")));
        Assert.That(children.Count(parent), Is.EqualTo(2));
        Assert.That(callsigns.Count("x"), Is.EqualTo(3));
    }

    [Test]
    public void CloningAStaleEntityThrows()
    {
        using var world = new World();
        var e = world.CreateEntity(new Position());
        e.Destroy();
        Assert.Throws<InvalidOperationException>(() => e.Clone());
        Assert.Throws<InvalidOperationException>(() => default(Entity).Clone());
        Assert.Throws<InvalidOperationException>(() => new Prefab().Read<Position>());
    }

    [Test]
    public void ABulkCopySpanningManyWordsKeepsTicksAndAbsentComponents()
    {
        using var world = new World();
        world.CreateEntities(new Entity[37], new Armor(1), new Seen());   // the copies start inside a word
        world.Update();
        ulong now = world.ChangeTick;
        var prefab = new Prefab().Set(new Armor(2)).Set(new Seen(3)).Set(new Position(4, 5)).Remove<Position>();
        var copies = new Entity[5_000];                                   // more than one 4,096-row block
        world.Instantiate(prefab, copies);
        var changed = QueryFilter.Create().Changed<Armor>(now);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(copies.Length));        // only the copies are new
        Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(1)), Is.Zero);
        var lacking = world.Instantiate(new Prefab().Set(new Seen()).Set(new Armor()).Remove<Armor>());
        Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(0)), Is.Zero); // tick 0: lacking, not lost
        lacking.Set(new Armor(1)).Remove<Armor>();
        Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(0)), Is.EqualTo(1));
        Assert.That(world.Count<Position>(), Is.Zero);                              // absent in every copy
        foreach (var e in copies)
            Assert.That((e.Read<Armor>(), e.Read<Seen>(), e.Has<Position>()), Is.EqualTo((new Armor(2), new Seen(3), false)));
        copies[4_999].Set(new Position(1, 1));                            // a column the table has: a bit flip
        Assert.That(world.Count<Position>(), Is.EqualTo(1));
    }

    [Test]
    public void BulkCopiesAndCreationsAreIndexed()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var children = world.Index<ChildOf, EntityId>();
        var callsigns = world.Index<Callsign, string>();
        Assert.That(children.Count(parent), Is.Zero);                     // builds the indexes
        world.CreateEntities(new Entity[100], new ChildOf(parent), new Callsign("a"));
        world.Instantiate(new Prefab().Set(new ChildOf(parent)).Set(new Callsign("b")), new Entity[50]);
        Assert.That(children.Count(parent), Is.EqualTo(150));
        Assert.That((callsigns.Count("a"), callsigns.Count("b")), Is.EqualTo((100, 50)));
    }

    [Test]
    public void PrefabsHaveTheNamedMembersOfEveryComponent()
    {
        var orc = new Prefab().Set(new Position(1, 2), new Armor(3))     // several at once
            .SetHealth(30).SetFrozen().SetStun(2);                       // generated: one per component
        Assert.That((orc.HasHealth, orc.IsFrozen, orc.Health.Value, orc.Armor.Value), Is.EqualTo((true, true, 30, 3)));
        orc.RemoveFrozen().Remove<Stun, Position>();
        Assert.That((orc.IsFrozen, orc.HasStun, orc.HasPosition), Is.EqualTo((false, false, false)));
        using var world = new World();
        var e = world.Instantiate(orc);
        Assert.That((e.Health.Value, e.IsFrozen, e.HasPosition), Is.EqualTo((30, false, false)));
    }

    [Test]
    public void APrefabWithMembersCreatesTheWholeGroupLinked()
    {
        using var world = new World();
        var boss = new Prefab().Set(new Health(500));
        var mount = boss.AddMember().Set(new Health(80));
        var minion = boss.AddMember().Set(new Health(20));
        mount.Set(new ChildOf(boss.Id));
        minion.Set(new ChildOf(boss.Id), new Follows(mount.Id));
        boss.Set(new Follows(new EntityId(99, 1)));                     // not a member: no entity of the instance

        var first = world.Instantiate(boss);
        var second = world.Instantiate(minion);                         // any member creates the group
        Assert.That(world.EntityCount, Is.EqualTo(6));
        Assert.That((first.Read<Health>().Value, first.Read<Follows>().Target), Is.EqualTo((500, EntityId.Null)));
        Assert.That(world.Index<ChildOf, EntityId>().Count(first), Is.EqualTo(2));
        var parent = world.GetEntity(second.Read<ChildOf>().Parent);
        Assert.That((second.Read<Health>().Value, parent.Read<Health>().Value), Is.EqualTo((20, 500)));
        Assert.That(world.GetEntity(second.Read<Follows>().Target).Read<Health>().Value, Is.EqualTo(80));
        Assert.That(parent, Is.Not.EqualTo(first));
        first.Destroy();                                                // the cascade takes its own group only
        Assert.That(world.EntityCount, Is.EqualTo(3));
    }

    [Test]
    public void ADerivedGroupCopiesEveryMemberAndReservingCoversThem()
    {
        using var world = new World();
        var squad = new Prefab().Set(new Health(1));
        squad.AddMember().Set(new ChildOf(squad.Id), new Armor(3));
        var elite = new Prefab(squad).Set(new Health(9));
        world.EnsureCapacity(elite, 11);
        var leaders = new Entity[10];
        world.Instantiate(elite, leaders);                              // warms the copy's buffers
        foreach (var leader in leaders) leader.Destroy();
        long before = GC.GetAllocatedBytesForCurrentThread();
        world.Instantiate(elite, leaders);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That((world.EntityCount, world.Count<Armor>(), leaders[3].Read<Health>().Value), Is.EqualTo((20, 10, 9)));
        Assert.That(world.Index<ChildOf, EntityId>().Count(leaders[3]), Is.EqualTo(1));
        Assert.That(world.Instantiate(squad).Read<Health>().Value, Is.EqualTo(1)); // the base kept its values
    }
}
