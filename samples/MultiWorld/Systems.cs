using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace MultiWorld;

// Moves every unit by its velocity, a whole run of rows per call: the method takes spans of the table's columns.
public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
    {
        // Two floats each, no padding: as Vector<float> spans, every lane is one coordinate (the JIT does not
        // vectorize loops by itself).
        var p = MemoryMarshal.Cast<Position, Vector<float>>(positions);
        var v = MemoryMarshal.Cast<Velocity, Vector<float>>(velocities);
        for (int i = 0; i < p.Length; i++)
        {
            p[i] += v[i];
        }

        // The coordinates left over after the last whole vector.
        var pf = MemoryMarshal.Cast<Position, float>(positions);
        var vf = MemoryMarshal.Cast<Velocity, float>(velocities);
        for (int i = p.Length * Vector<float>.Count; i < pf.Length; i++)
        {
            pf[i] += vf[i];
        }
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
