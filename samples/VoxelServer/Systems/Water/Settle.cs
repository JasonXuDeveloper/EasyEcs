using EasyEcs;

namespace VoxelServer;

// Ends a step of the water: [Changed<FluidStep>] visits the chunks that stepped. The water itself needs no copy: the
// step wrote the tile's second buffer and the pass swapped it in (a ping-pong). What is left is what the step
// summarized: a step that moved water makes its count the chunk's FluidCount (each chunk writes its own row only, so
// the chunks are split across threads, 64 at a time as the step's); a step that moved nothing lets the chunk sleep
// until something wakes it: it leaves the passes (Flowing goes). A chunk that settled is still woken by a neighbor
// whose water reached it in the same step: WaterWakeSystem reads the steps after this system.
[EcsSystem]
public ref struct WaterSettleSystem
{
    [ForEach(Parallel = true, ChunkSize = 64), Changed<FluidStep>]
    static void Count(in Stepped step, in FluidAhead ahead, ref FluidCount count)
    {
        if (step.Result == StepResult.Moved) count = ahead.Count;
    }

    [ForEach, Changed<FluidStep>]
    static void Sleep(Entity chunk, in Stepped step)
    {
        if (step.Result == StepResult.Settled) chunk.RemoveFlowing();
    }
}
