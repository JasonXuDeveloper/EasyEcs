using EasyEcs;
using GameLoop.Host;

namespace GameLoop;

// Turns every message waiting in the inbox into a request entity.
// It is in InputGroup, so the host runs it on every wake-up, not only on ticks:
// input never waits for the next tick to be read.
[UpdateInGroup<InputGroup>]
public sealed class ReceiveInputSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        Mailbox<ClientMessage> inbox = World.Inbox.Messages;
        ref var stats = ref World.Stats;
        while (inbox.TryTake(out var message))
        {
            stats.Received++;
            switch (message.Kind)
            {
                case MessageKind.Join:
                    World.CreateEntity(new JoinRequest(message.Client));
                    break;
                case MessageKind.Move:
                    World.CreateEntity(new MoveRequest(message.Client, message.X, message.Y));
                    break;
                case MessageKind.Fire:
                    World.CreateEntity(new FireRequest(message.Client));
                    break;
                case MessageKind.Leave:
                    World.CreateEntity(new LeaveRequest(message.Client));
                    break;
            }
        }
    }
}
