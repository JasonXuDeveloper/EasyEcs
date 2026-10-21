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
        Assert.That(one.Read<Armor>(), Is.EqualTo(new Armor(5)));
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
        world.Reserve(orc, 5000);
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
            if (visited++ == 10) world.Reserve(orc, 10_000); // the columns move under the pass
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
    public void APrefabWithMembersIsInstantiatedDuringAPass()
    {
        using var world = new World();
        var boss = new Prefab().Set(new Health(500));
        boss.AddMember().Set(new ChildOf(boss.Id), new Health(20));
        for (int i = 0; i < 3; i++) world.Instantiate(boss);
        int visited = 0;
        foreach (var item in world.Query<Health>())                     // a spawner reacting to what it walks
        {
            if (item.Health.Value == 500) world.Instantiate(boss);
            visited++;
        }

        Assert.That((visited, world.EntityCount, world.Count<ChildOf>()), Is.EqualTo((6, 12, 6)));
        var spawned = new Entity[2];
        foreach (var item in world.Query<Health>())
            if (item.Health.Value == 500)
            {
                world.Instantiate(boss, spawned);
                break;
            }

        Assert.That(world.Index<ChildOf, EntityId>().Count(spawned[1]), Is.EqualTo(1));
    }

    [Test]
    public void ADerivedGroupCopiesEveryMemberAndReservingCoversThem()
    {
        using var world = new World();
        var squad = new Prefab().Set(new Health(1));
        squad.AddMember().Set(new ChildOf(squad.Id), new Armor(3));
        var elite = new Prefab(squad).Set(new Health(9));
        world.Reserve(elite, 11);
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

    [Test]
    public void GivenComponentsJoinTheCopyInItsFinalTable()
    {
        using var world = new World();
        var orc = new Prefab().Set(new Position(1, 2)).Set(new Health(30)).Set(new Armor(5));
        world.Update();
        ulong now = world.ChangeTick;
        var e = world.Instantiate(orc, new Health(50), new Shield(3));  // one replaced, one added
        Assert.That((e.Read<Position>(), e.Read<Health>(), e.Read<Armor>(), e.Read<Shield>()),
            Is.EqualTo((new Position(1, 2), new Health(50), new Armor(5), new Shield(3))));
        Assert.That(orc.Read<Health>(), Is.EqualTo(new Health(30)));       // the prefab keeps its value
        var tables = world.Describe();                                    // the empty table and the final one: no table
        Assert.That(tables.Length, Is.EqualTo(2));                        // of the prefab's components alone
        Assert.That(Array.Find(tables, t => t.Entities == 1).Rows, Is.EqualTo(1));
        var since = QueryFilter.Create().Changed<Armor>(now);             // copied and given values count as written
        Assert.That((world.Count<Armor>(since), world.Count<Shield>(QueryFilter.Create().Changed<Shield>(now))), Is.EqualTo((1, 1)));
        Assert.That(world.Instantiate(orc, new Velocity(1, 1)).Read<Health>(), Is.EqualTo(new Health(30)));
        Assert.That(world.Describe().Length, Is.EqualTo(3));               // another signature, another final table
    }

    [Test]
    public void AGivenChildOfLinksTheHierarchy()
    {
        using var world = new World();
        var parent = world.CreateEntity(new Health(1));
        var minion = new Prefab().Set(new Health(20));
        var children = world.Index<ChildOf, EntityId>();
        Assert.That(children.Count(parent), Is.Zero);                     // builds the index
        var one = world.Instantiate(minion, new ChildOf(parent), new Armor(1));
        world.Instantiate(minion, new Entity[4], new ChildOf(parent), new Armor(1));
        Assert.That((children.Count(parent), one.Read<ChildOf>().Parent), Is.EqualTo((5, parent.Id)));
        parent.Destroy();                                                  // the cascade takes them all
        Assert.That(world.EntityCount, Is.Zero);
    }

    [Test]
    public void GivenComponentsReachIndexesRelationsAndGroups()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        var red = world.CreateEntity();
        var callsigns = world.Index<Callsign, string>();
        var members = world.Relations<MemberOf>();
        Assert.That((callsigns.Count("a"), members.Count(red)), Is.EqualTo((0, 0))); // builds the indexes
        var scout = new Prefab().Set(new Callsign("a")).Set(new Zone(1)).SetBuffer(new MemberOf(red));
        var e = world.Instantiate(scout, new Callsign("b"), new Zone(2));
        world.Instantiate(scout, new Entity[3], new Health(1));            // the prefab's values, in its zone
        Assert.That((callsigns.Count("a"), callsigns.Count("b"), members.Count(red)), Is.EqualTo((3, 1, 4)));
        Assert.That(callsigns.TryFind("b", out var found) && found == e, Is.True);
        Assert.That(world.Count<Callsign>(QueryFilter.Create().InGroup<Zone>(2)), Is.EqualTo(1));
        Assert.That(world.Count<Health>(QueryFilter.Create().InGroup<Zone>(1)), Is.EqualTo(3));
        e.RemoveRelation<MemberOf>(red);                                   // a list of its own
        Assert.That((scout.Read<DynamicBuffer<MemberOf>>().Count, members.Count(red)), Is.EqualTo((1, 3)));
    }

    [Test]
    public void AGivenComponentTheRecipeLacksOrKeepsDisabledIsPresentAndEnabled()
    {
        using var world = new World();
        var orc = new Prefab().Set(new Health(30)).Set(new Position(1, 1)).Remove<Position>();
        orc.Row.Disable<Health>();
        var given = world.Instantiate(orc, new Health(9), new Position(2, 2));
        Assert.That((given.Read<Health>().Value, given.IsDisabled<Health>(), given.Read<Position>()),
            Is.EqualTo((9, false, new Position(2, 2))));
        var kept = world.Instantiate(orc, new Armor(1));                   // what it does not give stays as it is
        Assert.That((kept.Has<Health>(), kept.IsDisabled<Health>(), kept.Has<Position>()), Is.EqualTo((false, true, false)));
        Assert.That(kept.Enable<Health>() && kept.Read<Health>().Value == 30, Is.True);
        var batch = new Entity[70];                                        // more than a word of presence bits
        world.Instantiate(orc, batch, new Health(4));
        Assert.That((world.Count<Health>(), batch[69].IsDisabled<Health>()), Is.EqualTo((72, false)));
    }

    [Test]
    public void APrefabWithMembersGetsTheGivenComponentsOnTheMemberPassed()
    {
        using var world = new World();
        var boss = new Prefab().Set(new Health(500));
        var minion = boss.AddMember().Set(new Health(20)).Set(new ChildOf(boss.Id));
        var e = world.Instantiate(minion, new Armor(7));
        Assert.That((world.EntityCount, world.Count<Armor>(), e.Read<Armor>().Value, e.Read<Health>().Value), Is.EqualTo((2, 1, 7, 20)));
        var leaders = new Entity[3];
        world.Instantiate(boss, leaders, new Health(9));
        Assert.That((world.EntityCount, leaders[2].Read<Health>().Value, world.Index<ChildOf, EntityId>().Count(leaders[2])),
            Is.EqualTo((8, 9, 1)));
    }

    [Test]
    public void InstantiatingWithGivenComponentsAllocatesNothingOnceWarm()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var minion = new Prefab().Set(new Health(20)).Set(new Armor(1));
        var batch = new Entity[100];
        world.Index<ChildOf, EntityId>().Count(parent);                    // the index learns every instance
        for (int i = 0; i < 100; i++) world.Instantiate(minion, new ChildOf(parent), new Position(i, 0)); // JIT and caches
        world.DestroyAll<Health>();
        world.Instantiate(minion, batch, new ChildOf(parent), new Position(1, 1));
        world.DestroyAll<Health>();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) world.Instantiate(minion, new ChildOf(parent), new Position(i, 0));
        world.DestroyAll<Health>();
        world.Instantiate(minion, batch, new ChildOf(parent), new Position(1, 1));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero);
        Assert.That((world.Index<ChildOf, EntityId>().Count(parent), batch[9].Read<Position>().X), Is.EqualTo((100, 1f)));
    }
}
