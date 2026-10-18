using EasyEcs;

namespace GameLoop.Host;

/// <summary>
/// Plumbing, not ECS: what a real server does with the outbox after replication. Here the bytes
/// are only counted, then the outbox is cleared for the next round.
/// </summary>
public static class Network
{
    public static void SendOutbox(World world)
    {
        ref var outbox = ref world.Outbox;
        ref var stats = ref world.Stats;

        // A real server writes outbox.Bytes.WrittenSpan to its client sockets here.
        stats.SentBytes += outbox.Bytes.WrittenCount;
        stats.Replicated += outbox.Updates;

        outbox.Bytes.ResetWrittenCount();
        outbox.Updates = 0;
    }
}
