using EasyEcs;

namespace VoxelServer;

// The water in one block of a chunk: a DynamicBuffer<Fluid> beside the blocks, one byte per block in the same layout,
// so a chunk's water is a grid that WaterFlowSystem steps 16 blocks at a time. Level 0 is no water, Spring (9) a
// spring that never dries, Falling (8) water with water above it, and 7 down to 1 water running sideways, one less
// per block from where it came, as in Minecraft (which counts the other way: 0 for a source, 7 at the edge).
public record struct Fluid(byte Level)
{
    public const byte Spring = 9, Falling = 8;
}

// The water of a chunk after the next step, in the layout of Fluid: WaterFlowSystem writes it, WaterCommitSystem copies
// it into the Fluid buffer once every chunk has stepped, so each chunk steps from its neighbours' water before the
// step, as a GPU ping-pongs two buffers. A flowing chunk keeps it from one step to the next, never cleared (each step
// writes every layer the last one wrote water on); a chunk that settles gives it back to the pool (WaterSleepSystem).
public record struct NextFluid(byte Level);

// The height map of a chunk, as Minecraft keeps one: a DynamicBuffer<Surface> of 256, the highest solid block of each
// column (x + 16 z). HeightMapSystem computes it when the blocks change; whatever asks whether the sky reaches a
// block, or where the ground of a column is, reads one byte instead of scanning the column.
public record struct Surface(byte Top);

// Where a chunk's water is: how many blocks hold some, and the lowest and highest layer holding any (Low > High when
// none). WaterCommitSystem writes it with each step; WaterBorderSystem reads it for the chunk and its neighbours instead
// of searching 8 KB, FloatSystem skips the dry chunks, and the report sums the column.
public record struct FluidCount(int Blocks, byte Low, byte High) : IComponent
{
    public static readonly FluidCount Dry = new(0, Terrain.Height, 0);
}

// The edges of the four chunks around, as the water step sees them: a DynamicBuffer<Border> of the water, then the
// blocks, of the row (north, south) or column (west, east) of each neighbour that touches this chunk, layer by layer
// (Terrain.BorderOf). Zero where a neighbour is not loaded: no water, no block. WaterBorderSystem gathers it before each
// step, so the step reads nothing but its own chunk's components.
public record struct Border(byte Value);

// The layers the next step shades, From to To: those holding water here or in the chunks around, and one below
// (From > To when there is none). It samples one more layer each way, Bottom to Top. WaterBorderSystem writes it with
// the border.
public record struct Reach(int From, int To) : IComponent
{
    public readonly int Bottom => From - 1;
    public readonly int Top => Math.Min(To + 1, Terrain.Height - 1);
}

// What the step in NextFluid did: nothing yet (a chunk just created), settled (no water moved), or moved; then its water
// (as FluidCount counts it), the blocks it filled, and the edges whose water changed (one bit per side, Terrain.North
// ...). ITracked: WaterFlowSystem writes it with every step, and WaterSleepSystem and WaterCommitSystem visit the
// chunks that stepped.
public record struct FluidStep(StepResult Result, FluidCount Water, int Filled, byte Edges) : IComponent, ITracked;

public enum StepResult : byte { None, Settled, Moved }

// A tag on a chunk whose water may still move: the water systems visit only these. A chunk loses it when a step changed
// nothing, and gets it back when a block changes in it or beside it, or the water at the edge beside it changed.
public struct Flowing : IComponent;
