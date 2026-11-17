using EasyEcs;

namespace VoxelServer;

// One value per world.

// Ticks since the server started, and how many make a day (the second half of each is the night).
public record struct Clock(long Tick, int DayLength) : ISingletonComponent;

// The recipes chunks, players, zombies and items are created from.
public record struct Prefabs(Prefab Chunk, Prefab Player, Prefab Zombie, Prefab Item) : ISingletonComponent;

// Where unloaded chunks are kept: the region files of a vanilla server. Host/RegionFiles.cs stands in for the disk.
public record struct Regions(RegionFiles Files) : ISingletonComponent;

// What happened since the server started, for the report. [Accumulate] makes each pass that counts into it (a ref
// Reduce<Stats>: every chunk of rows counting into its own copy, an OpenMP reduction) add its result to the total when
// the pass ends, so no row waits on a lock; [Sum] combines every field by addition.
[Accumulate, Sum]
public partial record struct Stats : ISingletonComponent
{
    public long ChunksGenerated, ChunksLoaded, ChunksUnloaded, ChunksSent, ChunksForgotten;
    public long BlocksDug, ItemsPickedUp, ItemsExpired;
    public long ZombiesSpawned, ZombiesBurned, Hits, PlayerDeaths;
    public long WaterCells, Drifts;
    public long BlockUpdates, EntityUpdates, BytesSent;
    public long CameIntoView, LeftView, Despawns;
}
