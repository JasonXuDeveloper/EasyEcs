using EasyEcs;
using Godot;

namespace GodotSample;

// Moves every entity by its velocity over one tick.
public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity, in Step step)
    {
        position = new Position(position.Value + velocity.Value * step.Seconds);
    }
}

// Turns an entity back when it leaves the arena, so every entity stays inside it.
public class BounceSystem : EcsSystem
{
    [ForEach]
    static void Bounce(ref Position position, ref Velocity velocity, in Arena arena)
    {
        Vector2 p = position.Value, v = velocity.Value;
        if (p.X < 0 || p.X > arena.Size.X) v.X = -v.X;
        if (p.Y < 0 || p.Y > arena.Size.Y) v.Y = -v.Y;
        position = new Position(p.Clamp(Vector2.Zero, arena.Size));
        velocity = new Velocity(v);
    }
}
