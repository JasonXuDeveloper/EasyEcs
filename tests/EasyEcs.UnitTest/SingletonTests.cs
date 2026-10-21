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

        Assert.That(world.RemoveSingleton<Clock>(), Is.True);
        Assert.That(world.RemoveSingleton<Clock>(), Is.False);
        Assert.That(world.TryGetSingleton<Clock>(out _), Is.False);
        world.GetOrAddSingleton<Clock>().Tick = 1;
        Assert.That(world.GetOrAddSingleton<Clock>().Tick, Is.EqualTo(1));
    }

    public class Ticker : EcsSystem
    {
        [ForEach, With<Health>] static void Tick(ref Clock clock) => clock.Tick++;
    }

    [Test]
    public void ASystemWhoseSingletonWasNeverSetThrows()
    {
        using var world = new World();
        world.CreateEntity(new Health(1));
        world.AddSystem<Ticker>();
        var error = Assert.Throws<InvalidOperationException>(() => world.Update());
        Assert.That(error!.Message, Does.Contain("Singleton Clock was not added").And.Contain("SetClock"));
        Assert.That(world.HasSingleton<Clock>(), Is.False, "a read never adds the singleton");
        Assert.Throws<InvalidOperationException>(() => world.SingletonRef<Clock>());

        world.SetClock(10);
        world.Update();
        Assert.That(world.Clock.Tick, Is.EqualTo(11));
    }

    [Test]
    public void AddingAgainThrowsAndKeepsTheValue()
    {
        using var world = new World();
        world.AddSingleton<Clock>();
        var clock = world.SingletonRef<Clock>();
        clock.Value.Tick = 5;
        var error = Assert.Throws<InvalidOperationException>(() => world.AddSingleton<Clock>());
        Assert.That(error!.Message, Does.Contain("added to this world already").And.Contain("SetClock").And.Contain("GetOrAddClock"));
        Assert.That(world.GetSingleton<Clock>().Tick, Is.EqualTo(5), "nothing was reset");
        world.SetClock(default);                 // a reset says so, in the same storage
        Assert.That(clock.Value.Tick, Is.Zero, "the handle still reaches the singleton");
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
