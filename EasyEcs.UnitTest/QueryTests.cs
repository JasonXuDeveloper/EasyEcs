using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class QueryTests
{
    private World _world;

    [SetUp]
    public void SetUp()
    {
        _world = new World();
        // 10 movers (2 frozen, 1 dead), 5 static entities, 3 empty ones.
        for (int i = 0; i < 10; i++)
        {
            var e = _world.CreateEntity(new Position(i, 0), new Velocity(1, 2));
            if (i < 2) e.Set(new Frozen());
            if (i == 9) e.Set(new Dead());
        }

        for (int i = 0; i < 5; i++) _world.CreateEntity(new Position(100 + i, 0));
        for (int i = 0; i < 3; i++) _world.CreateEntity();
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private static readonly QueryFilter Active = QueryFilter.Create().Without<Frozen>().Without<Dead>();

    [Test]
    public void CountAndAnyMatchArchetypes()
    {
        Assert.That(_world.Count<Position>(), Is.EqualTo(15));
        Assert.That(_world.Count<Position, Velocity>(), Is.EqualTo(10));
        Assert.That(_world.Count<Position, Velocity>(Active), Is.EqualTo(7));
        Assert.That(_world.Count<Position>(QueryFilter.Create().With<Frozen>()), Is.EqualTo(2));
        Assert.That(_world.Any<Health>(), Is.False);
        Assert.That(_world.Any<Velocity>(Active), Is.True);
    }

    [Test]
    public void QueryYieldsEntitiesAndWritableRefs()
    {
        foreach (var item in _world.Query<Position, Velocity>(Active))
            item.Component1.X += item.Component2.X;

        var moved = new List<float>();
        foreach (var item in _world.Query<Position>())
            if (item.Entity.Has<Velocity>()) moved.Add(item.Component1.X);

        moved.Sort();
        Assert.That(moved, Is.EqualTo(new float[] { 0, 1, 3, 4, 5, 6, 7, 8, 9, 9 }));
        Assert.That(_world.Query<Position, Velocity>(Active).Count, Is.EqualTo(7));
        Assert.That(_world.Query<Health>().IsEmpty, Is.True);
    }

    private struct Integrate : IForEach<Position, Velocity>
    {
        public int Visited;

        public void Execute(int entityId, ref Position p, ref Velocity v)
        {
            p.Y += v.Y;
            Visited++;
        }
    }

    [Test]
    public void ForEachRunsAStructJobOverMatches()
    {
        var job = new Integrate();
        _world.ForEach<Position, Velocity, Integrate>(ref job, Active);
        Assert.That(job.Visited, Is.EqualTo(7));
        Assert.That(_world.Count<Position>(), Is.EqualTo(15));
    }

    private struct KillAndSpawn(World world) : IForEach<Velocity>
    {
        public int Visited;

        public void Execute(int entityId, ref Velocity v)
        {
            Visited++;
            var e = world.GetEntity(entityId);
            e.Set(new Dead());                                   // moves the entity out of the matching archetype
            world.CreateEntity(new Position(), new Velocity());  // may or may not be visited
            if (Visited > 1000) Assert.Fail("iteration did not terminate");
        }
    }

    [Test]
    public void StructuralChangesDuringIterationAreSafe()
    {
        var job = new KillAndSpawn(_world);
        _world.ForEach<Velocity, KillAndSpawn>(ref job, Active);

        Assert.That(job.Visited, Is.GreaterThanOrEqualTo(7));
        Assert.That(_world.Count<Velocity>(QueryFilter.Create().With<Dead>()), Is.EqualTo(job.Visited + 1));
    }

    [Test]
    public void CompactionRemovesTombstonesAndOrdersIds()
    {
        var spawned = new List<Entity>();
        for (int i = 0; i < 50; i++) spawned.Add(_world.CreateEntity(new Health(i)));
        for (int i = 0; i < 50; i += 2) spawned[i].Destroy();
        for (int i = 0; i < 10; i++) _world.CreateEntity(new Health(1000 + i)); // reuse ids LIFO: out of order

        Assert.That(_world.GetFragmentationStats().FragmentationRatio, Is.GreaterThan(0));
        _world.CompactArchetypes();
        Assert.That(_world.GetFragmentationStats().FragmentationRatio, Is.Zero);

        var ids = new List<int>();
        foreach (var item in _world.Query<Health>()) ids.Add(item.Entity.Id);
        Assert.That(ids, Is.Ordered);
        Assert.That(ids, Has.Count.EqualTo(35));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void UpdateReSortsArchetypesWhoseOrderDegraded(bool sortOnUpdate)
    {
        using var world = new World(new WorldOptions { SortArchetypesOnUpdate = sortOnUpdate });
        var entities = new List<Entity>();
        for (int i = 0; i < 1_000; i++) entities.Add(world.CreateEntity(new Health(i)));
        // Churn: every other entity leaves and comes back; freed slots are reused LIFO, so the order reverses.
        for (int i = 0; i < entities.Count; i += 2) entities[i].Set(new Frozen());
        for (int i = 0; i < entities.Count; i += 2) entities[i].Remove<Frozen>();

        world.Update();

        var ids = new List<int>();
        foreach (var item in world.Query<Health>())
        {
            ids.Add(item.Entity.Id);
            Assert.That(item.Component1.Value, Is.EqualTo(item.Entity.Id), "data follows its entity");
        }

        Assert.That(ids, Has.Count.EqualTo(1_000));
        Assert.That(ids, sortOnUpdate ? Is.Ordered : Is.Not.Ordered);
        Assert.That(world.GetFragmentationStats().FragmentationRatio, sortOnUpdate ? Is.Zero : Is.GreaterThan(0));
    }
}
