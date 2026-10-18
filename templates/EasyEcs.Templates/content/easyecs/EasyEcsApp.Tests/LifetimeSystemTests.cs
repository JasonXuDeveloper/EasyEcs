using EasyEcs;
using NUnit.Framework;

namespace EasyEcsApp.Tests;

public class LifetimeSystemTests
{
    [Test]
    public void DestroysAnEntityWhenItsLifetimeRunsOut()
    {
        using var world = new World();
        world.AddSystem<LifetimeSystem>();
        var expiring = world.CreateEntity(new Lifetime(1));
        var lasting = world.CreateEntity(new Lifetime(3));

        world.Update();

        Assert.That(expiring.IsAlive, Is.False);
        Assert.That(lasting.IsAlive, Is.True);
        Assert.That(lasting.Lifetime.Ticks, Is.EqualTo(2));
    }
}
