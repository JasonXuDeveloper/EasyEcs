using EasyEcs;

namespace VoxelServer;

// The other half of a block update (HeightMapSystem is the first): the chunk's water, and its neighbors', may move again.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct WaterWakeSystem
{
    [ForEach, Changed<DynamicBuffer<Block>>]
    static void Wake(Entity chunk, in Chunk key, World world)
    {
        chunk.SetFlowing();
        Terrain.Wake(world, key.Pos, Terrain.AllSides);
    }
}
