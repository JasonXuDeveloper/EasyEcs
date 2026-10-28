using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    public void AListOfIdsGoesAsItsSpanWithoutAllocating()
    {
        using var a = new World();
        using var b = new World();
        var group = new List<EntityId>();
        var moved = new Entity[2];
        var parent = a.CreateEntity(new Health(7));
        a.CreateEntity(new ChildOf(parent), new Position(1, 2));
        group.Add(parent.Id);
        foreach (var part in a.Descendants(parent)) group.Add(part.Id);
        var output = new NinoArrayBufferWriter();
        var (from, to) = (a, b);

        int arrived = 0;
        void Round() // the group goes there and back: saved, moved, loaded onto the moved entities
        {
            output.ResetWrittenCount();
            from.SaveEntities(output, CollectionsMarshal.AsSpan(group));
            arrived += from.MoveEntities(to, CollectionsMarshal.AsSpan(group), moved);
            group.Clear();
            foreach (var e in moved) group.Add(e.Id);
            arrived += to.LoadEntitiesInto(output.WrittenSpan, [moved[0], moved[1]]);
            (from, to) = (to, from);
        }

        Allocations.WarmUp(Round, 10); // tables, edges, buffers, until the JIT settles
        arrived = 0;
        long before = Allocations.Baseline();
        for (int i = 0; i < 100; i++) Round();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(arrived, Is.EqualTo(100 * 4));
        Assert.That((moved[0].Read<Health>().Value, moved[1].Read<ChildOf>().Parent), Is.EqualTo((7, moved[0].Id)));
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
            from.SaveEntitiesWith(output, [bank.Id, vault.Id], in Record);
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
    public void SaveEntitiesWithoutLeavesOutTheComponentsOfItsSet()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
        {
            var chunk = from.CreateEntity(new Health(9), new Armor(2), new Shield(4));
            var gone = from.CreateEntity(new Shield(1));                            // every component left out
            from.SaveEntitiesWithout(output, [chunk.Id, gone.Id], ComponentSet.Create().With<Shield>());
            Assert.Throws<ArgumentException>(() =>
                from.SaveEntitiesWithout(output, [chunk.Id], ComponentSet.Create().WithSingleton<Clock>()));
        }

        using var to = new World();
        var loaded = new Entity[2];
        Assert.That(to.LoadEntities(output.WrittenSpan, loaded), Is.EqualTo(2));
        Assert.That((loaded[0].Read<Health>(), loaded[0].Read<Armor>(), loaded[0].Has<Shield>()),
            Is.EqualTo((new Health(9), new Armor(2), false)));
        Assert.That((loaded[1].IsAlive, loaded[1].Has<Shield>()), Is.EqualTo((true, false)));    // written without them
    }

    [Test]
    public void ALoadCanLeaveTheChangeTicksAlone()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
        {
            var first = from.CreateEntity(new Armor(5), new Health(1));
            var second = from.CreateEntity(new Armor(5), new Health(2));
            from.SaveEntitiesWith(output, [first.Id, second.Id], ComponentSet.Create().With<Armor>().With<Health>());
        }

        using var to = new World();
        var a = to.CreateEntity(new Armor(1), new Shield(3));                   // written when it was made (a prefab default)
        var b = to.CreateEntity(new Position());
        to.Update();
        ulong tick = to.ChangeTick;
        Assert.That(to.LoadEntitiesInto(output.WrittenSpan, [a, b], markWritten: false), Is.EqualTo(2));

        Assert.That((a.Read<Armor>().Value, b.Read<Armor>().Value), Is.EqualTo((5, 5)));
        Assert.That(to.Count<Armor>(QueryFilter.Create().Changed<Armor>(tick)), Is.Zero); // nothing reads as a write
        Assert.That(to.Count<Armor>(QueryFilter.Create().Changed<Armor>(1)), Is.Zero);    // a's replaced default lost its tick
        Assert.That(to.Count<Shield>(QueryFilter.Create().Changed<Shield>(1)), Is.EqualTo(1)); // what the save lacks keeps its tick
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
            from.SaveEntitiesWith(output, [child.Id], in Record);
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
            from.SaveEntitiesWith(output, [from.CreateEntity(new Health(5)).Id, from.CreateEntity(new Health(6)).Id], in Record);

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
            from.SaveEntitiesWith(output, [actor.Id], in Record);
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
        long before = Allocations.Baseline();
        for (int i = 0; i < 100; i++) Arrive();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void ARetiredEntityDoesNotMove()
    {
        using var from = new World();
        using var to = new World();
        var retired = from.CreateEntity(new Lease(1), new Health(5));
        retired.Destroy();
        var live = from.CreateEntity(new Health(7));
        var moved = new Entity[2];
        Assert.That(from.MoveEntities(to, [retired.Id, live.Id], moved), Is.EqualTo(1));
        Assert.That((moved[0], moved[1].Read<Health>().Value), Is.EqualTo((default(Entity), 7)));
        Assert.That((to.EntityCount, to.Count<Lease>()), Is.EqualTo((1, 0)));               // not copied: it exists once
        Assert.That((retired.IsAlive, retired.Has<Retired>(), retired.Read<Lease>().Slot), Is.EqualTo((true, true, 1)));
        Assert.That((from.EntityCount, live.IsAlive), Is.EqualTo((1, false)));
    }

    [Test]
    public void CleanupComponentsStayBehindOnAMove()
    {
        using var from = new World();
        using var to = new World();
        var e = from.CreateEntity(new Lease(1), new Ticket(2), new Health(5));
        var plain = from.CreateEntity(new Lease(3));
        var moved = new Entity[2];
        Assert.That(from.MoveEntities(to, [e.Id, plain.Id], moved), Is.EqualTo(2));

        var (m, p) = (moved[0], moved[1]);
        Assert.That((m.Read<Health>().Value, m.Has<Lease>(), m.Has<Ticket>()), Is.EqualTo((5, false, false)));
        Assert.That((p.IsAlive, p.Has<Lease>()), Is.EqualTo((true, false)));
        Assert.That((to.Count<Lease>(), to.Count<Ticket>()), Is.EqualTo((0, 0)));
        // The originals are retired with their cleanup components: only this world releases them.
        Assert.That((e.IsAlive, e.Has<Retired>(), e.Read<Lease>().Slot, plain.Read<Lease>().Slot), Is.EqualTo((true, true, 1, 3)));
        m.Destroy();                                                         // nothing to release there: freed at once
        Assert.That((m.IsAlive, to.EntityCount), Is.EqualTo((false, 1)));
    }

    [Test]
    public void LoadingIntoOneEntityTwiceIsRefused()
    {
        var output = new NinoArrayBufferWriter();
        using (var from = new World())
        {
            var first = from.CreateEntity(new Health(1));
            var second = from.CreateEntity(new Health(2));
            from.SaveEntitiesWith(output, [first.Id, second.Id], ComponentSet.Create().With<Health>());
        }

        using var to = new World();
        var a = to.CreateEntity(new Health(0));
        var b = to.CreateEntity(new Position());
        Assert.Throws<ArgumentException>(() => to.LoadEntitiesInto(output.WrittenSpan, [a, a]));
        Assert.That((a.Read<Health>().Value, b.Has<Health>()), Is.EqualTo((0, false)));   // nothing written
        Assert.That(to.LoadEntitiesInto(output.WrittenSpan, [a, b]), Is.EqualTo(2));
        Assert.That((a.Read<Health>().Value, b.Read<Health>().Value), Is.EqualTo((1, 2)));
    }

    [Test]
    public void ACallRefusedDuringAPassIsNamed()
    {
        using var from = new World();
        using var to = new World();
        var player = from.CreateEntity(new Health(1));
        var bytes = new NinoArrayBufferWriter();
        from.SaveEntities(bytes, player.Id);
        foreach (var item in to.Query<Health>()) { }                    // warms the query: no table is created below
        string Refused(Action call)
        {
            to.CreateEntity(new Health(2));
            try
            {
                foreach (var item in to.Query<Health>()) call();
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }

            return null;
        }

        // Each names itself, not another call (a move, a load or a save once read as a snapshot's refusal).
        Assert.That(Refused(() => from.MoveEntities(to, [player.Id])), Does.StartWith("MoveEntities "));
        Assert.That(Refused(() => to.LoadEntities(bytes.WrittenSpan)), Does.StartWith("LoadEntities "));
        Assert.That(Refused(() => to.Save(new NinoArrayBufferWriter())), Does.StartWith("Save "));
        Assert.That(Refused(() => to.Capture()), Does.StartWith("Capture "));
        Assert.That(player.IsAlive, Is.True);                           // nothing moved
        Assert.That(from.MoveEntities(to, [player.Id]), Is.EqualTo(1)); // the passes ended
    }

    [Test]
    public void MovingIntoItselfIsRefused() =>
        Assert.Throws<ArgumentException>(() => { using var world = new World(); world.MoveEntities(world, []); });
}
