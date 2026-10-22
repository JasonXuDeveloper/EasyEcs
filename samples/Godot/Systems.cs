using System;
using EasyEcs;
using Godot;

namespace GodotSample;

// Moves every entity by its velocity over one tick. Vector2's fields are lanes and its operators work field by field,
// so the generator runs this method as a vector loop over the position and velocity columns.
public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity, in Step step)
    {
        position = new Position(position.Value + velocity.Value * step.Seconds);
    }
}

// Turns an entity back when it leaves the arena, so every entity stays inside it. Each field is clamped on its own
// (Math.Clamp, not Vector2.Clamp, a call the vector loop cannot see into), so this one is a vector loop too.
public class BounceSystem : EcsSystem
{
    [ForEach]
    static void Bounce(ref Position position, ref Velocity velocity, in Arena arena)
    {
        Vector2 p = position.Value, v = velocity.Value;
        if (p.X < 0 || p.X > arena.Size.X) v.X = -v.X;
        if (p.Y < 0 || p.Y > arena.Size.Y) v.Y = -v.Y;
        position = new Position(new Vector2(Math.Clamp(p.X, 0, arena.Size.X), Math.Clamp(p.Y, 0, arena.Size.Y)));
        velocity = new Velocity(v);
    }
}
