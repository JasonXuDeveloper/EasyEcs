using EasyEcs;

namespace VoxelServer;

// The other half of a block update (HeightMapSystem is the first): the chunk's water, and its neighbours', may move again.
[UpdateInGroup<Simulation>]
public class WaterWakeSystem : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<Block>>]
    void Wake(Entity chunk, in Chunk key)
    {
        chunk.SetFlowing();
        Terrain.Wake(World, key.Pos, Terrain.AllSides);
    }
}
