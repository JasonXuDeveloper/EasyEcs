using EasyEcs;

namespace VoxelServer;

// Where a block lives: the chunk of its column and its slot in that chunk's buffer.
public static class Terrain
{
    public const int Size = 16;
    public const int Height = 32;
    public const int BlocksPerChunk = Size * Size * Height;

    // An arithmetic shift rounds down, so block -1 is in chunk -1.
    public static ChunkPos ChunkOf(int x, int z) => new(x >> 4, z >> 4);

    public static int IndexOf(int x, int y, int z) => (y * Size + (z & 15)) * Size + (x & 15);

    // The top solid block of a column, from 10 to 17: random heights every 8 blocks, blended between the four
    // corners in integers, so every machine builds the same world.
    public static int SurfaceHeight(int x, int z)
    {
        int gx = x >> 3, gz = z >> 3, fx = x & 7, fz = z & 7;
        int top = Rules.Hash(gx, gz) * (8 - fx) + Rules.Hash(gx + 1, gz) * fx;
        int bottom = Rules.Hash(gx, gz + 1) * (8 - fx) + Rules.Hash(gx + 1, gz + 1) * fx;
        int noise = (top * (8 - fz) + bottom * fz) / 64;
        return 10 + noise * 8 / 256;
    }

    // Bedrock at the bottom, stone, three blocks of dirt and grass on top.
    public static void Generate(ChunkPos chunk, Span<Block> blocks)
    {
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int top = SurfaceHeight(chunk.X * Size + x, chunk.Z * Size + z);
            for (int y = 0; y < Height; y++)
            {
                blocks[IndexOf(x, y, z)] =
                    y == 0 ? Block.Bedrock :
                    y < top - 3 ? Block.Stone :
                    y < top ? Block.Dirt :
                    y == top ? Block.Grass : Block.Air;
            }
        }
    }

    // The block at a position. A chunk that is not loaded counts as stone, so nothing falls out of the world.
    public static Block BlockAt(World world, int x, int y, int z)
    {
        if (y < 0) return Block.Bedrock;
        if (y >= Height) return Block.Air;
        if (!world.ChunkIndex.TryGet(ChunkOf(x, z), out var chunk)) return Block.Stone;
        return chunk.Block[IndexOf(x, y, z)];
    }

    // Water and air let a body through.
    public static bool IsSolid(Block block) => block is not (Block.Air or Block.Water);

    public static bool IsSolid(World world, float x, float y, float z) => IsSolid(BlockAt(world, Cell(x, y, z)));

    public static Block BlockAt(World world, BlockPos at) => BlockAt(world, at.X, at.Y, at.Z);

    // The block a point is in.
    public static BlockPos Cell(float x, float y, float z) =>
        new((int)MathF.Floor(x), (int)MathF.Floor(y), (int)MathF.Floor(z));

    // Fills an air block with water: the chunk's buffer gets Block.Water (a tracked write, so the chunk is sent again)
    // and the cell becomes an entity that will flow FlowDelay ticks from now.
    public static void PourWater(World world, BlockPos at, BlockPos source, int level, long tick, ref Stats stats)
    {
        if (BlockAt(world, at) != Block.Air) return;   // a chunk that is not loaded reads as stone
        PourWater(world, world.ChunkIndex.Get(ChunkOf(at.X, at.Z)), at, source, level, tick, ref stats);
    }

    public static void PourWater(World world, Entity chunk, BlockPos at, BlockPos source, int level, long tick, ref Stats stats)
    {
        chunk.WriteBlock().AsSpan()[IndexOf(at.X, at.Y, at.Z)] = Block.Water;
        world.CreateEntity(new Water(at, source, level), new Flowing(tick + Rules.FlowDelay), new ChildOf(chunk));
        stats.WaterCells++;
    }
}
