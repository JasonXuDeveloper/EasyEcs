using System.Collections.Concurrent;

namespace EasyEcs.Core;

internal static class Pool<T> where T : new()
{
    private static readonly ConcurrentQueue<T> Queue = new();

    public static T Rent()
    {
        if (Queue.TryDequeue(out var item))
        {
            return item;
        }

        return new T();
    }

    public static void Return(T item)
    {
        Queue.Enqueue(item);
    }
}