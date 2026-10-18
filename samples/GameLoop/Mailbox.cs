using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace GameLoop;

/// <summary>
/// A bounded queue from any thread to the world's owner thread. Producers post and wake the owner; a system on the owner
/// thread drains it into data. EasyEcs has no cross-thread queue of its own: the application picks the bound and what
/// happens when it is full (here the message is refused and counted).
/// </summary>
public sealed class Mailbox<T>(int capacity)
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private int _count;
    private long _refused;

    /// <summary>Messages refused because the mailbox was full.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Post a message from any thread. False (and counted) when the mailbox is full.</summary>
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

    /// <summary>Take the next message. Owner thread.</summary>
    public bool TryTake([MaybeNullWhen(false)] out T message)
    {
        if (!_queue.TryDequeue(out message)) return false;
        Interlocked.Decrement(ref _count);
        return true;
    }

    /// <summary>
    /// Block the owner thread until a message arrives or <paramref name="timeout"/> passes, rounded up to the next
    /// millisecond (a shorter wait would return at once and spin).
    /// </summary>
    public void Wait(TimeSpan timeout) => _wake.WaitOne((int)Math.Ceiling(timeout.TotalMilliseconds));
}
