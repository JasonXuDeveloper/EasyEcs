using EasyEcs;

namespace VoxelServer;

// #region load
// A chunk's life, each step one system and each state data: a player's view asks for it (a LoadRequest, AoiViewSystem),
// ChunkReadSystem reads it back from the region files or ChunkGenerateSystem generates it, it is Fresh for a tick
// (AoiChunkSystem sends it whole), its water steps while it is Flowing and the players' scent while it is Spreading (both
// from the start: what was saved has moved on since), it is marked Unloading once nobody has seen it for a while, and
// ChunkSaveSystem writes it to the region files with what is in it.
//
// Every chunk is an instance of the chunk prefab (Program.cs), which has every component and tag a chunk ever has,
// the tags absent. So all chunks share one table, a read chunk included (its save keeps the columns), and each change
// of state is a presence bit: no chunk moves to another table, and a pass that wakes the chunks beside the one it
// visits still visits them (a pass skips an entity that joined a matching table after it started).
//
// Reads and generation serve Rules.MaxRequests requests a tick at most ([ForEach(MaxRows = N)]): the generator collects
// the first requests and runs the method on each outside the walk, where it may load (LoadEntities) and create
// entities, and [Consume] destroys each request served. The others wait a tick.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ChunkReadSystem
{
    [ForEach(MaxRows = Rules.MaxRequests), With<Saved>, Consume]
    static void Read(in LoadRequest request, World world, in Regions regions, ref Stats stats)
    {
        regions.Files.TryRead(request.Pos, out var bytes);
        world.LoadEntities(bytes);                                               // the chunk and what was in it
        world.ChunkIndex.Get(request.Pos).Set(new Fresh(), new Flowing(), new Spreading());   // presence bits
        stats.ChunksLoaded++;
    }
}

// Generates the chunks asked for that were never saved: an instance of the chunk prefab, its blocks written in place
// (SetCount gives the span to write), its water empty but for a spring in one chunk in four.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ChunkGenerateSystem
{
    [ForEach(MaxRows = Rules.MaxRequests), Without<Saved>, Consume]
    static void Generate(in LoadRequest request, World world, in Prefabs prefabs, ref Stats stats)
    {
        var pos = request.Pos;
        int x = pos.X * Terrain.Size + Terrain.Size / 2, z = pos.Z * Terrain.Size + Terrain.Size / 2;
        int y = Terrain.SurfaceHeight(x, z) + 1;                                 // a spring's block, on the ground
        bool spring = Rules.HasSpring(pos);
        var chunk = world.Instantiate(prefabs.Chunk, new Chunk(pos), new Ground(pos),
            spring ? new FluidCount(1, (byte)y, (byte)y) : FluidCount.Dry);
        if (((pos.X + pos.Z) & 1) == 0) chunk.RemoveBlack();                     // a square of the checkerboard: a presence bit
        else chunk.RemoveRed();
        Terrain.Generate(pos, chunk.WriteBlock().SetCount(Terrain.BlocksPerChunk));
        var water = chunk.WriteFluid().SetCount(Terrain.BlocksPerChunk);        // zeroed: no water
        if (spring) water[Terrain.IndexOf(x, y, z)] = new Fluid(Fluid.Spring);
        // The field's buffers, zeroed (no scent; the footing is FootingSystem's, later this tick), taken here on the
        // owner thread: the pool keeps its lists per thread, and the field's first writes are on helper threads.
        const int columns = Terrain.Size * Terrain.Size;
        chunk.WriteFooting().SetCount(columns);
        chunk.WriteScent().SetCount(columns);
        chunk.WriteNextScent().SetCount(columns);
        chunk.WriteTrail().SetCount(columns);
        stats.ChunksGenerated++;
    }
}
// #endregion load

// #region unload
// Marks the chunks no player has had in view for five seconds. Slices = 20 spreads the check over a second: each tick
// visits a twentieth of the chunks.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ChunkUnloadSystem
{
    [ForEach(Slices = Rules.TicksPerSecond), With<Chunk>, Without<Unloading>]
    static void Unload(Entity chunk, in Viewers viewers, in Clock clock)
    {
        if (viewers.Count == 0 && clock.Tick - viewers.EmptySince > Rules.UnloadAfter) chunk.SetUnloading();
    }
}

// Unloads the marked chunks as a vanilla server does: the chunk and the entities in it (zombies, items) go to the
// region files with SaveEntities (a vanilla server keeps the entities in region files of their own) and are destroyed,
// so nothing ticks where no player is, and a player who comes back finds the holes it dug and the zombies it left.
// Rules.MaxRequests a tick at most, outside the walk like the loads. A chunk a player stands in (one who just respawned
// there) or holding more than Rules.MaxSaved entities stays loaded and is marked again later.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ChunkSaveSystem
{
    [ForEach(MaxRows = Rules.MaxRequests), With<Unloading>]
    static void Save(Entity chunk, in Chunk at, World world, in Regions regions, ref Stats stats)
    {
        chunk.RemoveUnloading();                                                 // not in the region file
        Span<EntityId> group = stackalloc EntityId[Rules.MaxSaved];
        int size = 0;
        group[size++] = chunk;
        foreach (var entity in world.InChunkIndex.Find(at.Pos))
        {
            if (entity.HasPlayer || size == group.Length) return;               // kept
            group[size++] = entity;
        }
        world.SaveEntities(regions.Files.Buffer, group[..size]);
        regions.Files.Write(at.Pos);
        world.DestroyEntities(group[..size]);
        stats.ChunksUnloaded++;
    }
}
// #endregion unload
