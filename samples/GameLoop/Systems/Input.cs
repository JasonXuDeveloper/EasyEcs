using EasyEcs;
using GameLoop.Host;

namespace GameLoop;

// Turns every message waiting in the mailbox into a request entity.
// It is in InputGroup, so the host runs it on every wake-up, not only on ticks:
// input never waits for the next tick to be read.
// The mailbox is the host's, in singleton Inbox: [NotSaved], since it is the world's connection
// to the network, not world state (World.Save writes every other singleton).
[EcsSystem, UpdateInGroup<InputGroup>]
public ref struct ReceiveInputSystem
{
    static void OnUpdate(World world)
    {
        ref var stats = ref world.Stats;
        var inbox = world.Inbox.Messages;
        while (inbox.TryTake(out var message))
        {
            stats.Received++;
            switch (message.Kind)
            {
                case MessageKind.Join:
                    world.CreateEntity(new JoinRequest(message.Client));
                    break;
                case MessageKind.Move:
                    world.CreateEntity(new MoveRequest(message.Client, message.X, message.Y));
                    break;
                case MessageKind.Fire:
                    world.CreateEntity(new FireRequest(message.Client));
                    break;
                case MessageKind.Leave:
                    world.CreateEntity(new LeaveRequest(message.Client));
                    break;
            }
        }
    }
}
