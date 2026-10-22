using EasyEcs;

namespace VoxelServer;

// One block of the world. A chunk holds 16 x 16 x 32 of them (Terrain.cs has the layout).
public enum Block : byte { Air, Grass, Dirt, Stone, Bedrock }

public record struct ChunkPos(int X, int Z);

public record struct BlockPos(int X, int Y, int Z);

// A 16 x 16 column of the world. Its blocks are a DynamicBuffer<Block> on the same entity, tracked like any
// component, so SendChunksSystem sees the chunks that were created or dug. The unique index finds the chunk at a
// position: world.ChunkIndex.TryGet(pos, out chunk), the lookup every tick makes thousands of times (the blocks around
// a body, the chunk an entity is in, the edges of the water). It is direct-indexed: a chunk's slot is its position
// modulo 64, so the chunks loaded around the players, never 64 apart, each have their own slot, and a lookup reads one
// array entry (the hash finds the others). Every chunk is an instance of one prefab (Program.cs), with every component
// and tag below and in Water.cs, so all chunks share one table whatever happens to them.
public record struct Chunk(ChunkPos Pos) : IComponent, IDirectIndexed<ChunkPos>
{
    ChunkPos IIndexed<ChunkPos>.Key => Pos;
    static int IDirectIndexed<ChunkPos>.Slots => 64 * 64;
    static int IDirectIndexed<ChunkPos>.SlotOf(ChunkPos pos) => (pos.Z & 63) * 64 + (pos.X & 63);
}

// A chunk a player needs that is not loaded: AoiViewSystem records it as an entity, one per chunk (the unique index), and
// ChunkReadSystem reads it from the region files or ChunkGenerateSystem generates it, after the pass (a pass may not
// load or create entities, EECS041).
public record struct LoadRequest(ChunkPos Pos) : IComponent, IUniqueIndexed<ChunkPos>
{
    ChunkPos IIndexed<ChunkPos>.Key => Pos;
}

// A tag on a chunk loaded this tick: its clients get it whole, with what is in it (AoiChunkSystem), so neither its blocks
// (SendChunksSystem) nor its entities (a spawn per entity) are sent as changes. ChunkSentSystem removes it.
public struct Fresh : IComponent;

// A tag on a chunk nobody saw for a while: ChunkSaveSystem writes it and what is in it to the region files and
// destroys them, after the pass.
public struct Unloading : IComponent;

// The players that have the chunk in view, and since when none has: who is sent what happens in it, and whether it can
// be unloaded. The AOI systems keep it as the players' views move; ChunkUnloadSystem frees the chunks nobody has seen for a
// while.
public record struct Viewers(int Count, long EmptySince) : IComponent;
