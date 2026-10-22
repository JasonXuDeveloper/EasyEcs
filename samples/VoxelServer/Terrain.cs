using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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

    // Bedrock at the bottom, stone, three blocks of dirt and grass on top: the tops of the 256 columns first, then
    // each layer 16 columns at a time, the block of each lane chosen by comparing the layer with its column's top.
    public static void Generate(ChunkPos chunk, Span<Block> blocks)
    {
        Span<byte> top = stackalloc byte[Size * Size];
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
            top[z * Size + x] = (byte)SurfaceHeight(chunk.X * Size + x, chunk.Z * Size + z);

        var cells = MemoryMarshal.Cast<Block, byte>(blocks);
        cells[..(Size * Size)].Fill((byte)VoxelServer.Block.Bedrock);
        for (int y = 1; y < Height; y++)
        {
            var layer = Vector128.Create((byte)y);
            var below = Vector128.Create((byte)(y + 3));                        // y < top - 3: stone
            for (int i = 0; i < Size * Size; i += Size)
            {
                var tops = Vector128.Create((ReadOnlySpan<byte>)top.Slice(i, Size));
                var block = Vector128.ConditionalSelect(Vector128.LessThan(below, tops), Vector128.Create((byte)VoxelServer.Block.Stone),
                    Vector128.ConditionalSelect(Vector128.LessThan(layer, tops), Vector128.Create((byte)VoxelServer.Block.Dirt),
                        Vector128.Equals(layer, tops) & Vector128.Create((byte)VoxelServer.Block.Grass)));   // above: air (0)
                block.CopyTo(cells.Slice(y * Size * Size + i, Size));
            }
        }
    }

    // Only air lets a body through; water is not a block but the chunk's Fluid grid beside the blocks.
    public static bool IsSolid(Block block) => block != Block.Air;

    // The block a point is in.
    public static BlockPos Cell(float x, float y, float z) =>
        new((int)MathF.Floor(x), (int)MathF.Floor(y), (int)MathF.Floor(z));

    public static BlockPos Cell(Vector3 p) => Cell(p.X, p.Y, p.Z);

    // The chunk a point is in.
    public static ChunkPos ChunkOf(Vector3 p) => ChunkOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z));

    // Whether a view of `range` blocks from the chunk `center` reaches the chunk `chunk`: the square of ChunksAround.
    public static bool InReach(ChunkPos center, ChunkPos chunk, float range)
    {
        int reach = Rules.ChunksIn(range);
        return Math.Abs(chunk.X - center.X) <= reach && Math.Abs(chunk.Z - center.Z) <= reach;
    }

    // The chunks a view of `range` blocks reaches around `center`: foreach (var chunk in Terrain.ChunksAround(...)).
    public static ChunkSquare ChunksAround(ChunkPos center, float range) => new(center, Rules.ChunksIn(range));

    // The chunks a view of `range` blocks around `center` reaches and one around `other` does not.
    public static ChunkStrip Gained(ChunkPos center, ChunkPos other, float range) => new(center, other, true, range);


    // A chunk's Border: for each side (north, south, west, east) and layer, the 16 water levels of the neighbour's
    // facing edge, then (blocks) its 16 blocks.
    public const int BorderSize = 2 * 4 * Height * Size;

    public static int BorderOf(int side, int y, bool blocks) => ((blocks ? 4 : 0) + side) * Height * Size + y * Size;

    // The four sides of a chunk, one bit each, and the chunk beside it on each side.
    public const byte North = 1, South = 2, West = 4, East = 8, AllSides = North | South | West | East;

    // The water of the chunks beside `pos` on `sides` may move again: the water or a block at that edge changed.
    public static void Wake(World world, ChunkPos pos, byte sides)
    {
        ReadOnlySpan<(byte Side, int Dx, int Dz)> around = [(North, 0, -1), (South, 0, 1), (West, -1, 0), (East, 1, 0)];
        foreach (var (side, dx, dz) in around)
            if ((sides & side) != 0 && world.ChunkIndex.TryGet(new ChunkPos(pos.X + dx, pos.Z + dz), out var chunk))
                chunk.SetFlowing();
    }

    // The blocks of water in the world, for the report.
    public static int WaterIn(World world)
    {
        int count = 0;
        foreach (var chunk in world.Read<FluidCount>()) count += chunk.FluidCount.Blocks;
        return count;
    }
}

// The blocks and the water around one body. The chunk of the first block read is looked up in the chunk index once,
// and the reads after it in the same chunk, most of them (a body is a block wide, a chunk sixteen), read its buffers
// straight; a read across the edge looks up that chunk instead. Each buffer is fetched on its first read: a body out of
// the water never fetches the blocks for FloatSystem. A ref struct, on the stack of one row: the spans stay valid until
// the next structural change, and the systems that read through it make none before they are done with it.
public ref struct Nearby(World world)
{
    ChunkPos _pos;
    bool _looked;
    Entity _chunk;                     // default when the chunk is not loaded
    ReadOnlySpan<Block> _blocks;       // empty until read: a loaded chunk's buffers never are
    ReadOnlySpan<Fluid> _water;
    ReadOnlySpan<Surface> _surface;

    // The block at a position. A chunk that is not loaded counts as stone, so nothing falls out of the world.
    public Block Block(int x, int y, int z)
    {
        if (y < 0) return VoxelServer.Block.Bedrock;
        if (y >= Terrain.Height) return VoxelServer.Block.Air;
        return Look(x, z) ? Blocks()[Terrain.IndexOf(x, y, z)] : VoxelServer.Block.Stone;
    }

    public Block Block(BlockPos at) => Block(at.X, at.Y, at.Z);

    public bool IsSolid(float x, float y, float z) => Terrain.IsSolid(Block(Terrain.Cell(x, y, z)));

    // The water level at a block, 0 where there is none or the chunk is not loaded.
    public int Water(BlockPos at)
    {
        if (at.Y < 0 || at.Y >= Terrain.Height || !Look(at.X, at.Z)) return 0;
        if (_water.IsEmpty) _water = _chunk.Fluid.AsReadOnlySpan();
        return _water[Terrain.IndexOf(at.X, at.Y, at.Z)].Level;
    }

    // The highest solid block of a column, from the chunk's height map; -1 if its chunk is not loaded.
    public int Top(int x, int z)
    {
        if (!Look(x, z)) return -1;
        if (_surface.IsEmpty) _surface = _chunk.Surface.AsReadOnlySpan();
        return _surface[(z & 15) * Terrain.Size + (x & 15)].Top;
    }

    // Whether nothing solid is above a block: the sky reaches it.
    public bool UnderSky(BlockPos at) => Top(at.X, at.Z) < at.Y;

    ReadOnlySpan<Block> Blocks()
    {
        if (_blocks.IsEmpty) _blocks = _chunk.Block.AsReadOnlySpan();
        return _blocks;
    }

    // Whether the chunk of a column is loaded, looked up only when it is not the chunk of the last read.
    bool Look(int x, int z)
    {
        var pos = Terrain.ChunkOf(x, z);
        if (_looked && pos == _pos) return _chunk != default;
        _looked = true;
        _pos = pos;
        _blocks = default;
        _water = default;
        _surface = default;
        if (!world.ChunkIndex.TryGet(pos, out _chunk)) _chunk = default;
        return _chunk != default;
    }
}

// The chunks of the square around `center` that the square around `other` does not hold (all of them without an
// other): what a view gains when it moves from `other` to `center`, and, the other way round, what it loses. A step
// into the next chunk is a strip of one row of the square; a jump (a respawn) is the whole square.
public ref struct ChunkStrip(ChunkPos center, ChunkPos other, bool hasOther, float range)
{
    ChunkSquare _square = Terrain.ChunksAround(center, range);

    public readonly ChunkStrip GetEnumerator() => this;

    public readonly ChunkPos Current => _square.Current;

    public bool MoveNext()
    {
        while (_square.MoveNext())
            if (!hasOther || !Terrain.InReach(other, _square.Current, range)) return true;
        return false;
    }
}

// The chunks at a ring distance of `ring` from `center` (its own chunk alone for ring 0): the square of side
// 2 ring + 1 without the square inside it. A walk ring by ring goes from near to far.
public ref struct ChunkRing(ChunkPos center, int ring)
{
    ChunkSquare _square = new(center, ring);

    public readonly ChunkRing GetEnumerator() => this;

    public readonly ChunkPos Current => _square.Current;

    public bool MoveNext()
    {
        while (_square.MoveNext())
            if (Math.Max(Math.Abs(_square.Current.X - center.X), Math.Abs(_square.Current.Z - center.Z)) == ring) return true;
        return false;
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
