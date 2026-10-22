using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// The water steps every FlowDelay ticks, the way Minecraft spreads it: an open block under water fills as falling water
// (8); an open block beside water that stands on something solid fills one level below it (a spring or falling water
// gives 7, then 6 ... 1, so water runs 7 blocks on flat ground); everything else empties. The same rule, applied again
// each step, makes water recede when its spring or the water above it is gone, and fill a pit dug beside a lake.
// (Simplified: no new sources between two springs, and water spreads every way rather than toward the nearest drop.)
//
// It runs as a GPU runs a shader, in passes that hand data to each other, on the chunks that may still move (Flowing):
// WaterFlowSystem shades every block into NextFluid, in parallel; WaterSleepSystem lets the chunks whose step moved
// nothing sleep; WaterCommitSystem makes the step the water once every chunk has stepped (each chunk steps from its
// neighbours' water before the step, as a GPU ping-pongs two buffers) and wakes the chunks beside the edges that changed.
//
// The shader. Shade computes one block's next level from its own and its six neighbours', nothing else, and runs on
// 16 blocks at once (a row of the chunk, one Vector128<byte>). Each chunk first loads its tile and the halo around it
// into a padded texture on the stack, a ring of one block around each layer, as a CUDA block loads its tile and halo
// into shared memory: its own layers from its buffers, the ring from the facing row (north, south) or column (west,
// east) of each neighbour, read through lookups (Lookup<T>: a neighbour's components as they were before the pass, so
// the method stays parallel, and only NextFluid is written). The loop then has no edge cases. Byte lanes are integer
// arithmetic: the same water on every machine.
//
// The stencil, as a GPU's early stencil test skips the fragments a mask rules out: while padding, a bit per row of the
// texture records whether it holds water (with the halo block at each end); the rows a step can wet are those whose
// stencil (the row, the row above, the rows north and south) has a bit set, and the loop visits only them, the next one
// found by counting trailing zeros. The others are not written at all: the step's buffer equals the water after every
// commit (a moved step is copied into it, a settled one wrote back what was there), so a row whose stencil is dry is
// already zero in it.
[UpdateInGroup<Simulation>, UpdateInterval(Rules.FlowDelay), System.Runtime.CompilerServices.SkipLocalsInit]
public class WaterFlowSystem : EcsSystem
{
    const int Size = Terrain.Size, Blocks = Terrain.BlocksPerChunk;
    const int Row = Size + 2, Layer = Row * Row;      // the padded texture: a ring of one block around each layer

    // A few hundred chunks of 16 x 16 x 32 blocks: far under the 4,096 rows of a default chunk of a pass that writes a
    // tracked component, so the threads take 64 chunks at a time (OpenMP's schedule(dynamic, 64)) instead of one taking all.
    [ForEach(Parallel = true, ChunkRows = 64), With<Flowing>]
    static void Flow(in Chunk key, in FluidCount count, in DynamicBuffer<Block> blocks, in DynamicBuffer<Fluid> fluid,
        Lookup<Chunk> chunks, Lookup<FluidCount> counts, Lookup<DynamicBuffer<Fluid>> waters,
        Lookup<DynamicBuffer<Block>> grounds, Mut<DynamicBuffer<NextFluid>> step, ref FluidStep summary)
    {
        // The neighbours (north, south, west, east; default when not loaded) and the layers to shade: those with water
        // here and beside (FluidCount, no search), and one below, where water falls.
        int low = count.Low, high = count.High;
        Span<EntityId> around = stackalloc EntityId[4];
        for (int side = 0; side < 4; side++)
        {
            if (!chunks.TryFind(Beside(key.Pos, side), out around[side])) continue;
            ref readonly var beside = ref counts.Read(around[side]);
            if (beside.Low > beside.High) continue;
            (low, high) = (Math.Min(low, beside.Low), Math.Max(high, beside.High));
        }
        var reach = new Reach(Math.Max(low - 1, 1), high);
        if (reach.From > reach.To)
        {
            summary = new FluidStep(StepResult.Settled, default, 0, 0); // no water here or beside: nothing moves
            return;
        }
        // The texture is not cleared, as a fragment shader's target is not: Pad writes every texel the loop samples,
        // and only the layer above the world's top, which no chunk holds, is set to no water and no block.
        int layers = reach.To + 2 - reach.Bottom;
        Span<byte> water = stackalloc byte[layers * Layer];
        Span<byte> ground = stackalloc byte[layers * Layer];
        Span<uint> wetRows = stackalloc uint[layers];                  // bit z + 1: row z of the layer holds water
        if (reach.Top <= reach.To)
        {
            water.Slice((layers - 1) * Layer).Clear();
            ground.Slice((layers - 1) * Layer).Clear();
            wetRows[layers - 1] = 0;
        }
        Pad(new Tile(fluid, blocks), Tile.Of(around[0], waters, grounds), Tile.Of(around[1], waters, grounds),
            Tile.Of(around[2], waters, grounds), Tile.Of(around[3], waters, grounds), water, ground, wetRows, reach);

        // The rows of the layers in reach that the stencil keeps, 16 blocks at a time, written straight into the step.
        // The layers outside hold no water before the step or after it, so the buffer keeps its count between steps.
        var next = MemoryMarshal.Cast<NextFluid, byte>(step.Write().SetCount(Blocks));
        int filled = 0, wet = 0, first = Terrain.Height, last = 0;
        uint edges = 0;
        bool changed = false;
        for (int y = reach.From; y <= reach.To; y++)
        {
            int layer = y - reach.Bottom;
            uint near = wetRows[layer];
            // Row z is shaded when row z (bit z + 1), the row above it, row z - 1 (bit z) or row z + 1 (bit z + 2) is wet.
            for (uint rows = ((near | wetRows[layer + 1]) >> 1 | near | near >> 2) & 0xFFFF; rows != 0; rows &= rows - 1)
            {
            int z = BitOperations.TrailingZeroCount(rows);
            int p = Texel(layer, z);
            var here = Sample(water, p);
            var row = next.Slice(Terrain.IndexOf(0, y, z), Size);
            var level = Shade(here, Sample(water, p + Layer),
                Sample(water, p - 1), Sample(ground, p - 1 - Layer), Sample(water, p + 1), Sample(ground, p + 1 - Layer),
                Sample(water, p - Row), Sample(ground, p - Row - Layer), Sample(water, p + Row), Sample(ground, p + Row - Layer),
                Sample(ground, p));
            level.CopyTo(row);

            uint lanes = (~Vector128.Equals(level, Vector128<byte>.Zero)).ExtractMostSignificantBits();
            wet += BitOperations.PopCount(lanes);
            if (lanes != 0) (first, last) = (Math.Min(first, y), y);
            filled += BitOperations.PopCount(lanes & Vector128.Equals(here, Vector128<byte>.Zero).ExtractMostSignificantBits());
            uint moved = (~Vector128.Equals(level, here)).ExtractMostSignificantBits();
            if (moved == 0) continue;
            changed = true;
            if (z == 0) edges |= Terrain.North;
            if (z == Size - 1) edges |= Terrain.South;
            if ((moved & 1) != 0) edges |= Terrain.West;
            if ((moved & 1u << (Size - 1)) != 0) edges |= Terrain.East;
            }
        }
        summary = new FluidStep(changed ? StepResult.Moved : StepResult.Settled, new FluidCount(wet, (byte)first, (byte)last),
            filled, (byte)edges);
    }

    // The chunk beside `pos` on side 0 to 3: north, south, west, east (Terrain.North ... in that order).
    static ChunkPos Beside(ChunkPos pos, int side) => side switch
    {
        0 => new(pos.X, pos.Z - 1),
        1 => new(pos.X, pos.Z + 1),
        2 => new(pos.X - 1, pos.Z),
        _ => new(pos.X + 1, pos.Z),
    };

    // The layers the step shades, From to To (From > To when there is none); it samples one more layer each way,
    // Bottom to Top.
    readonly record struct Reach(int From, int To)
    {
        public int Bottom => From - 1;
        public int Top => Math.Min(To + 1, Terrain.Height - 1);
    }

    // A chunk's water and blocks as bytes, in the layout of its buffers; empty for a chunk that is not loaded.
    readonly ref struct Tile(ReadOnlySpan<byte> water, ReadOnlySpan<byte> blocks)
    {
        public readonly ReadOnlySpan<byte> Water = water, Blocks = blocks;

        public Tile(in DynamicBuffer<Fluid> water, in DynamicBuffer<Block> blocks)
            : this(MemoryMarshal.Cast<Fluid, byte>(water.AsReadOnlySpan()), MemoryMarshal.Cast<Block, byte>(blocks.AsReadOnlySpan())) { }

        public static Tile Of(EntityId chunk, Lookup<DynamicBuffer<Fluid>> waters, Lookup<DynamicBuffer<Block>> grounds) =>
            chunk == default ? default : new Tile(in waters.Read(chunk), in grounds.Read(chunk));
    }

    // One block's next level from its own, the one above and the four beside, with the block under each of those
    // (water stands on it) and its own block: 16 blocks at once. A spring stays; an open block under water fills as
    // falling water; an open block beside water standing on something gets one level less than that water; anything
    // else empties.
    static Vector128<byte> Shade(Vector128<byte> here, Vector128<byte> above,
        Vector128<byte> west, Vector128<byte> underWest, Vector128<byte> east, Vector128<byte> underEast,
        Vector128<byte> north, Vector128<byte> underNorth, Vector128<byte> south, Vector128<byte> underSouth,
        Vector128<byte> block)
    {
        var side = Vector128.Max(Vector128.Max(Gives(west, underWest), Gives(east, underEast)),
            Vector128.Max(Gives(north, underNorth), Gives(south, underSouth)));
        var falling = ~Vector128.Equals(above, Vector128<byte>.Zero) & Vector128.Create(Fluid.Falling);
        var open = Vector128.Equals(block, Vector128<byte>.Zero);
        var spring = Vector128.Equals(here, Vector128.Create(Fluid.Spring));
        return Vector128.ConditionalSelect(spring, here, Vector128.Max(side, falling) & open);
    }

    // What water hands to the blocks beside it: one level less than its own (a spring or falling water counts as 8),
    // if it stands on something solid.
    static Vector128<byte> Gives(Vector128<byte> level, Vector128<byte> under)
    {
        var standing = ~Vector128.Equals(under, Vector128<byte>.Zero);
        return (Vector128.Max(Vector128.Min(level, Vector128.Create(Fluid.Falling)), Vector128<byte>.One) - Vector128<byte>.One) & standing;
    }

    // 16 texels of the texture from `at`, with no bounds check. The invariant: the shaded layers are 1 to layers - 2 of
    // the texture and a row's texels lie inside the ring, so `at` with its neighbours (one row, one layer and one texel
    // each way) stays within [0, layers * Layer - Size].
    static Vector128<byte> Sample(ReadOnlySpan<byte> texture, int at) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(texture), (nuint)at);

    // Where the row z of the texture's layer `layer` starts, inside the ring (x = 0).
    static int Texel(int layer, int z) => layer * Layer + (z + 1) * Row + 1;

    // The texture: the chunk's layers Bottom to Top in the middle, the halo around them, a row of 16 at a time; and the
    // stencil's mask, a bit per row with water (row z at bit z + 1, the north halo's row at bit 0, the south's at bit 17),
    // the halo block at each end of a row counting with it. A neighbour not loaded is no water and no block.
    static void Pad(Tile self, Tile north, Tile south, Tile west, Tile east, Span<byte> water, Span<byte> ground,
        Span<uint> wetRows, in Reach reach)
    {
        for (int y = reach.Bottom; y <= reach.Top; y++)
        {
            int layer = y - reach.Bottom;
            uint wet = 0;
            for (int z = 0; z < Size; z++)
            {
                int at = Terrain.IndexOf(0, y, z), p = Texel(layer, z);
                var row = Vector128.Create(self.Water.Slice(at, Size));
                row.CopyTo(water.Slice(p, Size));
                self.Blocks.Slice(at, Size).CopyTo(ground.Slice(p, Size));
                // The west neighbour's column x = 15 and the east neighbour's x = 0, beside this row's ends.
                byte w = water[p - 1] = west.Water.IsEmpty ? (byte)0 : west.Water[at + Size - 1];
                ground[p - 1] = west.Water.IsEmpty ? (byte)0 : west.Blocks[at + Size - 1];
                byte e = water[p + Size] = east.Water.IsEmpty ? (byte)0 : east.Water[at];
                ground[p + Size] = east.Water.IsEmpty ? (byte)0 : east.Blocks[at];
                if ((w | e) != 0 || row != Vector128<byte>.Zero) wet |= 2u << z;
            }
            // The north neighbour's row z = 15 and the south neighbour's z = 0.
            if (Edge(north, Terrain.IndexOf(0, y, Size - 1), water.Slice(Texel(layer, -1), Size), ground.Slice(Texel(layer, -1), Size)))
                wet |= 1;
            if (Edge(south, Terrain.IndexOf(0, y, 0), water.Slice(Texel(layer, Size), Size), ground.Slice(Texel(layer, Size), Size)))
                wet |= 1u << (Size + 1);
            wetRows[layer] = wet;
        }
    }

    // A neighbour's facing row into the ring; whether it holds water.
    static bool Edge(Tile tile, int at, Span<byte> water, Span<byte> ground)
    {
        if (tile.Water.IsEmpty)
        {
            water.Clear();
            ground.Clear();
            return false;
        }
        var row = Vector128.Create(tile.Water.Slice(at, Size));
        row.CopyTo(water);
        tile.Blocks.Slice(at, Size).CopyTo(ground);
        return row != Vector128<byte>.Zero;
    }
}
