using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EasyEcs.Core;

/// <summary>
/// Represents a pooled collection. Use this for iterating (i.e. foreach) over a collection. Remember to dispose it.
/// </summary>
/// <typeparam name="TCollection"></typeparam>
/// <typeparam name="TElement"></typeparam>
public class PooledCollection<TCollection, TElement> : ICollection<TElement>, IDisposable
    where TCollection : ICollection<TElement>, new()
{
    internal TCollection Collection;
    private static readonly ConcurrentQueue<PooledCollection<TCollection, TElement>> Pool = new();

    /// <summary>
    /// Create a pooled collection.
    /// </summary>
    /// <returns></returns>
    public static PooledCollection<TCollection, TElement> Create()
    {
        if (Pool.TryDequeue(out var pooled))
        {
            return pooled;
        }

        return new PooledCollection<TCollection, TElement>();
    }

    public TElement this[int index] => Collection.ElementAt(index);

    private PooledCollection()
    {
        Collection = new TCollection();
    }

    public IEnumerator<TElement> GetEnumerator()
    {
        return Collection.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Dispose()
    {
        Pool.Enqueue(this);
    }

    public void Add(TElement item)
    {
        Collection.Add(item);
    }

    public void Clear()
    {
        Collection.Clear();
    }

    public bool Contains(TElement item)
    {
        return Collection.Contains(item);
    }

    public void CopyTo(TElement[] array, int arrayIndex)
    {
        Collection.CopyTo(array, arrayIndex);
    }

    public bool Remove(TElement item)
    {
        return Collection.Remove(item);
    }

    public int Count => Collection.Count;
    public bool IsReadOnly => Collection.IsReadOnly;
}