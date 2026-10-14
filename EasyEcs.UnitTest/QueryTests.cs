using System.Collections.Generic;
using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class QueryTests
{
    private static World Populate(int count)
    {
        var world = new World();
        for (int i = 0; i < count; i++)
        {
            ref var e = ref world.CreateEntity().Value;
            e.AddComponent<Position>().Value.X = i;
            if (i % 2 == 0) e.AddComponent<Velocity>().Value.X = 1;
            if (i % 3 == 0) e.AddComponent<Frozen>();
        }

        return world;
    }

    [Test]
    public void QueryVisitsExactlyMatchingEntities()
    {
        using var world = Populate(30);
        var seen = new HashSet<int>();
        foreach (var item in world.Query<Position, Velocity>())
        {
            Assert.That(item.Entity.HasComponent<Velocity>(), Is.True);
            Assert.That(seen.Add(item.EntityId), Is.True, "each entity visited once");
            Assert.That(item.Component1.X % 2, Is.EqualTo(0));
        }

        Assert.That(seen, Has.Count.EqualTo(15));
    }

    [Test]
    public void QueryRefsWriteThrough()
    {
        using var world = Populate(10);
        foreach (var item in world.Query<Position, Velocity>())
            item.Component1.X += item.Component2.X;

        foreach (var item in world.Query<Position>())
        {
            bool moving = item.Entity.HasComponent<Velocity>();
            Assert.That(item.Component1.X, Is.EqualTo(item.EntityId + (moving ? 1 : 0)));
        }
    }

    [Test]
    public void CountAnyAndEnumeratorCount()
    {
        using var world = Populate(30);
        Assert.That(world.Count<Position>(), Is.EqualTo(30));
        Assert.That(world.Count<Position, Velocity>(), Is.EqualTo(15));
        Assert.That(world.Query<Position, Velocity>().Count, Is.EqualTo(15));
        Assert.That(world.Any<Velocity, Frozen>(), Is.True);
        Assert.That(world.Any<Dead>(), Is.False);
        Assert.That(world.Query<Dead>().IsEmpty, Is.True);
    }

    [Test]
    public void FiltersWithAndWithout()
    {
        using var world = Populate(30);
        var notFrozen = QueryFilter.Create().Without<Frozen>();
        var movingFrozen = QueryFilter.Create().With<Velocity>().With<Frozen>();
        var none = QueryFilter.Create().Without<Position>();

        Assert.That(world.Count<Position>(notFrozen), Is.EqualTo(20));
        Assert.That(world.Count<Position>(movingFrozen), Is.EqualTo(5)); // multiples of 6
        Assert.That(world.Count<Position>(none), Is.EqualTo(0));

        foreach (var item in world.Query<Position>(notFrozen))
            Assert.That(item.Entity.HasComponent<Frozen>(), Is.False);
    }

    [Test]
    public void CachedQueriesSeeNewArchetypes()
    {
        using var world = new World();
        world.CreateEntity().Value.AddComponent<Position>();
        Assert.That(world.Count<Position>(), Is.EqualTo(1)); // query cached now

        world.CreateEntity().Value.AddComponents<Position, Velocity>();
        world.CreateEntity().Value.AddComponents<Position, Frozen>();
        Assert.That(world.Count<Position>(), Is.EqualTo(3));
        Assert.That(world.Count<Position>(QueryFilter.Create().Without<Frozen>()), Is.EqualTo(2));
    }

    [Test]
    public void StructuralChangesDuringIteration()
    {
        using var world = Populate(100);
        int visited = 0;
        foreach (var item in world.Query<Position>())
        {
            visited++;
            ref var e = ref item.Entity;
            if (item.EntityId % 4 == 0) world.DestroyEntity(e);
            else if (item.EntityId % 4 == 1) e.AddComponent<Dead>();
            else if (item.EntityId % 4 == 2) e.RemoveComponent<Position>();
            world.CreateEntity(); // entities without Position are never visited
        }

        Assert.That(visited, Is.GreaterThanOrEqualTo(100).And.LessThanOrEqualTo(100 + 25));
        Assert.That(world.Count<Position>(), Is.EqualTo(50));
        Assert.That(world.Count<Position, Dead>(), Is.EqualTo(25));
    }

    [Test]
    public void NineComponentQuery()
    {
        using var world = new World();
        world.CreateEntity().Value.AddComponents<C1, C2, C3, C4, C5, C6, C7, C8, C9>();
        int n = 0;
        foreach (var item in world.Query<C1, C2, C3, C4, C5, C6, C7, C8, C9>())
        {
            item.Component9.V = 9;
            n++;
        }

        Assert.That(n, Is.EqualTo(1));
    }
}
