using System;
using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class ComponentTests
{
    [Test]
    public void AddGetHasRemove()
    {
        using var world = new World();
        ref var entity = ref world.CreateEntity().Value;

        Assert.That(entity.HasComponent<Health>(), Is.False);
        Assert.That(entity.TryGetComponent<Health>(out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => world.GetEntityById(0).Value.GetComponent<Health>());

        entity.AddComponent<Health>().Value.Value = 7;
        Assert.That(entity.HasComponent<Health>(), Is.True);
        Assert.That(entity.GetComponent<Health>().Value.Value, Is.EqualTo(7));
        Assert.That(entity.TryGetComponent<Health>(out var h) && h.Value.Value == 7, Is.True);

        entity.RemoveComponent<Health>();
        Assert.That(entity.HasComponent<Health>(), Is.False);
        entity.RemoveComponent<Health>(); // removing a missing component is a no-op
    }

    [Test]
    public void ReAddResetsToDefault()
    {
        using var world = new World();
        ref var entity = ref world.CreateEntity().Value;
        entity.AddComponent<Health>().Value.Value = 7;
        Assert.That(entity.AddComponent<Health>().Value.Value, Is.EqualTo(0));
    }

    [Test]
    public void RefWritesGoToStorage()
    {
        using var world = new World();
        ref var entity = ref world.CreateEntity().Value;
        entity.AddComponent<Position>();

        ref var p = ref entity.GetComponent<Position>().Value;
        p.X = 3;
        Assert.That(entity.GetComponent<Position>().Value.X, Is.EqualTo(3));

        var copy = entity.GetComponent<Position>().Value; // a copy: writes are lost
        copy.X = 99;
        Assert.That(entity.GetComponent<Position>().Value.X, Is.EqualTo(3));
    }

    [Test]
    public void AddComponentsReturnsAllRefsInOneTransition()
    {
        using var world = new World();
        ref var entity = ref world.CreateEntity().Value;
        var (c1, c2, c3) = entity.AddComponents<C1, C2, C3>();
        c1.Value.V = 1;
        c2.Value.V = 2;
        c3.Value.V = 3;

        Assert.That(entity.GetComponent<C2>().Value.V, Is.EqualTo(2));
        Assert.That(world.Count<C1, C2, C3>(), Is.EqualTo(1));
    }

    [Test]
    public void NineComponentArity()
    {
        using var world = new World();
        ref var entity = ref world.CreateEntity().Value;
        var refs = entity.AddComponents<C1, C2, C3, C4, C5, C6, C7, C8, C9>();
        refs.Item9.Value.V = 9;
        Assert.That(world.Count<C1, C2, C3, C4, C5, C6, C7, C8, C9>(), Is.EqualTo(1));

        entity.RemoveComponents<C1, C2, C3, C4, C5, C6, C7, C8, C9>();
        Assert.That(world.Any<C1>(), Is.False);
        Assert.That(world.EntityCount, Is.EqualTo(1));
    }

    [Test]
    public void RemoveComponentsIgnoresMissingOnes()
    {
        using var world = new World();
        ref var entity = ref world.CreateEntity().Value;
        entity.AddComponents<C1, C3>();
        entity.RemoveComponents<C1, C2>();
        Assert.That(entity.HasComponent<C1>(), Is.False);
        Assert.That(entity.HasComponent<C3>(), Is.True);
    }

    [Test]
    public void RemoveReleasesManagedReferences()
    {
        using var world = new World();
        ref var entity = ref world.CreateEntity().Value;
        entity.AddComponent<Name>().Value.Value = "x";
        entity.RemoveComponent<Name>();
        Assert.That(entity.AddComponent<Name>().Value.Value, Is.Null);
    }

    [Test]
    public void ComponentsAreIsolatedPerWorld()
    {
        using var a = new World();
        using var b = new World();
        a.CreateEntity().Value.AddComponent<Health>();
        Assert.That(a.Count<Health>(), Is.EqualTo(1));
        Assert.That(b.Count<Health>(), Is.EqualTo(0));
        Assert.That(b.Any<Health>(), Is.False);
    }
}
