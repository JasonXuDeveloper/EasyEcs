using System.Runtime.CompilerServices;
using EasyEcs;

namespace VoxelServer;

// #region load
// Counts, on every chunk in a player's view, the players that see it, and asks for the chunks missing there. A chunk
// seen first in this pass starts its count again. Twice a second is enough: a player walks four blocks a second and
// the view reaches two chunks ahead. UpdatePhase = 1 runs it on the first update of the group, so the chunks are there
// at tick 0.
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
            else World.CreateEntity(new LoadRequest(pos, clock.Tick));
        }
    }
}

// Brings in the chunks asked for: read back from the region files if one was unloaded before, or else generated: an
// entity with its Chunk key and two buffers of 8,192 bytes, its blocks and its water (a spring in one chunk in four).
// An OnUpdate, not a [ForEach]: LoadEntities may not run while a pass walks the world, so the requests are collected
// first, then served. Two players asking for one chunk make one chunk seen by both.
[UpdateInGroup<Simulation>]
public class ChunkReadSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        if (!World.Any<LoadRequest>()) return;
        Span<LoadRequest> asked = stackalloc LoadRequest[Rules.MaxRequests];
        int count = 0;
        foreach (var request in World.Read<LoadRequest>())
            if (count < asked.Length) asked[count++] = request.LoadRequest;   // the rest waits for the next pass
        World.DestroyAll<LoadRequest>();

        var files = World.Regions.Files;
        ref var stats = ref World.Stats;
        foreach (var request in asked[..count])
        {
            if (World.ChunkIndex.TryGet(request.Pos, out var chunk))
            {
                var seen = chunk.Viewers;                                         // asked twice: one more viewer
                chunk.Viewers = seen with { Count = seen.Count + 1 };
                continue;
            }
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
            chunk.SetViewers(request.Tick, 1);
        }
    }

    // A new chunk: its blocks and its water grid, empty but for a spring in one chunk in four.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Entity Generate(World world, ChunkPos pos)
    {
        Span<Block> blocks = stackalloc Block[Terrain.BlocksPerChunk];
        Terrain.Generate(pos, blocks);
        Span<Fluid> water = stackalloc Fluid[Terrain.BlocksPerChunk];
        water.Clear();
        if (Rules.HasSpring(pos))
        {
            int x = pos.X * Terrain.Size + Terrain.Size / 2, z = pos.Z * Terrain.Size + Terrain.Size / 2;
            water[Terrain.IndexOf(x, Terrain.SurfaceHeight(x, z) + 1, z)] = new Fluid(Fluid.Spring);
        }
        var chunk = world.CreateEntity(new Chunk(pos), new DynamicBuffer<Block>(), new DynamicBuffer<Fluid>());
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
// region files with SaveEntities and are destroyed, so nothing ticks where no player is, and a player who comes back
// finds the holes it dug and the zombies it left. After the pass, like the loads.
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
        Span<EntityId> group = stackalloc EntityId[1 + Rules.MaxSeen];
        foreach (var id in marked[..count])
        {
            var chunk = World.GetEntity(id);
            chunk.Remove<Unloading>();                                       // not in the region file
            var pos = chunk.Chunk.Pos;
            int size = 0;
            group[size++] = id;
            foreach (var entity in World.InChunkIndex.Find(pos))
                if (size < group.Length) group[size++] = entity;
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
// The chunks each player's client has, as a vanilla server tracks them: twice a second, a chunk that came into the
// view is sent whole and one that left it is forgotten. The set is a DynamicBuffer<InView> on the player, written
// through Mut<T> only when it changed.
[UpdateInGroup<Replication>]
public class ChunkViewSystem : EcsSystem
{
    public override int UpdateInterval => Rules.LoadEvery;
    public override int UpdatePhase => 1;

    [ForEach]
    static void Track(in Position position, in Vision vision, Mut<DynamicBuffer<InView>> view, ref Stats stats)
    {
        var center = Terrain.ChunkOf(position.Value);
        int reach = Rules.ChunksIn(vision.Range), kept = 0;
        var before = view.Value.AsReadOnlySpan();
        foreach (var had in before)
            if (Math.Abs(had.Chunk.X - center.X) <= reach && Math.Abs(had.Chunk.Z - center.Z) <= reach) kept++;
        int side = 2 * reach + 1, came = side * side - kept, left = before.Length - kept;
        if (came == 0 && left == 0) return;

        ref var buffer = ref view.Write();
        buffer.Clear();
        foreach (var chunk in Terrain.ChunksAround(center, vision.Range)) buffer.Add(new InView(chunk));
        stats.ChunksSent += came;
        stats.ChunksForgotten += left;
    }
}
// #endregion view
