namespace GameLoop;

/// <summary>
/// Stands in for the network: a thread of its own that plays <see cref="Count"/> clients. They join, steer, fire, and
/// leave and rejoin now and then, posting messages to the world's mailbox. Seeded, so every run sends the same stream.
/// </summary>
public sealed class Clients(Mailbox<ClientMessage> mailbox, int count)
{
    public int Count => count;

    public Thread Start(CancellationToken stop)
    {
        var thread = new Thread(() => Run(stop)) { Name = "clients", IsBackground = true };
        thread.Start();
        return thread;
    }

    private void Run(CancellationToken stop)
    {
        var random = new Random(42);
        for (int client = 0; client < count; client++) mailbox.Post(new ClientMessage(client, MessageKind.Join));
        while (!stop.WaitHandle.WaitOne(5))                           // a burst of messages every 5 ms
        {
            int client = random.Next(count);
            switch (random.Next(10))
            {
                case < 6: mailbox.Post(new ClientMessage(client, MessageKind.Move, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1)); break;
                case < 9: mailbox.Post(new ClientMessage(client, MessageKind.Fire)); break;
                default:
                    mailbox.Post(new ClientMessage(client, MessageKind.Leave));
                    mailbox.Post(new ClientMessage(client, MessageKind.Join));
                    break;
            }
        }
    }
}
