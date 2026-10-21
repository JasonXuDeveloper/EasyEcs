using EasyEcs;

namespace VoxelServer;

// #region interest
// What each client has, as a vanilla server tracks it: the loaded chunks of the square around its view center, and the
// entities in them. One rule serves the chunks and the entities: a view is a square of chunks, so whatever changes,
// what a client gains or loses is the difference of two squares, a strip of one row for a step into the next chunk
// (Terrain.Gained). Nothing keeps a list per player, nothing polls, and each change is data:
// - Look ([With<Player>, Changed<InChunk>]): a player crossed into another chunk (or appeared). The chunks its view
//   gains are sent whole with their entities, or asked for (a LoadRequest) when they are not loaded; the chunks it
//   loses are forgotten. Each chunk's Viewers follow, and the player's ViewCenter moves.
// - Moved ([Changed<InChunk>]): an entity crossed into another chunk. It is spawned for the players that see its new
//   chunk and not its old one, and despawned for the reverse: the view centers in the strip between the two squares,
//   counted by the index of view centers (world.ViewCenterIndex.Count, O(1) a chunk). One just created is spawned for
//   the viewers of its chunk. Visible keeps the chunk the clients know it in.
// - Loaded ([With<Fresh>]): a chunk loaded this tick goes whole, with its entities, to the view centers around it.
// - Gone and Left ([With<Retired>]): Visible and ViewCenter are cleanup components, so a destroyed entity stays,
//   Retired, until it is despawned for its chunk's viewers, and a player who left until its view is released.
// A chunk that is Fresh is not known to any client yet: Look and Moved leave it to Loaded, which counts what is in it
// when it goes. Every player sees Rules.PlayerVision, so the viewers of a chunk are the view centers of the square
// around it. An entity that stays in its chunk, and a player who does, cost nothing here, however many ticks go by.
[UpdateInGroup<Replication>]
public class InterestSystem : EcsSystem
{
    const float Range = Rules.PlayerVision;

    [ForEach, With<Player>, Changed<InChunk>]
    void Look(Entity player, in InChunk at, in Clock clock, ref Stats stats)
    {
        bool placed = player.HasViewCenter;                               // an index's component: read through the entity
        var from = placed ? player.ViewCenter.Chunk : default;
        if (placed && from == at.Pos) return;
        foreach (var chunk in new ChunkStrip(at.Pos, from, placed, Range))
        {
            if (Known(chunk, out var loaded))
            {
                loaded.Viewers.Count++;
                stats.ChunksSent++;
                stats.CameIntoView += World.InChunkIndex.Count(chunk);
            }
            else if (loaded == default && !World.LoadRequestIndex.TryGet(chunk, out _))
                World.CreateEntity(new LoadRequest(chunk));
        }

        if (placed) Release(from, at.Pos, true, clock.Tick, ref stats);
        player.SetViewCenter(at.Pos);                                     // last: it may move the row
    }

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

    [ForEach, With<Fresh>]
    void Loaded(in Chunk key, ref Viewers viewers, ref Stats stats)
    {
        int watching = 0;
        foreach (var chunk in Terrain.ChunksAround(key.Pos, Range)) watching += World.ViewCenterIndex.Count(chunk);
        viewers.Count = watching;
        stats.ChunksSent += watching;
        stats.CameIntoView += watching * World.InChunkIndex.Count(key.Pos);
    }

    [ForEach, With<Retired>]
    void Gone(Entity entity, in Visible visible, ref Stats stats)
    {
        if (visible.Placed) stats.Despawns += ViewersOf(visible.Chunk);
        entity.Remove<Visible>();                                         // freed once its view is released too
    }

    [ForEach, With<Retired>]
    void Left(Entity player, in ViewCenter view, in Clock clock, ref Stats stats)
    {
        Release(view.Chunk, default, false, clock.Tick, ref stats);
        player.Remove<ViewCenter>();
    }

    // The chunks a view around `center` holds and one around `other` does not: forgotten by the client, one viewer
    // less each, and a chunk left with none notes since when.
    void Release(ChunkPos center, ChunkPos other, bool hasOther, long tick, ref Stats stats)
    {
        foreach (var chunk in new ChunkStrip(center, other, hasOther, Range))
        {
            if (!Known(chunk, out var loaded)) continue;
            ref var viewers = ref loaded.Viewers;
            if (--viewers.Count == 0) viewers.EmptySince = tick;
            stats.ChunksForgotten++;
            stats.LeftView += World.InChunkIndex.Count(chunk);
        }
    }

    // A chunk the clients may have: loaded, and not loaded this tick (Loaded sends that one whole).
    bool Known(ChunkPos pos, out Entity chunk) => World.ChunkIndex.TryGet(pos, out chunk) && !chunk.IsFresh;

    // The players that have `chunk` in view.
    int ViewersOf(ChunkPos chunk) => Known(chunk, out var loaded) ? loaded.Viewers.Count : 0;

    // The players that have `chunk` in view and not `other`: the view centers of the strip between the two squares.
    int Watching(ChunkPos chunk, ChunkPos other)
    {
        if (!Known(chunk, out var loaded)) return 0;                      // in no view yet
        if (!Known(other, out _)) return loaded.Viewers.Count;            // the other in none: every viewer
        int count = 0;
        foreach (var at in Terrain.Gained(chunk, other, Range)) count += World.ViewCenterIndex.Count(at);
        return count;
    }
}
// #endregion interest
