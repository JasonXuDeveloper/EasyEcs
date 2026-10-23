using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// Lifts whatever stands in water, players, zombies and items, and carries it with the current, as Minecraft does:
// along the slope of the water toward the lower water around it, and the weaker the lower the water: full next to the
// spring or under falling water, an eighth at the edge of a run. It visits the chunks, skips the dry ones (FluidCount)
// and, through the index of entities by chunk, finds the bodies in the others: the water of a body's block is the
// loop's own row, and its four neighbors are read only for the few bodies in water. Runs after the walk set the
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
        var near = new Nearby(World);                                   // the neighbors across the edge
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
