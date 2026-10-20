using System;
using Nino.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>An actor's login: a managed value with a formatter, indexed by it.</summary>
[NinoType(false)]
public partial struct Account : IComponent, IIndexed<string>
{
    [NinoMember(0)] public string Login;
    public readonly string Key => Login;
}

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

    private static readonly ComponentSet Record = ComponentSet.Create().With<Health>().With<Follows>().With<Account>()
        .With<Armor>().With<ChildOf>().With<Zone>().With<Frozen>().With<DynamicBuffer<DynamicBufferTests.Loot>>();

    [Test]
    public void ARecordLoadsOntoTheEntitiesMadeForIt()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
        {
            var guild = from.CreateEntity(new Health(0));                         // stays behind
            var bank = from.CreateEntity(new Health(50), new Account { Login = "bank" }, new Armor(1), new Zone(2), new Frozen());
            var vault = from.CreateEntity(new ChildOf(bank), new Health(7), new Follows(guild));
            bank.Set(new Follows(vault)).Set(new DynamicBuffer<DynamicBufferTests.Loot>());
            bank.Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(1, 1));
            from.SaveEntities(output, [bank.Id, vault.Id], in Record);
        }

        using var to = new World();
        to.GroupBy<Zone>();
        var accounts = to.Index<Account, string>();
        var follows = to.Index<Follows, EntityId>();
        var a = to.CreateEntity(new Position(9, 9), new Account { Login = "old" }, new Health(1), new Zone(0)); // runtime state and old values
        var b = to.CreateEntity(new Position(8, 8));
        ulong tick = to.ChangeTick;
        Assert.That(to.LoadEntitiesInto(output.WrittenSpan, [a, b]), Is.EqualTo(2));

        Assert.That((a.Read<Position>(), a.Read<Health>(), a.Read<Account>().Login, a.Has<Frozen>()), Is.EqualTo((new Position(9, 9), new Health(50), "bank", true)));
        Assert.That((accounts.Count("old"), accounts.Count("bank")), Is.EqualTo((0, 1)));            // re-keyed
        Assert.That((a.Read<Follows>().Target, follows.Count(b.Id)), Is.EqualTo((b.Id, 1)));            // inside the group: the entity loaded into
        Assert.That(a.Read<DynamicBuffer<DynamicBufferTests.Loot>>()[0], Is.EqualTo(new DynamicBufferTests.Loot(1, 1)));
        Assert.That(to.Count<Armor>(QueryFilter.Create().Changed<Armor>(tick)), Is.EqualTo(1));        // a loaded value counts as written
        Assert.That(to.Count<Position>(QueryFilter.Create().InGroup<Zone>(2)), Is.EqualTo(1));          // its group's table
        Assert.That((b.Read<Position>(), b.Read<ChildOf>().Parent, b.Read<Health>()), Is.EqualTo((new Position(8, 8), a.Id, new Health(7))));
        Assert.That(b.Read<Follows>().Target, Is.EqualTo(EntityId.Null));                              // the guild did not come
        Assert.That((b.Has<Account>(), b.Has<Frozen>(), b.Has<Zone>()), Is.EqualTo((false, false, false))); // only what its record holds
        a.Destroy();                                                                                    // the hierarchy works on them
        Assert.That((b.IsAlive, to.EntityCount), Is.EqualTo((false, 0)));
    }

    [Test]
    public void ALoadCanLeaveTheChangeTicksAlone()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
        {
            var first = from.CreateEntity(new Armor(5), new Health(1));
            var second = from.CreateEntity(new Armor(5), new Health(2));
            from.SaveEntities(output, [first.Id, second.Id], ComponentSet.Create().With<Armor>().With<Health>());
        }

        using var to = new World();
        var a = to.CreateEntity(new Armor(1));                                   // written when it was made
        var b = to.CreateEntity(new Position());
        to.Update();
        ulong tick = to.ChangeTick;
        Assert.That(to.LoadEntitiesInto(output.WrittenSpan, [a, b], markWritten: false), Is.EqualTo(2));

        Assert.That((a.Read<Armor>().Value, b.Read<Armor>().Value), Is.EqualTo((5, 5)));
        Assert.That(to.Count<Armor>(QueryFilter.Create().Changed<Armor>(tick)), Is.Zero);   // nothing reads as a write
        Assert.That(to.Count<Armor>(QueryFilter.Create().Changed<Armor>(1)), Is.EqualTo(1)); // a keeps its tick, b's is never
    }

    [Test]
    public void ARecordKeepsWhatItDoesNotHoldAndBringsItsDisabledValues()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
        {
            var parent = from.CreateEntity(new Health(1));                        // stays behind
            var child = from.CreateEntity(new ChildOf(parent), new Health(5), new Armor(2));
            child.Disable<Health>();
            from.SaveEntities(output, [child.Id], in Record);
        }

        using var to = new World();
        var here = to.CreateEntity(new Health(1));
        var c = to.CreateEntity(new ChildOf(here), new Health(9));
        to.LoadEntitiesInto(output.WrittenSpan, [c]);
        Assert.That(c.Read<ChildOf>().Parent, Is.EqualTo(here.Id));                // its parent stayed behind: kept as it was
        Assert.That((c.Has<Health>(), c.IsDisabled<Health>(), c.Read<Armor>()), Is.EqualTo((false, true, new Armor(2))));
        Assert.That((c.Enable<Health>(), c.Read<Health>()), Is.EqualTo((true, new Health(5))));
        Assert.That(to.Index<ChildOf, EntityId>().Count(here.Id), Is.EqualTo(1));
    }

    [Test]
    public void LoadingIntoTheWrongEntitiesIsRefusedAndChangesNothing()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
            from.SaveEntities(output, [from.CreateEntity(new Health(5)).Id, from.CreateEntity(new Health(6)).Id], in Record);

        using var to = new World();
        var a = to.CreateEntity(new Health(1));
        var gone = to.CreateEntity(new Health(2));
        gone.Destroy();
        Assert.Throws<ArgumentException>(() => to.LoadEntitiesInto(output.WrittenSpan, [a]));          // two saved, one given
        Assert.Throws<ArgumentException>(() => to.LoadEntitiesInto(output.WrittenSpan, [a, gone]));    // a stale entity
        Assert.That((a.Read<Health>(), to.EntityCount), Is.EqualTo((new Health(1), 1)));
    }

    [Test]
    public void LoadingIntoAgainAllocatesNothing()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
        {
            var actor = from.CreateEntity(new Health(5), new Armor(1), new Zone(1));   // values without references
            actor.Set(new DynamicBuffer<DynamicBufferTests.Loot>()).Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(1, 1));
            from.SaveEntities(output, [actor.Id], in Record);
        }

        using var to = new World();
        var into = new Entity[1];
        void Arrive()
        {
            into[0] = to.CreateEntity(new Position(1, 1));
            to.LoadEntitiesInto(output.WrittenSpan, into);
            into[0].Destroy();
        }

        for (int i = 0; i < 5; i++) Arrive();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Arrive();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void MovingIntoItselfIsRefused() =>
        Assert.Throws<ArgumentException>(() => { using var world = new World(); world.MoveEntities(world, []); });
}
