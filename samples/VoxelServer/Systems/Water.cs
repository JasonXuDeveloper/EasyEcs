using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// #region wake
// A block update: a chunk whose blocks changed (dug, or just created or loaded) gets its water, and its neighbours'
// moving again. [Changed<T>] visits only those chunks; the dig itself knows nothing of water.
[UpdateInGroup<Simulation>]
public class BlockUpdateSystem : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<Block>>]
    void Wake(in Chunk chunk) => Terrain.Wake(World, chunk.Pos);
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
// The rule is a stencil, the same arithmetic for every block from its six neighbours, so it runs as SIMD over the
// grid: a row of the chunk is 16 blocks, one Vector128<byte>, and its neighbours are the rows next to it in the same
// buffer. The neighbours across the chunk's edges are copied into a halo around a padded copy of the grid first, so
// the vector loop has no edge cases. Byte lanes are integer arithmetic: the same water on every machine. The step
// goes into NextFluid, not Fluid, so every chunk steps from the water before the step, whatever order the chunks are
// visited in; WaterCommitSystem then makes it the water.
[UpdateInGroup<Simulation>]
public class WaterFlowSystem : EcsSystem
{
    public override int UpdateInterval => Rules.FlowDelay;

    const int Size = Terrain.Size, Height = Terrain.Height;
    const int Row = Size + 2, Layer = Row * Row;      // the padded grid: a halo of one block on each side

    [ForEach, With<Flowing>]
    void Flow(Entity chunk, in Chunk key, in DynamicBuffer<Block> blocks, in DynamicBuffer<Fluid> fluid,
        Mut<DynamicBuffer<NextFluid>> step, ref Stats stats)
    {
        var levels = MemoryMarshal.Cast<Fluid, byte>(fluid.AsReadOnlySpan());
        var solid = MemoryMarshal.Cast<Block, byte>(blocks.AsReadOnlySpan());

        // What each block hands to its sides: one level less than its own, if it stands on something solid. Only the
        // layers holding water give anything, so only they are filled in, here and in the halo.
        Span<byte> spread = stackalloc byte[Height * Layer];             // zeroed: nothing given
        var (low, high) = Layers(levels);
        for (int y = Math.Max(low, 1); y <= high; y++)
        for (int z = 0; z < Size; z++)
            Gives(levels, solid, Terrain.IndexOf(0, y, z)).CopyTo(spread.Slice(y * Layer + (z + 1) * Row + 1, Size));
        Halo(World, key.Pos, spread, ref low, ref high);

        // Water can change only on the layers some water reaches: those with water here or beside, and the one under
        // the lowest, which water falls into. The others stay as they are, empty.
        Span<byte> next = stackalloc byte[levels.Length];
        levels.CopyTo(next);
        var changed = Vector128<byte>.Zero;
        int filled = 0;
        for (int y = Math.Max(low - 1, 1); y <= high; y++)
        for (int z = 0; z < Size; z++)
        {
            int at = Terrain.IndexOf(0, y, z), padded = y * Layer + (z + 1) * Row + 1;
            var side = Vector128.Max(
                Vector128.Max(Vector128.Create(spread.Slice(padded - 1, Size)), Vector128.Create(spread.Slice(padded + 1, Size))),
                Vector128.Max(Vector128.Create(spread.Slice(padded - Row, Size)), Vector128.Create(spread.Slice(padded + Row, Size))));
            var above = y + 1 < Height ? Vector128.Create(levels.Slice(at + Size * Size, Size)) : Vector128<byte>.Zero;
            var falling = ~Vector128.Equals(above, Vector128<byte>.Zero) & Vector128.Create(Fluid.Falling);
            var open = Vector128.Equals(Vector128.Create(solid.Slice(at, Size)), Vector128<byte>.Zero);
            var now = Vector128.Create(levels.Slice(at, Size));
            var spring = Vector128.Equals(now, Vector128.Create(Fluid.Spring));
            var level = Vector128.ConditionalSelect(spring, now, Vector128.Max(side, falling) & open);
            level.CopyTo(next.Slice(at, Size));
            changed |= ~Vector128.Equals(level, now);
            filled += BitOperations.PopCount(
                (Vector128.Equals(now, Vector128<byte>.Zero) & ~Vector128.Equals(level, Vector128<byte>.Zero)).ExtractMostSignificantBits());
        }

        if (changed == Vector128<byte>.Zero)
        {
            chunk.Remove<Flowing>();                                    // settled: not visited again until woken
            return;
        }
        ref var buffer = ref step.Write();
        buffer.Clear();
        buffer.AddRange(MemoryMarshal.Cast<byte, NextFluid>(next));
        stats.WaterCells += filled;
    }

    // What the 16 blocks of a row starting at `at` hand to their sides.
    static Vector128<byte> Gives(ReadOnlySpan<byte> levels, ReadOnlySpan<byte> solid, int at)
    {
        var level = Vector128.Min(Vector128.Create(levels.Slice(at, Size)), Vector128.Create(Fluid.Falling));
        var standing = ~Vector128.Equals(Vector128.Create(solid.Slice(at - Size * Size, Size)), Vector128<byte>.Zero);
        return (Vector128.Max(level, Vector128<byte>.One) - Vector128<byte>.One) & standing;
    }

    // Copies what the neighbouring chunks hand across the edges into the halo of `spread`: a whole row of a chunk to
    // the north or south, one block per row of a chunk to the west or east, on the layers holding their water, which
    // widen [low, high]. A chunk that is not loaded hands nothing.
    static void Halo(World world, ChunkPos pos, Span<byte> spread, ref int low, ref int high)
    {
        if (Grids(world, pos with { Z = pos.Z - 1 }, out var levels, out var solid, ref low, ref high, out int from, out int to))
            for (int y = from; y <= to; y++)
                Gives(levels, solid, Terrain.IndexOf(0, y, Size - 1)).CopyTo(spread.Slice(y * Layer + 1, Size));
        if (Grids(world, pos with { Z = pos.Z + 1 }, out levels, out solid, ref low, ref high, out from, out to))
            for (int y = from; y <= to; y++)
                Gives(levels, solid, Terrain.IndexOf(0, y, 0)).CopyTo(spread.Slice(y * Layer + (Row - 1) * Row + 1, Size));
        if (Grids(world, pos with { X = pos.X - 1 }, out levels, out solid, ref low, ref high, out from, out to))
            Column(levels, solid, spread, Size - 1, 0, from, to);
        if (Grids(world, pos with { X = pos.X + 1 }, out levels, out solid, ref low, ref high, out from, out to))
            Column(levels, solid, spread, 0, Row - 1, from, to);
    }

    // The lowest and the highest layer holding water, low > high if none: two vectorized searches for a non-zero byte.
    static (int Low, int High) Layers(ReadOnlySpan<byte> levels)
    {
        int first = levels.IndexOfAnyExcept((byte)0);
        return first < 0 ? (Height, 0) : (first / (Size * Size), levels.LastIndexOfAnyExcept((byte)0) / (Size * Size));
    }

    // The water and the blocks of a loaded neighbour as bytes, and the layers [from, to] holding its water; false if
    // it is not loaded or holds none.
    static bool Grids(World world, ChunkPos pos, out ReadOnlySpan<byte> levels, out ReadOnlySpan<byte> solid,
        ref int low, ref int high, out int from, out int to)
    {
        levels = solid = default;
        from = to = 0;
        if (!world.ChunkIndex.TryGet(pos, out var chunk)) return false;
        levels = MemoryMarshal.Cast<Fluid, byte>(chunk.Fluid.AsReadOnlySpan());
        solid = MemoryMarshal.Cast<Block, byte>(chunk.Block.AsReadOnlySpan());
        (from, to) = Layers(levels);
        if (from > to) return false;
        from = Math.Max(from, 1);
        low = Math.Min(low, from);
        high = Math.Max(high, to);
        return true;
    }

    // The column x of a neighbour to the west or east, into column haloX of the halo: one block per row, 16 apart.
    static void Column(ReadOnlySpan<byte> levels, ReadOnlySpan<byte> solid, Span<byte> spread, int x, int haloX,
        int from, int to)
    {
        for (int y = from; y <= to; y++)
        for (int z = 0; z < Size; z++)
        {
            int at = Terrain.IndexOf(x, y, z);
            if (solid[at - Size * Size] == 0) continue;                // not standing on anything: gives nothing
            spread[y * Layer + (z + 1) * Row + haloX] = (byte)Math.Max(Math.Min(levels[at], Fluid.Falling) - 1, 0);
        }
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
// toward the lower water around it. Runs after the walk set the velocity and before physics moves the body.
[UpdateInGroup<Simulation>]
public class FloatSystem : EcsSystem
{
    [ForEach]
    void Float(in Position position, ref Velocity velocity, ref Stats stats)
    {
        var p = position.Value;
        var at = Terrain.Cell(p);
        var near = new Nearby(World);
        int here = Math.Min(near.Water(at), Fluid.Falling);
        if (here == 0) return;

        var current = Vector2.Zero;
        Push(ref near, ref current, at with { X = at.X + 1 }, here, new Vector2(1, 0));
        Push(ref near, ref current, at with { X = at.X - 1 }, here, new Vector2(-1, 0));
        Push(ref near, ref current, at with { Z = at.Z + 1 }, here, new Vector2(0, 1));
        Push(ref near, ref current, at with { Z = at.Z - 1 }, here, new Vector2(0, -1));
        if (current != Vector2.Zero) current = Vector2.Normalize(current) * Rules.Current;

        var v = velocity.Value;
        velocity = new Velocity(new Vector3(v.X + current.X, Rules.FloatSpeed, v.Z + current.Y));
        stats.Drifts++;
    }

    // The pull toward a neighbour: the drop from this block's level to its, none toward a solid block.
    static void Push(ref Nearby near, ref Vector2 current, BlockPos neighbour, int here, Vector2 direction)
    {
        if (Terrain.IsSolid(near.Block(neighbour))) return;
        current += direction * (here - Math.Min(near.Water(neighbour), Fluid.Falling));
    }
}
// #endregion float
