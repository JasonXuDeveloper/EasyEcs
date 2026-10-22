using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// #region wake
// A block update: a chunk whose blocks changed (dug, or just created or loaded) gets its height map computed again and
// its water, and its neighbours', moving again: two systems, one job each, both [Changed<DynamicBuffer<Block>>], so
// they visit only those chunks and the dig itself knows nothing of water or of the sky.
//
// The height map is 16 vectors of 16 columns: each layer from the bottom up overwrites the lanes whose block is solid
// with its height, so after the 32 layers each lane holds the top of its column, with no branch. The 256 bytes are read
// and written once per layer, on the stack (measured: an order of magnitude faster than in the buffer's array), and
// copied into the buffer once, through the span SetCount returns.
[UpdateInGroup<Simulation>]
public class HeightMapSystem : EcsSystem
{
    const int Columns = Terrain.Size * Terrain.Size;

    [ForEach, Changed<DynamicBuffer<Block>>]
    static void Update(in DynamicBuffer<Block> blocks, Mut<DynamicBuffer<Surface>> surface)
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
        top.CopyTo(MemoryMarshal.Cast<Surface, byte>(surface.Write().SetCount(Columns)));
    }
}

[UpdateInGroup<Simulation>]
public class WaterWakeSystem : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<Block>>]
    void Wake(Entity chunk, in Chunk key)
    {
        chunk.SetFlowing();
        Terrain.Wake(World, key.Pos, Terrain.AllSides);
    }
}
// #endregion wake

// #region flow
// The water steps every FlowDelay ticks, the way Minecraft spreads it: an open block under water fills as falling water
// (8); an open block beside water that stands on something solid fills one level below it (a spring or falling water
// gives 7, then 6 ... 1, so water runs 7 blocks on flat ground); everything else empties. The same rule, applied again
// each step, makes water recede when its spring or the water above it is gone, and fill a pit dug beside a lake.
// (Simplified: no new sources between two springs, and water spreads every way rather than toward the nearest drop.)
//
// It runs as a GPU runs a shader, in passes that hand data to each other, on the chunks that may still move (Flowing):
// WaterBorderSystem gathers what the step needs from the chunks around (their edges, into the Border buffer, and the
// layers to shade, Reach); WaterFlowSystem shades every block from its own components only, into NextFluid, so it runs
// in parallel; WaterSleepSystem lets the chunks whose step moved nothing sleep; WaterCommitSystem makes the step the
// water once every chunk has stepped (each chunk steps from its neighbours' water before the step, as a GPU ping-pongs
// two buffers) and wakes the chunks beside the edges that changed.
[UpdateInGroup<Simulation>]
public class WaterBorderSystem : EcsSystem
{
    public override int UpdateInterval => Rules.FlowDelay;

    [ForEach, With<Flowing>]
    void Gather(in Chunk key, in FluidCount count, Mut<DynamicBuffer<Border>> border, ref Reach reach)
    {
        // The layers: those with water here and beside (FluidCount, no search), and one below, where water falls.
        int low = count.Low, high = count.High;
        ReadOnlySpan<(int Dx, int Dz)> sides = [(0, -1), (0, 1), (-1, 0), (1, 0)];
        Span<EntityId> around = stackalloc EntityId[4];
        for (int side = 0; side < 4; side++)
        {
            if (!World.ChunkIndex.TryGet(new ChunkPos(key.Pos.X + sides[side].Dx, key.Pos.Z + sides[side].Dz), out var chunk)) continue;
            around[side] = chunk;
            ref readonly var water = ref chunk.FluidCount;
            if (water.Low > water.High) continue;
            (low, high) = (Math.Min(low, water.Low), Math.Max(high, water.High));
        }
        reach = new Reach(Math.Max(low - 1, 1), high);
        if (reach.From > reach.To) return;

        // The edge of each neighbour that touches this chunk, on the layers the step samples.
        var cells = MemoryMarshal.Cast<Border, byte>(border.Write().SetCount(Terrain.BorderSize));
        for (int side = 0; side < 4; side++)
        {
            bool loaded = World.TryGetEntity(around[side], out var chunk);
            var levels = loaded ? MemoryMarshal.Cast<Fluid, byte>(chunk.Fluid.AsReadOnlySpan()) : default;
            var solid = loaded ? MemoryMarshal.Cast<Block, byte>(chunk.Block.AsReadOnlySpan()) : default;
            for (int y = reach.Bottom; y <= reach.Top; y++)
            {
                var water = cells.Slice(Terrain.BorderOf(side, y, false), Terrain.Size);
                var blocks = cells.Slice(Terrain.BorderOf(side, y, true), Terrain.Size);
                if (!loaded)                                               // not loaded: no water, no block
                {
                    water.Clear();
                    blocks.Clear();
                }
                else if (sides[side].Dz != 0)                              // north or south: the facing row
                {
                    int at = Terrain.IndexOf(0, y, sides[side].Dz < 0 ? Terrain.Size - 1 : 0);
                    levels.Slice(at, Terrain.Size).CopyTo(water);
                    solid.Slice(at, Terrain.Size).CopyTo(blocks);
                }
                else                                                       // west or east: the facing column
                    for (int z = 0; z < Terrain.Size; z++)
                    {
                        int at = Terrain.IndexOf(sides[side].Dx < 0 ? Terrain.Size - 1 : 0, y, z);
                        (water[z], blocks[z]) = (levels[at], solid[at]);
                    }
            }
        }
    }
}

// The shader. Shade computes one block's next level from its own and its six neighbours', nothing else, and runs on
// 16 blocks at once (a row of the chunk, one Vector128<byte>). The chunk's own layers and its Border are first copied
// into a padded texture, a ring of one block around each layer, so the loop has no edge cases. It reads nothing but
// the row's components, holds no World, and is Parallel. Byte lanes are integer arithmetic: the same water on every
// machine.
[UpdateInGroup<Simulation>, System.Runtime.CompilerServices.SkipLocalsInit]
public class WaterFlowSystem : EcsSystem
{
    public override int UpdateInterval => Rules.FlowDelay;

    const int Size = Terrain.Size, Blocks = Terrain.BlocksPerChunk;
    const int Row = Size + 2, Layer = Row * Row;      // the padded texture: a ring of one block around each layer

    [ForEach(Parallel = true), With<Flowing>]
    static void Flow(in DynamicBuffer<Block> blocks, in DynamicBuffer<Fluid> fluid, in DynamicBuffer<Border> border,
        in Reach reach, Mut<DynamicBuffer<NextFluid>> step, ref FluidStep summary)
    {
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
        if (reach.Top <= reach.To)
        {
            water.Slice((layers - 1) * Layer).Clear();
            ground.Slice((layers - 1) * Layer).Clear();
        }
        Pad(MemoryMarshal.Cast<Fluid, byte>(fluid.AsReadOnlySpan()), MemoryMarshal.Cast<Block, byte>(blocks.AsReadOnlySpan()),
            MemoryMarshal.Cast<Border, byte>(border.AsReadOnlySpan()), water, ground, reach);

        // Every block of the layers in reach, 16 at a time, written straight into the step. The layers outside hold no
        // water before the step or after it: zero since the buffer was first sized, as the steps before wrote water only
        // on layers the reach of this one covers. So the buffer keeps its count between steps and is not cleared.
        var next = MemoryMarshal.Cast<NextFluid, byte>(step.Write().SetCount(Blocks));
        int filled = 0, wet = 0, first = Terrain.Height, last = 0;
        uint edges = 0;
        bool changed = false;
        for (int y = reach.From; y <= reach.To; y++)
        for (int z = 0; z < Size; z++)
        {
            int p = Texel(y - reach.Bottom, z);
            var here = Sample(water, p);
            var row = next.Slice(Terrain.IndexOf(0, y, z), Size);
            // The stencil: with no water in a row, above it or beside it, nothing can flow in, and the row stays dry.
            if ((here | Sample(water, p + Layer) | Sample(water, p - 1) | Sample(water, p + 1) | Sample(water, p - Row)
                 | Sample(water, p + Row)) == Vector128<byte>.Zero)
            {
                Vector128<byte>.Zero.CopyTo(row);
                continue;
            }
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

    // 16 texels of the texture from `at`, with no bounds check. The invariant: the shaded layers are 1 to layers - 2 of
    // the texture and a row's texels lie inside the ring, so `at` with its neighbours (one row, one layer and one texel
    // each way) stays within [0, layers * Layer - Size].
    static Vector128<byte> Sample(ReadOnlySpan<byte> texture, int at) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(texture), (nuint)at);

    // Where the row z of the texture's layer `layer` starts, inside the ring (x = 0).
    static int Texel(int layer, int z) => layer * Layer + (z + 1) * Row + 1;

    // The texture: the chunk's layers Bottom to Top in the middle, the border around them, a row of 16 at a time.
    static void Pad(ReadOnlySpan<byte> levels, ReadOnlySpan<byte> solid, ReadOnlySpan<byte> border, Span<byte> water,
        Span<byte> ground, in Reach reach)
    {
        for (int y = reach.Bottom; y <= reach.Top; y++)
        {
            int layer = y - reach.Bottom;
            for (int z = 0; z < Size; z++)
            {
                int at = Terrain.IndexOf(0, y, z), p = Texel(layer, z);
                levels.Slice(at, Size).CopyTo(water.Slice(p, Size));
                solid.Slice(at, Size).CopyTo(ground.Slice(p, Size));
                water[p - 1] = border[Terrain.BorderOf(2, y, false) + z];          // west
                ground[p - 1] = border[Terrain.BorderOf(2, y, true) + z];
                water[p + Size] = border[Terrain.BorderOf(3, y, false) + z];       // east
                ground[p + Size] = border[Terrain.BorderOf(3, y, true) + z];
            }
            border.Slice(Terrain.BorderOf(0, y, false), Size).CopyTo(water.Slice(Texel(layer, -1), Size));     // north
            border.Slice(Terrain.BorderOf(0, y, true), Size).CopyTo(ground.Slice(Texel(layer, -1), Size));
            border.Slice(Terrain.BorderOf(1, y, false), Size).CopyTo(water.Slice(Texel(layer, Size), Size));   // south
            border.Slice(Terrain.BorderOf(1, y, true), Size).CopyTo(ground.Slice(Texel(layer, Size), Size));
        }
    }
}

// A step that moved nothing lets the chunk sleep until something wakes it: it leaves the passes (Flowing goes) and gives
// its scratch buffers, the step and the border, back to the pool (TrimExcess of an empty buffer), so only the chunks
// whose water moves hold them, and the next chunk woken takes them back without allocating. It runs before the commit,
// so a chunk that settled is still woken by a neighbour whose water reached it in the same step.
[UpdateInGroup<Simulation>]
public class WaterSleepSystem : EcsSystem
{
    [ForEach, Changed<FluidStep>]
    static void Sleep(Entity chunk, in FluidStep step, Mut<DynamicBuffer<NextFluid>> next, Mut<DynamicBuffer<Border>> border)
    {
        if (step.Result != StepResult.Settled) return;
        chunk.RemoveFlowing();
        ref var scratch = ref next.Write();
        scratch.Clear();
        scratch.TrimExcess();
        ref var edges = ref border.Write();
        edges.Clear();
        edges.TrimExcess();
    }
}
// Makes the step the water: [Changed<FluidStep>] visits the chunks that stepped. A step that moved water is copied into
// the Fluid buffer (a write, which replication sends), its summary becomes the chunk's FluidCount, and the chunks beside
// the edges whose water changed are woken.
[UpdateInGroup<Simulation>]
public class WaterCommitSystem : EcsSystem
{
    [ForEach, Changed<FluidStep>]
    void Commit(in Chunk key, in FluidStep step, in DynamicBuffer<NextFluid> next, Mut<DynamicBuffer<Fluid>> fluid,
        ref FluidCount count, ref Stats stats)
    {
        if (step.Result != StepResult.Moved) return;
        next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Fluid, NextFluid>(fluid.Write().AsSpan()));
        count = step.Water;
        stats.WaterCells += step.Filled;
        Terrain.Wake(World, key.Pos, step.Edges);
    }
}

// #endregion flow

// #region float
// Lifts whatever stands in water, players, zombies and items, and carries it with the current, as Minecraft does:
// along the slope of the water toward the lower water around it, and the weaker the lower the water: full next to the
// spring or under falling water, an eighth at the edge of a run. It visits the chunks, skips the dry ones (FluidCount)
// and, through the index of entities by chunk, finds the bodies in the others: the water of a body's block is the
// loop's own row, and its four neighbours are read only for the few bodies in water. Runs after the walk set the
// velocity and before physics moves the body.
[UpdateInGroup<Simulation>]
public class FloatSystem : EcsSystem
{
    [ForEach]
    void Float(in Chunk key, in FluidCount count, in DynamicBuffer<Fluid> fluid, in DynamicBuffer<Block> blocks, ref Stats stats)
    {
        if (count.Blocks == 0) return;                                 // dry: nothing floats here
        var levels = fluid.AsReadOnlySpan();
        var solid = blocks.AsReadOnlySpan();
        var near = new Nearby(World);                                   // the neighbours across the edge
        foreach (var body in World.InChunkIndex.Find(key.Pos))
        {
            var at = Terrain.Cell(body.Position.Value);
            if ((uint)at.Y >= Terrain.Height || Terrain.ChunkOf(at.X, at.Z) != key.Pos) continue;   // crossing: next tick
            int level = Math.Min((int)levels[Terrain.IndexOf(at.X, at.Y, at.Z)].Level, Fluid.Falling);
            if (level == 0) continue;

            var push = new Vector2(
                Drop(levels, solid, ref near, key.Pos, level, at with { X = at.X + 1 }) - Drop(levels, solid, ref near, key.Pos, level, at with { X = at.X - 1 }),
                Drop(levels, solid, ref near, key.Pos, level, at with { Z = at.Z + 1 }) - Drop(levels, solid, ref near, key.Pos, level, at with { Z = at.Z - 1 }));
            if (push != Vector2.Zero) push = Vector2.Normalize(push) * (Rules.CurrentSpeed * level / Fluid.Falling);
            ref var velocity = ref body.Velocity;
            var v = velocity.Value;
            velocity = new Velocity(new Vector3(v.X + push.X, Rules.FloatSpeed, v.Z + push.Y));
            stats.Drifts++;
        }
    }

    // How far the water drops from `level` to the block `to`, as Minecraft computes the flow a body is pushed along (a
    // spring or falling water counts as 8): none toward a solid block. A block of this chunk is read from its buffers,
    // one across the edge through Nearby.
    static int Drop(ReadOnlySpan<Fluid> levels, ReadOnlySpan<Block> solid, ref Nearby near, ChunkPos chunk, int level, BlockPos to)
    {
        bool here = Terrain.ChunkOf(to.X, to.Z) == chunk;
        int i = Terrain.IndexOf(to.X, to.Y, to.Z);
        if (Terrain.IsSolid(here ? solid[i] : near.Block(to))) return 0;
        return level - Math.Min(here ? levels[i].Level : near.Water(to), Fluid.Falling);
    }
}
// #endregion float
