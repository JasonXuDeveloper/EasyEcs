using EasyEcs;

namespace GameLoop;

// One system per kind of request. Each finds the player by client id, acts,
// and destroys the request entity: a request lives for one tick.

// Removes a player that left. It runs before JoinSystem, so a client that
// leaves and joins again in the same tick ends up joined.
public sealed class LeaveSystem : EcsSystem
{
    [ForEach]
    static void Leave(Entity request, in LeaveRequest leave, ref Stats stats, World world)
    {
        if (world.ClientIndex.TryFind(leave.Client, out var player))
        {
            player.Destroy();
            stats.Left++;
        }

        request.Destroy();
    }
}

// Creates a player from the prefab. A second join of the same client is ignored.
public sealed class JoinSystem : EcsSystem
{
    [ForEach]
    static void Join(
        Entity request,
        in JoinRequest join,
        in Prefabs prefabs,
        ref Stats stats,
        World world)
    {
        bool alreadyJoined = world.ClientIndex.TryFind(join.Client, out _);
        if (!alreadyJoined)
        {
            // The prefab's components plus the client id, with the players spread out along
            // the X axis: given values land in the final table in one row copy.
            world.Instantiate(
                prefabs.Player,
                new Client(join.Client),
                new Position(join.Client * 10, 0));
            stats.Joined++;
        }

        request.Destroy();
    }
}

// Sets the velocity a client asked for.
public sealed class MoveSystem : EcsSystem
{
    [ForEach]
    static void Steer(Entity request, in MoveRequest move, World world)
    {
        if (world.ClientIndex.TryFind(move.Client, out var player))
        {
            player.Velocity = new Velocity(move.X, move.Y);
        }

        request.Destroy();
    }
}

// Fires a bullet from the player's position, in the direction the player moves.
public sealed class FireSystem : EcsSystem
{
    const float BulletSpeed = 4;

    [ForEach]
    static void Fire(
        Entity request,
        in FireRequest fire,
        in Prefabs prefabs,
        ref Stats stats,
        World world)
    {
        if (world.ClientIndex.TryFind(fire.Client, out var player))
        {
            Position from = player.Position;
            Velocity aim = player.Velocity;
            world.Instantiate(
                prefabs.Bullet,
                from,
                new Velocity(aim.X * BulletSpeed, aim.Y * BulletSpeed));
            stats.Fired++;
        }

        request.Destroy();
    }
}
