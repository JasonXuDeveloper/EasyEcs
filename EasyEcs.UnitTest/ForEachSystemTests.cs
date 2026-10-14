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
/// outcome is data only: state (Dead) plus a notification (Died) that other systems react to without Damage knowing them.
/// </summary>
public partial class Damage : EcsSystem
{
    // Without<Dead>: an entity moved into another matching archetype mid-pass may be visited again; filters keep jobs idempotent.
    [ForEach, With<Velocity>, Without<Dead>]
    private void Hurt(Entity entity, ref Health health)
    {
        if (--health.Value > 0) return;
        entity.Set(new Dead(), new Died()); // Scoreboard counts Died; Damage does not know it exists
    }

    [ForEach]
    private static void Corpses(Entity entity, in Dead _) => entity.Remove<Velocity>();
}

/// <summary>Reacts to the Died notification (runs after Damage, before Cleanup). State lives in a singleton.</summary>
public partial class Scoreboard : EcsSystem
{
    public override int Priority => 1;

    [ForEach]
    private static void Count(in Died _, World world)
    {
        ref var score = ref world.GetOrAddSingleton<Score>();
        score.Deaths++;
        score.Points += 10;
    }
}

/// <summary>Removes notifications after every consumer ran, so each is seen exactly once.</summary>
public partial class Cleanup : EcsSystem
{
    public override int Priority => int.MaxValue;

    [ForEach]
    private static void Clear(Entity entity, in Died _) => entity.Remove<Died>();
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
        world.AddSystem<Scoreboard>();
        world.AddSystem<Cleanup>();

        world.Update();
        Assert.That(world.GetSingleton<Score>(), Is.EqualTo(new Score(1, 10)));
        Assert.That(world.Count<Died>(), Is.Zero, "notifications live for one update");
        Assert.That(world.Count<Velocity>(), Is.EqualTo(2), "Corpses ran after Hurt in the same update");

        world.Update();
        world.Update();
        Assert.That(world.GetSingleton<Score>(), Is.EqualTo(new Score(3, 30)), "each death counted exactly once");
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
