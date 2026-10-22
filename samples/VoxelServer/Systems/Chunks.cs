using EasyEcs;

namespace VoxelServer;

// #region load
// A chunk's life, each step one system and each state data: a player's view asks for it (a LoadRequest, AoiViewSystem),
// ChunkReadSystem reads it back from the region files or ChunkGenerateSystem generates it, it is Fresh for a tick
// (AoiChunkSystem sends it whole), its water steps while it is Flowing, it is marked Unloading once nobody has seen it for a
// while, and ChunkSaveSystem writes it to the region files with what is in it.
//
// Every chunk is an instance of the chunk prefab (Program.cs), which has every component and tag a chunk ever has,
// the tags absent. So all chunks share one table, a read chunk included (its save keeps the columns), and each change
// of state is a presence bit: no chunk moves to another table, and a pass that wakes the chunks beside the one it
// visits still visits them (a pass skips an entity that joined a matching table after it started).
//
// Reads and generation are OnUpdates, not [ForEach] methods: LoadEntities and Instantiate may not run while a pass
// walks the world. Each serves Rules.MaxRequests a tick at most; the others wait a tick.
[UpdateInGroup<Simulation>]
public class ChunkReadSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        Span<EntityId> served = stackalloc EntityId[Rules.MaxRequests];
        int count = World.CollectIds<LoadRequest>(served, InFiles);           // the rest waits a tick
        if (count == 0) return;
        Span<ChunkPos> saved = stackalloc ChunkPos[Rules.MaxRequests];
        for (int i = 0; i < count; i++) saved[i] = World.GetEntity(served[i]).LoadRequest.Pos;
        World.DestroyEntities(served[..count]);

        var files = World.Regions.Files;
        long tick = World.Clock.Tick;
        foreach (var pos in saved[..count])
        {
            files.TryRead(pos, out var bytes);
            World.LoadEntities(bytes);                                           // the chunk and what was in it
            World.ChunkIndex.Get(pos).Set(new Fresh(), new Flowing());           // two presence bits
            World.Stats.ChunksLoaded++;
        }
    }

    static readonly QueryFilter InFiles = QueryFilter.Create().With<Saved>();
}

// Generates the chunks asked for that were never saved: an instance of the chunk prefab, its blocks written in place
// (SetCount gives the span to write), its water empty but for a spring in one chunk in four.
[UpdateInGroup<Simulation>]
public class ChunkGenerateSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        Span<EntityId> served = stackalloc EntityId[Rules.MaxRequests];
        int count = World.CollectIds<LoadRequest>(served, NotInFiles);
        if (count == 0) return;
        Span<ChunkPos> asked = stackalloc ChunkPos[Rules.MaxRequests];
        for (int i = 0; i < count; i++) asked[i] = World.GetEntity(served[i]).LoadRequest.Pos;
        World.DestroyEntities(served[..count]);

        var prefab = World.Prefabs.Chunk;
        foreach (var pos in asked[..count])
        {
            int x = pos.X * Terrain.Size + Terrain.Size / 2, z = pos.Z * Terrain.Size + Terrain.Size / 2;
            int y = Terrain.SurfaceHeight(x, z) + 1;                             // a spring's block, on the ground
            bool spring = Rules.HasSpring(pos);
            var chunk = World.Instantiate(prefab, new Chunk(pos), spring ? new FluidCount(1, (byte)y, (byte)y) : FluidCount.Dry);
            Terrain.Generate(pos, chunk.WriteBlock().SetCount(Terrain.BlocksPerChunk));
            var water = chunk.WriteFluid().SetCount(Terrain.BlocksPerChunk);    // zeroed: no water
            if (spring) water[Terrain.IndexOf(x, y, z)] = new Fluid(Fluid.Spring);
            World.Stats.ChunksGenerated++;
        }
    }

    static readonly QueryFilter NotInFiles = QueryFilter.Create().Without<Saved>();
}
// #endregion load

// #region unload
// Marks the chunks no player has had in view for five seconds. Slices = 20 spreads the check over a second: each tick
// visits a twentieth of the chunks.
[UpdateInGroup<Simulation>]
public class ChunkUnloadSystem : EcsSystem
{
    [ForEach(Slices = Rules.TicksPerSecond), With<Chunk>, Without<Unloading>]
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
        int count = World.CollectIds<Chunk>(marked, Unload);                  // the rest waits a tick

        var files = World.Regions.Files;
        Span<EntityId> group = stackalloc EntityId[Rules.MaxSaved];
        foreach (var id in marked[..count])
        {
            var chunk = World.GetEntity(id);
            chunk.RemoveUnloading();                                         // not in the region file
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
