using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core;

namespace EasyEcs;

/// <summary>
/// A list of <typeparamref name="T"/> per entity, held as a component: an inventory, a path, the hits of this tick.
/// Writes go through a <c>ref</c> to the component (<c>ref</c> parameters of <c>[ForEach]</c> methods and jobs,
/// <see cref="Entity.Get{T}"/>), so they are tracked like any write of an <see cref="ITrackChanges"/> component and
/// <c>[Changed&lt;DynamicBuffer&lt;T&gt;&gt;]</c> selects the entities whose list changed. Through <c>in</c> or
/// <see cref="Entity.Read{T}"/> the list is read-only: the mutating methods
/// (<see cref="DynamicBufferExtensions"/>) take <c>ref this</c>, so a write through a read-only reference does not
/// compile. Snapshots copy the elements (<see cref="World.Capture()"/>); a moved row keeps its list. Adding within the
/// capacity allocates nothing.
/// </summary>
/// <remarks>
/// A <c>new DynamicBuffer&lt;T&gt;()</c> holds no list: each entity's list is made by its first write (reserve room
/// with <see cref="DynamicBufferExtensions.EnsureCapacity{T}"/>), so one value can create many entities. The list
/// lives in an object the component refers to, and a copy of the component would refer to the same list: the analyzer
/// rejects copies (EECS012), reach the list through the entity.
/// </remarks>
public struct DynamicBuffer<T> : IComponent, ITrackChanges, IBuffer
{
    // The only field: snapshots read a column of buffers as a column of BufferStore references (ComponentInfo).
    internal BufferStore<T> Store;

    readonly bool IBuffer.Relational => default(T) is IRelation;

    readonly void IBuffer.Write(ref Writer writer, BufferStore store) => BufferStore<T>.Write(ref writer, Unsafe.As<BufferStore<T>>(store));

    readonly BufferStore IBuffer.Read(ref Reader reader, BufferStore reuse) => BufferStore<T>.Read(ref reader, reuse as BufferStore<T>);

    readonly void IBuffer.Remap(BufferStore store, IdMap map)
    {
        if (store is BufferStore<T> typed) map.Apply(typed.Items.AsSpan(0, typed.Count));
    }

    /// <summary>The number of elements.</summary>
    public readonly int Count => Store?.Count ?? 0;

    /// <summary>The number of elements the list holds before it grows.</summary>
    public readonly int Capacity => Store?.Items.Length ?? 0;

    /// <summary>
    /// The element at <paramref name="index"/>, read-only (write through <see cref="DynamicBufferExtensions.AsSpan{T}"/>).
    /// </summary>
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
/// The writes of a <see cref="DynamicBuffer{T}"/>. Each takes the buffer by <c>ref</c>: it must be reached through a
/// write access (a tracked change), never through <c>in</c>.
/// </summary>
public static class DynamicBufferExtensions
{
    /// <summary>The elements, writable.</summary>
    public static Span<T> AsSpan<T>(this ref DynamicBuffer<T> buffer) =>
        buffer.Store is { } store ? new Span<T>(store.Items, 0, store.Count) : default;

    /// <summary>Append <paramref name="item"/>. O(1) amortized; allocates only when the list grows.</summary>
    public static void Add<T>(this ref DynamicBuffer<T> buffer, in T item)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if (store.Count == store.Items.Length) store.Grow(store.Count + 1);
        store.Items[store.Count++] = item;
    }

    /// <summary>Append <paramref name="items"/>. O(items).</summary>
    public static void AddRange<T>(this ref DynamicBuffer<T> buffer, ReadOnlySpan<T> items)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if (store.Count + items.Length > store.Items.Length) store.Grow(store.Count + items.Length);
        items.CopyTo(store.Items.AsSpan(store.Count));
        store.Count += items.Length;
    }

    /// <summary>Insert <paramref name="item"/> at <paramref name="index"/>, keeping the order. O(count).</summary>
    public static void Insert<T>(this ref DynamicBuffer<T> buffer, int index, in T item)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if ((uint)index > (uint)store.Count) DynamicBuffer<T>.ThrowIndex();
        if (store.Count == store.Items.Length) store.Grow(store.Count + 1);
        Array.Copy(store.Items, index, store.Items, index + 1, store.Count - index);
        store.Items[index] = item;
        store.Count++;
    }

    /// <summary>Remove the element at <paramref name="index"/>, keeping the order. O(count).</summary>
    public static void RemoveAt<T>(this ref DynamicBuffer<T> buffer, int index)
    {
        var store = buffer.Store;
        if (store == null || (uint)index >= (uint)store.Count) DynamicBuffer<T>.ThrowIndex();
        int count = --store.Count;
        Array.Copy(store.Items, index + 1, store.Items, index, count - index);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) store.Items[count] = default;
    }

    /// <summary>Remove the element at <paramref name="index"/> by moving the last one into its place. O(1).</summary>
    public static void RemoveAtSwapBack<T>(this ref DynamicBuffer<T> buffer, int index)
    {
        var store = buffer.Store;
        if (store == null || (uint)index >= (uint)store.Count) DynamicBuffer<T>.ThrowIndex();
        int count = --store.Count;
        store.Items[index] = store.Items[count];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) store.Items[count] = default;
    }

    /// <summary>Remove every element; the capacity stays. O(1), or O(count) for elements holding references.</summary>
    public static void Clear<T>(this ref DynamicBuffer<T> buffer)
    {
        if (buffer.Store is not { } store) return;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(store.Items, 0, store.Count);
        store.Count = 0;
    }

    /// <summary>
    /// Shrink the capacity to the count, so a list that once grew large stops holding its peak (the list's array is
    /// reallocated; an empty list holds none). O(count).
    /// </summary>
    public static void TrimExcess<T>(this ref DynamicBuffer<T> buffer)
    {
        if (buffer.Store is { } store && store.Count < store.Items.Length) store.Resize(store.Count);
    }

    /// <summary>Make room for at least <paramref name="capacity"/> elements.</summary>
    public static void EnsureCapacity<T>(this ref DynamicBuffer<T> buffer, int capacity)
    {
        var store = buffer.Store ??= new BufferStore<T>(0);
        if (capacity > store.Items.Length) store.Resize(capacity);
    }
}

/// <summary>Marks <see cref="DynamicBuffer{T}"/> for the column operations that copy its elements.</summary>
internal interface IBuffer
{
    /// <summary>Its elements are <see cref="IRelation"/>s: the buffer is indexed by their targets.</summary>
    bool Relational { get; }

    /// <summary>Write the elements of <paramref name="store"/> (a buffer of this type's; null: none) for a save.</summary>
    void Write(ref Writer writer, BufferStore store);

    /// <summary>
    /// Read elements written by <see cref="Write"/> into <paramref name="reuse"/> (a store of this type's from an
    /// earlier Load, or null) when it has room, else into a new store; null for none and no store to reuse.
    /// </summary>
    BufferStore Read(ref Reader reader, BufferStore reuse);

    /// <summary>Point the <see cref="EntityId"/> fields of the elements of <paramref name="store"/> at a migration's entities.</summary>
    void Remap(BufferStore store, IdMap map);
}

/// <summary>The list of a <see cref="DynamicBuffer{T}"/>, typed away so a column can be copied without its <c>T</c>.</summary>
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
        var grown = capacity == 0 ? [] : new T[capacity];
        Array.Copy(Items, grown, Count);
        Items = grown;
    }

    public override BufferStore Clone() => new BufferStore<T>(0) { Items = Items.AsSpan(0, Count).ToArray(), Count = Count };

    /// <summary>Write the count, then the elements: raw rows when <typeparamref name="T"/> is unmanaged, else Nino values.</summary>
    public static void Write(ref Writer writer, BufferStore<T> store)
    {
        int count = store?.Count ?? 0;
        writer.Write(count);
        if (count == 0) return;
        var items = store!.Items.AsSpan(0, count);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) SavedTypes.WriteRows<T>(ref writer, Bytes(items));
        else for (int i = 0; i < items.Length; i++) NinoSerializer.Serialize(items[i], ref writer);
    }

    /// <summary>The bytes of unmanaged <paramref name="items"/> (T has no constraint, so not MemoryMarshal.AsBytes).</summary>
    private static Span<byte> Bytes(Span<T> items) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(items)), items.Length * Unsafe.SizeOf<T>());

    /// <summary>
    /// Read a store written by <see cref="Write"/> into <paramref name="reuse"/> when it has room, else a new one
    /// (null for an empty buffer without a store to reuse).
    /// </summary>
    public static BufferStore<T> Read(ref Reader reader, BufferStore<T> reuse)
    {
        int count = reader.ReadLength(RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? 1 : Unsafe.SizeOf<T>());
        if (count < 0) throw new System.IO.InvalidDataException("A damaged save: a buffer longer than its bytes");
        if (reuse != null && RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(reuse.Items, 0, reuse.Count);
        if (count == 0)
        {
            reuse?.Count = 0;
            return reuse;
        }

        var store = reuse != null && reuse.Items.Length >= count ? reuse : new BufferStore<T>(count);
        store.Count = count;
        var items = store.Items.AsSpan(0, count);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) SavedTypes.ReadRows<T>(ref reader, Bytes(items));
        else for (int i = 0; i < items.Length; i++) NinoDeserializer.Deserialize(ref items[i], ref reader);
        return store;
    }
}
