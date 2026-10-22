using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Zone(int Id) : IComponent, IGroup   // entities of one zone share tables
{
    public int Group => Id;
}

/// <summary>Every world groups its tables by the shard ([GroupBy] with [Group]): no GroupBy call to forget.</summary>
[GroupBy]
public partial record struct Shard([Group] int Id) : IComponent;

public class GroupTests
{
    [Test]
    public void ADeclaredGroupingSplitsEveryWorldsTables()
    {
        using var world = new World();                                       // never calls GroupBy
        world.CreateEntity(new Shard(0), new Health(1));
        world.CreateEntity(new Shard(1), new Health(10));
        var moved = world.CreateEntity(new Shard(2), new Health(100));
        var batch = new Entity[3];
        world.CreateEntities(batch, new Shard(1), new Health(1000));
        static int Sum(World w, int shard)
        {
            int sum = 0;
            foreach (var item in w.Query<Health>(QueryFilter.Create().InGroup<Shard>(shard))) sum += item.Component1.Value;
            return sum;
        }

        // Without the grouping every table is group 0, and InGroup(0) would match all of them: 4111.
        Assert.That((Sum(world, 0), Sum(world, 1), Sum(world, 2)), Is.EqualTo((1, 3010, 100)));
        moved.Set(new Shard(0));
        Assert.That((Sum(world, 0), Sum(world, 2)), Is.EqualTo((101, 0)));
        world.GroupBy<Shard>();                                              // idempotent
        Assert.That(Sum(world, 1), Is.EqualTo(3010));
    }

    private static int Sum(World world, int zone)
    {
        int sum = 0;
        foreach (var item in world.Query<Health>(QueryFilter.Create().InGroup<Zone>(zone))) sum += item.Component1.Value;
        return sum;
    }

    [Test]
    public void EntitiesOfOneGroupShareTablesAndQueriesWalkOnlyThem()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        var a = world.CreateEntity(new Zone(1), new Health(1));
        var b = world.CreateEntity(new Zone(2), new Health(10));
        var c = world.CreateEntity(new Zone(1), new Health(100));
        Assert.That((Sum(world, 1), Sum(world, 2), Sum(world, 3)), Is.EqualTo((101, 10, 0)));
        Assert.That(world.Count<Health>(QueryFilter.Create().InGroup<Zone>(1)), Is.EqualTo(2));

        b.Set(new Zone(1));                                              // moves to zone 1's table
        Assert.That((Sum(world, 1), Sum(world, 2), b.Read<Health>().Value, b.Read<Zone>().Id), Is.EqualTo((111, 0, 10, 1)));
        a.Set(new Position(1, 1));                                       // a new table, still in zone 1
        Assert.That(Sum(world, 1), Is.EqualTo(111));
        Assert.That(world.Count<Position>(QueryFilter.Create().InGroup<Zone>(1)), Is.EqualTo(1));
        Assert.That(c.IsAlive, Is.True);
    }

    [Test]
    public void GroupByMovesExistingEntitiesAndBatchesGoStraightToTheirGroup()
    {
        using var world = new World();
        for (int i = 0; i < 10; i++) world.CreateEntity(new Zone(i % 3), new Health(1));
        Assert.That(Sum(world, 0), Is.EqualTo(10));                      // not grouped yet: every table is group 0
        world.GroupBy<Zone>();
        Assert.That((Sum(world, 0), Sum(world, 1), Sum(world, 2)), Is.EqualTo((4, 3, 3)));

        var batch = new Entity[5];
        world.CreateEntities(batch, new Zone(7), new Health(2));
        Assert.That(Sum(world, 7), Is.EqualTo(10));
        Assert.That(world.Count<Health>(), Is.EqualTo(15));
    }

    [Test]
    public void ClonesAndPrefabsJoinTheirValuesGroup()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        var prefab = new Prefab().Set(new Zone(4)).Set(new Health(3));
        world.Instantiate(prefab);
        var e = world.CreateEntity(new Zone(5), new Health(5));
        e.Clone();
        Assert.That((Sum(world, 4), Sum(world, 5)), Is.EqualTo((3, 10)));
    }

    [Test]
    public void ForEachSetsWhileWalkingAGroup()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Zone(0), new Health(i));
        var moving = QueryFilter.Create().InGroup<Zone>(0);
        foreach (var item in world.Query<Health>(moving))
            if (item.Component1.Value % 2 == 0) item.Entity.Set(new Zone(1));
        Assert.That((world.Count<Health>(moving), world.Count<Health>(QueryFilter.Create().InGroup<Zone>(1))), Is.EqualTo((50, 50)));
    }

    [Test]
    public void RestoreKeepsGroups()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        var e = world.CreateEntity(new Zone(1), new Health(1));
        var snapshot = world.Capture();
        e.Set(new Zone(2));
        world.Restore(snapshot);
        Assert.That((Sum(world, 1), Sum(world, 2)), Is.EqualTo((1, 0)));
    }

    [Test]
    public void AFilterNamesOneGroupAndGroupByWaitsForPassesToEnd()
    {
#pragma warning disable EECS050 // the refused filter itself
        Assert.Throws<ArgumentException>(() => QueryFilter.Create().InGroup<Zone>(1).InGroup<Zone>(2));
#pragma warning restore EECS050
        Assert.DoesNotThrow(() => QueryFilter.Create().InGroup<Zone>(1).InGroup<Zone>(1));
        using var world = new World();
        world.CreateEntity(new Zone(1));
        foreach (var _ in world.Query<Zone>())
            Assert.Throws<InvalidOperationException>(() => world.GroupBy<Zone>());
    }
}
