using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace EasyEcs.Demo;

// DragSystem as a batch method: the same slowdown over runs of whole rows at once.
[EcsSystem, UpdateInGroup<Batched>]
public ref struct DragBatchSystem
{
    [ForEach]
    static void Slow(Span<Velocity> velocities, in Clock clock)
    {
        float keep = Units.Keep(clock.Dt);

        // WebAssembly SIMD is 128 bits wide: one Vector128<float> holds the X and Y of two sparks.
        // The cast is valid because Velocity is exactly two floats, so a run of rows is one float
        // array
        var v = MemoryMarshal.Cast<Velocity, Vector128<float>>(velocities);
        for (int i = 0; i < v.Length; i++)
        {
            v[i] *= keep;
        }

        // An odd run leaves one spark over
        if ((velocities.Length & 1) != 0)
        {
            ref var last = ref velocities[^1];
            last.X *= keep;
            last.Y *= keep;
        }
    }
}
