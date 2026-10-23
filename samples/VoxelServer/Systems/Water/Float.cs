using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// Lifts whatever stands in water, players, zombies and items, and carries it with the current, as Minecraft does:
// along the slope of the water toward the lower water around it, and the weaker the lower the water: full next to the
// spring or under falling water, an eighth at the edge of a run. It visits the bodies: the chunk a body is in (InChunk)
// is found by its key, a dry one (FluidCount) skipped after that one read, and the four blocks around are read only for
// the few bodies in water. Each body writes its own velocity and reads the chunks through lookups, so the bodies are
// split across threads, and the bodies carried are a reduction (Carried). Runs after the walk set the velocity and
// before physics moves the body.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct FloatSystem
{
    [ForEach(Parallel = true)]
    static void Float(in Position position, in InChunk at, ref Velocity velocity, Lookup<Chunk> chunks, Lookup<FluidCount> counts,
        Lookup<DynamicBuffer<Fluid>> water, Lookup<DynamicBuffer<Block>> blocks, ref Reduce<Carried> carried)
    {
        if (!chunks.TryFind(at.Pos, out var chunk) || counts.Read(chunk.Id).Blocks == 0) return;   // dry: nothing floats here
        var cell = Terrain.Cell(position.Value);
        if ((uint)cell.Y >= Terrain.Height || Terrain.ChunkOf(cell.X, cell.Z) != at.Pos) return;    // crossing: next tick
        var near = new Nearby(chunks, blocks, water);
        int level = Fluid.Strength(near.Water(cell));
        if (level == 0) return;

        var push = new Vector2(
            Drop(ref near, level, cell with { X = cell.X + 1 }) - Drop(ref near, level, cell with { X = cell.X - 1 }),
            Drop(ref near, level, cell with { Z = cell.Z + 1 }) - Drop(ref near, level, cell with { Z = cell.Z - 1 }));
        if (push != Vector2.Zero) push = Vector2.Normalize(push) * (Rules.CurrentSpeed * level / Fluid.Falling);
        var v = velocity.Value;
        velocity = new Velocity(new Vector3(v.X + push.X, Rules.FloatSpeed, v.Z + push.Y));
        carried.Value.Bodies++;
    }

    // How far the water drops from `level` to the block `to`, as Minecraft computes the flow a body is pushed along: none
    // toward a solid block.
    static int Drop(ref Nearby near, int level, BlockPos to) => near.IsSolid(to) ? 0 : level - Fluid.Strength(near.Water(to));
}
