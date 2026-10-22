using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

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
