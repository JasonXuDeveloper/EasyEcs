using EasyEcs;

namespace VoxelServer;

// Keeps each player's Footprint on the column it stands on: the seeds of the field (SeedSystem reads the players of a
// chunk from the index of footprints by chunk). [Changed<Position>]: only the players that moved, and of those only
// the few that stepped onto another column (a block in five ticks of walking) write their footprint, so the index of
// footprints by chunk is touched when a player crosses into another chunk.
[EcsSystem]
public ref struct FootprintSystem
{
    [ForEach, Changed<Position>]
    static void Step(Entity player, in Position position, in Footprint footprint)
    {
        var now = Footprint.At(position.Value);
        if (now != footprint) player.SetFootprint(now.Chunk, now.Column);   // indexed: written with Set
    }
}
