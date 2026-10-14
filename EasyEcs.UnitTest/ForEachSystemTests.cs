using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial class Movement : EcsSystem
{
    [ForEach, Without<Frozen>]
    private static void Move(ref Position p, in Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}

/// <summary>
/// Instance method, Entity and World parameters, With filter, structural changes, declaration order. Stateless: the
/// outcome is data only: the entity's state (Dead) and the world's statistics (Score singleton).
/// </summary>
public partial class Damage : EcsSystem
{
    // Without<Dead>: an entity moved into another matching archetype mid-pass may be visited again; filters keep jobs idempotent.
    [ForEach, With<Velocity>, Without<Dead>]
    private void Hurt(Entity entity, ref Health health, World world)
    {
        if (--health.Value > 0) return;
        entity.Set(new Dead());
        world.GetOrAddSingleton<Score>().Deaths++;
    }

    [ForEach]
    private static void Corpses(Entity entity, in Dead _) => entity.Remove<Velocity>();
}

/// <summary>Derived partial systems compose: base [ForEach] methods run first.</summary>
public partial class MovementWithGravity : Movement
{
    [ForEach]
    private static void Fall(ref Velocity v) => v.Y -= 1;
}

public class ForEachSystemTests
{
    [Test]
    public void StaticForEachWithFilter()
    {
        using var world = new World();
        var moving = world.CreateEntity(new Position(0, 0), new Velocity(1, 2));
        var frozen = world.CreateEntity(new Position(0, 0), new Velocity(1, 2), new Frozen());
        world.AddSystem<Movement>();
        world.Update();
        world.Update();

        Assert.That(moving.Get<Position>(), Is.EqualTo(new Position(2, 4)));
        Assert.That(frozen.Get<Position>(), Is.EqualTo(new Position(0, 0)));
    }

    [Test]
    public void InstanceForEachWithEntityAndStructuralChanges()
    {
        using var world = new World();
        for (int hp = 1; hp <= 3; hp++) world.CreateEntity(new Health(hp), new Velocity());
        var bystander = world.CreateEntity(new Health(1)); // no Velocity: filtered out
        world.AddSystem<Damage>();

        world.Update();
        Assert.That(world.GetSingleton<Score>().Deaths, Is.EqualTo(1));
        Assert.That(world.Count<Velocity>(), Is.EqualTo(2), "Corpses ran after Hurt in the same update");

        world.Update();
        world.Update();
        Assert.That(world.GetSingleton<Score>().Deaths, Is.EqualTo(3), "Without<Dead>: each death counted once");
        Assert.That(world.Count<Dead>(), Is.EqualTo(3));
        Assert.That(bystander.Get<Health>().Value, Is.EqualTo(1));
    }

    [Test]
    public void DerivedSystemsRunBaseMethodsFirst()
    {
        using var world = new World();
        var e = world.CreateEntity(new Position(0, 0), new Velocity(0, 0));
        world.AddSystem<MovementWithGravity>();
        world.Update(); // Move (v = 0), then Fall (v = -1)
        world.Update(); // Move (y = -1), then Fall (v = -2)
        Assert.That(e.Get<Position>().Y, Is.EqualTo(-1));
        Assert.That(e.Get<Velocity>().Y, Is.EqualTo(-2));
    }
}
