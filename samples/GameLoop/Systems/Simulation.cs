using EasyEcs;

namespace GameLoop;

// Moves players and bullets by their velocity over one fixed time step.
// Mut<Position> stamps only the positions that changed: a player standing still is not
// replicated (a ref parameter would stamp every row the method visits).
[EcsSystem]
public ref struct MovementSystem
{
    [ForEach]
    static void Move(Mut<Position> position, in Velocity velocity, in Simulation simulation)
    {
        float dt = simulation.DeltaTime;
        Position from = position.Value;
        position.SetIfChanged(new Position(from.X + velocity.X * dt, from.Y + velocity.Y * dt));
    }
}

// Counts down a bullet's lifetime and destroys it when it runs out.
// Destroying an entity in the middle of the pass is safe.
[EcsSystem]
public ref struct LifetimeSystem
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
[EcsSystem]
public ref struct ClockSystem
{
    [OnUpdate]
    static void OnUpdate(World world)
    {
        world.Simulation.Tick++;
    }
}
