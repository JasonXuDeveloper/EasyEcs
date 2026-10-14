using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EasyEcs.Generator;

/// <summary>Array with value equality, so generator models cache correctly between edits.</summary>
internal readonly struct EquatableArray<T>(T[]? items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly T[] _items = items ?? Array.Empty<T>();

    public int Length => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(EquatableArray<T> other) => _items.SequenceEqual(other._items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode() => _items.Aggregate(17, (h, item) => h * 31 + (item?.GetHashCode() ?? 0));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
