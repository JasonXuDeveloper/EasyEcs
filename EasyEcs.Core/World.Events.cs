using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

// Events: synchronous, typed, in-world publish/subscribe for decoupling modules (e.g. Died -> loot, quests,
// achievements) without systems referencing each other. Publishing is a loop of interface calls over an immutable
// handler array: no allocation, no boxing, no queue.
public sealed partial class World
{
    // Indexed by EventType<T>.Index; each slot is an EventChannel<T>.
    private EventChannel[] _channels = [];

    /// <summary>
    /// Deliver <paramref name="e"/> to every handler of <typeparamref name="T"/> in subscription order, synchronously
    /// on the calling thread. O(handlers). Handlers may publish further events and make structural changes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish<T>(in T e) where T : struct, IEvent
    {
        int index = EventType<T>.Index;
        var channels = _channels;
        if ((uint)index >= (uint)channels.Length || channels[index] is not EventChannel<T> channel) return;

        var handlers = channel.Handlers; // snapshot: (un)subscribing during delivery affects the next publish
        for (int i = 0; i < handlers.Length; i++)
            handlers[i].Handle(in e);
    }

    /// <summary>Subscribe <paramref name="handler"/> to <typeparamref name="T"/>. O(handlers). Thread-safe.</summary>
    public void Subscribe<T>(IEventHandler<T> handler) where T : struct, IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (StructuralLock)
        {
            int index = EventType<T>.Index;
            if (index >= _channels.Length)
                Array.Resize(ref _channels, Math.Max(index + 1, _channels.Length * 2));
            var channel = _channels[index] as EventChannel<T> ?? new EventChannel<T>();
            channel.Handlers = [.. channel.Handlers, handler];
            _channels[index] = channel;
        }
    }

    /// <summary>Unsubscribe <paramref name="handler"/> from <typeparamref name="T"/>. O(handlers). Thread-safe.</summary>
    public bool Unsubscribe<T>(IEventHandler<T> handler) where T : struct, IEvent
    {
        lock (StructuralLock)
        {
            int index = EventType<T>.Index;
            return index < _channels.Length && _channels[index] is { } channel && channel.Remove(handler);
        }
    }

    /// <summary>Remove <paramref name="handler"/> from every channel (system removal). O(channels × handlers).</summary>
    internal void UnsubscribeAll(object handler)
    {
        lock (StructuralLock)
        {
            foreach (var channel in _channels)
                channel?.Remove(handler);
        }
    }

    private abstract class EventChannel
    {
        public abstract bool Remove(object handler);
    }

    private sealed class EventChannel<T> : EventChannel where T : struct, IEvent
    {
        public IEventHandler<T>[] Handlers = [];

        public override bool Remove(object handler)
        {
            int index = Array.IndexOf(Handlers, handler);
            if (index < 0) return false;
            Handlers = [.. Handlers.AsSpan(0, index), .. Handlers.AsSpan(index + 1)];
            return true;
        }
    }
}
