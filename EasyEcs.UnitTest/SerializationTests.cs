using System;
using System.IO;
using Nino.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

[NinoType]
public partial struct Title : IComponent   // managed: written by its Nino formatter
{
    public string Text;
}

public struct Grip { private EntityId _holder; public Grip(EntityId holder) => _holder = holder; public EntityId Holder => _holder; }
public struct Held : IComponent { public Grip Grip; }                 // an id two levels down, in a private field

public class SerializationTests
{
    private static byte[] Save(World world)
    {
        var output = new NinoArrayBufferWriter();
        world.Save(output);
        return output.WrittenSpan.ToArray();
    }

    [Test]
    public void ALoadedWorldHasTheSavedEntitiesComponentsAndSingletons()
    {
        byte[] bytes;
        EntityId hero, crate, gone;
        using (var world = new World())
        {
            hero = world.CreateEntity(new Position(1, 2), new Armor(5), new Title { Text = "hero" });
            gone = world.CreateEntity(new Health(1));
            crate = world.CreateEntity(new Position(3, 4), new Health(9));
            var entity = world.GetEntity(crate);
            entity.Disable<Health>();                                    // kept value, absent
            entity.Set(new DynamicBuffer<DynamicBufferTests.Loot>());
            entity.Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(7, 2));
            world.GetEntity(gone).Destroy();                             // a free id, version bumped
            world.AddSingleton<Score>() = new Score(3, 4);
            bytes = Save(world);
        }

        using var loaded = new World();
        loaded.CreateEntity(new Velocity(1, 1));                         // replaced by the load
        loaded.Load(bytes);

        Assert.That(loaded.EntityCount, Is.EqualTo(2));
        var h = loaded.GetEntity(hero);                                   // same id and version
        Assert.That(h.Read<Position>(), Is.EqualTo(new Position(1, 2)));
        Assert.That(h.Read<Armor>(), Is.EqualTo(new Armor(5)));
        Assert.That(h.Get<Title>().Text, Is.EqualTo("hero"));
        var c = loaded.GetEntity(crate);
        Assert.That(c.Has<Health>(), Is.False);
        Assert.That(c.IsDisabled<Health>(), Is.True);
        Assert.That(c.Enable<Health>() && c.Read<Health>().Value == 9, Is.True);
        Assert.That(c.Read<DynamicBuffer<DynamicBufferTests.Loot>>()[0], Is.EqualTo(new DynamicBufferTests.Loot(7, 2)));
        Assert.That(loaded.IsAlive(gone), Is.False);
        Assert.That(loaded.GetSingleton<Score>(), Is.EqualTo(new Score(3, 4)));
        Assert.That(loaded.Count<Velocity>(), Is.Zero);
        Assert.That(loaded.CreateEntity().Index, Is.EqualTo(gone.Index)); // the free list is the saved one
    }

    [Test]
    public void IndexesHierarchiesAndGroupsFollowTheLoadedValues()
    {
        byte[] bytes;
        EntityId parent;
        using (var world = new World())
        {
            parent = world.CreateEntity(new Health(1));
            world.CreateEntity(new ChildOf(parent), new Zone(2), new Health(5));
            world.CreateEntity(new ChildOf(parent), new Zone(3), new Health(7));
            bytes = Save(world);
        }

        using var loaded = new World();
        loaded.GroupBy<Zone>();
        loaded.Load(bytes);

        int sum = 0;
        foreach (var item in loaded.Query<Health>(QueryFilter.Create().InGroup<Zone>(3))) sum += item.Component1.Value;
        Assert.That(sum, Is.EqualTo(7));
        Assert.That(loaded.Index<ChildOf, EntityId>().Count(parent), Is.EqualTo(2));
        loaded.GetEntity(parent).Destroy();                              // the cascade works on loaded entities
        Assert.That(loaded.EntityCount, Is.Zero);
    }

    [Test]
    public void SavingAgainAllocatesNothing()
    {
        using var world = new World();
        for (int i = 0; i < 1000; i++) world.CreateEntity(new Position(i, i), new Health(i));
        var output = new NinoArrayBufferWriter();
        world.Save(output);
        output.ResetWrittenCount();
        world.Save(output);

        output.ResetWrittenCount();
        long before = GC.GetAllocatedBytesForCurrentThread();
        world.Save(output);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void LoadingAgainReusesItsRowsAndDropsWhatTheBytesLack()
    {
        byte[] full, empty;
        using (var world = new World())
        {
            for (int i = 0; i < 1000; i++) world.CreateEntity(new Position(i, i), new Health(i));
            world.AddSingleton<Score>() = new Score(1, 2);
            full = Save(world);
        }

        using (var world = new World()) empty = Save(world);

        using var loaded = new World();
        loaded.Load(full);
        loaded.Load(empty);                                               // nothing of the first load stays
        Assert.That((loaded.EntityCount, loaded.HasSingleton<Score>()), Is.EqualTo((0, false)));
        loaded.Load(full);

        long before = GC.GetAllocatedBytesForCurrentThread();
        loaded.Load(full);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(1024)); // names, not rows
        int sum = 0;
        foreach (var item in loaded.Query<Health>()) sum += item.Component1.Value;
        Assert.That((loaded.EntityCount, sum, loaded.GetSingleton<Score>()), Is.EqualTo((1000, 999 * 1000 / 2, new Score(1, 2))));
    }

    [Test]
    public void AMovedGroupGetsNewIdsAndItsReferencesFollow()
    {
        byte[] bytes;
        EntityId player, sword;
        var output = new NinoArrayBufferWriter();
        using (var world = new World())
        {
            var guild = world.CreateEntity(new Health(100));              // stays behind
            player = world.CreateEntity(new Health(10), new Follows(guild));
            sword = world.CreateEntity(new ChildOf(player), new Follows(player), new Held { Grip = new Grip(player) });
            world.GetEntity(player).AddRelation(new MemberOf(sword));
            world.GetEntity(player).AddRelation(new MemberOf(guild));
            world.SaveEntities(output, [sword, player, EntityId.Null]);   // order kept, an empty id skipped
            bytes = output.WrittenSpan.ToArray();
        }

        using var arrival = new World();
        for (int i = 0; i < 5; i++) arrival.CreateEntity(new Position(i, i)); // ids taken here: the group gets others
        var moved = new Entity[2];
        Assert.That(arrival.LoadEntities(bytes, moved), Is.EqualTo(2));
        var (s, p) = (moved[0], moved[1]);
        Assert.That((s.Id, p.Id), Is.Not.EqualTo((sword, player)));
        Assert.That(s.Read<ChildOf>().Parent, Is.EqualTo(p.Id));         // inside the group: the new entity
        Assert.That(s.Read<Follows>().Target, Is.EqualTo(p.Id));
        Assert.That(s.Get<Held>().Grip.Holder, Is.EqualTo(p.Id));
        Assert.That(p.Read<Follows>().Target, Is.EqualTo(EntityId.Null)); // the guild did not come
        ref readonly var parties = ref p.Read<DynamicBuffer<MemberOf>>();                // buffer elements too
        Assert.That((parties[0], parties[1]), Is.EqualTo((new MemberOf(s), new MemberOf(EntityId.Null))));
        Assert.That(arrival.Index<Follows, EntityId>().Count(p), Is.EqualTo(1)); // indexes learn the new values
        Assert.That(arrival.Index<ChildOf, EntityId>().Count(p), Is.EqualTo(1));
        p.Destroy();                                                      // the cascade reaches the moved child
        Assert.That((s.IsAlive, arrival.EntityCount), Is.EqualTo((false, 5)));
    }

    [Test]
    public void AChildWhoseParentStayedIsARootAfterTheMove()
    {
        var output = new NinoArrayBufferWriter();
        using var world = new World();
        var parent = world.CreateEntity(new Health(1));
        var child = world.CreateEntity(new ChildOf(parent), new Health(2));
        world.SaveEntities(output, [child]);
        var moved = new Entity[1];
        world.LoadEntities(output.WrittenSpan, moved);                    // a copy into the same world
        Assert.That((moved[0].Has<ChildOf>(), moved[0].Read<Health>().Value, world.EntityCount), Is.EqualTo((false, 2, 3)));
    }

    [Test]
    public void BytesThatAreNotASaveAreRefused()
    {
        using var world = new World();
        Assert.Throws<InvalidDataException>(() => world.Load(new byte[16]));
    }
}
