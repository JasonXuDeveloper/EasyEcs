using EasyEcs;
using GameLoop.Host;

namespace GameLoop;

// Turns every message waiting in the mailbox into a request entity.
// It is in InputGroup, so the host runs it on every wake-up, not only on ticks:
// input never waits for the next tick to be read.
// The mailbox is a readonly field, given by the host: it is the system's connection to the
// network, not world state, so it stays out of singletons (World.Save writes every singleton).
[UpdateInGroup<InputGroup>]
public sealed class ReceiveInputSystem(Mailbox<ClientMessage> inbox) : EcsSystem
{
    private readonly Mailbox<ClientMessage> _inbox = inbox;

    protected override void OnUpdate()
    {
        ref var stats = ref World.Stats;
        while (_inbox.TryTake(out var message))
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
