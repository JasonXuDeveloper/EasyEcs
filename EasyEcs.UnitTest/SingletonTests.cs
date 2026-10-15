using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SingletonTests
{
    [Test]
    public void AddGetRemove()
    {
        using var world = new World();
        Assert.That(world.HasSingleton<Clock>(), Is.False);
        Assert.Throws<InvalidOperationException>(() => world.GetSingleton<Clock>());

        world.AddSingleton<Clock>().Tick = 5;
        world.GetSingleton<Clock>().Tick++;
        Assert.That(world.TryGetSingleton<Clock>(out var clock), Is.True);
        Assert.That(clock.Tick, Is.EqualTo(6));

        world.RemoveSingleton<Clock>();
        Assert.That(world.TryGetSingleton<Clock>(out _), Is.False);
        world.GetOrAddSingleton<Clock>().Tick = 1;
        Assert.That(world.GetOrAddSingleton<Clock>().Tick, Is.EqualTo(1));
    }

    [Test]
    public void WorldsAreIsolated()
    {
        using var a = new World();
        using var b = new World();
        a.AddSingleton<Clock>().Tick = 1;
        Assert.That(b.HasSingleton<Clock>(), Is.False);
    }
}
