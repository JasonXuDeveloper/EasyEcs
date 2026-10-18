using EasyEcs;
using NUnit.Framework;

namespace EasyEcsApp.Tests;

public class MovementSystemTests
{
    [Test]
    public void MovesEntitiesByTheirVelocity()
    {
        using var world = new World();
        world.SetClock(Tick: 0, DeltaTime: 0.5f);
        world.AddSystem<MovementSystem>();
        var mover = world.CreateEntity(new Position(0, 0), new Velocity(2, 4));
        var still = world.CreateEntity(new Position(1, 1));

        world.Update();

        Assert.That(mover.Position, Is.EqualTo(new Position(1, 2)));
        Assert.That(still.Position, Is.EqualTo(new Position(1, 1)), "no velocity: not a mover");
    }
}
