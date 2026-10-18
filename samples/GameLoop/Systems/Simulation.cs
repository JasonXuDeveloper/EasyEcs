using EasyEcs;

namespace GameLoop;

// Moves players and bullets by their velocity over one fixed time step.
public sealed class MovementSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity, in Simulation simulation)
    {
        float dt = simulation.DeltaTime;
        position = new Position(position.X + velocity.X * dt, position.Y + velocity.Y * dt);
    }
}

// Counts down a bullet's lifetime and destroys it when it runs out.
// Destroying an entity in the middle of the pass is safe.
public sealed class LifetimeSystem : EcsSystem
{
    [ForEach]
    static void Age(Entity bullet, ref Lifetime lifetime)
    {
        lifetime.Ticks--;
        if (lifetime.Ticks <= 0)
        {
            bullet.Destroy();
        }
    }
}

// Counts the simulated ticks.
public sealed class ClockSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        World.Simulation.Tick++;
    }
}
