using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class IndexTests
{
    /// <summary>The cascade of the relations guide: children of dying (Dead) parents are destroyed.</summary>
    public class DestroyOrphans : EcsSystem
    {
        [ForEach, With<Dead>]
        private void Cascade(Entity parent)
        {
            foreach (Entity child in World.Index<Follows, EntityId>().Find(parent)) child.Destroy();
        }
    }

#pragma warning disable EECS028 // a new key is a new string: the renames are what these systems test
    /// <summary>Renames every callsign: a system writes keys with Set.</summary>
    public class Rename : EcsSystem
    {
        [ForEach]
        private static void Apply(Entity entity, in Callsign callsign) => entity.Set(new Callsign(callsign.Value + "!"));
    }

    /// <summary>
    /// Looks up the index it writes, in the middle of a pass over the indexed component: before renaming each entity it
    /// records how many renamed ones the index already finds.
    /// </summary>
    public class RenameAfterLookup : EcsSystem
    {
        [ForEach]
        private void Apply(Entity entity, in Callsign callsign)
        {
            var index = World.Index<Callsign, string>();
            World.GetOrAddScore().Points = index.Count(callsign.Value + "!");
            entity.Set(new Callsign(callsign.Value + "!"));
        }
    }
#pragma warning restore EECS028

    /// <summary>A component with two key types: only one of them can be indexed.</summary>
    private record struct TwoKeys(int Number, string Name) : IComponent, IIndexed<int>, IIndexed<string>
    {
        readonly int IIndexed<int>.Key => Number;
        readonly string IIndexed<string>.Key => Name;
    }

    /// <summary>A unique component the generator registers nothing for (generic): its index waits for a first Index call.</summary>
    private record struct Ticket<TOf>(int Id) : IComponent, IUniqueIndexed<int>
    {
        public readonly int Key => Id;
    }

    private struct Unregistered;

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
    public void AComponentAddedByGetOrAddIsFoundByItsDefaultKey()
    {
        using var world = new World();
        var index = world.Index<Rating, int>();
        var a = world.CreateEntity(new Health(1));
        Add<Rating>(a);
        Assert.That(index.TryFind(0, out var found) && found == a, Is.True);
    }

    // Generic: a ref of an indexed component is EECS015 where its type is known.
    private static void Add<T>(Entity entity) where T : struct, IComponent => entity.GetOrAdd<T>();

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
        Assert.That((children.Find(parent).Count, children.Find(other).Count, world.Children(other).Count), Is.EqualTo((2, 1, 0)));

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
    public void EntitiesDestroyedDuringFindLeaveRoomForLaterOnes()
    {
        using var world = new World();
        var index = world.Index<Callsign, string>();
        var all = new Entity[6];
        world.CreateEntities(all, new Callsign("x"));
        var seen = new List<Entity>();
        foreach (var e in index.Find("x"))
        {
            seen.Add(e);
            e.Destroy();                                       // the current one, and an unvisited one each step
            foreach (var other in all)
                if (other.IsAlive && !seen.Contains(other)) { other.Destroy(); break; }
        }

        Assert.That(seen, Has.Count.EqualTo(3));
        Assert.That(index.Count("x"), Is.Zero);
        var later = new Entity[8];
        world.CreateEntities(later, new Callsign("x"));         // the emptied key comes back
        all[0] = world.CreateEntity(new Callsign("y"));
        later[3].Destroy();
        later[5].Set(new Callsign("y"));
        var alive = new List<Entity>();
        foreach (var e in later)
            if (e.IsAlive && e.Read<Callsign>().Value == "x") alive.Add(e);
        alive.Add(world.CreateEntity(new Callsign("x")));      // fills a hole
        Assert.That(All(index.Find("x")), Is.EquivalentTo(alive));
        Assert.That(index.Count("x"), Is.EqualTo(alive.Count));
        Assert.That(All(index.Find("y")), Is.EquivalentTo(new[] { all[0], later[5] }));
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

    [Test]
    public void AUniqueIndexFindsTheOneEntityOfAKey()
    {
        using var world = new World();
        var a = world.CreateEntity(new Login(1));
        var b = world.CreateEntity(new Login(2), new Health(5));
        var accounts = world.LoginIndex;                     // the generated member: Index<Login, int>()
        Assert.That(accounts, Is.SameAs(world.Index<Login, int>()));
        Assert.That(accounts.Get(1), Is.EqualTo(a));
        Assert.That(accounts.TryGet(2, out var found) && found == b, Is.True);
        Assert.That(accounts.TryGet(3, out _), Is.False);
        Assert.That(() => accounts.Get(3), Throws.InvalidOperationException.With.Message.Contains("3"));
        Assert.That(accounts.Count(1), Is.EqualTo(1));        // the multi-value API works too
        Assert.That(All(accounts.Find(2)), Is.EqualTo(new[] { b }));
        Assert.That(accounts.Ascending().MoveNext(), Is.True);
    }

    [Test]
    public void SetRejectsAKeyAnotherEntityHas()
    {
        using var world = new World();
        var accounts = world.Index<Login, int>();
        var a = world.CreateEntity(new Login(1));
        var b = world.CreateEntity(new Login(2));
        var c = world.CreateEntity(new Health(3));

        Assert.That(() => b.Set(new Login(1)),
            Throws.InvalidOperationException.With.Message.Contains(nameof(Login)).And.Message.Contains("key 1"));
        Assert.That(b.Read<Login>().Id, Is.EqualTo(2));      // unchanged, still indexed under its key
        Assert.That(accounts.Get(2), Is.EqualTo(b));
        Assert.That(accounts.Get(1), Is.EqualTo(a));

        Assert.That(() => c.Set(new Login(1)), Throws.InvalidOperationException);
        Assert.That(c.Has<Login>(), Is.False);              // the row did not move
        Assert.That(() => c.Set(new Login(1), new Frozen()), Throws.InvalidOperationException);
        Assert.That(c.Has<Frozen>(), Is.False);               // several components: none is written
        Assert.That(c.Read<Health>().Value, Is.EqualTo(3));

        a.Set(new Login(1));                                 // its own key again
        a.Set(new Login(1), new Health(1));
        a.Set(new Login(3));                                 // a key moves: the holder changes first
        b.Set(new Login(1));
        Assert.That((accounts.Get(1), accounts.Get(3), accounts.Count(2)), Is.EqualTo((b, a, 0)));
    }

    [Test]
    public void CreatingEntitiesRejectsTakenAndRepeatedKeys()
    {
        using var world = new World();
        var accounts = world.Index<Login, int>();
        world.CreateEntity(new Login(1));
        Assert.That(() => world.CreateEntity(new Login(1)), Throws.InvalidOperationException);
        Assert.That(() => world.CreateEntity(new Health(1), new Login(1)), Throws.InvalidOperationException);
        Assert.That(() => world.CreateEntities(new Entity[2], new Login(2)),
            Throws.InvalidOperationException.With.Message.Contains("several"));
        Assert.That(() => world.CreateEntities(new Entity[1], new Login(1)), Throws.InvalidOperationException);
        Assert.That((world.EntityCount, accounts.Count(2)), Is.EqualTo((1, 0)));

        var one = new Entity[1];
        world.CreateEntities(one, new Login(2));             // one entity: a free key is fine
        Assert.That(accounts.Get(2), Is.EqualTo(one[0]));
        world.CreateEntities(System.Span<Entity>.Empty, new Login(1));
        Assert.That(world.EntityCount, Is.EqualTo(2));
    }

    [Test]
    public void GetOrAddClaimsTheDefaultKey()
    {
        using var world = new World();
        var accounts = world.Index<Login, int>();
        var a = world.CreateEntity(new Health(1));
        var b = world.CreateEntity(new Health(2));
#pragma warning disable EECS015 // GetOrAdd of an indexed component is what this tests
        a.GetOrAdd<Login>();
        Assert.That(accounts.Get(0), Is.EqualTo(a));           // indexed under the default's key
        Assert.That(() => b.GetOrAdd<Login>(), Throws.InvalidOperationException);
#pragma warning restore EECS015
        Assert.That(b.Has<Login>(), Is.False);
    }

    [Test]
    public void DestroyRemoveAndDisableFreeTheKey()
    {
        using var world = new World();
        var accounts = world.Index<Login, int>();
        var a = world.CreateEntity(new Login(1));
        a.Destroy();
        var b = world.CreateEntity(new Login(1));
        b.Remove<Login>();
        var c = world.CreateEntity(new Login(1));
        Assert.That(c.Disable<Login>(), Is.True);            // a disabled key is no key
        var d = world.CreateEntity(new Login(1));
        Assert.That(accounts.Get(1), Is.EqualTo(d));

        // Enabling it again would give the key twice: refused, the component stays disabled.
        Assert.That(() => c.Enable<Login>(), Throws.InvalidOperationException);
        Assert.That(c.IsDisabled<Login>(), Is.True);
        d.Set(new Login(2));
        Assert.That(c.Enable<Login>(), Is.True);
        Assert.That(accounts.Get(1), Is.EqualTo(c));
    }

    [Test]
    public void BulkWritesClaimEveryKeyFirst()
    {
        using var world = new World();
        var accounts = world.Index<Login, int>();
        var a = world.CreateEntity(new Login(1), new Health(1));
        var b = world.CreateEntity(new Login(2), new Health(2));
        Assert.That(() => world.SetAll(new Login(7), QueryFilter.Create().With<Health>()), Throws.InvalidOperationException);
        Assert.That((a.Read<Login>().Id, b.Read<Login>().Id, accounts.Count(7)), Is.EqualTo((1, 2, 0)));

        b.Set(new Frozen());
        Assert.That(world.SetAll(new Login(7), QueryFilter.Create().Without<Frozen>()), Is.EqualTo(1)); // one entity: fine
        Assert.That(accounts.Get(7), Is.EqualTo(a));
        Assert.That(world.SetAll(new Login(7), QueryFilter.Create().Without<Frozen>()), Is.EqualTo(1)); // its own key
        Assert.That(() => world.SetAll(new Login(7), QueryFilter.Create().With<Frozen>()), Throws.InvalidOperationException);

        // Two kept values with one key: enabling both would give it twice.
        b.Set(new Login(9));
        b.Disable<Login>();
        a.Set(new Login(9));
        a.Disable<Login>();
        Assert.That(() => world.EnableAll<Login>(), Throws.InvalidOperationException);
        Assert.That(a.IsDisabled<Login>() && b.IsDisabled<Login>(), Is.True);
        Assert.That(accounts.Count(9), Is.Zero);
        a.Enable<Login>();
        a.Set(new Login(4));
        a.Disable<Login>();
        Assert.That(world.EnableAll<Login>(), Is.EqualTo(2));
        Assert.That((accounts.Get(4), accounts.Get(9)), Is.EqualTo((a, b)));
    }

    [Test]
    public void ClonesAndPrefabsCannotRepeatAKey()
    {
        using var world = new World();
        var accounts = world.Index<Login, int>();
        var a = world.CreateEntity(new Login(1), new Health(1));
        Assert.That(() => a.Clone(), Throws.InvalidOperationException);
        Assert.That(world.EntityCount, Is.EqualTo(1));
        var named = world.CreateEntity(new Handle(null), new Health(1));
        Assert.That(named.Clone().Has<Handle>(), Is.True);     // a null key is no key

        var prefab = new Prefab().Set(new Login(2), new Health(10));
        var first = world.Instantiate(prefab);
        Assert.That(accounts.Get(2), Is.EqualTo(first));
        Assert.That(() => world.Instantiate(prefab), Throws.InvalidOperationException);
        first.Destroy();
        Assert.That(() => world.Instantiate(prefab, new Entity[2]), Throws.InvalidOperationException);
        Assert.That(world.EntityCount, Is.EqualTo(3));
        world.Instantiate(prefab, new Entity[1]);

        // A group: its members' keys are distinct (the prefab's own index), but two instances repeat them.
        var boss = new Prefab().Set(new Login(10));
        boss.AddMember().Set(new Login(11));
        Assert.That(() => boss.AddMember().Set(new Login(10)), Throws.InvalidOperationException);
        Assert.That(() => world.Instantiate(boss, new Entity[2]), Throws.InvalidOperationException);
        Assert.That(world.EntityCount, Is.EqualTo(4));
        world.Instantiate(boss);
        Assert.That(() => world.Instantiate(boss), Throws.InvalidOperationException);
        Assert.That((world.EntityCount, accounts.Get(11).Has<Login>()), Is.EqualTo((7, true)));
    }

    [Test]
    public void InstantiateWithComponentsClaimsTheKeysItWrites()
    {
        using var world = new World();
        var accounts = world.Index<Login, int>();
        var prefab = new Prefab().Set(new Login(2), new Health(10));
        var first = world.Instantiate(prefab, new Health(1));                          // the prefab's key
        Assert.That(() => world.Instantiate(prefab, new Health(2)), Throws.InvalidOperationException);
        var second = world.Instantiate(prefab, new Login(3));                          // a given key replaces the prefab's
        Assert.That((accounts.Get(2), accounts.Get(3)), Is.EqualTo((first, second)));
        Assert.That(() => world.Instantiate(prefab, new Login(3)), Throws.InvalidOperationException);
        Assert.That(() => world.Instantiate(new Prefab().Set(new Health(1)), new Entity[2], new Login(4)), Throws.InvalidOperationException);
        Assert.That(() => world.Instantiate(prefab, new Entity[2], new Health(3)), Throws.InvalidOperationException);
        Assert.That(world.EntityCount, Is.EqualTo(2));                                 // refused before anything changed
        var batch = new Entity[1];
        world.Instantiate(new Prefab().Set(new Health(1)), batch, new Login(4));
        Assert.That(accounts.Get(4), Is.EqualTo(batch[0]));
    }

    [Test]
    public void NullKeysAreNoKeys()
    {
        using var world = new World();
        var handles = world.Index<Handle, string>();
        var a = world.CreateEntity(new Handle(null));
        var b = world.CreateEntity(new Handle(null));
        world.CreateEntities(new Entity[3], new Handle(null));
        world.SetAll(new Handle(null));
        Assert.That(handles.TryGet(null, out _), Is.False);
        a.Set(new Handle("a"));
        Assert.That(() => b.Set(new Handle("a")), Throws.InvalidOperationException);
        Assert.That(handles.Get("a"), Is.EqualTo(a));
    }

    [Test]
    public void LoadsRejectRepeatedAndTakenKeys()
    {
        using var source = new World();
        var first = source.CreateEntity(new Login(0x1234567), new Health(1));
        var second = source.CreateEntity(new Login(0x7654321), new Health(2));
        var save = Save(source);
        var twice = Repeat(save, 0x7654321, 0x1234567);       // the same save with the first key twice
        var group = SaveEntities(source, first.Id, second.Id);
        var groupTwice = Repeat(group, 0x7654321, 0x1234567);

        using var world = new World();
        var kept = world.CreateEntity(new Login(5));
        Assert.That(() => world.Load(twice), Throws.TypeOf<System.IO.InvalidDataException>().With.Message.Contains(nameof(Login)));
        Assert.That((world.EntityCount, world.LoginIndex.Get(5)), Is.EqualTo((1, kept)));
        Assert.That(() => world.LoadEntities(groupTwice), Throws.TypeOf<System.IO.InvalidDataException>());
        Assert.That(world.EntityCount, Is.EqualTo(1));

        world.Load(save);                                      // replaces the world: its keys go
        Assert.That((world.EntityCount, world.LoginIndex.TryGet(5, out _)), Is.EqualTo((2, false)));
        var loaded = world.LoginIndex.Get(0x1234567);
        Assert.That(loaded.Read<Health>().Value, Is.EqualTo(1));
        Assert.That(() => loaded.Set(new Login(0x7654321)), Throws.InvalidOperationException); // indexed at once

        // LoadEntities, LoadEntitiesInto, MoveEntities: a key the world has already is refused before anything arrives.
        Assert.That(() => world.LoadEntities(group), Throws.InvalidOperationException);
        Assert.That(world.EntityCount, Is.EqualTo(2));
        var into = new[] { world.CreateEntity(new Health(7)), world.CreateEntity(new Health(8)) };
        Assert.That(() => world.LoadEntitiesInto(group, into), Throws.InvalidOperationException);
        Assert.That(into[0].Has<Login>() || into[1].Has<Login>(), Is.False);
        Assert.That(() => source.MoveEntities(world, [first.Id]), Throws.InvalidOperationException);
        Assert.That((source.EntityCount, world.EntityCount, first.IsAlive), Is.EqualTo((2, 4, true)));

        using var empty = new World();
        Assert.That(empty.LoadEntities(group), Is.EqualTo(2));
        using var other = new World();
        var targets = new[] { other.CreateEntity(), other.CreateEntity(new Login(0x7654321)) };
        other.LoadEntitiesInto(group, targets);                // targets[1] gets the key it holds
        Assert.That((other.LoginIndex.Get(0x1234567), other.LoginIndex.Get(0x7654321)), Is.EqualTo((targets[0], targets[1])));
        using var moved = new World();
        Assert.That(source.MoveEntities(moved, [first.Id]), Is.EqualTo(1));
        Assert.That(moved.LoginIndex.Get(0x1234567).Read<Health>().Value, Is.EqualTo(1));
    }

    [Test]
    public void GroupMovesKeepUniqueKeys()
    {
        using var world = new World();
        var a = world.CreateEntity(new Login(1), new Zone(1));
        var b = world.CreateEntity(new Login(2), new Zone(2));
        world.GroupBy<Zone>();                                 // the rows move to their groups' tables: no key changes
        a.Set(new Zone(2));                                    // a row move between group tables: the key goes along
        Assert.That(() => b.Set(new Login(1), new Zone(1)), Throws.InvalidOperationException);
        Assert.That((b.Read<Zone>().Id, b.Read<Login>().Id), Is.EqualTo((2, 2)));
        Assert.That((world.LoginIndex.Get(1), world.LoginIndex.Get(2)), Is.EqualTo((a, b)));
        Assert.That(() => world.CreateEntity(new Login(2), new Zone(3)), Throws.InvalidOperationException);
        Assert.That(world.EntityCount, Is.EqualTo(2));
    }

    [Test]
    public void RestoreIndexesUniqueKeysAtOnce()
    {
        using var world = new World();
        var a = world.CreateEntity(new Login(1));
        var snapshot = world.Capture();
        a.Set(new Login(2));
        world.CreateEntity(new Login(1));
        world.Restore(snapshot);
        Assert.That(() => world.CreateEntity(new Login(1)), Throws.InvalidOperationException); // before any lookup
        Assert.That((world.EntityCount, world.LoginIndex.Get(1), world.LoginIndex.TryGet(2, out _)), Is.EqualTo((1, a, false)));
    }

    [Test]
    public void EachWorldHasItsOwnKeys()
    {
        using var one = new World();
        using var two = new World();
        var a = one.CreateEntity(new Login(1));
        var b = two.CreateEntity(new Login(1));
        Assert.That((one.LoginIndex.Get(1), two.LoginIndex.Get(1)), Is.EqualTo((a, b)));
    }

    [Test]
    public void AUniqueComponentWithoutARegisteredIndexWaitsForOne()
    {
        using var world = new World();
        Assert.That(() => world.CreateEntity(new Ticket<Unregistered>(1)), Throws.InvalidOperationException.With.Message.Contains("Index"));
        Assert.That(world.EntityCount, Is.Zero);              // nothing was created
        var tickets = world.Index<Ticket<Unregistered>, int>();
        var a = world.CreateEntity(new Ticket<Unregistered>(1));
        Assert.That(() => world.CreateEntity(new Ticket<Unregistered>(1)), Throws.InvalidOperationException);
        Assert.That(tickets.Get(1), Is.EqualTo(a));
        using var other = new World();                         // known to every world from now on
        other.CreateEntity(new Ticket<Unregistered>(1));
        Assert.That(() => other.CreateEntity(new Ticket<Unregistered>(1)), Throws.InvalidOperationException);
    }

    [Test]
    public void UniqueWritesAndLookupsDoNotAllocate()
    {
        using var world = new World();
        var accounts = world.LoginIndex;
        var players = new Entity[64];
        world.CreateEntities(players, new Health(1));
        for (int i = 0; i < players.Length; i++) players[i].Set(new Login(i));
        int Churn()
        {
            int found = 0;
            for (int i = 0; i < players.Length; i++) players[i].Set(new Login(i + 1000)); // every key moves
            for (int i = 0; i < players.Length; i++) players[i].Set(new Login(i));
            for (int i = 0; i < players.Length; i++)
            {
                if (accounts.Get(i) == players[i]) found++;
                if (accounts.TryGet(i + 1000, out _)) found--;
            }

            world.CreateEntity(new Login(-1)).Destroy();
            return found;
        }

        Churn();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        int result = Churn();
        Assert.That(System.GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(result, Is.EqualTo(players.Length));
    }

    private static byte[] Save(World world)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        world.Save(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] SaveEntities(World world, params EntityId[] ids)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        world.SaveEntities(buffer, ids);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary><paramref name="save"/> with the int <paramref name="find"/> (found once) replaced by <paramref name="with"/>.</summary>
    private static byte[] Repeat(byte[] save, int find, int with)
    {
        var copy = (byte[])save.Clone();
        var pattern = System.BitConverter.GetBytes(find);
        int at = System.MemoryExtensions.IndexOf(System.MemoryExtensions.AsSpan(copy), pattern);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        Assert.That(System.MemoryExtensions.IndexOf(System.MemoryExtensions.AsSpan(copy, at + 1), pattern), Is.LessThan(0));
        System.BitConverter.GetBytes(with).CopyTo(copy, at);
        return copy;
    }
}
