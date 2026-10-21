using System.Runtime.CompilerServices;
using EasyEcs;

namespace VoxelServer;

// #region load
// Brings in the chunks asked for: read back from the region files if one was unloaded before, or else generated: an
// entity with its Chunk key and two buffers of 8,192 bytes, its blocks and its water (a spring in one chunk in four).
// An OnUpdate, not a [ForEach]: LoadEntities may not run while a pass walks the world, so the requests are collected
// first, then served, Rules.MaxRequests a tick at most; the others stay and are served the next tick. A new chunk is
// Fresh: InterestSystem sends it whole to the players near it, who become its viewers.
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
            // What a chunk has from now on, read or generated, in one move: the save leaves out what it computes again.
            chunk.Set(new Viewers(0, tick), new Fresh(), new Flowing(), new DynamicBuffer<Surface>(), new DynamicBuffer<Current>());
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
            new DynamicBuffer<NextFluid>(), new FluidCount(Rules.HasSpring(pos) ? 1 : 0));
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
            World.SaveEntitiesWithout(files.Buffer, group[..size], Derived);
            files.Write(pos);
            World.DestroyEntities(group[..size]);
            World.Stats.ChunksUnloaded++;
        }
    }

    static readonly QueryFilter Unload = QueryFilter.Create().With<Unloading>();

    // What a chunk computes again after a load, left out of its save: its height map and its current.
    static readonly ComponentSet Derived = ComponentSet.Create().With<DynamicBuffer<Surface>>().With<DynamicBuffer<Current>>();
}
// #endregion unload

