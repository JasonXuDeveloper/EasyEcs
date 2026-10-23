using EasyEcs;

namespace VoxelServer;

// Wakes the water that may move again, the one system that does: a chunk whose blocks changed (dug, or just created or
// loaded), with its neighbors (the other half of a block update, HeightMapSystem is the first), and the chunks across
// the edges whose water the last step changed (FluidStep.Edges). Both are data the systems before left: the dig knows
// nothing of water, and the commit writes its own chunk only, so it runs on several threads. The step's edges are woken
// at the start of the next tick, not right after the commit: nothing but the water passes reads Flowing, and the next
// step is Rules.FlowDelay ticks away. The neighbors come from the tile (Chunk is a tile of the grid): Facing names the
// loaded ones across the given edges.
//
// A chunk woken gets its step's buffer here, on the owner thread: the pool keeps its lists per thread, and a list
// WaterSleepSystem gave back on this thread would not serve a first write of WaterFlowSystem on a helper thread, which
// would allocate one.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct WaterWakeSystem
{
    [ForEach, Changed<DynamicBuffer<Block>>]
    static void Dug(Entity chunk, Tile<Block, Chunk> blocks, World world)
    {
        Wake(chunk);
        foreach (var id in blocks.Facing(TileFaces.Sides)) Wake(world.GetEntity(id));
    }

    [ForEach, Changed<FluidStep>]
    static void Spread(in FluidStep step, Tile<Fluid, Chunk> water, World world)
    {
        if (step.Result != StepResult.Moved) return;
        foreach (var id in water.Facing(step.Edges)) Wake(world.GetEntity(id));
    }

    static void Wake(Entity chunk)
    {
        chunk.SetFlowing();                                                       // a presence bit
        if (chunk.NextFluid.Capacity < Terrain.BlocksPerChunk) chunk.WriteNextFluid().EnsureCapacity(Terrain.BlocksPerChunk);
    }
}
