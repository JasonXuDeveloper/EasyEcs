using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// ---- Blocks and chunks ----

// One block of the world. A chunk holds 16 x 16 x 32 of them (Terrain.cs has the layout).
public enum Block : byte { Air, Grass, Dirt, Stone, Bedrock }

public record struct ChunkPos(int X, int Z);

public record struct BlockPos(int X, int Y, int Z);

// A 16 x 16 column of the world. Its blocks are a DynamicBuffer<Block> on the same entity, tracked like any
// component, so SendChunksSystem sees the chunks that were created or dug. The unique index finds the chunk at a
// position: world.ChunkIndex.TryGet(pos, out chunk).
public record struct Chunk(ChunkPos Pos) : IComponent, IUniqueIndexed<ChunkPos>
{
    ChunkPos IIndexed<ChunkPos>.Key => Pos;
}

// A chunk a player needs that is not loaded: ChunkLoadSystem records it as an entity, one per chunk (the unique index),
// and ChunkReadSystem reads or generates it after the pass (a pass may not load entities, EECS041).
public record struct LoadRequest(ChunkPos Pos) : IComponent, IUniqueIndexed<ChunkPos>
{
    ChunkPos IIndexed<ChunkPos>.Key => Pos;
}

// A tag on a chunk loaded this tick: its clients get it whole (ChunkViewSystem), so SendChunksSystem does not send its
// blocks as changes. SendChunksSystem removes it.
public struct Fresh : IComponent;

// A tag on a chunk nobody saw for a while: ChunkSaveSystem writes it and what is in it to the region files and
// destroys them, after the pass.
public struct Unloading : IComponent;

// The players that have the chunk in view, and since when none has: who is sent what happens in it, and whether it can
// be unloaded. ChunkViewSystem keeps it as the players' views change; ChunkUnloadSystem frees the chunks nobody has
// seen for a while.
public record struct Viewers(int Count, long EmptySince) : IComponent;

// ---- Water ----

// The water in one block of a chunk: a DynamicBuffer<Fluid> beside the blocks, one byte per block in the same layout,
// so a chunk's water is a grid that WaterFlowSystem steps 16 blocks at a time. Level 0 is no water, Spring (9) a
// spring that never dries, Falling (8) water with water above it, and 7 down to 1 water running sideways, one less
// per block from where it came, as in Minecraft (which counts the other way: 0 for a source, 7 at the edge).
public record struct Fluid(byte Level)
{
    public const byte Spring = 9, Falling = 8;
}

// The water of a chunk after the next step, in the layout of Fluid: WaterFlowSystem writes it, WaterCommitSystem copies
// it into the Fluid buffer once every chunk has stepped, so each chunk steps from its neighbours' water before the
// step, as a GPU ping-pongs two buffers. Empty between two steps.
public record struct NextFluid(byte Level);

// A tag on a chunk whose water may still move: WaterFlowSystem visits only these. A chunk loses it when a step changed
// nothing, and gets it back when a block is dug in it or the water of a neighbour changed.
public struct Flowing : IComponent;

// ---- Everything that moves: players, zombies, dropped items ----

// The feet of a player or a zombie, the bottom of an item. ITracked: replication sends only what moved.
public record struct Position(Vector3 Value) : IComponent, ITracked;

// Blocks per tick.
public record struct Velocity(Vector3 Value) : IComponent;

// What PhysicsSystem found: standing on a block, walking into a wall higher than a step.
public record struct Body(bool OnGround, bool Blocked) : IComponent;

// ITracked: the systems that handle a death visit only the healths written ([Changed<Health>]).
public record struct Health(int Value) : IComponent, ITracked;

// How far a player or a zombie sees, in blocks: each prefab sets its own. A player's view is the square of chunks it
// reaches: the chunks loaded around it and sent to its client, with the entities in them. A zombie hunts the players
// within it.
public record struct Vision(float Range) : IComponent;

// The chunk an entity that moves is in: players, zombies and items. An index of them by chunk
// (world.InChunkIndex.Find(chunk)), the grid through which players and zombies see who is near. GridSystem keeps it
// current and writes it only when the entity crosses into another chunk; ITracked, so ChunkViewSystem visits only the
// players that crossed.
public record struct InChunk(ChunkPos Pos) : IComponent, IIndexed<ChunkPos>, ITracked
{
    ChunkPos IIndexed<ChunkPos>.Key => Pos;
}

// ---- Players ----

public record struct Player(int Client) : IComponent;

// The blocks a player dug: the scoreboard. An ordered index (world.ScoreIndex.Descending()) walks the players from
// the best digger down, kept sorted as the scores change, so the top of the board is never a sort.
public record struct Score(int BlocksDug) : IComponent, IIndexed<int>
{
    int IIndexed<int>.Key => BlocksDug;
}

// The chunk the clients see an entity in (Placed once it has one): what SightSystem compares a crossing with. A
// cleanup component: a destroyed entity keeps it, Retired, until SightSystem has despawned it for that chunk's
// viewers.
public record struct Visible(ChunkPos Chunk, bool Placed) : ICleanupComponent;

// One chunk a player's client has: a DynamicBuffer<InView> on the player, the loaded chunks of its view. A chunk that
// comes into view is sent whole; one that leaves it is forgotten by the client.
public record struct InView(ChunkPos Chunk);

// What the client asked for this tick: a direction to walk in (x, z) and a dig of the block underfoot.
public record struct Input(Vector2 Walk, bool Dig) : IComponent;

// One slot of a player's inventory: a DynamicBuffer<ItemStack> on the player.
public record struct ItemStack(Block Kind, int Count);

// ---- Zombies and dropped items ----

public struct Zombie : IComponent;

// The player a zombie chases, the nearest it saw (none: default). An EntityId, so a player who left is noticed
// (TryGetEntity fails).
public record struct Target(EntityId Player) : IComponent;

// Ticks until a zombie may hit again.
public record struct Cooldown(int Ticks) : IComponent;

// A zombie in daylight is on fire for this many more ticks, as in Minecraft: the fire hurts every Rules.BurnEvery
// ticks and goes on burning for a while after the zombie found shade.
public record struct Burning(int Ticks) : IComponent;

// A stack of dug blocks lying on the ground, for any player who walks over it.
public record struct Item(Block Kind, int Count) : IComponent;

// Ticks since an item was dropped: it can be picked up after Rules.PickupDelay and is gone after Rules.ItemLifetime.
public record struct Age(int Ticks) : IComponent;

// ---- Singletons: one value per world ----

// Ticks since the server started, and how many make a day (the second half of each is the night).
public record struct Clock(long Tick, int DayLength) : ISingletonComponent;

// The recipes players, zombies and items are created from.
public record struct Prefabs(Prefab Player, Prefab Zombie, Prefab Item) : ISingletonComponent;

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
