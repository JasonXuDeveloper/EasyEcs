using System.Numerics;
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

    // Only air lets a body through; water is not a block but the chunk's Fluid grid beside the blocks.
    public static bool IsSolid(Block block) => block != Block.Air;

    public static bool IsSolid(World world, float x, float y, float z) => IsSolid(BlockAt(world, Cell(x, y, z)));

    public static Block BlockAt(World world, BlockPos at) => BlockAt(world, at.X, at.Y, at.Z);

    // The block a point is in.
    public static BlockPos Cell(float x, float y, float z) =>
        new((int)MathF.Floor(x), (int)MathF.Floor(y), (int)MathF.Floor(z));

    public static BlockPos Cell(Vector3 p) => Cell(p.X, p.Y, p.Z);

    // The chunk a point is in.
    public static ChunkPos ChunkOf(Vector3 p) => ChunkOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z));

    // The chunks a view of `range` blocks reaches around `center`: foreach (var chunk in Terrain.ChunksAround(...)).
    public static ChunkSquare ChunksAround(ChunkPos center, float range) => new(center, Rules.ChunksIn(range));

    // The highest solid block of a column, -1 if its chunk is not loaded.
    public static int TopAt(World world, int x, int z)
    {
        if (!world.ChunkIndex.TryGet(ChunkOf(x, z), out var chunk)) return -1;
        var blocks = chunk.Block.AsReadOnlySpan();
        int y = Height - 1;
        while (y > 0 && blocks[IndexOf(x, y, z)] == Block.Air) y--;
        return y;
    }

    // Whether nothing solid is above a block: the sky reaches it.
    public static bool UnderSky(World world, BlockPos at) => TopAt(world, at.X, at.Z) < at.Y;

    // The water level at a block, 0 where there is none or the chunk is not loaded.
    public static int WaterAt(World world, BlockPos at)
    {
        if (at.Y < 0 || at.Y >= Height || !world.ChunkIndex.TryGet(ChunkOf(at.X, at.Z), out var chunk)) return 0;
        return chunk.Fluid[IndexOf(at.X, at.Y, at.Z)].Level;
    }

    // The water of a chunk and of its four neighbours may move again: a block changed at `pos`, or water beside it.
    public static void Wake(World world, ChunkPos pos)
    {
        foreach (var near in ChunksAround(pos, Size))
            if (near.X == pos.X || near.Z == pos.Z)
                if (world.ChunkIndex.TryGet(near, out var chunk)) chunk.Set(new Flowing());
    }

    // The blocks of water in the world, for the report.
    public static int WaterIn(World world)
    {
        int count = 0;
        foreach (var chunk in world.Read<DynamicBuffer<Fluid>>())
        {
            var levels = System.Runtime.InteropServices.MemoryMarshal.Cast<Fluid, byte>(chunk.Component1.AsReadOnlySpan());
            count += levels.Length - levels.Count((byte)0);   // Count is vectorized
        }
        return count;
    }
}

// The chunks of a square around a center, walked row by row with no allocation.
public ref struct ChunkSquare(ChunkPos center, int reach)
{
    int _dx = -reach - 1, _dz = -reach;

    public readonly ChunkSquare GetEnumerator() => this;

    public readonly ChunkPos Current => new(center.X + _dx, center.Z + _dz);

    public bool MoveNext()
    {
        if (++_dx <= reach) return true;
        _dx = -reach;
        return ++_dz <= reach;
    }
}
