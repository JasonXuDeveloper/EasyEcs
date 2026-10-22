using EasyEcs;

namespace VoxelServer;

// One value per world.

// Ticks since the server started, and how many make a day (the second half of each is the night).
public record struct Clock(long Tick, int DayLength) : ISingletonComponent;

// The recipes chunks, players, zombies and items are created from.
public record struct Prefabs(Prefab Chunk, Prefab Player, Prefab Zombie, Prefab Item) : ISingletonComponent;

// Where unloaded chunks are kept: the region files of a vanilla server. Host/RegionFiles.cs stands in for the disk.
public record struct Regions(RegionFiles Files) : ISingletonComponent;

// What happened, for the report.
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
