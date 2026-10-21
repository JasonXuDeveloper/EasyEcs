using System.Runtime.CompilerServices;
using EasyEcs;

namespace VoxelServer;

// #region load
// Asks for the chunks missing around a player who crossed into another chunk: one LoadRequest per chunk, found by its
// unique index, so two players asking for the same chunk make one request. [Changed<InChunk>] visits only the players
// that crossed (GridSystem writes InChunk only then) and the players just created: the view changes only there, so a
// player who stays in its chunk asks for nothing.
[UpdateInGroup<Simulation>]
public class ChunkLoadSystem : EcsSystem
{
    [ForEach, With<Player>, Changed<InChunk>]
    void Load(in InChunk center, in Vision vision)
    {
        foreach (var pos in Terrain.ChunksAround(center.Pos, vision.Range))
            if (!World.ChunkIndex.TryGet(pos, out _) && !World.LoadRequestIndex.TryGet(pos, out _))
                World.CreateEntity(new LoadRequest(pos));
    }
}

// Brings in the chunks asked for: read back from the region files if one was unloaded before, or else generated: an
// entity with its Chunk key and two buffers of 8,192 bytes, its blocks and its water (a spring in one chunk in four).
// An OnUpdate, not a [ForEach]: LoadEntities may not run while a pass walks the world, so the requests are collected
// first, then served, Rules.MaxRequests a tick at most; the others stay and are served the next tick. A new chunk is
// Fresh: ChunkViewSystem sends it whole to the players near it, who become its viewers.
[UpdateInGroup<Simulation>]
public class ChunkReadSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        if (!World.Any<LoadRequest>()) return;
        Span<LoadRequest> asked = stackalloc LoadRequest[Rules.MaxRequests];
        Span<EntityId> served = stackalloc EntityId[Rules.MaxRequests];
        int count = 0;
        foreach (var request in World.Read<LoadRequest>())
        {
            if (count == asked.Length) break;                                    // the rest waits a tick
            asked[count] = request.LoadRequest;
            served[count++] = request.Entity;
        }
        World.DestroyEntities(served[..count]);

        var files = World.Regions.Files;
        ref var stats = ref World.Stats;
        long tick = World.Clock.Tick;
        foreach (var request in asked[..count])
        {
            Entity chunk;
            if (files.TryRead(request.Pos, out var saved))
            {
                World.LoadEntities(saved);                                        // the chunk and what was in it
                chunk = World.ChunkIndex.Get(request.Pos);
                stats.ChunksLoaded++;
            }
            else
            {
                chunk = Generate(World, request.Pos);
                stats.ChunksGenerated++;
            }
            chunk.SetViewers(0, tick);
            chunk.Set(new Fresh());
        }
    }

    // A new chunk: its blocks and its water grid, empty but for a spring in one chunk in four.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Entity Generate(World world, ChunkPos pos)
    {
        Span<Block> blocks = stackalloc Block[Terrain.BlocksPerChunk];
        Terrain.Generate(pos, blocks);
        Span<Fluid> water = stackalloc Fluid[Terrain.BlocksPerChunk];   // zeroed: no water
        if (Rules.HasSpring(pos))
        {
            int x = pos.X * Terrain.Size + Terrain.Size / 2, z = pos.Z * Terrain.Size + Terrain.Size / 2;
            water[Terrain.IndexOf(x, Terrain.SurfaceHeight(x, z) + 1, z)] = new Fluid(Fluid.Spring);
        }
        var chunk = world.CreateEntity(new Chunk(pos), new DynamicBuffer<Block>(), new DynamicBuffer<Fluid>(),
            new DynamicBuffer<NextFluid>());
        chunk.WriteBlock().AddRange(blocks);
        chunk.WriteFluid().AddRange(water);
        return chunk;
    }
}
// #endregion load

// #region unload
// Marks the chunks no player has had in view for five seconds. Slices = 20 spreads the check over a second: each tick
// visits a twentieth of the chunks.
[UpdateInGroup<Simulation>]
public class ChunkUnloadSystem : EcsSystem
{
    [ForEach(Slices = Rules.TicksPerSecond), Without<Unloading>]
    static void Unload(Entity chunk, in Viewers viewers, in Clock clock)
    {
        if (viewers.Count == 0 && clock.Tick - viewers.EmptySince > Rules.UnloadAfter) chunk.Set(new Unloading());
    }
}

// Unloads the marked chunks as a vanilla server does: the chunk and the entities in it (zombies, items) go to the
// region files with SaveEntities (a vanilla server keeps the entities in region files of their own) and are destroyed,
// so nothing ticks where no player is, and a player who comes back finds the holes it dug and the zombies it left.
// After the pass, like the loads. A chunk a player stands in (one who just respawned there) or holding more than
// Rules.MaxSaved entities stays loaded and is marked again later.
[UpdateInGroup<Simulation>]
public class ChunkSaveSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        if (!World.Any<Unloading>()) return;
        Span<EntityId> marked = stackalloc EntityId[Rules.MaxRequests];
        int count = 0;
        foreach (var chunk in World.Read<Chunk>(Unload))
            if (count < marked.Length) marked[count++] = chunk.Entity;

        var files = World.Regions.Files;
        Span<EntityId> group = stackalloc EntityId[Rules.MaxSaved];
        foreach (var id in marked[..count])
        {
            var chunk = World.GetEntity(id);
            chunk.Remove<Unloading>();                                       // not in the region file
            var pos = chunk.Chunk.Pos;
            int size = 0;
            group[size++] = id;
            bool keep = false;
            foreach (var entity in World.InChunkIndex.Find(pos))
            {
                if (entity.HasPlayer || size == group.Length)
                {
                    keep = true;
                    break;
                }
                group[size++] = entity;
            }
            if (keep) continue;
            World.SaveEntities(files.Buffer, group[..size]);
            files.Write(pos);
            World.DestroyEntities(group[..size]);
            World.Stats.ChunksUnloaded++;
        }
    }

    static readonly QueryFilter Unload = QueryFilter.Create().With<Unloading>();
}
// #endregion unload

// #region view
// The chunks each player's client has, as a vanilla server tracks them: a loaded chunk that comes into the view is sent
// whole and one that leaves it is forgotten. The set is a DynamicBuffer<InView> on the player. It changes only when the
// player crosses into another chunk or a chunk near it finishes loading, and both are data: [Changed<InChunk>] visits
// the players that crossed (GridSystem writes InChunk only then), [With<Fresh>] the chunks loaded this tick. A player
// who stays in its chunk costs nothing, however many ticks go by. The chunks' Viewers follow the sets: a chunk that
// comes into a view counts one more viewer, one that leaves it one less, and a chunk left with none notes since when.
// The entities come and go with their chunk (spawn and despawn packets): the grid counts them (InChunkIndex.Count).
[UpdateInGroup<Replication>]
public class ChunkViewSystem : EcsSystem
{
    [ForEach, Changed<InChunk>]
    void Moved(in InChunk center, in Vision vision, Mut<DynamicBuffer<InView>> view, in Clock clock, ref Stats stats)
    {
        Span<InView> now = stackalloc InView[Rules.ViewChunks];
        int count = 0;
        foreach (var chunk in Terrain.ChunksAround(center.Pos, vision.Range))
            if (World.ChunkIndex.TryGet(chunk, out _) && count < now.Length) now[count++] = new InView(chunk);
        now = now[..count];

        var before = view.Value.AsReadOnlySpan();
        int came = 0, left = 0;
        foreach (var chunk in now)
            if (!before.Contains(chunk))
            {
                World.ChunkIndex.Get(chunk.Chunk).Viewers.Count++;
                stats.CameIntoView += World.InChunkIndex.Count(chunk.Chunk);
                came++;
            }
        foreach (var chunk in before)
            if (!now.Contains(chunk))
            {
                // Still loaded: a chunk is unloaded only when no view holds it.
                ref var viewers = ref World.ChunkIndex.Get(chunk.Chunk).Viewers;
                if (--viewers.Count == 0) viewers.EmptySince = clock.Tick;
                stats.LeftView += World.InChunkIndex.Count(chunk.Chunk);
                left++;
            }
        if (came == 0 && left == 0) return;

        ref var buffer = ref view.Write();
        buffer.Clear();
        buffer.AddRange(now);
        stats.ChunksSent += came;
        stats.ChunksForgotten += left;
    }

    // A chunk loaded this tick joins the view of the players near it, through the grid. A player that crossed this
    // tick has it already (Moved ran first).
    [ForEach, With<Fresh>]
    void Loaded(in Chunk key, ref Viewers viewers, ref Stats stats)
    {
        var item = new InView(key.Pos);
        foreach (var near in Terrain.ChunksAround(key.Pos, Rules.PlayerVision))
        foreach (var player in World.InChunkIndex.Find(near))
        {
            if (!player.HasPlayer || !Terrain.InReach(player.InChunk.Pos, key.Pos, player.Vision.Range)) continue;
            if (player.InView.AsReadOnlySpan().Contains(item)) continue;
            player.WriteInView().Add(item);
            viewers.Count++;
            stats.CameIntoView += World.InChunkIndex.Count(key.Pos);
            stats.ChunksSent++;
        }
    }
}
// #endregion view
