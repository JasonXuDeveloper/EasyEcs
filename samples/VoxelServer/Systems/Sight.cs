using EasyEcs;

namespace VoxelServer;

// #region sight
// What each client is told about the entities, as a vanilla server tracks them: a client sees the entities of the
// chunks it has (InView, ChunkViewSystem), so no player keeps a set of entities and nothing polls. A chunk that comes
// into a view brings its entities with it (spawn packets, counted by ChunkViewSystem); here, the other two ways a view
// changes, both data:
// - Moved ([Changed<InChunk>]): an entity that crossed into another chunk is spawned for the players that see its new
//   chunk and not its old one, and despawned for the reverse; one just created is spawned for the viewers of its
//   chunk (the chunk's Viewers). Visible keeps the chunk the viewers know it in.
// - Gone ([With<Retired>]): Visible is a cleanup component, so a destroyed entity stays, Retired, with that chunk until
//   this system has despawned it for the chunk's viewers; then Visible goes and the entity is freed.
// An entity that stays in its chunk costs nothing here, however many ticks go by.
[UpdateInGroup<Replication>]
public class SightSystem : EcsSystem
{
    [ForEach, Changed<InChunk>]
    void Moved(in InChunk at, ref Visible visible, ref Stats stats)
    {
        var from = visible;
        visible = new Visible(at.Pos, true);
        if (!from.Placed) stats.CameIntoView += ViewersOf(at.Pos);
        else if (from.Chunk != at.Pos)
        {
            stats.CameIntoView += Watching(at.Pos, from.Chunk);
            stats.LeftView += Watching(from.Chunk, at.Pos);
        }
    }

    [ForEach, With<Retired>]
    void Gone(Entity entity, in Visible visible, ref Stats stats)
    {
        if (visible.Placed) stats.Despawns += ViewersOf(visible.Chunk);
        entity.Remove<Visible>();                                    // its last cleanup component: freed
    }

    // The players that have `chunk` in view.
    int ViewersOf(ChunkPos chunk) => World.ChunkIndex.TryGet(chunk, out var loaded) ? loaded.Viewers.Count : 0;

    // The players that have `chunk` in view and not `other`: those standing where their view reaches the one and not
    // the other. A crossing is one chunk over, so this walks the few chunks of the strip between the two squares.
    int Watching(ChunkPos chunk, ChunkPos other)
    {
        if (!World.ChunkIndex.TryGet(chunk, out _)) return 0;             // not loaded: in no view
        bool otherLoaded = World.ChunkIndex.TryGet(other, out _);
        int count = 0;
        foreach (var at in Terrain.ChunksAround(chunk, Rules.PlayerVision))
        {
            if (otherLoaded && Terrain.InReach(at, other, Rules.PlayerVision)) continue;
            foreach (var player in World.InChunkIndex.Find(at))
                if (player.HasPlayer) count++;
        }
        return count;
    }
}
// #endregion sight
