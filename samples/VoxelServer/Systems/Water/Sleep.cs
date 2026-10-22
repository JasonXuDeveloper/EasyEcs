using EasyEcs;

namespace VoxelServer;

// A step that moved nothing lets the chunk sleep until something wakes it: it leaves the passes (Flowing goes) and gives
// its scratch buffers, the step and the border, back to the pool (TrimExcess of an empty buffer), so only the chunks
// whose water moves hold them, and the next chunk woken takes them back without allocating. It runs before the commit,
// so a chunk that settled is still woken by a neighbour whose water reached it in the same step.
[UpdateInGroup<Simulation>]
public class WaterSleepSystem : EcsSystem
{
    [ForEach, Changed<FluidStep>]
    static void Sleep(Entity chunk, in FluidStep step, Mut<DynamicBuffer<NextFluid>> next, Mut<DynamicBuffer<Border>> border)
    {
        if (step.Result != StepResult.Settled) return;
        chunk.RemoveFlowing();
        ref var scratch = ref next.Write();
        scratch.Clear();
        scratch.TrimExcess();
        ref var edges = ref border.Write();
        edges.Clear();
        edges.TrimExcess();
    }
}
