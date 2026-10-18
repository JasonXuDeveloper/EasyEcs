using EasyEcs;

namespace EasyEcsApp;

// Systems: one job each, no state of their own. They run in registration order on every world.Update().
public class MovementSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v, in Clock clock) =>
        p = new Position(p.X + v.X * clock.DeltaTime, p.Y + v.Y * clock.DeltaTime);
}

public class LifetimeSystem : EcsSystem
{
    [ForEach]
    static void Age(Entity entity, ref Lifetime life)
    {
        if (--life.Ticks <= 0) entity.Destroy();                    // safe in the middle of the pass
    }
}

public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
