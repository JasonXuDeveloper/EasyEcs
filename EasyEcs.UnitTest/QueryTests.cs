using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial class QueryTests
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
    public void WithAnyNeedsOneOfTheComponents()
    {
        var halted = QueryFilter.Create().WithAny<Frozen>().WithAny<Dead>();
        Assert.That(_world.Count<Position>(halted), Is.EqualTo(3));
        Assert.That(_world.Count<Position>(QueryFilter.Create().WithAny<Health>()), Is.Zero);

        // Removing the component keeps the row (a presence bit): the filter tests the bits.
        int removed = 0;
        foreach (var item in _world.Query<Position>(QueryFilter.Create().With<Frozen>()))
            if (removed++ == 0) item.Entity.Remove<Frozen>();
        Assert.That(_world.Count<Position>(halted), Is.EqualTo(2));
        Assert.That(_world.Any<Position>(QueryFilter.Create().WithAny<Frozen>()), Is.True);

        int visited = 0;
        foreach (var _ in _world.Query<Position>(halted)) visited++;
        Assert.That(visited, Is.EqualTo(2));
    }

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
            world.CreateEntity(new Position(), new Velocity());  // appended after the pass started: not visited
            if (Visited > 1000) Assert.Fail("iteration did not terminate");
        }
    }

    [Test]
    public void StructuralChangesDuringIterationAreSafe()
    {
        var job = new KillAndSpawn(_world);
        _world.ForEach<Velocity, KillAndSpawn>(ref job, Active);

        Assert.That(job.Visited, Is.EqualTo(7)); // entities created during the pass are not visited
        Assert.That(_world.Count<Velocity>(QueryFilter.Create().With<Dead>()), Is.EqualTo(job.Visited + 1));
    }

    [Test]
    public void RemovalOutsidePassesKeepsTablesDense()
    {
        var spawned = new List<Entity>();
        for (int i = 0; i < 50; i++) spawned.Add(_world.CreateEntity(new Health(i)));
        for (int i = 0; i < 50; i += 2) spawned[i].Destroy(); // the last row fills each hole at once

        Assert.That(_world.GetFragmentationStats().FragmentationRatio, Is.Zero);
        for (int i = 1; i < 50; i += 2) Assert.That(spawned[i].Get<Health>().Value, Is.EqualTo(i));
        Assert.That(_world.Count<Health>(), Is.EqualTo(25));
    }

    [Test]
    public void TombstonesLeftByAPassAreClosedWhenItEnds()
    {
        using var world = new World();
        for (int i = 0; i < 1_000; i++) world.CreateEntity(new Health(i));
        world.AddSystem<FreezeHalf>();
        for (int i = 0; i < 20; i++)
        {
            world.Update(); // 500 entities change archetype inside the pass: tombstones until it ends
            Assert.That(world.GetFragmentationStats(), Is.EqualTo((1_000, 1_000, 0f)));
        }

        int visited = 0;
        foreach (var item in world.Query<Health>())
        {
            visited++;
            Assert.That(item.Component1.Value, Is.EqualTo(item.Entity.Id), "data follows its entity");
        }

        Assert.That(visited, Is.EqualTo(1_000));
    }

    private partial class FreezeHalf : EcsSystem
    {
        [ForEach]
        private static void Toggle(Entity entity, in Health health)
        {
            if (health.Value % 2 != 0) return;
            if (entity.Has<Frozen>()) entity.Remove<Frozen>();
            else entity.Set(new Frozen());
        }
    }
}
