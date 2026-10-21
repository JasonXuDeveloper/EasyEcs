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

// A chunk a player needs that is not loaded: InterestSystem records it as an entity, one per chunk (the unique index),
// and ChunkReadSystem reads or generates it after the pass (a pass may not load entities, EECS041).
public record struct LoadRequest(ChunkPos Pos) : IComponent, IUniqueIndexed<ChunkPos>
{
    ChunkPos IIndexed<ChunkPos>.Key => Pos;
}

// A tag on a chunk loaded this tick: its clients get it whole, with what is in it (InterestSystem), so neither its
// blocks (SendChunksSystem) nor its entities (a spawn per entity) are sent as changes. SendChunksSystem removes it.
public struct Fresh : IComponent;

// A tag on a chunk nobody saw for a while: ChunkSaveSystem writes it and what is in it to the region files and
// destroys them, after the pass.
public struct Unloading : IComponent;

// The players that have the chunk in view, and since when none has: who is sent what happens in it, and whether it can
// be unloaded. InterestSystem keeps it as the players' views move; ChunkUnloadSystem frees the chunks nobody has seen
// for a while.
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

// The height map of a chunk, as Minecraft keeps one: a DynamicBuffer<Surface> of 256, the highest solid block of each
// column (x + 16 z). BlockUpdateSystem computes it when the blocks change; whatever asks whether the sky reaches a
// block, or where the ground of a column is, reads one byte instead of scanning the column. Not saved, like the
// current: a loaded chunk computes it again.
public record struct Surface(byte Top);

// The current of a chunk's water: a DynamicBuffer<Current> of two planes in the layout of Fluid, the slope toward x
// for every block, then toward z. WaterFlowSystem writes it with each step, FloatSystem pushes bodies along it.
// Derived from the water, so it is not saved with the chunk: a loaded chunk gets it back with its first step.
public record struct Current(sbyte Value);

// The blocks of water a chunk holds: WaterFlowSystem counts them with each step (one vectorized count), FloatSystem
// skips the dry chunks, and the report sums the column.
public record struct FluidCount(int Blocks) : IComponent;

// A tag on a chunk whose water may still move: WaterFlowSystem visits only these. A chunk loses it when a step changed
// nothing, and gets it back when a block is dug in it or the water of a neighbour changed. Every chunk is created with
// it (its water steps once), so its table has the column: losing it and getting it back flips a presence bit, the
// chunk stays in its table, and a pass that wakes the chunks beside it still visits them.
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

// How far a zombie sees a player from, in blocks: it hunts the players within it. (A player's view is not a component:
// every player sees Rules.PlayerVision around its ViewCenter.)
public record struct Vision(float Range) : IComponent;

// The chunk an entity that moves is in: players, zombies and items. An index of them by chunk
// (world.InChunkIndex.Find(chunk)), the grid through which players and zombies see who is near. GridSystem keeps it
// current and writes it only when the entity crosses into another chunk; ITracked, so InterestSystem visits only the
// entities that crossed.
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

// The chunk the clients see an entity in (Placed once it has one): what InterestSystem compares a crossing with. A
// cleanup component: a destroyed entity keeps it, Retired, until InterestSystem has despawned it for that chunk's
// viewers.
public record struct Visible(ChunkPos Chunk, bool Placed) : ICleanupComponent;

// The chunk a player's view is centered on: its client has the loaded chunks of the square of Rules.PlayerVision around
// it, so the view is this one value, not a list of chunks. InterestSystem moves it when the player crosses into another
// chunk, after sending what the step brings into view and forgetting what it takes out. An index of the players by it
// (world.ViewCenterIndex) counts the players of a chunk in O(1): who sees a chunk, who a zombie can hunt, who picks up
// an item. A cleanup component: a player who leaves keeps it, Retired, until InterestSystem has released its view.
public record struct ViewCenter(ChunkPos Chunk) : ICleanupComponent, IIndexed<ChunkPos>
{
    ChunkPos IIndexed<ChunkPos>.Key => Chunk;
}

// What the client asked for this tick: a direction to walk in (x, z) and a dig of the block underfoot.
public record struct Input(Vector2 Walk, bool Dig) : IComponent;

// One slot of a player's inventory: a DynamicBuffer<ItemStack> on the player.
public record struct ItemStack(Block Kind, int Count);

// ---- Zombies and dropped items ----

public struct Zombie : IComponent;

// The player a zombie chases, the nearest it saw (none: default). An EntityId, so a player who left is noticed
// (TryGetEntity fails).
public record struct Target(EntityId Player) : IComponent;

// The tick from which a zombie may hit again. A time, not a count of ticks left: nothing has to count it down.
public record struct Cooldown(long Until) : IComponent;

// A zombie in daylight is on fire until this tick, as in Minecraft: the fire hurts every Rules.BurnEvery ticks and
// goes on burning for a while after the zombie found shade. A time too: the sun moves it on, nothing counts it down.
public record struct Burning(long Until) : IComponent;

// A stack of dug blocks lying on the ground, for any player who walks over it.
public record struct Item(Block Kind, int Count) : IComponent;

// The tick an item was dropped: it can be picked up Rules.PickupDelay ticks later and is gone Rules.ItemLifetime ticks
// later. An ordered index of the items by it (world.DroppedIndex.Range) finds the items that expire without visiting
// the others: the oldest come first.
public record struct Dropped(long Tick) : IComponent, IIndexed<long>
{
    long IIndexed<long>.Key => Tick;
}

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
