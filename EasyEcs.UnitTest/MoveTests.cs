using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class MoveTests
{
    [Test]
    public void MovedEntitiesKeepTheirValuesAndPointAtEachOther()
    {
        using var from = new World();
        using var to = new World();
        to.CreateEntity(new Position(0, 0));                                 // ids taken there: the group gets others
        var guild = from.CreateEntity(new Health(0));
        var player = from.CreateEntity(new Health(10), new Follows(guild));
        var sword = from.CreateEntity(new ChildOf(player), new Follows(player), new Callsign("sword"));
        var moved = new Entity[4];
        Assert.That(from.MoveEntities(to, [player.Id, sword.Id, player.Id, default], moved), Is.EqualTo(2)); // a repeat, a stale id

        var (p, s) = (moved[0], moved[1]);
        Assert.That((moved[2], moved[3]), Is.EqualTo((p, default(Entity))));
        Assert.That((p.Read<Health>().Value, p.Read<Follows>().Target), Is.EqualTo((10, EntityId.Null))); // the guild stayed
        Assert.That((s.Read<ChildOf>().Parent, s.Read<Follows>().Target, s.Read<Callsign>().Value), Is.EqualTo((p.Id, p.Id, "sword")));
        Assert.That(to.Index<Follows, EntityId>().Count(p), Is.EqualTo(1));
        Assert.That((player.IsAlive, sword.IsAlive, guild.IsAlive), Is.EqualTo((false, false, true)));
        Assert.That((from.EntityCount, to.EntityCount), Is.EqualTo((1, 3)));
    }

    [Test]
    public void AChildLeftBehindIsDestroyedWithItsParent()
    {
        using var from = new World();
        using var to = new World();
        var parent = from.CreateEntity(new Health(1));
        var child = from.CreateEntity(new ChildOf(parent));
        var moved = new Entity[1];
        from.MoveEntities(to, [parent.Id], moved);
        Assert.That((child.IsAlive, moved[0].Read<Health>().Value, to.EntityCount), Is.EqualTo((false, 1, 1)));
    }

    [Test]
    public void AnEntityStillThereIsNotMistakenForOneMovedBefore()
    {
        using var from = new World();
        using var to = new World();
        var a = from.CreateEntity(new Health(1));
        var b = from.CreateEntity(new Health(2));
        from.MoveEntities(to, [a.Id]);                                       // a's index arrived once
        var c = from.CreateEntity(new Health(3));                            // reuses a's index
        b.Set(new Follows(c));
        var moved = new Entity[1];
        from.MoveEntities(to, [b.Id], moved);                                // c stays: its id is not the earlier arrival
        Assert.That((c.Id.Index, moved[0].Read<Follows>().Target), Is.EqualTo((a.Id.Index, EntityId.Null)));
    }

    [Test]
    public void MovingIntoItselfIsRefused() =>
        Assert.Throws<ArgumentException>(() => { using var world = new World(); world.MoveEntities(world, []); });
}
