using EasyEcs;

namespace VoxelServer;

// Wakes the field where it may change: a chunk whose ground changed, with its neighbors, whose halo holds its edge (a
// step across the edge of a chunk is checked against both footings). Data the system before left, as the water's wake:
// FootingSystem writes a footing only when a column changed.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ScentWakeSystem
{
    [ForEach, Changed<DynamicBuffer<Footing>>]
    static void Ground(Entity chunk, Tile<Footing, Ground> ground, World world)
    {
        chunk.SetSpreading();                                         // a presence bit
        foreach (var id in ground.Facing(TileFaces.Sides)) world.GetEntity(id).SetSpreading();
    }
}

// Keeps each player's Footprint on the column it stands on and wakes the field of the chunks it left and entered: the
// seeds of the field moved. [Changed<Position>]: only the players that moved, and of those only the few that stepped
// onto another column (a block in five ticks of walking) write their footprint, so the index of footprints by chunk is
// touched when a player crosses into another chunk.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct FootprintSystem
{
    [ForEach, Changed<Position>]
    static void Step(Entity player, in Position position, in Footprint footprint, World world)
    {
        var now = Footprint.At(position.Value);
        if (now == footprint) return;
        Wake(world, footprint.Chunk);
        if (now.Chunk != footprint.Chunk) Wake(world, now.Chunk);
        player.SetFootprint(now.Chunk, now.Column);                   // indexed: written with Set
    }

    static void Wake(World world, ChunkPos pos)
    {
        if (world.GroundIndex.TryGet(pos, out var chunk)) chunk.SetSpreading();
    }
}
