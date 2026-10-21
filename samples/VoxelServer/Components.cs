using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// ---- Blocks and chunks ----

// One block of the world. A chunk holds 16 x 16 x 32 of them (Terrain.cs has the layout).
public enum Block : byte { Air, Grass, Dirt, Stone, Bedrock, Water }

public record struct ChunkPos(int X, int Z);

public record struct BlockPos(int X, int Y, int Z);

// A 16 x 16 column of the world. Its blocks are a DynamicBuffer<Block> on the same entity, tracked like any
// component, so SendChunksSystem sees the chunks that were created or dug. The unique index finds the chunk at a
// position: world.ChunkIndex.TryGet(pos, out chunk).
public record struct Chunk(ChunkPos Pos) : IComponent, IUniqueIndexed<ChunkPos>
{
    ChunkPos IIndexed<ChunkPos>.Key => Pos;
}

// The last tick a player had the chunk in view. ChunkUnloadSystem frees the chunks nobody has seen for a while.
public record struct LastSeen(long Tick) : IComponent;

// ---- Water ----

// A block of water: an entity beside the Block.Water in its chunk's buffer, for what the buffer cannot hold. Level is
// how far the water ran from its spring (0 at the spring, Rules.WaterReach at the edge); Source is that spring. The
// unique index finds the water at a block (world.WaterIndex). Each cell is a child of its chunk (ChildOf), so
// unloading a chunk takes its water with it.
public record struct Water(BlockPos Pos, BlockPos Source, int Level) : IComponent, IUniqueIndexed<BlockPos>
{
    BlockPos IIndexed<BlockPos>.Key => Pos;
}

// Water that will spread at tick At. WaterFlowSystem removes it once the cell has spread: settled water costs nothing.
// Digging next to water adds it back (a block update).
public record struct Flowing(long At) : IComponent;

// ---- Everything that moves: players, zombies, dropped items ----

// The feet of a player or a zombie, the center of an item. ITracked: replication sends only what moved.
public record struct Position(Vector3 Value) : IComponent, ITracked;

// Blocks per tick.
public record struct Velocity(Vector3 Value) : IComponent;

// What PhysicsSystem found: standing on a block, walking into a wall higher than a step.
public record struct Body(bool OnGround, bool Blocked) : IComponent;

public record struct Health(int Value) : IComponent;

// A tag: players and zombies. Humanoids float in water and drift with it; dropped items sink.
public struct Humanoid : IComponent;

// ---- Players ----

public record struct Player(int Client) : IComponent;

// What the client asked for this tick: a direction to walk in (x, z) and a dig of the block underfoot.
public record struct Input(Vector2 Walk, bool Dig) : IComponent;

// One slot of a player's inventory: a DynamicBuffer<ItemStack> on the player.
public record struct ItemStack(Block Kind, int Count);

// ---- Zombies and dropped items ----

public struct Zombie : IComponent;

// The player a zombie chases. An EntityId, so a player who left is noticed (TryGetEntity fails).
public record struct Target(EntityId Player) : IComponent;

// Ticks until a zombie may hit again.
public record struct Cooldown(int Ticks) : IComponent;

// A dug block lying on the ground, waiting for the player who dug it.
public record struct Item(Block Kind, EntityId Owner) : IComponent;

// Ticks until an item nobody picked up disappears.
public record struct Lifetime(int Ticks) : IComponent;

// ---- Singletons: one value per world ----

// Ticks since the server started, and how many make a day (the second half of each is the night).
public record struct Clock(long Tick, int DayLength) : ISingletonComponent;

// The recipes players, zombies and items are created from.
public record struct Prefabs(Prefab Player, Prefab Zombie, Prefab Item) : ISingletonComponent;

// What happened, for the report.
public record struct Stats(
    long ChunksLoaded,
    long ChunksUnloaded,
    long BlocksDug,
    long ItemsPickedUp,
    long ItemsExpired,
    long ZombiesSpawned,
    long ZombiesBurned,
    long Hits,
    long PlayerDeaths,
    long WaterCells,
    long Drifts,
    long ChunkUpdates,
    long PositionUpdates,
    long Despawns) : ISingletonComponent;
