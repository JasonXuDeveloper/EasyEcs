using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class BulkTests
{
    [Test]
    public void SetAllAddsOrReplacesOnEveryEntityTheFilterSelects()
    {
        using var world = new World();
        var stunned = world.CreateEntity(new Health(1), new Stun(5));
        var healthy = world.CreateEntity(new Health(2));
        var frozen = world.CreateEntity(new Health(3), new Frozen());
        var bystander = world.CreateEntity(new Position(0, 0));
        Assert.That(world.SetAll(new Stun(9), QueryFilter.Create().With<Health>().Without<Frozen>()), Is.EqualTo(2));
        Assert.That((stunned.Get<Stun>(), healthy.Get<Stun>()), Is.EqualTo((new Stun(9), new Stun(9)))); // replaced, added
        Assert.That((frozen.Has<Stun>(), bystander.Has<Stun>()), Is.EqualTo((false, false)));
        Assert.That(world.SetAll(new Marked()), Is.EqualTo(4));                                       // no filter: everyone
    }

    [Test]
    public void SetAllCountsAsAWriteAndIndexesAndGroupsFollow()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        var leader = world.CreateEntity(new Health(0));
        var units = new Entity[200];
        for (int i = 0; i < units.Length; i++) units[i] = world.CreateEntity(new Health(i), new Zone(i % 2), new Armor(1));
        world.Update();
        ulong since = world.ChangeTick;

        world.SetAll(new Follows(leader.Id), QueryFilter.Create().With<Zone>());
        world.SetAll(new Zone(7), QueryFilter.Create().InGroup<Zone>(1));                  // half of them move to zone 7
        world.SetAll(new Armor(2), QueryFilter.Create().InGroup<Zone>(7));
        Assert.That(world.Index<Follows, EntityId>().Count(leader), Is.EqualTo(200));
        var deputy = world.CreateEntity(new Health(0));
        world.SetAll(new Follows(deputy.Id), QueryFilter.Create().With<Follows>()); // in place now: the index still follows
        Assert.That((world.Index<Follows, EntityId>().Count(leader), world.Index<Follows, EntityId>().Count(deputy)), Is.EqualTo((0, 200)));
        Assert.That((world.Count<Health>(QueryFilter.Create().InGroup<Zone>(7)), world.Count<Health>(QueryFilter.Create().InGroup<Zone>(1))),
            Is.EqualTo((100, 0)));
        Assert.That(world.Count<Health>(QueryFilter.Create().Changed<Armor>(since)), Is.EqualTo(100));
        for (int i = 0; i < units.Length; i++)
            Assert.That((units[i].Read<Zone>().Id, units[i].Read<Armor>().Value, units[i].Read<Health>().Value), Is.EqualTo((i % 2 == 1 ? (7, 2, i) : (0, 1, i))));
    }

    [Test]
    public void RemoveAllTakesTheComponentFromTheSelectedEntitiesOnly()
    {
        using var world = new World();
        var entities = new Entity[300];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity(new Armor(i), new Seen(), new Stun(i));
        world.Update();
        ulong since = world.ChangeTick;
        for (int i = 0; i < entities.Length; i += 3) entities[i].Set(new Frozen());
        Assert.That(world.RemoveAll<Armor>(QueryFilter.Create().With<Frozen>()), Is.EqualTo(100));
        Assert.That(world.RemoveAll<Armor>(QueryFilter.Create().With<Frozen>()), Is.Zero);             // nothing left to remove
        for (int i = 0; i < entities.Length; i++)
            Assert.That((entities[i].Has<Armor>(), entities[i].Has<Stun>()), Is.EqualTo((i % 3 != 0, true)));
        Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(since)), Is.EqualTo(100)); // Removed<T> sees each
    }

    [Test]
    public void RemoveAllOfTheLastCleanupComponentFreesRetiredEntities()
    {
        using var world = new World();
        var retired = world.CreateEntity(new Lease(1), new Health(1));
        var parent = world.CreateEntity(new Lease(2));
        var child = world.CreateEntity(new ChildOf(parent));
        var kept = world.CreateEntity(new Lease(3));
        retired.Destroy();
        parent.Destroy();                                         // retired too: the child waits for it
        Assert.That(world.RemoveAll<Lease>(QueryFilter.Create().With<Destroyed>()), Is.EqualTo(2));
        Assert.That((retired.IsAlive, parent.IsAlive, child.IsAlive, kept.Has<Lease>()), Is.EqualTo((false, false, false, true)));
    }

    [Test]
    public void DisableAllKeepsTheValuesThatEnableAllGivesBack()
    {
        using var world = new World();
        var entities = new Entity[130];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity(new Stun(i), new Health(i));
        Assert.That(world.DisableAll<Stun>(QueryFilter.Create().With<Health>()), Is.EqualTo(130));
        Assert.That((world.Count<Stun>(), world.Count<Disabled<Stun>>()), Is.EqualTo((0, 130)));
        Assert.That(world.EnableAll<Stun>(), Is.EqualTo(130));
        for (int i = 0; i < entities.Length; i++) Assert.That(entities[i].Get<Stun>().Ticks, Is.EqualTo(i));
#pragma warning disable EECS048 // the refused call itself
        Assert.Throws<System.InvalidOperationException>(() => world.DisableAll<ChildOf>());
#pragma warning restore EECS048
    }

    [Test]
    public void EnableAllOfATrackedComponentCountsAsAWriteOfEachRow()
    {
        using var world = new World();
        var entities = new Entity[200];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity(new Armor(i), new Health(i));
        world.DisableAll<Armor>();
        world.EnableAll<Armor>();
        world.DisableAll<Armor>(QueryFilter.Create().With<Health>());    // in place now: words of rows at once
        world.Update();
        ulong since = world.ChangeTick;
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(since)), Is.Zero);
        Assert.That(world.EnableAll<Armor>(), Is.EqualTo(200));
        Assert.That(world.Count<Health>(QueryFilter.Create().Changed<Armor>(since)), Is.EqualTo(200));
        for (int i = 0; i < entities.Length; i++) Assert.That(entities[i].Read<Armor>().Value, Is.EqualTo(i));
    }
}
