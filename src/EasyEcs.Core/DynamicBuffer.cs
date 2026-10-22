using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Runtime.InteropServices;
using Nino.Core;

namespace EasyEcs;

/// <summary>
/// A list of <typeparamref name="T"/> per entity, held as a component: an inventory, a path, the hits of this tick.
/// Writes go through a <c>ref</c> to the component (<c>ref</c> parameters of <c>[ForEach]</c> methods and jobs,
/// <see cref="Entity.Get{T}"/>), so they are tracked like any write of an <see cref="ITracked"/> component and
/// <c>[Changed&lt;DynamicBuffer&lt;T&gt;&gt;]</c> selects the entities whose list changed. Through <c>in</c> or
/// <see cref="Entity.Read{T}"/> the list is read-only: the mutating methods
/// (<see cref="DynamicBufferExtensions"/>) take <c>ref this</c>, so a write through a read-only reference does not
/// compile. Snapshots copy the elements (<see cref="World.Capture()"/>); a moved row keeps its list. Adding within the
/// capacity allocates nothing.
/// </summary>
/// <remarks>
/// A <c>new DynamicBuffer&lt;T&gt;()</c> holds no list: each entity's list is made by its first write (reserve room
/// with <see cref="DynamicBufferExtensions.EnsureCapacity{T}"/>), so one value can create many entities; an instance of
/// a prefab whose buffer is empty holds none either, so its first write takes a list from the pool. The list
/// lives in an object the component refers to, and a copy of the component would refer to the same list: the analyzer
/// rejects copies (EECS012), reach the list through the entity.
/// </remarks>
public struct DynamicBuffer<T> : IComponent, ITracked, IBuffer
{
    // The only field: snapshots read a column of buffers as a column of BufferStore references (ComponentInfo).
    internal BufferStore<T> Store;

    /// <summary>
    /// The longest list, in elements, that a destroyed or removed buffer of <typeparamref name="T"/> gives back to the
    /// pool, for the next buffer's first write to reuse instead of allocating one, on any thread: the lists the owner
    /// thread takes back serve the first writes of a parallel <c>[ForEach]</c> too (default: 4,096 bytes of
    /// elements, 4,096 for a <c>byte</c> or a byte-sized enum). A longer list is left to the GC. Raise it for buffers of
    /// one size that come and go in numbers, such as the blocks of chunks loaded and unloaded around players; 0 pools
    /// none. The pool holds at most as many lists as died while none were taken, so what it keeps is that peak times
    /// this capacity. Process-wide, for every world and thread: set it at startup, before a world uses the type.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public static int PoolCapacity
    {
        get => BufferStore<T>.PoolCapacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            BufferStore<T>.PoolCapacity = value;
        }
    }

    readonly bool IBuffer.Relational => default(T) is IRelation;

    readonly int IBuffer.RowSize => SavedTypes.RowSize<T>();

    readonly int IBuffer.Layout => SavedTypes.RowSize<T>() != 0 ? SavedTypes.RawLayout<T>.Hash : 0;

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
        var store = buffer.Store ??= BufferStore<T>.Rent();
        if (store.Count == store.Items.Length) store.Grow(store.Count + 1);
        store.Items[store.Count++] = item;
    }

    /// <summary>Append <paramref name="items"/> (a list: <c>CollectionsMarshal.AsSpan(list)</c>, no copy). O(items).</summary>
    public static void AddRange<T>(this ref DynamicBuffer<T> buffer, params ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return; // an empty buffer stays without a list: its first element takes one from the pool
        var store = buffer.Store ??= BufferStore<T>.Rent();
        if (store.Count + items.Length > store.Items.Length) store.Grow(store.Count + items.Length);
        items.CopyTo(store.Items.AsSpan(store.Count));
        store.Count += items.Length;
    }

    /// <summary>Insert <paramref name="item"/> at <paramref name="index"/>, keeping the order. O(count).</summary>
    public static void Insert<T>(this ref DynamicBuffer<T> buffer, int index, in T item)
    {
        var store = buffer.Store ??= BufferStore<T>.Rent();
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

    /// <summary>
    /// Set the number of elements to <paramref name="count"/>: elements past it are removed, elements added are
    /// <c>default</c>, so a system writes a whole buffer in place through the span returned instead of filling a span
    /// and copying it in (<c>CollectionsMarshal.SetCount</c> of a list, with the new elements cleared). The span is
    /// <see cref="AsSpan{T}"/>: valid until the buffer's next change of count or capacity.
    /// O(elements added or removed); allocates only when the list grows.
    /// </summary>
    public static Span<T> SetCount<T>(this ref DynamicBuffer<T> buffer, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Store is not { } store)
        {
            if (count == 0) return default; // an empty buffer stays without a list
            store = buffer.Store = BufferStore<T>.Rent();
        }
        if (count > store.Items.Length) store.Resize(count);
        if (count > store.Count) Array.Clear(store.Items, store.Count, count - store.Count); // a pooled list holds old values
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(store.Items, count, store.Count - count);
        store.Count = count;
        return store.Items.AsSpan(0, count);
    }

    /// <summary>Make room for at least <paramref name="capacity"/> elements.</summary>
    public static void EnsureCapacity<T>(this ref DynamicBuffer<T> buffer, int capacity)
    {
        var store = buffer.Store ??= BufferStore<T>.Rent();
        if (capacity > store.Items.Length) store.Resize(capacity);
    }
}

/// <summary>Marks <see cref="DynamicBuffer{T}"/> for the column operations that copy its elements.</summary>
internal interface IBuffer
{
    /// <summary>Its elements are <see cref="IRelation"/>s: the buffer is indexed by their targets.</summary>
    bool Relational { get; }

    /// <summary>How a save writes the elements (<see cref="SavedTypes.RowSize{T}"/>).</summary>
    int RowSize { get; }

    /// <summary>The layout hash of raw elements (<see cref="SavedTypes.Raw{T}"/>), else 0.</summary>
    int Layout { get; }

    /// <summary>Write the elements of <paramref name="store"/> (a buffer of this type's; null: none) for a save.</summary>
    void Write(ref Writer writer, BufferStore store);

    /// <summary>
    /// Read elements written by <see cref="Write"/> into <paramref name="reuse"/> (a store of this type's from an
    /// earlier Load, grown when it lacks room), else into one from the pool; null for none and no store to reuse.
    /// </summary>
    BufferStore Read(ref Reader reader, BufferStore reuse);

    /// <summary>Point the <see cref="EntityId"/> fields of the elements of <paramref name="store"/> at a migration's entities.</summary>
    void Remap(BufferStore store, IdMap map);
}

/// <summary>The list of a <see cref="DynamicBuffer{T}"/>, typed away so a column can be copied without its <c>T</c>.</summary>
internal abstract class BufferStore
{
    /// <summary>A store with copies of the elements, taken from the pool; null for an empty one (a buffer with no list).</summary>
    public abstract BufferStore Clone();

    /// <summary>Empty the store and keep it for the next first write (its buffer died: nothing refers to it).</summary>
    public abstract void Recycle();
}

internal sealed class BufferStore<T>(int capacity) : BufferStore
{
    public T[] Items = capacity == 0 ? [] : new T[capacity];
    public int Count;

    // The emptied stores, taken by first writes: a world's structural changes (where buffers die) run on its owner
    // thread, so a world's lists come back to the buffers of that world, and an actor loaded after one unloaded reuses
    // its list instead of allocating one. Each thread keeps up to 2 × Batch stores of its own (no lock); past that it
    // hands Batch of them to a pool every thread shares, and a thread whose own run out takes Batch back from it, one
    // lock per Batch stores: the lists the owner thread takes back serve the first writes of a parallel [ForEach] on
    // the helper threads too. Bounded like a table's rows: the pools hold at most as many stores as died while none
    // were taken (the peak a table's capacity keeps too), each with at most PoolCapacity elements (a list that grew
    // larger is left to the GC; DynamicBuffer<T>.PoolCapacity sets it).
    internal static int PoolCapacity = 4096 / Unsafe.SizeOf<T>();

    private const int Batch = 32;

    [ThreadStatic] private static BufferStore<T>[] t_free;
    [ThreadStatic] private static int t_freeCount;

    private static readonly Lock s_gate = new();
    private static BufferStore<T>[] s_free = [];
    private static int s_freeCount; // written under s_gate; read without it only to skip an empty pool

    /// <summary>An empty store: a recycled one (this thread's, else the shared pool's), else a new one.</summary>
    public static BufferStore<T> Rent()
    {
        if (t_freeCount == 0 && !Refill()) return new BufferStore<T>(0);
        var free = t_free;
        int last = --t_freeCount;
        var store = free[last];
        free[last] = null;
        return store;
    }

    // This thread's stores ran out: take up to Batch from the shared pool.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Refill()
    {
        if (Volatile.Read(ref s_freeCount) == 0) return false;
        var free = t_free ??= new BufferStore<T>[2 * Batch];
        lock (s_gate)
        {
            int taken = Math.Min(Batch, s_freeCount);
            int from = s_freeCount - taken;
            Array.Copy(s_free, from, free, 0, taken);
            Array.Clear(s_free, from, taken);
            s_freeCount = from;
            t_freeCount = taken;
            return taken != 0;
        }
    }

    public override void Recycle()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(Items, 0, Count);
        Count = 0;
        if (Items.Length > PoolCapacity) return;
        var free = t_free ??= new BufferStore<T>[2 * Batch];
        if (t_freeCount == free.Length) Spill(free);
        free[t_freeCount++] = this;
    }

    // This thread holds 2 × Batch stores: hand the oldest Batch to the shared pool.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Spill(BufferStore<T>[] free)
    {
        lock (s_gate)
        {
            if (s_freeCount + Batch > s_free.Length) Array.Resize(ref s_free, Math.Max(2 * Batch, s_free.Length * 2));
            Array.Copy(free, 0, s_free, s_freeCount, Batch);
            s_freeCount += Batch;
        }

        Array.Copy(free, Batch, free, 0, Batch);
        Array.Clear(free, Batch, Batch);
        t_freeCount = Batch;
    }

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

    public override BufferStore Clone()
    {
        if (Count == 0) return null;
        var copy = Rent();
        if (copy.Items.Length < Count) copy.Resize(Count);
        Array.Copy(Items, copy.Items, Count);
        copy.Count = Count;
        return copy;
    }

    /// <summary>Write the count, then the elements: raw rows when <typeparamref name="T"/> is unmanaged, else Nino values.</summary>
    public static void Write(ref Writer writer, BufferStore<T> store)
    {
        int count = store?.Count ?? 0;
        writer.Write(count);
        if (count == 0) return;
        var items = store!.Items.AsSpan(0, count);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) SavedTypes.WriteRows<T>(ref writer, Bytes(items)); // raw or [NinoType]
        else for (int i = 0; i < items.Length; i++) NinoSerializer.Serialize(items[i], ref writer);
    }

    /// <summary>The bytes of unmanaged <paramref name="items"/> (T has no constraint, so not MemoryMarshal.AsBytes).</summary>
    private static Span<byte> Bytes(Span<T> items) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(items)), items.Length * Unsafe.SizeOf<T>());

    /// <summary>
    /// Read a store written by <see cref="Write"/> into <paramref name="reuse"/> (grown when it lacks room), else into
    /// one from the pool (null for an empty buffer without a store to reuse).
    /// </summary>
    public static BufferStore<T> Read(ref Reader reader, BufferStore<T> reuse)
    {
        int count = reader.ReadLength(Math.Max(1, SavedTypes.RowSize<T>()));
        if (count < 0) throw new System.IO.InvalidDataException("A damaged save: a buffer longer than its bytes");
        if (reuse != null && RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(reuse.Items, 0, reuse.Count);
        if (count == 0)
        {
            reuse?.Count = 0;
            return reuse;
        }

        var store = reuse ?? Rent();
        store.Count = 0;
        if (store.Items.Length < count) store.Resize(count);
        store.Count = count;
        var items = store.Items.AsSpan(0, count);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) SavedTypes.ReadRows<T>(ref reader, Bytes(items));
        else for (int i = 0; i < items.Length; i++) NinoDeserializer.Deserialize(ref items[i], ref reader);
        return store;
    }
}
