using EasyEcs;
using NUnit.Framework;

namespace EasyEcsApp.Tests;

// A system is tested through a world of its own: the singletons and entities it needs,
// one update, then assertions on the data it left.
public class SpawnSystemTests
{
    [Test]
    public void SpawnsAWaveWhenOneIsDue()
    {
        using var world = new World();
        world.SetClock(Tick: 0, DeltaTime: 0.05f);
        world.SetSpawner(Bullet: new Prefab().SetLifetime(5), Wave: 3, Interval: 2);
        world.AddSystem<SpawnSystem>();

        world.Update();

        Assert.That(world.Count<Lifetime>(), Is.EqualTo(3));
    }

    [Test]
    public void SpawnsNothingBetweenWaves()
    {
        using var world = new World();
        world.SetClock(Tick: 1, DeltaTime: 0.05f);
        world.SetSpawner(Bullet: new Prefab().SetLifetime(5), Wave: 3, Interval: 2);
        world.AddSystem<SpawnSystem>();

        world.Update();

        Assert.That(world.EntityCount, Is.Zero);
    }
}
