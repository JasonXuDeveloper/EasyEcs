using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// #region walk
// Turns a player's input into a horizontal velocity; gravity keeps the vertical one. Each row depends on nothing
// but itself, so the rows may be split across threads (Parallel = true). The runner times the split against one
// thread and keeps it only while the helpers make the pass faster: a row this cheap pays from tens of thousands of
// players, and below that the pass stays on the calling thread at no cost.
[UpdateInGroup<Simulation>]
public class PlayerMoveSystem : EcsSystem
{
    [ForEach(Parallel = true)]
    static void Walk(in Input input, ref Velocity velocity)
    {
        var v = velocity.Value;
        velocity = new Velocity(new Vector3(input.Walk.X * Rules.WalkSpeed, v.Y, input.Walk.Y * Rules.WalkSpeed));
    }
}
// #endregion walk

// #region physics
// Moves everything with a velocity through the blocks. Walking onto a block one high steps up onto it (from the
// ground, with room for the head: the auto-jump of Bedrock Edition); a higher wall stops the walk. Then gravity, one
// block at a time so a fast fall cannot pass through the ground: land on the block below, or stop at the one above the
// head. A body at rest (on the ground, not walking, ground still under it) is left as it is: most items and idle players
// cost one block lookup. The position is a Mut<Position>, written only when it changes, so a body standing still is
// not sent again. It stays on one thread: the block lookups go through World (the chunk index), and a parallel method may not hold the
// World (EECS010).
[UpdateInGroup<Simulation>]
public class PhysicsSystem : EcsSystem
{
    [ForEach]
    void Move(Mut<Position> position, ref Velocity velocity, ref Body body)
    {
        var p = position.Value.Value;
        var v = velocity.Value;
        var near = new Nearby(World);
        if (body.OnGround && v == Vector3.Zero && near.IsSolid(p.X, p.Y - 1, p.Z)) return;   // at rest
        v.Y = MathF.Max(v.Y - Rules.Gravity, -3.9f);

        float x = p.X + v.X, y = p.Y, z = p.Z + v.Z;
        bool blocked = false;
        if (v.X != 0 || v.Z != 0)
        {
            bool feet = near.IsSolid(x, y, z), head = near.IsSolid(x, y + 1, z);
            if (feet && !head && body.OnGround && !near.IsSolid(x, y + 2, z)) y = MathF.Floor(y) + 1;
            else if (feet || head)
            {
                (x, z) = (p.X, p.Z);
                v.X = v.Z = 0;
                blocked = true;
            }
        }

        bool onGround = false;
        int steps = (int)MathF.Ceiling(MathF.Abs(v.Y));
        float step = steps == 0 ? 0 : v.Y / steps;
        for (int i = 0; i < steps; i++)
        {
            float next = y + step;
            if (step < 0 && near.IsSolid(x, next, z))
            {
                y = MathF.Floor(next) + 1;
                v.Y = 0;
                onGround = true;
                break;
            }
            if (step > 0 && near.IsSolid(x, next + 1.8f, z))
            {
                v.Y = 0;
                break;
            }
            y = next;
        }

        position.SetIfChanged(new Position(new Vector3(x, y, z)));
        velocity = new Velocity(v);
        body = new Body(onGround, blocked);
    }
}
// #endregion physics

// #region grid
// Keeps the InChunk of everything that moves on the chunk it is in: players, zombies, items. [Changed<Position>] visits
// only what moved this tick (physics writes a position only when it changes), and SetInChunkIfChanged writes only when
// the entity crossed into another chunk, so the index is touched a few times a second, not every tick.
[UpdateInGroup<Simulation>]
public class GridSystem : EcsSystem
{
    [ForEach, With<InChunk>, Changed<Position>]
    static void Track(Entity entity, in Position position)
    {
        entity.SetInChunkIfChanged(Terrain.ChunkOf(position.Value));
    }
}
// #endregion grid
