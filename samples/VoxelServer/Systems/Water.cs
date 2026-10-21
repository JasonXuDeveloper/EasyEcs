using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// #region wake
// A block update: a chunk whose blocks changed (dug, or just created or loaded) computes its height map again and gets
// its water, and its neighbours', moving again. [Changed<T>] visits only those chunks; the dig itself knows nothing of
// water or of the sky. The height map is 16 vectors of 16 columns: each layer from the bottom up overwrites the lanes
// whose block is solid with its height, so after the 32 layers each lane holds the top of its column, with no branch.
[UpdateInGroup<Simulation>]
public class BlockUpdateSystem : EcsSystem
{
    const int Columns = Terrain.Size * Terrain.Size;

    [ForEach, Changed<DynamicBuffer<Block>>]
    void Update(in Chunk chunk, in DynamicBuffer<Block> blocks, Mut<DynamicBuffer<Surface>> surface)
    {
        var solid = MemoryMarshal.Cast<Block, byte>(blocks.AsReadOnlySpan());
        Span<byte> top = stackalloc byte[Columns];                     // zeroed: bedrock, at 0, is never dug
        for (int y = 1; y < Terrain.Height; y++)
        {
            var layer = solid.Slice(y * Columns, Columns);
            var height = Vector128.Create((byte)y);
            for (int i = 0; i < Columns; i += Vector128<byte>.Count)
            {
                var air = Vector128.Equals(Vector128.Create(layer.Slice(i, Vector128<byte>.Count)), Vector128<byte>.Zero);
                var below = Vector128.Create((ReadOnlySpan<byte>)top.Slice(i, Vector128<byte>.Count));
                Vector128.ConditionalSelect(air, below, height).CopyTo(top.Slice(i, Vector128<byte>.Count));
            }
        }
        ref var map = ref surface.Write();
        map.Clear();
        map.AddRange(MemoryMarshal.Cast<byte, Surface>(top));
        Terrain.Wake(World, chunk.Pos);
    }
}
// #endregion wake

// #region flow
// Steps the water of every chunk that may still move, every FlowDelay ticks, the way Minecraft spreads it: an open
// block under water fills as falling water (8); an open block beside water that stands on something solid fills one
// level below it (a spring or falling water gives 7, then 6 ... 1, so water runs 7 blocks on flat ground); everything
// else empties. The same rule, applied again each step, makes water recede when its spring or the water above it is
// gone, and fill a pit dug beside a lake.
//
// A simplified version of Minecraft's rule: no new sources between two springs, and water spreads every way rather
// than toward the nearest drop.
//
// It is written as a fragment shader is: Shade computes one block's next level from its own and its six neighbours',
// nothing else, and runs on 16 blocks at once (a row of the chunk, one Vector128<byte>). The water is kept per chunk
// (it loads, saves and replicates with its chunk, and only chunks that may still move step), so the shader reads a
// padded copy of the chunk first: its water and blocks with a ring of one block from the four chunks around it, the
// texture the shader samples, so the loop has no edge cases. Only the layers the water reaches are copied and shaded,
// and the step and the current are written in place (DynamicBuffer.SetCount), not built on the stack and copied.
// Byte lanes are integer arithmetic: the same water on every machine. The step goes into NextFluid, not Fluid, so
// every chunk steps from the water before the step, whatever order the chunks are visited in; WaterCommitSystem then
// makes it the water. The same pass writes the Current of each block (below), for FloatSystem.
[UpdateInGroup<Simulation>]
public class WaterFlowSystem : EcsSystem
{
    public override int UpdateInterval => Rules.FlowDelay;

    const int Size = Terrain.Size, Height = Terrain.Height, Blocks = Terrain.BlocksPerChunk;
    const int Row = Size + 2, Layer = Row * Row;      // the padded grid: a ring of one block around each layer

    [ForEach, With<Flowing>]
    void Flow(Entity chunk, in Chunk key, in DynamicBuffer<Block> blocks, in DynamicBuffer<Fluid> fluid,
        Mut<DynamicBuffer<NextFluid>> step, Mut<DynamicBuffer<Current>> current, ref FluidCount count, ref Stats stats)
    {
        var levels = MemoryMarshal.Cast<Fluid, byte>(fluid.AsReadOnlySpan());
        var solid = MemoryMarshal.Cast<Block, byte>(blocks.AsReadOnlySpan());

        // The texture: the layers holding water here or beside, one more below (water falls into it) and one above
        // (what falls from it), and the ring from the neighbours. Zeroed: no water, no block outside.
        var (low, high) = Layers(levels);
        Span<Neighbour> around = stackalloc Neighbour[4];
        Neighbours(World, key.Pos, around, ref low, ref high);
        if (low > high)
        {
            current.Write().Clear();                                    // no water here or beside: nothing moves
            count = default;
            chunk.Remove<Flowing>();
            return;
        }
        int from = Math.Max(low - 1, 1), to = high;
        int bottom = from - 1, top = Math.Min(to + 1, Height - 1);     // the layers the shader samples
        int layers = to + 2 - bottom;                                  // and one above the top, zeroed (no water)
        Span<byte> water = stackalloc byte[layers * Layer];
        Span<byte> ground = stackalloc byte[layers * Layer];
        Pad(levels, solid, water, ground, bottom, top);
        foreach (var side in around) side.Ring(World, water, ground, bottom, top);

        // The pass: every block of those layers, 16 at a time, written straight into the step and the current.
        ref var stepped = ref step.Write();
        stepped.SetCount(Blocks);
        var next = MemoryMarshal.Cast<NextFluid, byte>(stepped.AsSpan());
        levels.CopyTo(next);
        ref var field = ref current.Write();
        field.SetCount(2 * Blocks);                                    // default: no current where no water is
        var flow = MemoryMarshal.Cast<Current, sbyte>(field.AsSpan());
        var changed = Vector128<byte>.Zero;
        int filled = 0;
        for (int y = from; y <= to; y++)
        for (int z = 0; z < Size; z++)
        {
            int at = Terrain.IndexOf(0, y, z), p = Texel(y - bottom, z);
            var here = Sample(water, p);
            var level = Shade(here, Sample(water, p + Layer),
                Sample(water, p - 1), Sample(ground, p - 1 - Layer), Sample(water, p + 1), Sample(ground, p + 1 - Layer),
                Sample(water, p - Row), Sample(ground, p - Row - Layer), Sample(water, p + Row), Sample(ground, p + Row - Layer),
                Sample(ground, p));
            level.CopyTo(next.Slice(at, Size));
            changed |= ~Vector128.Equals(level, here);
            filled += BitOperations.PopCount(
                (Vector128.Equals(here, Vector128<byte>.Zero) & ~Vector128.Equals(level, Vector128<byte>.Zero)).ExtractMostSignificantBits());

            var (x, zFlow) = Slope(here, Sample(water, p + 1), Sample(ground, p + 1), Sample(water, p - 1), Sample(ground, p - 1),
                Sample(water, p + Row), Sample(ground, p + Row), Sample(water, p - Row), Sample(ground, p - Row));
            x.CopyTo(flow.Slice(at, Size));
            zFlow.CopyTo(flow.Slice(Blocks + at, Size));
        }

        count = new FluidCount(Blocks - next.Count((byte)0));          // Count is vectorized
        if (count.Blocks == 0) field.Clear();                          // a dry chunk holds no current
        if (changed == Vector128<byte>.Zero)
        {
            stepped.Clear();                                            // nothing for WaterCommitSystem to copy
            chunk.Remove<Flowing>();                                    // settled: not visited again until woken
            return;
        }
        stats.WaterCells += filled;
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

    // The current of 16 blocks: the drop of the water toward each open block beside it, summed per axis, as Minecraft
    // computes the flow a body in water is pushed along (a spring or falling water counts as 8). It gives the
    // direction; FloatSystem scales the push by the level, so water far from its spring pushes less.
    static (Vector128<sbyte> X, Vector128<sbyte> Z) Slope(Vector128<byte> level,
        Vector128<byte> east, Vector128<byte> eastBlock, Vector128<byte> west, Vector128<byte> westBlock,
        Vector128<byte> south, Vector128<byte> southBlock, Vector128<byte> north, Vector128<byte> northBlock)
    {
        var here = Depth(level);
        var wet = ~Vector128.Equals(level, Vector128<byte>.Zero);
        var x = Drop(here, east, eastBlock) - Drop(here, west, westBlock);
        var z = Drop(here, south, southBlock) - Drop(here, north, northBlock);
        return (x & wet.AsSByte(), z & wet.AsSByte());
    }

    // The drop from `here` to a neighbour, none toward a solid block.
    static Vector128<sbyte> Drop(Vector128<sbyte> here, Vector128<byte> level, Vector128<byte> block) =>
        (here - Depth(level)) & Vector128.Equals(block, Vector128<byte>.Zero).AsSByte();

    static Vector128<sbyte> Depth(Vector128<byte> level) => Vector128.Min(level, Vector128.Create(Fluid.Falling)).AsSByte();

    // 16 texels of the padded grid from `at`.
    static Vector128<byte> Sample(ReadOnlySpan<byte> grid, int at) => Vector128.Create(grid.Slice(at, Size));

    // Where the row z of the padded layer `layer` starts, inside the ring (x = 0).
    static int Texel(int layer, int z) => layer * Layer + (z + 1) * Row + 1;

    // The chunk's own layers [bottom, top] into the middle of the padded grids (whose layer 0 is `bottom`), a row of 16
    // at a time.
    static void Pad(ReadOnlySpan<byte> levels, ReadOnlySpan<byte> solid, Span<byte> water, Span<byte> ground, int bottom, int top)
    {
        for (int y = bottom; y <= top; y++)
        for (int z = 0; z < Size; z++)
        {
            int at = Terrain.IndexOf(0, y, z), p = Texel(y - bottom, z);
            levels.Slice(at, Size).CopyTo(water.Slice(p, Size));
            solid.Slice(at, Size).CopyTo(ground.Slice(p, Size));
        }
    }

    // A loaded chunk beside this one, and which of its edges touches it: what goes into the ring.
    struct Neighbour
    {
        public EntityId Chunk;                                          // unmanaged, so four fit on the stack
        public bool Loaded;
        public int Dx, Dz;

        // Its edge row or column, on layers [bottom, top], into the ring of the padded grids.
        public readonly void Ring(World world, Span<byte> water, Span<byte> ground, int bottom, int top)
        {
            if (!Loaded) return;                                        // not loaded: no water, no block
            var chunk = world.GetEntity(Chunk);
            var levels = MemoryMarshal.Cast<Fluid, byte>(chunk.Fluid.AsReadOnlySpan());
            var solid = MemoryMarshal.Cast<Block, byte>(chunk.Block.AsReadOnlySpan());
            for (int y = bottom; y <= top; y++)
            {
                if (Dz != 0)                                            // north or south: a whole row
                {
                    int at = Terrain.IndexOf(0, y, Dz < 0 ? Size - 1 : 0), p = Texel(y - bottom, Dz < 0 ? -1 : Size);
                    levels.Slice(at, Size).CopyTo(water.Slice(p, Size));
                    solid.Slice(at, Size).CopyTo(ground.Slice(p, Size));
                    continue;
                }
                int x = Dx < 0 ? Size - 1 : 0, ring = Dx < 0 ? 0 : Row - 1;   // west or east: one block per row
                for (int z = 0; z < Size; z++)
                {
                    int at = Terrain.IndexOf(x, y, z), p = Texel(y - bottom, z) - 1 + ring;
                    water[p] = levels[at];
                    ground[p] = solid[at];
                }
            }
        }
    }

    // The four chunks around, as Neighbours (empty when not loaded), widening [low, high] to the layers of their water.
    static void Neighbours(World world, ChunkPos pos, Span<Neighbour> around, ref int low, ref int high)
    {
        ReadOnlySpan<(int Dx, int Dz)> sides = [(0, -1), (0, 1), (-1, 0), (1, 0)];
        for (int i = 0; i < sides.Length; i++)
        {
            around[i] = new Neighbour { Dx = sides[i].Dx, Dz = sides[i].Dz };
            if (!world.ChunkIndex.TryGet(new ChunkPos(pos.X + sides[i].Dx, pos.Z + sides[i].Dz), out var chunk)) continue;
            (around[i].Chunk, around[i].Loaded) = (chunk.Id, true);
            var (from, to) = Layers(MemoryMarshal.Cast<Fluid, byte>(chunk.Fluid.AsReadOnlySpan()));
            if (from > to) continue;
            low = Math.Min(low, Math.Max(from, 1));
            high = Math.Max(high, to);
        }
    }

    // The lowest and the highest layer holding water, low > high if none: two vectorized searches for a non-zero byte.
    static (int Low, int High) Layers(ReadOnlySpan<byte> levels)
    {
        int first = levels.IndexOfAnyExcept((byte)0);
        return first < 0 ? (Height, 0) : (first / (Size * Size), levels.LastIndexOfAnyExcept((byte)0) / (Size * Size));
    }
}
// Makes the step the water, once every chunk has stepped: [Changed<T>] visits the chunks whose NextFluid was written,
// and the copy is a write of the Fluid buffer, which replication sends. The water of their neighbours may move now
// (it reaches across the edges), so they are woken.
[UpdateInGroup<Simulation>]
public class WaterCommitSystem : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<NextFluid>>]
    void Commit(in Chunk key, Mut<DynamicBuffer<NextFluid>> step, Mut<DynamicBuffer<Fluid>> fluid)
    {
        var next = step.Value.AsReadOnlySpan();
        if (next.Length == 0) return;                                   // emptied by the last commit
        next.CopyTo(MemoryMarshal.Cast<Fluid, NextFluid>(fluid.Write().AsSpan()));
        step.Write().Clear();
        Terrain.Wake(World, key.Pos);
    }
}
// #endregion flow

// #region float
// Lifts whatever stands in water, players, zombies and items, and carries it with the current, as Minecraft does:
// along the Current WaterFlowSystem wrote for its block, the slope of the water toward the lower water around it, and
// the weaker the lower the water: full next to the spring or under falling water, an eighth at the edge of a run.
// It visits the chunks, skips the dry ones (FluidCount) and, through the index of entities by chunk, finds the bodies
// in the others: the water of a body's chunk is the loop's own row, not a lookup. Runs after the walk
// set the velocity and before physics moves the body.
[UpdateInGroup<Simulation>]
public class FloatSystem : EcsSystem
{
    [ForEach]
    void Float(in Chunk key, in FluidCount count, in DynamicBuffer<Fluid> fluid, in DynamicBuffer<Current> current,
        ref Stats stats)
    {
        if (count.Blocks == 0) return;                                 // dry: nothing floats here
        var levels = fluid.AsReadOnlySpan();
        var flow = current.AsReadOnlySpan();                            // empty until the water first stepped
        foreach (var body in World.InChunkIndex.Find(key.Pos))
        {
            var at = Terrain.Cell(body.Position.Value);
            if ((uint)at.Y >= Terrain.Height || Terrain.ChunkOf(at.X, at.Z) != key.Pos) continue;   // crossing: next tick
            int i = Terrain.IndexOf(at.X, at.Y, at.Z);
            int level = Math.Min((int)levels[i].Level, Fluid.Falling);
            if (level == 0) continue;

            var push = flow.IsEmpty ? Vector2.Zero : new Vector2(flow[i].Value, flow[Terrain.BlocksPerChunk + i].Value);
            if (push != Vector2.Zero) push = Vector2.Normalize(push) * (Rules.CurrentSpeed * level / Fluid.Falling);
            ref var velocity = ref body.Velocity;
            var v = velocity.Value;
            velocity = new Velocity(new Vector3(v.X + push.X, Rules.FloatSpeed, v.Z + push.Y));
            stats.Drifts++;
        }
    }
}
// #endregion float
