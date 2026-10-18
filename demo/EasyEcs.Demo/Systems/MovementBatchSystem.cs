using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace EasyEcs.Demo;

// MovementSystem as a batch method: element i of both spans is the same spark, so the positions
// and velocities of a run line up and add as vectors.
[UpdateInGroup<Batched>]
public class MovementBatchSystem : EcsSystem
{
    [ForEach]
    static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock)
    {
        // Position and Velocity are two floats each: one Vector128<float> is two sparks
        var p = MemoryMarshal.Cast<Position, Vector128<float>>(positions);
        var v = MemoryMarshal.Cast<Velocity, Vector128<float>>(velocities);
        var dt = Vector128.Create(clock.Dt);
        for (int i = 0; i < p.Length; i++)
        {
            p[i] += v[i] * dt;
        }

        // An odd run leaves one spark over
        if ((positions.Length & 1) != 0)
        {
            ref var last = ref positions[^1];
            last.X += velocities[^1].X * clock.Dt;
            last.Y += velocities[^1].Y * clock.Dt;
        }
    }
}
