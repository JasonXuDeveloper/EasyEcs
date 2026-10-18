using System.Buffers.Binary;
using EasyEcs;

namespace GameLoop;

// Input: drains the mailbox into request entities, on every wake-up of the owner thread.
[UpdateInGroup<InputGroup>]
public sealed class ReceiveInput(Mailbox<ClientMessage> inbox) : EcsSystem
{
    private readonly Mailbox<ClientMessage> _inbox = inbox;   // filled by the network threads

    protected override void OnUpdate()
    {
        ref var stats = ref World.Stats;
        while (_inbox.TryTake(out var message))
        {
            stats.Received++;
            switch (message.Kind)
            {
                case MessageKind.Join: World.CreateEntity(new JoinRequest(message.Client)); break;
                case MessageKind.Move: World.CreateEntity(new MoveIntent(message.Client, message.X, message.Y)); break;
                case MessageKind.Fire: World.CreateEntity(new FireIntent(message.Client)); break;
                case MessageKind.Leave: World.CreateEntity(new LeaveRequest(message.Client)); break;
            }
        }
    }
}

// Simulation, in the order the host registers them: requests, then movement, then lifetimes, then the clock.
public sealed class JoinSystem : EcsSystem
{
    [ForEach]
    static void Join(Entity request, in JoinRequest join, in Prefabs prefabs, ref Stats stats, World world)
    {
        if (!world.ClientIndex.TryFind(join.Client, out _))           // a repeated join is ignored
        {
            world.Instantiate(prefabs.Player).SetClient(join.Client).SetPosition(join.Client * 10, 0);
            stats.Joined++;
        }

        request.Destroy();
    }
}

public sealed class MoveIntentSystem : EcsSystem
{
    [ForEach]
    static void Steer(Entity request, in MoveIntent move, World world)
    {
        if (world.ClientIndex.TryFind(move.Client, out var player)) player.Velocity = new Velocity(move.X, move.Y);
        request.Destroy();
    }
}

public sealed class FireSystem : EcsSystem
{
    [ForEach]
    static void Fire(Entity request, in FireIntent fire, in Prefabs prefabs, ref Stats stats, World world)
    {
        if (world.ClientIndex.TryFind(fire.Client, out var player))
        {
            ref readonly var at = ref player.Position;
            ref readonly var aim = ref player.Velocity;
            world.Instantiate(prefabs.Bullet).SetPosition(at.X, at.Y).SetVelocity(aim.X * 4, aim.Y * 4);
            stats.Fired++;
        }

        request.Destroy();
    }
}

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

public sealed class MovementSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v, in Simulation sim) =>
        p = new Position(p.X + v.X * sim.DeltaTime, p.Y + v.Y * sim.DeltaTime);
}

public sealed class LifetimeSystem : EcsSystem
{
    [ForEach]
    static void Age(Entity bullet, ref Lifetime life)
    {
        if (--life.Ticks <= 0) bullet.Destroy();                     // safe in the middle of the pass
    }
}

public sealed class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Simulation.Tick++;
}

// Replication: every position written since this system last ran, as 12 bytes (id, x, y) into the outbox. Nothing is
// visited for entities that did not move; the host sends the bytes and clears the outbox.
[UpdateInGroup<ReplicationGroup>]
public sealed class ReplicationSystem : EcsSystem
{
    [ForEach, Changed<Position>]
    static void Send(Entity entity, in Position p, ref Outbox outbox)
    {
        var bytes = outbox.Bytes.GetSpan(12);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, entity.Id.Index);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], p.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], p.Y);
        outbox.Bytes.Advance(12);
        outbox.Updates++;
    }
}
