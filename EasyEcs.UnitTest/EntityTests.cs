using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class EntityTests
{
    private World _world;

    [SetUp]
    public void SetUp() => _world = new World(new WorldOptions { InitialEntityCapacity = 2 });

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void CreateAndDestroyTrackTheCount()
    {
        var a = _world.CreateEntity();
        var b = _world.CreateEntity();
        Assert.That(a.Id, Is.Not.EqualTo(b.Id));
        Assert.That(_world.EntityCount, Is.EqualTo(2));

        a.Destroy();
        a.Destroy(); // destroying a stale handle is a no-op
        Assert.That(_world.EntityCount, Is.EqualTo(1));
        Assert.That(a.IsAlive, Is.False);
        Assert.That(b.IsAlive, Is.True);
    }

    [Test]
    public void RecycledIdsGetANewVersionAndOldHandlesStayStale()
    {
        var old = _world.CreateEntity().Set(new Health(1));
        old.Destroy();
        var reused = _world.CreateEntity().Set(new Health(2));

        Assert.That(reused.Id, Is.EqualTo(old.Id));
        Assert.That(reused.Version, Is.EqualTo(old.Version + 1));
        Assert.That(reused, Is.Not.EqualTo(old));
        Assert.Throws<InvalidOperationException>(() => old.Get<Health>());
        Assert.Throws<InvalidOperationException>(() => old.Has<Health>());
        Assert.That(reused.Get<Health>().Value, Is.EqualTo(2), "the stale handle never touches the new occupant");
    }

    [Test]
    public void LookupByIdAndEnumeration()
    {
        var entities = new List<Entity>();
        for (int i = 0; i < 100; i++) entities.Add(_world.CreateEntity()); // grows past the initial capacity
        entities[10].Destroy();

        Assert.That(_world.TryGetEntity(entities[5].Id, out var found), Is.True);
        Assert.That(found, Is.EqualTo(entities[5]));
        Assert.That(_world.TryGetEntity(entities[10].Id, out _), Is.False);
        Assert.That(_world.TryGetEntity(-1, out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => _world.GetEntity(entities[10].Id));

        var seen = new List<Entity>();
        foreach (var e in _world.Entities) seen.Add(e);
        entities.RemoveAt(10);
        Assert.That(seen, Is.EqualTo(entities));
    }

    [Test]
    public void HandlesFromAnotherWorldAreRejected()
    {
        using var other = new World();
        var foreign = other.CreateEntity();
        _world.DestroyEntity(foreign);
        Assert.That(foreign.IsAlive, Is.True);
        Assert.That(default(Entity).IsAlive, Is.False);
    }

    [Test]
    public void DisposeInvalidatesEveryHandle()
    {
        var e = _world.CreateEntity().Set(new Health(1));
        _world.Dispose();
        Assert.That(e.IsAlive, Is.False);
        Assert.Throws<InvalidOperationException>(() => e.Get<Health>());
    }
}
