using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial class IndexTests
{
    /// <summary>The cascade of the relations guide: children of dying parents are destroyed before the parents.</summary>
    public partial class DestroyOrphans : EcsSystem
    {
        [ForEach, With<Dead>]
        private void Cascade(Entity parent)
        {
            foreach (Entity child in World.Index<ChildOf, Entity>().Find(parent)) child.Destroy();
        }
    }

    /// <summary>Renames every callsign: keys written by a system's ref parameter.</summary>
    public partial class Rename : EcsSystem
    {
        [ForEach]
        private static void Apply(ref Callsign callsign) => callsign = new Callsign(callsign.Value + "!");
    }

    /// <summary>Looks up the index it writes: a lookup in the middle of a pass over the indexed component.</summary>
    public partial class RenameAfterLookup : EcsSystem
    {
        [ForEach]
        private void Apply(ref Callsign callsign)
        {
            if (World.Index<Callsign, string>().Count(callsign.Value) > 0) callsign = new Callsign(callsign.Value + "!");
        }
    }

    /// <summary>A component with two key types: only one of them can be indexed.</summary>
    private record struct TwoKeys(int Number, string Name) : IComponent, IIndexed<int>, IIndexed<string>
    {
        readonly int IIndexed<int>.Key => Number;
        readonly string IIndexed<string>.Key => Name;
    }

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

    [Test]
    public void SystemsWriteKeysAndCascadeThroughRelations()
    {
        using var world = new World();
        world.AddSystem<Rename>();
        world.AddSystem<DestroyOrphans>();
        var callsigns = world.Index<Callsign, string>();
        var children = world.Index<ChildOf, Entity>();
        var parent = world.CreateEntity(new Callsign("p"));
        var kids = new Entity[3];
        world.CreateEntities(kids, new ChildOf(parent), new Callsign("k"));

        world.Update();
        Assert.That(callsigns.Count("k!"), Is.EqualTo(3));
        Assert.That(callsigns.Count("k"), Is.Zero);

        parent.Add<Dead>();
        world.Update();
        foreach (var kid in kids) Assert.That(kid.IsAlive, Is.False);
        Assert.That(children.Count(parent), Is.Zero);
        Assert.That(callsigns.Count("k!!"), Is.Zero);  // the kids are gone from every index
        Assert.That(callsigns.Count("p!!"), Is.EqualTo(1));
    }

    [Test]
    public void ReusedIdsAreIndexedOnce()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var a = world.CreateEntity(new Callsign("x"));
        Assert.That(index.Count("x"), Is.EqualTo(1));
        a.Destroy();
        var b = world.CreateEntity(new Callsign("x")); // reuses a's id before any lookup
        Assert.That(b.Id, Is.EqualTo(a.Id));
        Assert.That(All(index.Find("x")), Is.EqualTo(new[] { b }));
    }

    [Test]
    public void NullKeysAreNotIndexed()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var a = world.CreateEntity(new Callsign("x"));
        var b = world.CreateEntity();
        b.Add<Callsign>(); // default: a null key
        Assert.That(index.Count("x"), Is.EqualTo(1));
        a.Get<Callsign>() = default; // moves a out of "x" and indexes it nowhere
        Assert.That(index.Count("x"), Is.Zero);
        Assert.That(index.Count(null), Is.Zero);
        a.Set(new Callsign("x"));
        Assert.That(index.Count("x"), Is.EqualTo(1));
    }

    private static List<Entity> All(ValueIndex<Callsign, string>.Matches matches)
    {
        var list = new List<Entity>();
        foreach (var e in matches) list.Add(e);
        return list;
    }

    [Test]
    public void TwoIncarnationsOfOneIdKeepSeparateRelations()
    {
        using var world = new World();
        var children = world.Index<ChildOf, Entity>();
        var old = world.CreateEntity(new Health(1));
        var oldKids = new Entity[2];
        world.CreateEntities(oldKids, new ChildOf(old));
        Assert.That(children.Count(old), Is.EqualTo(2));

        old.Destroy();                                     // the links stay: they keep the old version
        var next = world.CreateEntity(new Health(2));      // reuses the id
        Assert.That(next.Id, Is.EqualTo(old.Id));
        var newKids = new Entity[3];
        world.CreateEntities(newKids, new ChildOf(next));
        Assert.That(All(children.Find(next)), Is.EquivalentTo(newKids));
        Assert.That(All(children.Find(old)), Is.EquivalentTo(oldKids));

        foreach (var kid in oldKids) kid.Remove<ChildOf>(); // the old list empties
        Assert.That(children.Count(old), Is.Zero);
        Assert.That(children.Count(next), Is.EqualTo(3));
        newKids[0].Destroy();
        var more = world.CreateEntity(new ChildOf(next));
        Assert.That(All(children.Find(next)), Is.EquivalentTo(new[] { newKids[1], newKids[2], more }));
        Assert.That(children.Count(default), Is.Zero);
    }

    [Test]
    public void AnIndexCreatedLaterFindsExistingEntities()
    {
        using var world = new World();
        var a = world.CreateEntity(new Callsign("x"));
        world.CreateEntity(new Callsign("y"), new Frozen());
        var index = world.Index<Callsign, string>();
        Assert.That(All(index.Find("x")), Is.EqualTo(new[] { a }));
        Assert.That(index.Count("y"), Is.EqualTo(1));
    }

    [Test]
    public void BatchDestroyAndMultiRemoveUnlink()
    {
        using var world = new World();
        var callsigns = world.Index<Callsign, string>();
        var children = world.Index<ChildOf, Entity>();
        var parent = world.CreateEntity(new Health(1));
        var kids = new Entity[4];
        world.CreateEntities(kids, new ChildOf(parent), new Callsign("k"));
        kids[0].Add<Frozen>();
        Assert.That(callsigns.Count("k"), Is.EqualTo(4));

        kids[1].Remove<ChildOf, Callsign>();                   // one structural change, two indexes
        Assert.That(callsigns.Count("k"), Is.EqualTo(3));
        Assert.That(children.Count(parent), Is.EqualTo(3));

        Assert.That(world.DestroyEntities<Callsign>(QueryFilter.Create().With<Frozen>()), Is.EqualTo(1));
        Assert.That(All(callsigns.Find("k")), Is.EquivalentTo(new[] { kids[2], kids[3] }));
        Assert.That(world.DestroyEntities<ChildOf>(), Is.EqualTo(2));
        Assert.That(callsigns.Count("k"), Is.Zero);
        Assert.That(children.Count(parent), Is.Zero);
    }

    [Test]
    public void MovingToAnotherTableKeepsTheEntry()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var a = world.CreateEntity(new Callsign("x"));
        Assert.That(index.Count("x"), Is.EqualTo(1));
        a.Add<Frozen>();                                       // copies the row, not a write
        a.Set(new Health(1));
        a.Remove<Frozen>();
        Assert.That(All(index.Find("x")), Is.EqualTo(new[] { a }));
        a.Get<Callsign>() = new Callsign("y");                 // written in its new table
        Assert.That(index.Count("x"), Is.Zero);
        Assert.That(index.TryFind("y", out var y) && y == a, Is.True);
    }

    [Test]
    public void DestroyingALaterMatchDuringFindSkipsIt()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var all = new Entity[4];
        world.CreateEntities(all, new Callsign("x"));
        var seen = new List<Entity>();
        foreach (var e in index.Find("x"))
        {
            seen.Add(e);
            if (seen.Count == 1)
                foreach (var other in all)
                    if (other != e && other.IsAlive) { other.Destroy(); break; }
        }
        Assert.That(seen, Has.Count.EqualTo(3));
        Assert.That(seen, Has.All.Matches<Entity>(e => e.IsAlive));
        Assert.That(index.Count("x"), Is.EqualTo(3));
    }

    [Test]
    public void RelationsToDefaultAndOtherWorldsAreHashed()
    {
        using var world = new World();
        using var other = new World();
        var children = world.Index<ChildOf, Entity>();
        var local = world.CreateEntity(new Health(1));
        var foreign = other.CreateEntity(new Health(1)); // same id and version as local, another world
        Assert.That(foreign.Id, Is.EqualTo(local.Id));
        var orphan = world.CreateEntity(new ChildOf(default));
        var exile = world.CreateEntity(new ChildOf(foreign));
        var kid = world.CreateEntity(new ChildOf(local));

        Assert.That(All(children.Find(default)), Is.EqualTo(new[] { orphan }));
        Assert.That(All(children.Find(foreign)), Is.EqualTo(new[] { exile }));
        Assert.That(All(children.Find(local)), Is.EqualTo(new[] { kid }));
        exile.Destroy();
        orphan.Remove<ChildOf>();
        Assert.That(children.Count(foreign), Is.Zero);
        Assert.That(children.Count(default), Is.Zero);
        Assert.That(children.Count(local), Is.EqualTo(1));
    }

    [Test]
    public void RestoreReindexesRelations()
    {
        using var world = new World();
        var children = world.Index<ChildOf, Entity>();
        var parent = world.CreateEntity(new Health(1));
        var kid = world.CreateEntity(new ChildOf(parent));
        Assert.That(children.Count(parent), Is.EqualTo(1));
        var snapshot = world.Capture();

        kid.Destroy();
        world.CreateEntities(new Entity[2], new ChildOf(parent));
        Assert.That(children.Count(parent), Is.EqualTo(2));

        world.Restore(snapshot);
        Assert.That(All(children.Find(parent)), Is.EqualTo(new[] { kid }));
    }

    [Test]
    public void AddingBackWithoutAValueIndexesTheDefault()
    {
        using var world = new World();
        var children = world.Index<ChildOf, Entity>();
        var parent = world.CreateEntity(new Health(1));
        var kid = world.CreateEntity(new ChildOf(parent));
        Assert.That(children.Count(parent), Is.EqualTo(1));
        kid.Remove<ChildOf>();
        kid.Add<ChildOf>();                                    // default(Entity) key
        Assert.That(children.Count(parent), Is.Zero);
        Assert.That(All(children.Find(default)), Is.EqualTo(new[] { kid }));
        kid.Get<ChildOf>() = new ChildOf(parent);
        Assert.That(children.Count(parent), Is.EqualTo(1));
        Assert.That(children.Count(default), Is.Zero);
    }

    [Test]
    public void DisposeEmptiesIndexesStillHeld()
    {
        var world = new World();
        var index = world.Index<Callsign, string>();
        world.CreateEntity(new Callsign("x"));
        Assert.That(index.Count("x"), Is.EqualTo(1));
        world.Dispose();
        Assert.That(index.Count("x"), Is.Zero);
        Assert.That(index.TryFind("x", out _), Is.False);
    }

    [Test]
    public void OneIndexPerComponent()
    {
        using var world = new World();
        var first = world.Index<TwoKeys, int>();
        Assert.That(world.Index<TwoKeys, int>(), Is.SameAs(first));
        Assert.That(() => world.Index<TwoKeys, string>(), Throws.InvalidOperationException);
    }

    [Test]
    public void LookupsInsideAPassThatWritesTheKeySeeEveryWrite()
    {
        using var world = new World();
        world.AddSystem<RenameAfterLookup>();
        var index = world.Index<Callsign, string>();
        var all = new Entity[200];
        world.CreateEntities(all, new Callsign("a"));
        world.Update(); // each lookup happens after earlier rows of the same pass were renamed
        Assert.That(index.Count("a!"), Is.EqualTo(200));
        Assert.That(index.Count("a"), Is.Zero);
        world.Update();
        Assert.That(index.Count("a!!"), Is.EqualTo(200));
    }
}
