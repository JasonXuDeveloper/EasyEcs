using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class ForEachTests
{
    private struct Integrate : IForEach<Position, Velocity>
    {
        public int Visited;

        public void Execute(int entityId, ref Position p, ref Velocity v)
        {
            p.X += v.X;
            Visited++;
        }
    }

    private struct SpawnWhileIterating : IForEach<Health>
    {
        public World World;
        public int Sum;

        public void Execute(int entityId, ref Health h)
        {
            Sum += h.Value;
            h.Value++;
            if (entityId == 0)
                for (int i = 0; i < 500; i++)
                    World.CreateEntity().Value.AddComponent<Position>(); // reallocates storage mid-pass
        }
    }

    [Test]
    public void VisitsAndWritesMatchingEntities()
    {
        using var world = new World();
        for (int i = 0; i < 10; i++)
        {
            var (_, v) = world.CreateEntity().Value.AddComponents<Position, Velocity>();
            v.Value.X = 2;
        }

        world.CreateEntity().Value.AddComponent<Position>();

        var job = new Integrate();
        world.ForEach<Position, Velocity, Integrate>(ref job);
        Assert.That(job.Visited, Is.EqualTo(10));
        foreach (var item in world.Query<Position, Velocity>())
            Assert.That(item.Component1.X, Is.EqualTo(2));
    }

    [Test]
    public void RespectsFilter()
    {
        using var world = new World();
        for (int i = 0; i < 10; i++)
        {
            ref var e = ref world.CreateEntity().Value;
            e.AddComponents<Position, Velocity>();
            if (i < 4) e.AddComponent<Frozen>();
        }

        var job = new Integrate();
        world.ForEach<Position, Velocity, Integrate>(ref job, QueryFilter.Create().Without<Frozen>());
        Assert.That(job.Visited, Is.EqualTo(6));
    }

    [Test]
    public void WritesSurviveStorageReallocationDuringThePass()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 4 });
        for (int i = 0; i < 10; i++)
            world.CreateEntity().Value.AddComponent<Health>().Value.Value = i;

        var job = new SpawnWhileIterating { World = world };
        world.ForEach<Health, SpawnWhileIterating>(ref job);
        Assert.That(job.Sum, Is.EqualTo(45));

        int total = 0;
        foreach (var item in world.Query<Health>()) total += item.Component1.Value;
        Assert.That(total, Is.EqualTo(55), "every increment landed in the live (reallocated) column");
    }

    [Test]
    public void EmptyWorldIsANoOp()
    {
        using var world = new World();
        var job = new Integrate();
        world.ForEach<Position, Velocity, Integrate>(ref job);
        Assert.That(job.Visited, Is.EqualTo(0));
    }
}
