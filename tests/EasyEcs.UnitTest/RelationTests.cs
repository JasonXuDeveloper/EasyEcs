using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct MemberOf(EntityId Target) : IRelation;   // an entity in many parties at once

public class RelationTests
{
    private static List<Entity> All(RelationIndex<MemberOf>.Sources sources)
    {
        var list = new List<Entity>();
        foreach (var e in sources) list.Add(e);
        return list;
    }

    [Test]
    public void AnEntityLinksToManyTargetsAndTargetsFindTheirSources()
    {
        using var world = new World();
        var red = world.CreateEntity();
        var blue = world.CreateEntity();
        var a = world.CreateEntity().AddRelation(new MemberOf(red)).AddRelation(new MemberOf(blue));
        var b = world.CreateEntity().AddRelation(new MemberOf(red));
        var parties = world.Relations<MemberOf>();
        Assert.That(All(parties.Find(red)), Is.EquivalentTo(new[] { a, b }));
        Assert.That(All(parties.Find(blue)), Is.EqualTo(new[] { a }));
        Assert.That((parties.Find(red).Count, parties.Find(blue).Count, parties.TargetCount), Is.EqualTo((2, 1, 2)));
        Assert.That((a.HasRelation<MemberOf>(blue), b.HasRelation<MemberOf>(blue)), Is.EqualTo((true, false)));

        a.AddRelation(new MemberOf(blue));                                // links are counted, like buffer elements
        Assert.That(parties.Count(blue), Is.EqualTo(2));
        Assert.That(a.RemoveRelation<MemberOf>(blue), Is.EqualTo(2));
        Assert.That((parties.Count(blue), a.Read<DynamicBuffer<MemberOf>>().Count, parties.TargetCount), Is.EqualTo((0, 1, 1)));

        b.Destroy();                                                      // a destroyed source leaves
        Assert.That(All(parties.Find(red)), Is.EqualTo(new[] { a }));
        a.Remove<DynamicBuffer<MemberOf>>();                              // so does a removed buffer
        Assert.That((parties.Count(red), parties.TargetCount), Is.EqualTo((0, 0)));
    }

    [Test]
    public void TheFirstLookupIndexesExistingLinksAndSetReplacesThem()
    {
        using var world = new World();
        var t1 = world.CreateEntity();
        var t2 = world.CreateEntity();
        var e = world.CreateEntity().AddRelation(new MemberOf(t1));       // before the index exists
        var parties = world.Relations<MemberOf>();
        Assert.That(All(parties.Find(t1)), Is.EqualTo(new[] { e }));

        var other = world.CreateEntity().AddRelation(new MemberOf(t2));
        var copy = other.Clone();                                          // a clone links too
        Assert.That(parties.Count(t2), Is.EqualTo(2));
        e.Set(new DynamicBuffer<MemberOf>());                              // a whole new buffer: no links
        Assert.That((parties.Count(t1), copy.HasRelation<MemberOf>(t2)), Is.EqualTo((0, true)));
    }

    [Test]
    public void ATargetsNextIncarnationHasItsOwnLinks()
    {
        using var world = new World();
        var parties = world.Relations<MemberOf>();
        var old = world.CreateEntity();
        var member = world.CreateEntity().AddRelation(new MemberOf(old));
        old.Destroy();
        var next = world.CreateEntity();                                   // reuses the index
        Assert.That(next.Id.Index, Is.EqualTo(old.Id.Index));
        var joiner = world.CreateEntity().AddRelation(new MemberOf(next));
        Assert.That((All(parties.Find(old))[0], All(parties.Find(next))[0]), Is.EqualTo((member, joiner)));
        member.RemoveRelation<MemberOf>(old);
        Assert.That((parties.Count(old), parties.Count(next)), Is.EqualTo((0, 1)));
    }

    [Test]
    public void RestoreReindexes()
    {
        using var world = new World();
        var parties = world.Relations<MemberOf>();
        var target = world.CreateEntity();
        var a = world.CreateEntity().AddRelation(new MemberOf(target));
        var snapshot = world.Capture();
        a.RemoveRelation<MemberOf>(target);
        world.Restore(snapshot);
        Assert.That(All(parties.Find(target)), Is.EqualTo(new[] { a }));
    }

    [Test]
    public void BuildingAnIndexIsNotAChange()
    {
        using var world = new World();
        world.CreateEntity(new Armor(1));
        var tracked = world.CreateEntity().AddRelation(new MemberOf(world.CreateEntity()));
        ulong mark = world.ChangeTick + 1;
        world.Update();
        _ = world.Relations<MemberOf>().Count(default);
        Assert.That(world.Count<DynamicBuffer<MemberOf>>(QueryFilter.Create().Changed<DynamicBuffer<MemberOf>>(mark)), Is.Zero);
        Assert.That(tracked.IsAlive, Is.True);
    }

    [Test]
    public void RemovingNoLinkWritesNothing()
    {
        using var world = new World();
        var party = world.CreateEntity();
        var member = world.CreateEntity().AddRelation(new MemberOf(party.Id));
        world.Update();
        var changed = QueryFilter.Create().Changed<DynamicBuffer<MemberOf>>(world.ChangeTick);
        Assert.That(member.RemoveRelation<MemberOf>(member.Id), Is.Zero);
        Assert.That(world.Count<DynamicBuffer<MemberOf>>(changed), Is.Zero);    // no link went: not a write
        Assert.That(member.HasRelation<MemberOf>(party.Id), Is.True);
        Assert.That(member.RemoveRelation<MemberOf>(party.Id), Is.EqualTo(1));
        Assert.That(world.Count<DynamicBuffer<MemberOf>>(changed), Is.EqualTo(1));
    }
}
