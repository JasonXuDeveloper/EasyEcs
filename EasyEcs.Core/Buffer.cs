using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// A list of <typeparamref name="T"/> per entity, held as a component: an inventory, a path, the hits of this tick.
/// Writes go through a <c>ref</c> to the component (<c>ref</c> parameters of <c>[ForEach]</c> methods and jobs,
/// <see cref="Entity.Get{T}"/>), so they are tracked like any write of an <see cref="ITrackChanges"/> component and
/// <c>[Changed&lt;Buffer&lt;T&gt;&gt;]</c> selects the entities whose list changed. Through <c>in</c> or
/// <see cref="Entity.Read{T}"/> the list is read-only: the mutating methods (<see cref="BufferExtensions"/>) take
/// <c>ref this</c>, so a write through a read-only reference does not compile. Snapshots copy the elements
/// (<see cref="World.Capture"/>); a moved row keeps its list. Adding within the capacity allocates nothing.
/// </summary>
/// <remarks>
/// A <c>new Buffer&lt;T&gt;()</c> holds no list: each entity's list is made by its first write (reserve room with
/// <see cref="BufferExtensions.EnsureCapacity{T}"/>), so one value can create many entities. The list lives in an
/// object the component refers to, and a copy of the component would refer to the same list: the analyzer rejects
/// copies (EECS012), reach the list through the entity.
/// </remarks>
public struct Buffer<T> : IComponent, ITrackChanges, IBuffer
{
    // The only field: snapshots read a column of buffers as a column of BufferStore references (ComponentInfo).
    internal BufferStore<T> Store;

    /// <summary>The number of elements.</summary>
    public readonly int Count => Store?.Count ?? 0;

    /// <summary>The number of elements the list holds before it grows.</summary>
    public readonly int Capacity => Store?.Items.Length ?? 0;

    /// <summary>The element at <paramref name="index"/>, read-only (write through <see cref="BufferExtensions.AsSpan{T}"/>).</summary>
    public readonly ref readonly T this[int index]
    {
        get
        {
            var store = Store;
            if (store == null || (uint)index >= (uint)store.Count) ThrowIndex();
            return ref store.Items[index];
        }
    }

    /// <summary>The elements, read-only.</summary>
    public readonly ReadOnlySpan<T> AsReadOnlySpan() => Store is { } store ? new ReadOnlySpan<T>(store.Items, 0, store.Count) : default;

    /// <summary>Enumerates the elements (read-only, no allocation).</summary>
    public readonly ReadOnlySpan<T>.Enumerator GetEnumerator() => AsReadOnlySpan().GetEnumerator();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowIndex() => throw new ArgumentOutOfRangeException("index");
}

/// <summary>
/// The writes of a <see cref="Buffer{T}"/>. Each takes the buffer by <c>ref</c>: it must be reached through a write
/// access (a tracked change), never through <c>in</c>.
/// </summary>
public static class BufferExtensions
{
    /// <summary>The elements, writable.</summary>
    public static Span<T> AsSpan<T>(this ref Buffer<T> buffer) =>
        buffer.Store is { } store ? new Span<T>(store.Items, 0, store.Count) : default;

    /// <summary>Append <paramref name="item"/>. O(1) amortized; allocates only when the list grows.</summary>
    public static void Add<T>(this ref Buffer<T> buffer, in T item)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if (store.Count == store.Items.Length) store.Grow(store.Count + 1);
        store.Items[store.Count++] = item;
    }

    /// <summary>Append <paramref name="items"/>. O(items).</summary>
    public static void AddRange<T>(this ref Buffer<T> buffer, ReadOnlySpan<T> items)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if (store.Count + items.Length > store.Items.Length) store.Grow(store.Count + items.Length);
        items.CopyTo(store.Items.AsSpan(store.Count));
        store.Count += items.Length;
    }

    /// <summary>Insert <paramref name="item"/> at <paramref name="index"/>, keeping the order. O(count).</summary>
    public static void Insert<T>(this ref Buffer<T> buffer, int index, in T item)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if ((uint)index > (uint)store.Count) Buffer<T>.ThrowIndex();
        if (store.Count == store.Items.Length) store.Grow(store.Count + 1);
        Array.Copy(store.Items, index, store.Items, index + 1, store.Count - index);
        store.Items[index] = item;
        store.Count++;
    }

    /// <summary>Remove the element at <paramref name="index"/>, keeping the order. O(count).</summary>
    public static void RemoveAt<T>(this ref Buffer<T> buffer, int index)
    {
        var store = buffer.Store;
        if (store == null || (uint)index >= (uint)store.Count) Buffer<T>.ThrowIndex();
        int count = --store.Count;
        Array.Copy(store.Items, index + 1, store.Items, index, count - index);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) store.Items[count] = default;
    }

    /// <summary>Remove the element at <paramref name="index"/> by moving the last one into its place. O(1).</summary>
    public static void RemoveAtSwapBack<T>(this ref Buffer<T> buffer, int index)
    {
        var store = buffer.Store;
        if (store == null || (uint)index >= (uint)store.Count) Buffer<T>.ThrowIndex();
        int count = --store.Count;
        store.Items[index] = store.Items[count];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) store.Items[count] = default;
    }

    /// <summary>Remove every element; the capacity stays. O(1), or O(count) for elements holding references.</summary>
    public static void Clear<T>(this ref Buffer<T> buffer)
    {
        if (buffer.Store is not { } store) return;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(store.Items, 0, store.Count);
        store.Count = 0;
    }

    /// <summary>Make room for at least <paramref name="capacity"/> elements.</summary>
    public static void EnsureCapacity<T>(this ref Buffer<T> buffer, int capacity)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if (capacity > store.Items.Length) store.Resize(capacity);
    }
}

/// <summary>Marks <see cref="Buffer{T}"/> for the column operations that copy its elements (snapshots).</summary>
internal interface IBuffer;

/// <summary>The list of a <see cref="Buffer{T}"/>, typed away so a column of buffers can be copied without its <c>T</c>.</summary>
internal abstract class BufferStore
{
    /// <summary>A store with copies of the elements.</summary>
    public abstract BufferStore Clone();
}

internal sealed class BufferStore<T>(int capacity) : BufferStore
{
    public T[] Items = capacity == 0 ? [] : new T[capacity];
    public int Count;

    /// <summary>Grow to hold at least <paramref name="needed"/> elements (doubling, at least 4).</summary>
    public void Grow(int needed) => Resize(Math.Max(needed, Math.Max(4, Items.Length * 2)));

    /// <summary>Move the elements to an array of <paramref name="capacity"/> (at least <see cref="Count"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Resize(int capacity)
    {
        var grown = new T[capacity];
        Array.Copy(Items, grown, Count);
        Items = grown;
    }

    public override BufferStore Clone() => new BufferStore<T>(0) { Items = Items.AsSpan(0, Count).ToArray(), Count = Count };
}
