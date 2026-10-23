using EasyEcs;

namespace MultiWorld;

// Moves every unit by its velocity. The method is written for one unit, and the generator runs it as a vector loop
// over the position and velocity columns: two floats in, two floats out, several units per instruction.
[EcsSystem]
public ref struct MoveSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity)
    {
        position = new Position(position.X + velocity.X, position.Y + velocity.Y);
    }
}

// The zone's hazard: units farther than SafeRadius from the centre lose one health per tick.
[EcsSystem]
public ref struct DamageSystem
{
    const float SafeRadius = 500;

    // ref Reduce<Tally>: the deaths of this pass, counted in a register and stored in the Tally singleton when the
    // pass ends, so no singleton is written per unit and the method is a vector loop too.
    [ForEach]
    static void Hurt(in Position position, ref Health health, ref Reduce<Tally> tally)
    {
        // Compare squared distances: no square root needed.
        float distanceSquared = position.X * position.X + position.Y * position.Y;
        if (distanceSquared < SafeRadius * SafeRadius)
        {
            return;
        }

        health.Value--;
        if (health.Value == 0)
        {
            tally.Value.Deaths++;
        }
    }
}

// The dead come back at the centre with full health.
[EcsSystem]
public ref struct RespawnSystem
{
    [ForEach]
    static void Respawn(ref Position position, ref Health health)
    {
        if (health.Value > 0)
        {
            return;
        }

        position = new Position(0, 0);
        health = new Health(100);
    }
}
