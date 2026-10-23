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
// neighbors' water before the step, as a GPU ping-pongs two buffers) and wakes the chunks beside the edges that changed.
//
// The shader. Shade computes one block's next level from its own and its six neighbors', nothing else, and runs on
// 16 blocks at once (a row of the chunk, one Vector128<byte>). Each chunk first loads its tile and the halo around it,
// as a CUDA block loads its tile and halo into shared memory: a Tile<Fluid, Chunk> parameter is the chunk's water with
// its neighbors' (Chunk is a tile of the grid, ITile), and Pad copies the layers in reach into a texture with a ring of
// one block around each layer, from the facing row or column of each neighbor (read as they were before the pass, so
// the method stays parallel, and only NextFluid is written), and a layer under and over them. The loop then has no
// edge cases. Byte lanes are integer arithmetic: the same water on every machine.
//
// The stencil, as a GPU's early stencil test skips the fragments a mask rules out: while padding, the tile records
// which rows of the texture hold water (with the halo block at each end); Rows visits the rows a step can wet, those
// whose stencil (the row, the rows north and south, the row above: TileReads.Layer | Above) holds some, the next one
// found by counting trailing zeros. The others are not written at all: the step's buffer equals the water after every
// commit (a moved step is copied into it, a settled one wrote back what was there), so a row whose stencil is dry is
// already zero in it.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.FlowDelay)]
public ref struct WaterFlowSystem
{
    const int Size = Terrain.Size, Blocks = Terrain.BlocksPerChunk;

    // A few hundred chunks of 16 x 16 x 32 blocks: far under the 4,096 rows of a default chunk of a pass that writes a
    // tracked component, so the threads take 64 chunks at a time (OpenMP's schedule(dynamic, 64)) instead of one taking all.
    [ForEach(Parallel = true, ChunkRows = 64), With<Flowing>]
    static void Flow(in FluidCount count, Lookup<FluidCount> counts, Tile<Fluid, Chunk> fluid, Tile<Block, Chunk> blocks,
        Mut<DynamicBuffer<NextFluid>> step, ref FluidStep summary)
    {
        // The layers to shade: those with water here and beside (FluidCount, no search), and one below, where water falls.
        int low = count.Low, high = count.High;
        for (var side = TileFaces.North; side <= TileFaces.East; side = (TileFaces)((int)side << 1))
        {
            var beside = fluid.Beside(side);
            if (beside == default) continue;
            ref readonly var there = ref counts.Read(beside);
            if (there.Low > there.High) continue;
            (low, high) = (Math.Min(low, there.Low), Math.Max(high, there.High));
        }
        int from = Math.Max(low - 1, 1), to = high;
        if (from > to)
        {
            summary = new FluidStep(StepResult.Settled, default, 0, 0); // no water here or beside: nothing moves
            return;
        }
        // The textures: the water with its stencil, the blocks (only sampled). Above the world's top, no chunk holds a
        // layer: it is no water and no block, as a neighbor that is not loaded.
        var water = fluid.Pad(from, to);
        var ground = blocks.Pad(from, to, mask: false);

        // The rows the stencil keeps, 16 blocks at a time, written straight into the step. The layers outside hold no
        // water before the step or after it, so the buffer keeps its count between steps.
        var next = MemoryMarshal.Cast<NextFluid, byte>(step.Write().SetCount(Blocks));
        int filled = 0, wet = 0, first = Terrain.Height, last = 0;
        uint edges = 0;
        bool changed = false;
        foreach (var row in water.Rows(TileReads.Layer | TileReads.Above))
        {
            var w = water.At(row);
            var g = ground.At(row);
            var here = w.Load128<byte>();
            // (dx, dy, dz) from the row: the row above, west and east (with the block under each), north and south, its blocks.
            var level = Shade(here, w.Load128<byte>(0, 1, 0),
                w.Load128<byte>(-1), g.Load128<byte>(-1, -1), w.Load128<byte>(1), g.Load128<byte>(1, -1),
                w.Load128<byte>(0, 0, -1), g.Load128<byte>(0, -1, -1), w.Load128<byte>(0, 0, 1), g.Load128<byte>(0, -1, 1),
                g.Load128<byte>());
            level.CopyTo(next.Slice(row.Index, Size));

            uint lanes = (~Vector128.Equals(level, Vector128<byte>.Zero)).ExtractMostSignificantBits();
            wet += BitOperations.PopCount(lanes);
            if (lanes != 0) (first, last) = (Math.Min(first, row.Y), row.Y);
            filled += BitOperations.PopCount(lanes & Vector128.Equals(here, Vector128<byte>.Zero).ExtractMostSignificantBits());
            uint moved = (~Vector128.Equals(level, here)).ExtractMostSignificantBits();
            if (moved == 0) continue;
            changed = true;
            if (row.Z == 0) edges |= Terrain.North;
            if (row.Z == Size - 1) edges |= Terrain.South;
            if ((moved & 1) != 0) edges |= Terrain.West;
            if ((moved & 1u << (Size - 1)) != 0) edges |= Terrain.East;
        }
        summary = new FluidStep(changed ? StepResult.Moved : StepResult.Settled, new FluidCount(wet, (byte)first, (byte)last),
            filled, (byte)edges);
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
}
