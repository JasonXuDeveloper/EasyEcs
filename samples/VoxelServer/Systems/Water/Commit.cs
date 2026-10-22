using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// Makes the step the water: [Changed<FluidStep>] visits the chunks that stepped. A step that moved water is copied into
// the Fluid buffer (a write, which replication sends), its summary becomes the chunk's FluidCount, and the chunks beside
// the edges whose water changed are woken.
[UpdateInGroup<Simulation>]
public class WaterCommitSystem : EcsSystem
{
    [ForEach, Changed<FluidStep>]
    void Commit(in Chunk key, in FluidStep step, in DynamicBuffer<NextFluid> next, Mut<DynamicBuffer<Fluid>> fluid,
        ref FluidCount count, ref Stats stats)
    {
        if (step.Result != StepResult.Moved) return;
        next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Fluid, NextFluid>(fluid.Write().AsSpan()));
        count = step.Water;
        stats.WaterCells += step.Filled;
        Terrain.Wake(World, key.Pos, step.Edges);
    }
}
