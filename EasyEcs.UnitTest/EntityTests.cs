using System;
using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class EntityTests
{
    [Test]
    public void CreateAndDestroyTrackCount()
    {
        using var world = new World();
        var a = world.CreateEntity();
        var b = world.CreateEntity();
        Assert.That(world.EntityCount, Is.EqualTo(2));
        Assert.That(a.Value.Id, Is.Not.EqualTo(b.Value.Id));

        world.DestroyEntity(a);
        Assert.That(world.EntityCount, Is.EqualTo(1));
        world.DestroyEntity(a); // second destroy of a stale ref is a no-op
        Assert.That(world.EntityCount, Is.EqualTo(1));
    }

    [Test]
    public void DestroyedIdIsRecycledWithNewVersion()
    {
        using var world = new World();
        var a = world.CreateEntity();
        int id = a.Value.Id, version = a.Value.Version;
        world.DestroyEntity(a);

        var b = world.CreateEntity();
        Assert.That(b.Value.Id, Is.EqualTo(id));
        Assert.That(b.Value.Version, Is.EqualTo(version + 1));
    }

    [Test]
    public void StaleReferencesAreRejected()
    {
        using var world = new World();
        var a = world.CreateEntity();
        Entity staleCopy = a.Value;
        var staleComponent = a.Value.AddComponent<Health>();
        world.DestroyEntity(a);
        var b = world.CreateEntity(); // reuses the id

        Assert.Throws<InvalidOperationException>(() => _ = a.Value);
        Assert.Throws<InvalidOperationException>(() => _ = staleComponent.Value);
        Assert.Throws<InvalidOperationException>(() => staleCopy.AddComponent<Position>());
        Assert.That(b.Value.HasComponent<Position>(), Is.False, "the new occupant must be untouched");
    }

    [Test]
    public void LookupById()
    {
        using var world = new World();
        var a = world.CreateEntity();
        int id = a.Value.Id;

        Assert.That(world.TryGetEntityById(id, out var found), Is.True);
        Assert.That(found, Is.EqualTo(a));
        Assert.That(world.GetEntityById(id), Is.EqualTo(a));

        world.DestroyEntity(a);
        Assert.That(world.TryGetEntityById(id, out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => world.GetEntityById(id));
        Assert.That(world.TryGetEntityById(-1, out _), Is.False);
        Assert.That(world.TryGetEntityById(1 << 20, out _), Is.False);
    }

    [Test]
    public void EqualityIncludesVersion()
    {
        using var world = new World();
        var a = world.CreateEntity();
        Entity first = a.Value;
        world.DestroyEntity(a);
        Entity second = world.CreateEntity().Value;

        Assert.That(first.Id, Is.EqualTo(second.Id));
        Assert.That(first, Is.Not.EqualTo(second));
        Assert.That(first == second, Is.False);
    }

    [Test]
    public void GrowthKeepsDataAndRefsStayValid()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 2 });
        var first = world.CreateEntity();
        var health = first.Value.AddComponent<Health>();
        health.Value.Value = 42;

        for (int i = 0; i < 1000; i++) world.CreateEntity().Value.AddComponent<Health>();

        Assert.That(health.Value.Value, Is.EqualTo(42), "ComponentRef re-resolves storage after growth");
        Assert.That(first.Value.GetComponent<Health>().Value.Value, Is.EqualTo(42));
    }

    [Test]
    public void StorageStaysBoundedUnderChurn()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
        var refs = new EntityRef[16];
        for (int round = 0; round < 1000; round++)
        {
            for (int i = 0; i < refs.Length; i++) refs[i] = world.CreateEntity();
            for (int i = 0; i < refs.Length; i++) world.DestroyEntity(refs[i]);
        }

        Assert.That(world.CreateEntity().Value.Id, Is.LessThan(16));
    }

    [Test]
    public void DestroyReleasesComponentData()
    {
        using var world = new World();
        var a = world.CreateEntity();
        a.Value.AddComponent<Name>().Value.Value = "player";
        world.DestroyEntity(a);

        var b = world.CreateEntity();
        Assert.That(b.Value.AddComponent<Name>().Value.Value, Is.Null);
    }

    [Test]
    public void AllEntitiesEnumeratesAliveOnes()
    {
        using var world = new World();
        var a = world.CreateEntity();
        var b = world.CreateEntity();
        var c = world.CreateEntity();
        world.DestroyEntity(b);

        int count = 0;
        foreach (var e in world.AllEntities)
        {
            Assert.That(e == a || e == c, Is.True);
            count++;
        }

        Assert.That(count, Is.EqualTo(2));
    }
}
