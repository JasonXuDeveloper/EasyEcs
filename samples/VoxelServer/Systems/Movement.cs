using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// Turns a player's input into a horizontal velocity; gravity keeps the vertical one. Each row depends on nothing
// but itself, so the rows are split across threads (Parallel = true).
public class PlayerMoveSystem : EcsSystem
{
    [ForEach(Parallel = true)]
    static void Walk(in Input input, ref Velocity velocity)
    {
        var v = velocity.Value;
        velocity = new Velocity(new Vector3(input.Walk.X * Rules.WalkSpeed, v.Y, input.Walk.Y * Rules.WalkSpeed));
    }
}

// Moves everything with a velocity through the blocks. Walking onto a block one high steps up onto it (from the
// ground, with room for the head); a higher wall stops the walk. Then gravity: fall onto the block below, or stop at
// the one above the head. The position is a Mut<Position>, written only when it changes, so a player standing still
// is not sent again.
public class PhysicsSystem : EcsSystem
{
    [ForEach]
    void Move(Mut<Position> position, ref Velocity velocity, ref Body body)
    {
        var p = position.Value.Value;
        var v = velocity.Value;
        v.Y = MathF.Max(v.Y - Rules.Gravity, -3.9f);

        float x = p.X + v.X, y = p.Y, z = p.Z + v.Z;
        bool blocked = false;
        if (v.X != 0 || v.Z != 0)
        {
            bool feet = Terrain.IsSolid(World, x, y, z), head = Terrain.IsSolid(World, x, y + 1, z);
            if (feet && !head && body.OnGround && !Terrain.IsSolid(World, x, y + 2, z)) y = MathF.Floor(y) + 1;
            else if (feet || head)
            {
                (x, z) = (p.X, p.Z);
                v.X = v.Z = 0;
                blocked = true;
            }
        }

        y += v.Y;
        bool onGround = false;
        if (v.Y <= 0 && Terrain.IsSolid(World, x, y, z))
        {
            y = MathF.Floor(y) + 1;
            v.Y = 0;
            onGround = true;
        }
        else if (v.Y > 0 && Terrain.IsSolid(World, x, y + 1.8f, z))
        {
            y = p.Y;
            v.Y = 0;
        }

        position.SetIfChanged(new Position(new Vector3(x, y, z)));
        velocity = new Velocity(v);
        body = new Body(onGround, blocked);
    }
}
