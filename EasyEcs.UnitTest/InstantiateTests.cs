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
    public void IndexesLearnTheNewEntities()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var children = world.Index<ChildOf, Entity>();
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
        var children = world.Index<ChildOf, Entity>();
        var callsigns = world.Index<Callsign, string>();
        Assert.That(children.Count(parent), Is.Zero);                     // builds the indexes
        world.CreateEntities(new Entity[100], new ChildOf(parent), new Callsign("a"));
        world.Instantiate(new Prefab().Set(new ChildOf(parent)).Set(new Callsign("b")), new Entity[50]);
        Assert.That(children.Count(parent), Is.EqualTo(150));
        Assert.That((callsigns.Count("a"), callsigns.Count("b")), Is.EqualTo((100, 50)));
    }
}
