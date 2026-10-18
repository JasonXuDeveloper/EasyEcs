using EasyEcs;
using NUnit.Framework;

namespace EasyEcsApp.Tests;

public class ClockSystemTests
{
    [Test]
    public void AdvancesTheClockOncePerUpdate()
    {
        using var world = new World();
        world.SetClock(Tick: 0, DeltaTime: 0.05f);
        world.AddSystem<ClockSystem>();

        world.Update();
        world.Update();

        Assert.That(world.Clock.Tick, Is.EqualTo(2));
    }
}
