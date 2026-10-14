using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Subscribed automatically by the generator for as long as it is in a world. State lives in a singleton.</summary>
public partial class Scoreboard : EcsSystem, IEventHandler<Died>, IEventHandler<Scored>
{
    public void Handle(in Died e)
    {
        World.GetOrAddSingleton<Score>().Deaths++;
        World.Publish(new Scored(10)); // nested publish
    }

    public void Handle(in Scored e) => World.GetOrAddSingleton<Score>().Points += e.Points;
}

public class EventTests
{
    private sealed class Recorder : IEventHandler<Died>
    {
        public readonly List<int> Ids = [];
        public void Handle(in Died e) => Ids.Add(e.EntityId);
    }

    [Test]
    public void ManualSubscribersReceiveInOrderUntilUnsubscribed()
    {
        using var world = new World();
        var a = new Recorder();
        var b = new Recorder();
        world.Publish(new Died(0)); // no subscribers: no-op
        world.Subscribe(a);
        world.Subscribe(b);
        world.Publish(new Died(1));
        Assert.That(world.Unsubscribe(a), Is.True);
        Assert.That(world.Unsubscribe(a), Is.False);
        world.Publish(new Died(2));

        Assert.That(a.Ids, Is.EqualTo(new[] { 1 }));
        Assert.That(b.Ids, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void SystemsAreSubscribedForTheirLifetime()
    {
        using var world = new World();
        world.AddSystem<Scoreboard>();
        world.Publish(new Died(1));
        world.Publish(new Scored(5));
        Assert.That(world.GetSingleton<Score>(), Is.EqualTo(new Score(1, 15)));

        world.RemoveSystem<Scoreboard>();
        world.Publish(new Died(2));
        Assert.That(world.GetSingleton<Score>().Deaths, Is.EqualTo(1));
    }
}
