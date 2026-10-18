using EasyEcs;

namespace MultiWorld;

// Moves every unit by its velocity.
public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity)
    {
        position = new Position(position.X + velocity.X, position.Y + velocity.Y);
    }
}

// The zone's hazard: units farther than SafeRadius from the centre lose one health per tick.
public class DamageSystem : EcsSystem
{
    const float SafeRadius = 500;

    [ForEach]
    static void Hurt(in Position position, ref Health health, ref Tally tally)
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
            tally.Deaths++;
        }
    }
}

// The dead come back at the centre with full health.
public class RespawnSystem : EcsSystem
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
