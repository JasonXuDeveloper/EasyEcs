using EasyEcs;

namespace VoxelServer;

// One value per world.

// Ticks since the server started, and how many make a day (the second half of each is the night).
public record struct Clock(long Tick, int DayLength) : ISingletonComponent;

// The recipes chunks, players, zombies and items are created from.
public record struct Prefabs(Prefab Chunk, Prefab Player, Prefab Zombie, Prefab Item) : ISingletonComponent;

// Where unloaded chunks are kept: the region files of a vanilla server. Host/RegionFiles.cs stands in for the disk.
public record struct Regions(RegionFiles Files) : ISingletonComponent;

// What happened since the server started, for the report. The systems on one thread add to it; a system on several
// threads counts into a reduction of its own (below), which TallySystem adds to it at the end of the tick.
public record struct Stats(
    long ChunksGenerated,
    long ChunksLoaded,
    long ChunksUnloaded,
    long ChunksSent,
    long ChunksForgotten,
    long BlocksDug,
    long ItemsPickedUp,
    long ItemsExpired,
    long ZombiesSpawned,
    long ZombiesBurned,
    long Hits,
    long PlayerDeaths,
    long WaterCells,
    long Drifts,
    long BlockUpdates,
    long EntityUpdates,
    long BytesSent,
    long CameIntoView,
    long LeftView,
    long Despawns) : ISingletonComponent;

// What this tick's passes on several threads counted: each a Reduce<T> of its system, every chunk of rows counting into
// its own copy, combined when the pass ends (an OpenMP reduction), so no row waits on a lock. A pass replaces its
// result, it does not add to it: TallySystem adds the tick's results to Stats.

// The bodies water carried this tick (FloatSystem).
public partial struct Carried : ISingletonComponent
{
    [Sum] public long Bodies;
}

// The blocks water filled this tick (WaterCommitSystem).
public partial struct Filled : ISingletonComponent
{
    [Sum] public long Blocks;
}
