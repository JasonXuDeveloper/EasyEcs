namespace GameLoop.Host;

/// <summary>
/// Plumbing, not ECS: stands in for the network so the sample runs on its own. A thread plays
/// <c>count</c> clients that join, steer, fire, and now and then leave and rejoin. The random
/// generator has a fixed seed, so every run sends the same messages.
/// </summary>
public sealed class FakeClients(Mailbox<ClientMessage> mailbox, int count)
{
    public Thread Start(CancellationToken stop)
    {
        var thread = new Thread(() => Run(stop))
        {
            Name = "clients",
            IsBackground = true,
        };
        thread.Start();
        return thread;
    }

    private void Run(CancellationToken stop)
    {
        var random = new Random(42);
        for (int client = 0; client < count; client++)
        {
            mailbox.Post(new ClientMessage(client, MessageKind.Join));
        }

        // One message (or two) every 5 ms until the host stops.
        while (!stop.WaitHandle.WaitOne(5))
        {
            int client = random.Next(count);
            int roll = random.Next(10);
            if (roll < 6)
            {
                float x = random.NextSingle() * 2 - 1;
                float y = random.NextSingle() * 2 - 1;
                mailbox.Post(new ClientMessage(client, MessageKind.Move, x, y));
            }
            else if (roll < 9)
            {
                mailbox.Post(new ClientMessage(client, MessageKind.Fire));
            }
            else
            {
                mailbox.Post(new ClientMessage(client, MessageKind.Leave));
                mailbox.Post(new ClientMessage(client, MessageKind.Join));
            }
        }
    }
}
