using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace GameLoop.Host;

/// <summary>
/// Plumbing, not ECS: the one thing two threads share. Network threads post messages;
/// the world's own thread takes them. EasyEcs has no cross-thread queue of its own, so the
/// application picks the bound and what happens when the queue is full: here the message
/// is refused and counted.
/// </summary>
public sealed class Mailbox<T>(int capacity)
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private int _count;
    private long _refused;

    /// <summary>Messages refused because the mailbox was full.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>
    /// Posts a message from any thread. Returns false, and counts it, when the mailbox is full.
    /// </summary>
    public bool Post(in T message)
    {
        if (Interlocked.Increment(ref _count) > capacity)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _refused);
            return false;
        }

        _queue.Enqueue(message);
        _wake.Set();
        return true;
    }

    /// <summary>Takes the next message. Called on the world's thread only.</summary>
    public bool TryTake([MaybeNullWhen(false)] out T message)
    {
        if (!_queue.TryDequeue(out message))
        {
            return false;
        }

        Interlocked.Decrement(ref _count);
        return true;
    }

    /// <summary>
    /// Blocks the world's thread until a message arrives or <paramref name="timeout"/> passes.
    /// The wait is rounded up to a whole millisecond: a shorter one would return at once
    /// and spin.
    /// </summary>
    public void Wait(TimeSpan timeout)
    {
        int milliseconds = (int)Math.Ceiling(timeout.TotalMilliseconds);
        _wake.WaitOne(milliseconds);
    }
}
