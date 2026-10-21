using System.Runtime.CompilerServices;
using EasyEcs;

namespace VoxelServer;

// #region load
// Counts, on every chunk in a player's view, the players that see it, and asks for the chunks missing there: one
// LoadRequest per chunk, found by its unique index, which counts the players that asked. A chunk or a request seen
// first in this pass starts its count again. Twice a second is enough: a player walks four blocks a second and the
// view reaches two chunks ahead. UpdatePhase = 1 runs it on the first update of the group, so the first requests are
// served at the first tick.
[UpdateInGroup<Simulation>]
public class ChunkLoadSystem : EcsSystem
{
    public override int UpdateInterval => Rules.LoadEvery;
    public override int UpdatePhase => 1;

    [ForEach, With<Player>]
    void Load(in Position position, in Vision vision, in Clock clock)
    {
        foreach (var pos in Terrain.ChunksAround(Terrain.ChunkOf(position.Value), vision.Range))
        {
            if (World.ChunkIndex.TryGet(pos, out var chunk))
            {
                var seen = chunk.Viewers;
                chunk.Viewers = new Viewers(clock.Tick, seen.Tick == clock.Tick ? seen.Count + 1 : 1);
            }
            else if (World.LoadRequestIndex.TryGet(pos, out var asked))
            {
                var request = asked.LoadRequest;
                asked.SetLoadRequest(pos, clock.Tick, request.Tick == clock.Tick ? request.Viewers + 1 : 1);
            }
            else World.CreateEntity(new LoadRequest(pos, clock.Tick, 1));
        }
    }
}

// Brings in the chunks asked for: read back from the region files if one was unloaded before, or else generated: an
// entity with its Chunk key and two buffers of 8,192 bytes, its blocks and its water (a spring in one chunk in four).
// An OnUpdate, not a [ForEach]: LoadEntities may not run while a pass walks the world, so the requests are collected
// first, then served, Rules.MaxRequests a tick at most; the others stay and are served the next tick. A new chunk is
// Fresh: its clients get it whole, not as block changes.
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
            chunk.SetViewers(request.Tick, request.Viewers);
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
// Marks the chunks no player has seen for five seconds. Slices = 20 spreads the check over a second: each tick visits a
// twentieth of the chunks.
[UpdateInGroup<Simulation>]
public class ChunkUnloadSystem : EcsSystem
{
    [ForEach(Slices = Rules.TicksPerSecond), Without<Unloading>]
    static void Unload(Entity chunk, in Viewers seen, in Clock clock)
    {
        if (clock.Tick - seen.Tick > Rules.UnloadAfter) chunk.Set(new Unloading());
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
// who stays in its chunk costs nothing, however many ticks go by.
[UpdateInGroup<Replication>]
public class ChunkViewSystem : EcsSystem
{
    [ForEach, Changed<InChunk>]
    void Moved(in InChunk center, in Vision vision, Mut<DynamicBuffer<InView>> view, ref Stats stats)
    {
        Span<InView> now = stackalloc InView[Rules.ViewChunks];
        int count = 0;
        foreach (var chunk in Terrain.ChunksAround(center.Pos, vision.Range))
            if (World.ChunkIndex.TryGet(chunk, out _) && count < now.Length) now[count++] = new InView(chunk);
        now = now[..count];

        var before = view.Value.AsReadOnlySpan();
        int kept = 0;
        foreach (var chunk in now)
            if (before.Contains(chunk)) kept++;
        int came = now.Length - kept, left = before.Length - kept;
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
    void Loaded(in Chunk key, ref Stats stats)
    {
        var item = new InView(key.Pos);
        foreach (var near in Terrain.ChunksAround(key.Pos, Rules.PlayerVision))
        foreach (var player in World.InChunkIndex.Find(near))
        {
            if (!player.HasPlayer || !Terrain.InReach(player.InChunk.Pos, key.Pos, player.Vision.Range)) continue;
            if (player.InView.AsReadOnlySpan().Contains(item)) continue;
            player.WriteInView().Add(item);
            stats.ChunksSent++;
        }
    }
}
// #endregion view
