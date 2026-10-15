using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class IndexTests
{
    private static List<Entity> All(ValueIndex<ChildOf, Entity>.Matches matches)
    {
        var list = new List<Entity>();
        foreach (var e in matches) list.Add(e);
        return list;
    }

    [Test]
    public void FindsEntitiesByKeyAndFollowsWrites()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var a = world.CreateEntity(new Callsign("alpha"));
        var b = world.CreateEntity(new Callsign("bravo"));

        Assert.That(index.TryFind("alpha", out var found), Is.True);
        Assert.That(found, Is.EqualTo(a));
        Assert.That(index.Count("bravo"), Is.EqualTo(1));
        Assert.That(index.TryFind("charlie", out _), Is.False);

        b.Get<Callsign>() = new Callsign("alpha");   // a ref write moves b to its new key
        Assert.That(index.Count("alpha"), Is.EqualTo(2));
        Assert.That(index.Count("bravo"), Is.Zero);

        foreach (var item in world.Query<Callsign>()) item.Component1 = new Callsign("delta");
        Assert.That(index.Count("alpha"), Is.Zero);
        Assert.That(index.Count("delta"), Is.EqualTo(2));
    }

    [Test]
    public void DestroyedEntitiesAndRemovedComponentsAreNotFound()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var a = world.CreateEntity(new Callsign("x"));
        var b = world.CreateEntity(new Callsign("x"));
        var c = world.CreateEntity(new Callsign("x"));
        Assert.That(index.Count("x"), Is.EqualTo(3));

        a.Destroy();
        b.Remove<Callsign>();
        Assert.That(index.Count("x"), Is.EqualTo(1));
        Assert.That(index.TryFind("x", out var only) && only == c, Is.True);

        var reused = world.CreateEntity(new Callsign("y"));   // may reuse a's id
        Assert.That(index.Count("x"), Is.EqualTo(1));
        Assert.That(index.TryFind("y", out var y) && y == reused, Is.True);
        b.Set(new Callsign("x"));                              // adding back is a write
        Assert.That(index.Count("x"), Is.EqualTo(2));
    }

    [Test]
    public void RelationsFindTheEntitiesPointingAtAnEntity()
    {
        using var world = new World();
        var children = world.Index<ChildOf, Entity>();
        var parent = world.CreateEntity(new Health(1));
        var other = world.CreateEntity(new Health(1));
        var kids = new Entity[3];
        world.CreateEntities(kids, new ChildOf(parent));

        Assert.That(All(children.Find(parent)), Is.EquivalentTo(kids));
        Assert.That(children.Count(other), Is.Zero);

        kids[0].Set(new ChildOf(other));                       // re-parent
        Assert.That(All(children.Find(parent)), Is.EquivalentTo(new[] { kids[1], kids[2] }));
        Assert.That(All(children.Find(other)), Is.EqualTo(new[] { kids[0] }));

        // Destroying the parent leaves the links: the handle keeps its version, so they never match a later entity.
        parent.Destroy();
        var next = world.CreateEntity(new Health(2));          // may reuse the parent's id
        Assert.That(children.Count(next), Is.Zero);
        Assert.That(children.Count(parent), Is.EqualTo(2));
    }

    [Test]
    public void RestoreReindexes()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var a = world.CreateEntity(new Callsign("before"));
        Assert.That(index.Count("before"), Is.EqualTo(1));
        var snapshot = world.Capture();

        a.Set(new Callsign("after"));
        world.CreateEntity(new Callsign("after"));
        Assert.That(index.Count("after"), Is.EqualTo(2));

        world.Restore(snapshot);
        Assert.That(index.Count("after"), Is.Zero);
        Assert.That(index.TryFind("before", out var e) && e == a, Is.True);
    }
}
