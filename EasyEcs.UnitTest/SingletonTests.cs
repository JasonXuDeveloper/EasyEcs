using System;
using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SingletonTests
{
    [Test]
    public void AddGetHasRemove()
    {
        using var world = new World();
        Assert.That(world.HasSingleton<Clock>(), Is.False);
        Assert.Throws<InvalidOperationException>(() => world.GetSingleton<Clock>());

        world.AddSingleton<Clock>().Tick = 5;
        world.GetSingleton<Clock>().Tick++;
        Assert.That(world.GetSingleton<Clock>().Tick, Is.EqualTo(6));
        Assert.That(world.TryGetSingleton<Clock>(out var clock) && clock.Value.Tick == 6, Is.True);

        world.RemoveSingleton<Clock>();
        Assert.That(world.HasSingleton<Clock>(), Is.False);
        Assert.That(world.TryGetSingleton<Clock>(out var missing), Is.False);
        Assert.That(missing.IsValid, Is.False);
    }

    [Test]
    public void AddResetsValue()
    {
        using var world = new World();
        world.AddSingleton<Config>().Value = 3;
        Assert.That(world.AddSingleton<Config>().Value, Is.EqualTo(0));
    }

    [Test]
    public void SingletonsArePerWorld()
    {
        using var a = new World();
        using var b = new World();
        a.AddSingleton<Clock>().Tick = 1;
        Assert.That(b.HasSingleton<Clock>(), Is.False);
        b.AddSingleton<Clock>().Tick = 2;
        Assert.That(a.GetSingleton<Clock>().Tick, Is.EqualTo(1));
        Assert.That(b.GetSingleton<Clock>().Tick, Is.EqualTo(2));
    }
}
