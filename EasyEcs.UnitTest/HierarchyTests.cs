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

        Assert.That(world.DestroyEntities<Health>(), Is.EqualTo(50));     // the matched entities
        Assert.That(world.EntityCount, Is.EqualTo(50));                   // the children with a lease remain, retired
        Assert.That(world.Count<Lease>(QueryFilter.Create().With<Destroyed>()), Is.EqualTo(50));
        Assert.That(world.Count<ChildOf>(), Is.Zero);                     // the grandchildren went with the retirement
        world.DestroyEntities<Lease>();                                   // retired already: no cascade again
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
