using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial class IndexTests
{
    /// <summary>The cascade of the relations guide: children of dying (Dead) parents are destroyed.</summary>
    public partial class DestroyOrphans : EcsSystem
    {
        [ForEach, With<Dead>]
        private void Cascade(Entity parent)
        {
            foreach (Entity child in World.Index<Follows, EntityId>().Find(parent)) child.Destroy();
        }
    }

    /// <summary>Renames every callsign: a system writes keys with Set.</summary>
    public partial class Rename : EcsSystem
    {
        [ForEach]
        private static void Apply(Entity entity, in Callsign callsign) => entity.Set(new Callsign(callsign.Value + "!"));
    }

    /// <summary>
    /// Looks up the index it writes, in the middle of a pass over the indexed component: before renaming each entity it
    /// records how many renamed ones the index already finds.
    /// </summary>
    public partial class RenameAfterLookup : EcsSystem
    {
        [ForEach]
        private void Apply(Entity entity, in Callsign callsign)
        {
            var index = World.Index<Callsign, string>();
            World.GetOrAddSingleton<Score>().Points = index.Count(callsign.Value + "!");
            entity.Set(new Callsign(callsign.Value + "!"));
        }
    }

    /// <summary>A component with two key types: only one of them can be indexed.</summary>
    private record struct TwoKeys(int Number, string Name) : IComponent, IIndexed<int>, IIndexed<string>
    {
        readonly int IIndexed<int>.Key => Number;
        readonly string IIndexed<string>.Key => Name;
    }

    private static List<Entity> All<T, TKey>(ValueIndex<T, TKey>.Matches matches)
        where T : struct, IComponent, IIndexed<TKey> where TKey : System.IEquatable<TKey>
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

        b.Set(new Callsign("alpha"));                // a write moves b to its new key
        Assert.That(index.Count("alpha"), Is.EqualTo(2));
        Assert.That(index.Count("bravo"), Is.Zero);

        foreach (var item in world.Query<Callsign>()) item.Entity.Set(new Callsign("delta"));
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
        var children = world.Index<Follows, EntityId>();
        var parent = world.CreateEntity(new Health(1));
        var other = world.CreateEntity(new Health(1));
        var kids = new Entity[3];
        world.CreateEntities(kids, new Follows(parent));

        Assert.That(All(children.Find(parent)), Is.EquivalentTo(kids));
        Assert.That(children.Count(other), Is.Zero);

        kids[0].Set(new Follows(other));                       // re-parent
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
        var children = world.Index<Follows, EntityId>();
        var parent = world.CreateEntity(new Callsign("p"));
        var kids = new Entity[3];
        world.CreateEntities(kids, new Follows(parent), new Callsign("k"));

        world.Update();
        Assert.That(callsigns.Count("k!"), Is.EqualTo(3));
        Assert.That(callsigns.Count("k"), Is.Zero);

        parent.Set(new Dead());
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
        Assert.That(b.Id.Index, Is.EqualTo(a.Id.Index));
        Assert.That(All(index.Find("x")), Is.EqualTo(new[] { b }));
    }

    [Test]
    public void NullKeysAreNotIndexed()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var a = world.CreateEntity(new Callsign("x"));
        var b = world.CreateEntity();
        b.Set(default(Callsign)); // a null key
        Assert.That(index.Count("x"), Is.EqualTo(1));
        a.Set(default(Callsign)); // moves a out of "x" and indexes it nowhere
        Assert.That(index.Count("x"), Is.Zero);
        Assert.That(index.Count(null), Is.Zero);
        a.Set(new Callsign("x"));
        Assert.That(index.Count("x"), Is.EqualTo(1));
    }

    [Test]
    public void TwoIncarnationsOfOneIdKeepSeparateRelations()
    {
        using var world = new World();
        var children = world.Index<Follows, EntityId>();
        var old = world.CreateEntity(new Health(1));
        var oldKids = new Entity[2];
        world.CreateEntities(oldKids, new Follows(old));
        Assert.That(children.Count(old), Is.EqualTo(2));

        old.Destroy();                                     // the links stay: they keep the old version
        var next = world.CreateEntity(new Health(2));      // reuses the id
        Assert.That(next.Id.Index, Is.EqualTo(old.Id.Index));
        var newKids = new Entity[3];
        world.CreateEntities(newKids, new Follows(next));
        Assert.That(All(children.Find(next)), Is.EquivalentTo(newKids));
        Assert.That(All(children.Find(old)), Is.EquivalentTo(oldKids));

        foreach (var kid in oldKids) kid.Remove<Follows>(); // the old list empties
        Assert.That(children.Count(old), Is.Zero);
        Assert.That(children.Count(next), Is.EqualTo(3));
        newKids[0].Destroy();
        var more = world.CreateEntity(new Follows(next));
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
        var children = world.Index<Follows, EntityId>();
        var parent = world.CreateEntity(new Health(1));
        var kids = new Entity[4];
        world.CreateEntities(kids, new Follows(parent), new Callsign("k"));
        kids[0].Set(new Frozen());
        Assert.That(callsigns.Count("k"), Is.EqualTo(4));

        kids[1].Remove<Follows, Callsign>();                   // one structural change, two indexes
        Assert.That(callsigns.Count("k"), Is.EqualTo(3));
        Assert.That(children.Count(parent), Is.EqualTo(3));

        Assert.That(world.DestroyEntities<Callsign>(QueryFilter.Create().With<Frozen>()), Is.EqualTo(1));
        Assert.That(All(callsigns.Find("k")), Is.EquivalentTo(new[] { kids[2], kids[3] }));
        Assert.That(world.DestroyEntities<Follows>(), Is.EqualTo(2));
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
        a.Set(new Frozen());                                       // copies the row, not a write
        a.Set(new Health(1));
        a.Remove<Frozen>();
        Assert.That(All(index.Find("x")), Is.EqualTo(new[] { a }));
        a.Set(new Callsign("y"));                              // written in its new table
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
    public void RelationsToTheNullIdAreHashed()
    {
        using var world = new World();
        var children = world.Index<Follows, EntityId>();
        var parent = world.CreateEntity(new Health(1));
        var orphan = world.CreateEntity(new Follows(EntityId.Null));
        var kid = world.CreateEntity(new Follows(parent));

        Assert.That(All(children.Find(EntityId.Null)), Is.EqualTo(new[] { orphan }));
        Assert.That(All(children.Find(parent)), Is.EqualTo(new[] { kid }));
        orphan.Remove<Follows>();
        Assert.That(children.Count(EntityId.Null), Is.Zero);
        Assert.That(children.Count(parent), Is.EqualTo(1));
    }

    [Test]
    public void RestoreReindexesRelations()
    {
        using var world = new World();
        var children = world.Index<Follows, EntityId>();
        var parent = world.CreateEntity(new Health(1));
        var kid = world.CreateEntity(new Follows(parent));
        Assert.That(children.Count(parent), Is.EqualTo(1));
        var snapshot = world.Capture();

        kid.Destroy();
        world.CreateEntities(new Entity[2], new Follows(parent));
        Assert.That(children.Count(parent), Is.EqualTo(2));

        world.Restore(snapshot);
        Assert.That(All(children.Find(parent)), Is.EqualTo(new[] { kid }));
    }

    [Test]
    public void AddingBackTheDefaultIndexesIt()
    {
        using var world = new World();
        var children = world.Index<Follows, EntityId>();
        var parent = world.CreateEntity(new Health(1));
        var kid = world.CreateEntity(new Follows(parent));
        Assert.That(children.Count(parent), Is.EqualTo(1));
        kid.Remove<Follows>();
        kid.Set(default(Follows));                             // default(Entity) key
        Assert.That(children.Count(parent), Is.Zero);
        Assert.That(All(children.Find(default)), Is.EqualTo(new[] { kid }));
        kid.Set(new Follows(parent));
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
        Assert.That(world.GetSingleton<Score>().Points, Is.EqualTo(199)); // the last lookup saw every earlier rename
        Assert.That(index.Count("a!"), Is.EqualTo(200));
        Assert.That(index.Count("a"), Is.Zero);
        world.Update();
        Assert.That(index.Count("a!!"), Is.EqualTo(200));
    }
}
