using EasyEcs;

namespace VoxelServer;

// The water in one block of a chunk: a DynamicBuffer<Fluid> beside the blocks, one byte per block in the same layout,
// so a chunk's water is a grid that WaterFlowSystem steps 16 blocks at a time. Level 0 is no water, Spring (9) a
// spring that never dries, Falling (8) water with water above it, and 7 down to 1 water running sideways, one less
// per block from where it came, as in Minecraft (which counts the other way: 0 for a source, 7 at the edge).
public record struct Fluid(byte Level)
{
    public const byte Spring = 9, Falling = 8;

    // How hard water of `level` pushes or drops: a spring or falling water counts as 8.
    public static int Strength(int level) => Math.Min(level, Falling);
}

// The height map of a chunk, as Minecraft keeps one: a DynamicBuffer<Surface> of 256, the highest solid block of each
// column (x + 16 z). HeightMapSystem computes it when the blocks change; whatever asks whether the sky reaches a
// block, or where the ground of a column is, reads one byte instead of scanning the column.
public record struct Surface(byte Top);

// Where a chunk's water is: how many blocks hold some, and the lowest and highest layer holding any (Low > High when
// none). WaterSettleSystem writes it with each step; WaterFlowSystem reads it for the chunk and its neighbors instead
// of searching 8 KB, FloatSystem skips the dry chunks, and the report sums the column.
public record struct FluidCount(int Blocks, byte Low, byte High) : IComponent
{
    public static readonly FluidCount Dry = new(0, Terrain.Height, 0);
}

// What the water's last step did: nothing yet (a chunk just created), settled (no water moved), or moved; and the edges
// whose water changed. FluidStep is ITracked: WaterFlowSystem writes it with every step, and the systems after it
// (WaterSettleSystem, and WaterWakeSystem, which wakes the chunks across those edges) visit the chunks that stepped. The
// result is a component of its own, a column of one byte per chunk that WaterSettleSystem takes as a vector loop's mask.
public record struct FluidStep(TileFaces Edges) : IComponent, ITracked;

public record struct Stepped(StepResult Result) : IComponent;

// The water of the chunk after the step, as FluidCount counts it: kept beside the step until WaterSettleSystem makes it
// the chunk's FluidCount, since the chunks beside it read their FluidCount while the pass runs.
public record struct FluidAhead(FluidCount Count) : IComponent;

public enum StepResult : byte { None, Settled, Moved }

// A tag on a chunk whose water may still move: the water systems visit only these. A chunk loses it when a step changed
// nothing (WaterSettleSystem), and gets it back when a block changes in it or beside it, or the water at the edge beside
// it changed (WaterWakeSystem, the one system that wakes).
public struct Flowing : IComponent;
