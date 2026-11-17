using EasyEcs;

namespace VoxelServer;

// #region aoi
// The area of interest (AOI): what each client has, as a vanilla server tracks it: the loaded chunks of the square of
// Rules.PlayerVision around its player's ViewCenter, and the entities in them. A view is a square of chunks, so
// whatever changes, what a client gains or loses is the difference of two squares, a strip of one row for a step into
// the next chunk (Terrain.Gained). Nothing keeps a list per player and nothing polls: four systems react to four
// changes, each its own data.
//
// | Change                                        | System           | The client gets                               |
// | a player crosses into another chunk, appears  | AoiViewSystem    | the chunks of the strip gained, whole         |
// | a player leaves                               | AoiViewSystem    | (its view is released)                        |
// | a chunk finishes loading (Fresh)              | AoiChunkSystem   | the chunk, whole, for the players around it   |
// | an entity crosses into another chunk, appears | AoiSpawnSystem   | a spawn, or a despawn, for the strip between  |
// | an entity is destroyed (Retired)              | AoiDespawnSystem | a despawn for its chunk's viewers             |
//
// Who sees a chunk is counted, never searched: every player sees the same distance, so the viewers of a chunk are the
// view centers of the square around it, and the index of view centers counts the players of a chunk in O(1). Each
// chunk's Viewers follows the views. A chunk that is Fresh is not known to any client yet: it goes whole, with what is
// in it, from AoiChunkSystem. An entity that stays in its chunk, and a player who does, cost nothing here.

// A player's view: the chunks it gains go whole, with their entities (or a LoadRequest when not loaded), the chunks it
// loses are forgotten, each chunk's Viewers follow, and ViewCenter moves. ViewCenter is a cleanup component, so a player
// who leaves stays, Retired, until its view is released.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct AoiViewSystem
{
    [ForEach, With<Player>, Changed<InChunk>]
    static void Look(Entity player, in InChunk at, ReadOnlyMaybe<ViewCenter> view, in Clock clock, in Regions regions, ref Reduce<Stats> stats, World world)
    {
        bool placed = view.HasValue;                                      // none yet: the player just appeared
        var from = placed ? view.Value.Chunk : default;
        if (placed && from == at.Pos) return;
        foreach (var chunk in new ChunkStrip(at.Pos, from, placed, Aoi.Range))
        {
            if (Aoi.Known(world, chunk, out var loaded))
            {
                loaded.Viewers.Count++;
                stats.Value.ChunksSent++;
                stats.Value.CameIntoView += world.InChunkIndex.Count(chunk);
            }
            else if (loaded == default && !world.LoadRequestIndex.TryGet(chunk, out _))
            {
                var request = world.CreateEntity(new LoadRequest(chunk), new Saved());
                if (!regions.Files.Has(chunk)) request.RemoveSaved();     // never saved: generated
            }
        }
        if (placed) Release(world, from, at.Pos, true, clock.Tick, ref stats.Value);
        player.SetViewCenter(at.Pos);                                     // last: the view is not read after it
    }

    [ForEach, With<Retired>]
    static void Left(Entity player, in ViewCenter view, in Clock clock, ref Reduce<Stats> stats, World world)
    {
        Release(world, view.Chunk, default, false, clock.Tick, ref stats.Value);
        player.RemoveViewCenter();
    }

    // The chunks a view around `center` holds and one around `other` does not: forgotten by the client, one viewer less
    // each, and a chunk left with none notes since when.
    static void Release(World world, ChunkPos center, ChunkPos other, bool hasOther, long tick, ref Stats stats)
    {
        foreach (var chunk in new ChunkStrip(center, other, hasOther, Aoi.Range))
        {
            if (!Aoi.Known(world, chunk, out var loaded)) continue;
            ref var viewers = ref loaded.Viewers;
            if (--viewers.Count == 0) viewers.EmptySince = tick;
            stats.ChunksForgotten++;
            stats.LeftView += world.InChunkIndex.Count(chunk);
        }
    }
}

// A chunk loaded this tick goes whole, with its entities, to the players around it, who become its viewers. The counts
// come from lookups, so the system does not take the World.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct AoiChunkSystem
{
    [ForEach, With<Fresh>]
    static void Loaded(in Chunk key, ref Viewers viewers, in Clock clock, ref Reduce<Stats> stats, Lookup<ViewCenter> views,
        Lookup<InChunk> entities)
    {
        int watching = 0;
        foreach (var chunk in Terrain.ChunksAround(key.Pos, Aoi.Range)) watching += views.Count(chunk);
        viewers = new Viewers(watching, clock.Tick);
        stats.Value.ChunksSent += watching;
        stats.Value.CameIntoView += watching * entities.Count(key.Pos);
    }
}

// An entity just created is spawned for the viewers of its chunk (it has no Visible yet: it gains it here), one that
// crossed into another chunk for the players that see its new chunk and not its old one, and despawned for the
// reverse. Visible keeps the chunk the clients know it in. Lookups, not the World: what is asked of the chunks is
// plain reads (Views). The view-center index is read through Watching, a helper of this type: a pass builds the index of
// a lookup only for the code of its own type.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct AoiSpawnSystem
{
    [ForEach, Without<Visible>]
    static void Appeared(Entity entity, in InChunk at, ref Reduce<Stats> stats, Lookup<Chunk> chunks, Lookup<Fresh> fresh,
        Lookup<Viewers> viewers)
    {
        stats.Value.CameIntoView += new Views(chunks, fresh, viewers).ViewersOf(at.Pos);
        entity.SetVisible(at.Pos);                                        // last: the row is not read after it
    }

    [ForEach, Changed<InChunk>]
    static void Moved(in InChunk at, ref Visible visible, ref Reduce<Stats> stats, Lookup<Chunk> chunks, Lookup<Fresh> fresh,
        Lookup<Viewers> viewers, Lookup<ViewCenter> centers)
    {
        if (visible.Chunk == at.Pos) return;
        var views = new Views(chunks, fresh, viewers);
        stats.Value.CameIntoView += Watching(views, centers, at.Pos, visible.Chunk);
        stats.Value.LeftView += Watching(views, centers, visible.Chunk, at.Pos);
        visible = new Visible(at.Pos);
    }

    // The players that have `chunk` in view and not `other`: the view centers of the strip between the two squares.
    static long Watching(in Views views, Lookup<ViewCenter> centers, ChunkPos chunk, ChunkPos other)
    {
        if (!views.Known(chunk)) return 0;                                // in no view yet
        if (!views.Known(other)) return views.ViewersOf(chunk);           // the other in none: every viewer
        long count = 0;
        foreach (var at in Terrain.Gained(chunk, other, Aoi.Range)) count += centers.Count(at);
        return count;
    }
}

// Visible is a cleanup component: a destroyed entity stays, Retired, until it is despawned for its chunk's viewers.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct AoiDespawnSystem
{
    [ForEach, With<Retired>]
    static void Gone(Entity entity, in Visible visible, ref Reduce<Stats> stats, Lookup<Chunk> chunks, Lookup<Fresh> fresh,
        Lookup<Viewers> viewers)
    {
        stats.Value.Despawns += new Views(chunks, fresh, viewers).ViewersOf(visible.Chunk);
        entity.RemoveVisible();                                           // freed once its view is released too
    }
}

// What the AOI systems ask of the chunks: plain reads of their data.
public static class Aoi
{
    public const float Range = Rules.PlayerVision;

    // A chunk the clients may have: loaded, and not loaded this tick (AoiChunkSystem sends that one whole).
    public static bool Known(World world, ChunkPos pos, out Entity chunk) => world.ChunkIndex.TryGet(pos, out chunk) && !chunk.IsFresh;
}

// The same through lookups, for the systems that do not write the chunks.
public readonly ref struct Views
{
    readonly Lookup<Chunk> chunks;
    readonly Lookup<Fresh> fresh;
    readonly Lookup<Viewers> viewers;

    public Views(Lookup<Chunk> chunks, Lookup<Fresh> fresh, Lookup<Viewers> viewers)
    {
        this.chunks = chunks;
        this.fresh = fresh;
        this.viewers = viewers;
    }

    // Whether the clients may have the chunk: loaded, and not loaded this tick.
    public bool Known(ChunkPos pos) => Known(pos, out _);

    // The players that have `chunk` in view.
    public long ViewersOf(ChunkPos chunk) => Known(chunk, out var id) ? viewers.Read(id).Count : 0;

    bool Known(ChunkPos pos, out EntityId id)
    {
        id = default;
        if (!chunks.TryFind(pos, out var chunk) || fresh.Has(chunk.Id)) return false;
        id = chunk.Id;
        return true;
    }
}
// #endregion aoi
