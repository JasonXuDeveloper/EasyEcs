using System.Collections.Generic;
using EasyEcs.Core;
using EasyEcs.Core.Systems;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Static [Each] with filters.</summary>
public partial class MoveSystem : SystemBase
{
    [Each, Without<Frozen>]
    private static void Move(ref Position p, in Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}

/// <summary>Instance [Each] methods, optional parameters, OnUpdate ordering and declaration order.</summary>
public partial class TraceSystem : SystemBase
{
    public readonly List<string> Trace = new();
    public readonly List<int> Ids = new();

    protected override void OnUpdate() => Trace.Add("OnUpdate");

    [Each, With<Velocity>]
    private void First(int entityId, in Position p, EntityRef entity, World world)
    {
        Trace.Add("First");
        Ids.Add(entityId);
        Assert.That(entity.Value.Id, Is.EqualTo(entityId));
        Assert.That(world, Is.SameAs(World));
    }

    [Each]
    private void Second(ref Health h)
    {
        Trace.Add("Second");
        h.Value--;
    }
}

/// <summary>Structural changes from inside [Each].</summary>
public partial class ReaperSystem : SystemBase
{
    [Each]
    private static void Reap(int entityId, in Health h, World world)
    {
        if (h.Value <= 0) world.DestroyEntity(world.GetEntityById(entityId));
    }
}

public class EachSystemTests
{
    [Test]
    public void EachRunsForMatchingEntitiesWithFilters()
    {
        using var world = new World();
        world.AddSystem<MoveSystem>();
        var moving = world.CreateEntity();
        var (_, v) = moving.Value.AddComponents<Position, Velocity>();
        v.Value.X = 2;
        var frozen = world.CreateEntity();
        frozen.Value.AddComponents<Position, Velocity, Frozen>().Item2.Value.X = 2;

        world.Update();
        world.Update();

        Assert.That(moving.Value.GetComponent<Position>().Value.X, Is.EqualTo(4));
        Assert.That(frozen.Value.GetComponent<Position>().Value.X, Is.EqualTo(0));
    }

    [Test]
    public void OnUpdateRunsBeforeEachMethodsInDeclarationOrder()
    {
        using var world = new World();
        var system = world.AddSystem<TraceSystem>();
        var a = world.CreateEntity();
        a.Value.AddComponents<Position, Velocity>();
        a.Value.AddComponent<Health>().Value.Value = 5;
        world.CreateEntity().Value.AddComponent<Position>(); // no Velocity: skipped by First

        world.Update();

        Assert.That(system.Trace, Is.EqualTo(new[] { "OnUpdate", "First", "Second" }));
        Assert.That(system.Ids, Is.EqualTo(new[] { a.Value.Id }));
        Assert.That(a.Value.GetComponent<Health>().Value.Value, Is.EqualTo(4));
    }

    [Test]
    public void EachCanDestroyEntities()
    {
        using var world = new World();
        world.AddSystem<ReaperSystem>();
        for (int i = 0; i < 10; i++) world.CreateEntity().Value.AddComponent<Health>().Value.Value = i % 2;
        world.Update();
        Assert.That(world.Count<Health>(), Is.EqualTo(5));
        Assert.That(world.EntityCount, Is.EqualTo(5));
    }
}
