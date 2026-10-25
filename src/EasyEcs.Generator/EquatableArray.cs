using System;
using System.Collections;
using System.Collections.Generic;

namespace EasyEcs.Generator;

/// <summary>Array with value equality, so generator models cache correctly between edits. default == empty.</summary>
internal readonly struct EquatableArray<T>(T[]? items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly T[]? _items = items;

    private T[] Items => _items ?? Array.Empty<T>();

    public int Length => Items.Length;

    public T this[int index] => Items[index];

    // Loops, not LINQ: the driver compares models on every edit for every step.
    public bool Equals(EquatableArray<T> other)
    {
        T[] a = Items, b = other.Items;
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < a.Length; i++)
            if (!comparer.Equals(a[i], b[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (var item in Items) hash = hash * 31 + (item?.GetHashCode() ?? 0);
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
