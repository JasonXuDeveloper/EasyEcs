using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// Makes the step the water: [Changed<FluidStep>] visits the chunks that stepped. A step that moved water is copied into
// the Fluid buffer (a write, which replication sends) and its summary becomes the chunk's FluidCount; the blocks it
// filled are a reduction (Filled). Each chunk writes its own row only (the chunks beside the edges that changed are
// woken by WaterWakeSystem, from the step's Edges), so the chunks are split across threads, 64 at a time as the step's.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct WaterCommitSystem
{
    [ForEach(Parallel = true, ChunkRows = 64), Changed<FluidStep>]
    static void Commit(in FluidStep step, in DynamicBuffer<NextFluid> next, Mut<DynamicBuffer<Fluid>> fluid, ref FluidCount count,
        ref Reduce<Filled> filled)
    {
        if (step.Result != StepResult.Moved) return;
        next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Fluid, NextFluid>(fluid.Write().AsSpan()));
        count = step.Water;
        filled.Value.Blocks += step.Filled;
    }
}
