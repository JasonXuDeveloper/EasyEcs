using EasyEcs;

namespace VoxelServer;

// A step that moved nothing lets the chunk sleep until something wakes it: it leaves the passes (Flowing goes) and gives
// its scratch buffer, the step, back to the pool (TrimExcess of an empty buffer), so only the chunks whose water moves
// hold one, and the next chunk woken takes it back without allocating (WaterWakeSystem, on this thread too). A chunk that
// settled is still woken by a neighbor whose water reached it in the same step: WaterWakeSystem reads the steps after
// this system.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct WaterSleepSystem
{
    [ForEach, Changed<FluidStep>]
    static void Sleep(Entity chunk, in FluidStep step, Mut<DynamicBuffer<NextFluid>> next)
    {
        if (step.Result != StepResult.Settled) return;
        chunk.RemoveFlowing();
        ref var scratch = ref next.Write();
        scratch.Clear();
        scratch.TrimExcess();
    }
}
