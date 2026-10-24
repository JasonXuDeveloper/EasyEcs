using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class HierarchyTests
{
    private static List<Entity> All(ValueIndex<ChildOf, EntityId>.Matches matches)
    {
        var list = new List<Entity>();
        foreach (var e in matches) list.Add(e);
        return list;
    }

    [Test]
    public void ChildrenListsTheDirectChildren()
    {
        using var world = new World();
        var root = world.CreateEntity();
        var a = world.CreateEntity(new ChildOf(root));
        var b = world.CreateEntity(new ChildOf(root));
        var grandchild = world.CreateEntity(new ChildOf(a));
        Assert.That(All(world.Children(root)), Is.EquivalentTo(new[] { a, b }));
        Assert.That(All(world.Children(a)), Is.EqualTo(new[] { grandchild }));

        grandchild.Set(new ChildOf(b));                                   // re-parent
        Assert.That((All(world.Children(a)).Count, All(world.Children(b))[0]), Is.EqualTo((0, grandchild)));
    }

    [Test]
    public void DescendantsWalkTheWholeSubtreeEachBeforeItsChildren()
    {
        using var world = new World();
        var root = world.CreateEntity();
        var a = world.CreateEntity(new ChildOf(root));
        var b = world.CreateEntity(new ChildOf(root));
        var a1 = world.CreateEntity(new ChildOf(a));
        var a11 = world.CreateEntity(new ChildOf(a1));
        var b1 = world.CreateEntity(new ChildOf(b));
        world.CreateEntity();                                              // not in the subtree

        var seen = new List<Entity>();
        foreach (var e in world.Descendants(root.Id)) seen.Add(e);
        Assert.That(seen, Is.EquivalentTo(new[] { a, b, a1, a11, b1 }));
        foreach (var (parent, child) in new[] { (a, a1), (a1, a11), (b, b1) })
            Assert.That(seen.IndexOf(parent), Is.LessThan(seen.IndexOf(child)));

        seen.Clear();
        foreach (var e in world.Descendants(a.Id))                           // a nested walk has its own stack
            foreach (var inner in world.Descendants(e.Id)) seen.Add(inner);
        Assert.That(seen, Is.EqualTo(new[] { a11 }));
        foreach (var _ in world.Descendants(b1.Id)) Assert.Fail("a leaf has no descendants");
    }

    [Test]
    public void ASubtreeIsAddedToAListRootFirstWithoutAllocating()
    {
        using var world = new World();
        var root = world.CreateEntity();
        var a = world.CreateEntity(new ChildOf(root));
        var a1 = world.CreateEntity(new ChildOf(a));
        var ids = new List<EntityId> { default };                          // added after what the list holds
        Assert.That(world.Descendants(root).AddTo(ids, includeRoot: true), Is.EqualTo(3));
        Assert.That(ids, Is.EqualTo(new[] { default, root.Id, a.Id, a1.Id }));
        Assert.That(world.Descendants(a).AddTo(ids, includeRoot: false), Is.EqualTo(1));

        var dead = world.CreateEntity();
        dead.Destroy();
        Assert.That(world.Descendants(dead).AddTo(ids, includeRoot: true), Is.Zero); // a stale root adds nothing

        long before = Allocations.Baseline();
        for (int i = 0; i < 100; i++)
        {
            ids.Clear();
            world.Descendants(root).AddTo(ids, includeRoot: true);
        }

        Assert.That(System.GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void DescendantsSkipWhatTheWalkDestroysAndRefuseCycles()
    {
        using var world = new World();
        var root = world.CreateEntity();
        var a = world.CreateEntity(new ChildOf(root));
        var b = world.CreateEntity(new ChildOf(root));
        world.CreateEntity(new ChildOf(a));
        int seen = 0;
        foreach (var e in world.Descendants(root.Id))
        {
            seen++;
            if (e == b) a.Destroy();                                       // a and its child go
            else if (e == a) b.Destroy();
        }

        Assert.That(seen, Is.LessThanOrEqualTo(2));

        var x = world.CreateEntity();
        var y = world.CreateEntity(new ChildOf(x));
        x.Set(new ChildOf(y));
        Assert.Throws<System.InvalidOperationException>(() => { foreach (var _ in world.Descendants(x.Id)) { } });
    }

    [Test]
    public void DestroyingAParentDestroysItsDescendants()
    {
        using var world = new World();
        var root = world.CreateEntity(new Health(1));
        var child = world.CreateEntity(new ChildOf(root));
        var grandchild = world.CreateEntity(new ChildOf(child));
        var detached = world.CreateEntity(new ChildOf(child));
        detached.Remove<ChildOf>();
        var other = world.CreateEntity(new Health(2));
        root.Destroy();
        Assert.That((child.IsAlive, grandchild.IsAlive, detached.IsAlive, other.IsAlive), Is.EqualTo((false, false, true, true)));
        Assert.That(world.EntityCount, Is.EqualTo(2));
    }

    [Test]
    public void BatchDestroyCascadesAndChildrenWithCleanupRetire()
    {
        using var world = new World();
        var parents = new Entity[50];
        world.CreateEntities(parents, new Health(1));
        foreach (var parent in parents)
        {
            var child = world.CreateEntity(new ChildOf(parent), new Lease(1));
            world.CreateEntity(new ChildOf(child));
        }

        Assert.That(world.DestroyAll<Health>(), Is.EqualTo(50));     // the matched entities
        Assert.That(world.EntityCount, Is.EqualTo(50));                   // the children with a lease remain, retired
        Assert.That(world.Count<Lease>(QueryFilter.Create().With<Retired>()), Is.EqualTo(50));
        Assert.That(world.Count<ChildOf>(), Is.Zero);                     // the grandchildren went with the retirement
        world.DestroyAll<Lease>();                                   // retired already: no cascade again
        Assert.That(world.EntityCount, Is.EqualTo(50));
    }

    [Test]
    public void ACycleEnds()
    {
        using var world = new World();
        var a = world.CreateEntity();
        var b = world.CreateEntity(new ChildOf(a));
        a.Set(new ChildOf(b));
        a.Destroy();
        Assert.That((a.IsAlive, b.IsAlive, world.EntityCount), Is.EqualTo((false, false, 0)));
    }
}
