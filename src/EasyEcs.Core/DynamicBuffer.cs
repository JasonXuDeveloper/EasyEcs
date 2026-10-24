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
/// compile. Prefab instances, clones and snapshots (<see cref="World.Capture()"/>) share the list until one of them
/// writes it, which first copies it (copy-on-write); a moved row keeps its list. Adding within the capacity allocates
/// nothing.
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
    public static int MaxPooledLength
    {
        get => BufferStore<T>.MaxPooledLength;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            BufferStore<T>.MaxPooledLength = value;
        }
    }

    /// <summary>
    /// Fill the pool with lists until it holds <paramref name="lists"/>, each of <see cref="MaxPooledLength"/> elements, so
    /// the first writes of that many buffers take a list instead of allocating one: a server that knows how many
    /// buffers of <typeparamref name="T"/> it keeps at its peak (the chunks around its players) pays for them at
    /// startup, and its ticks allocate nothing even while the world grows to that peak. Process-wide, like the pool;
    /// lists already in the pool count. O(<paramref name="lists"/>) allocations once; nothing when the pool holds enough.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lists"/> is negative.</exception>
    public static void FillPool(int lists)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lists);
        BufferStore<T>.Fill(lists);
    }

    readonly bool IBuffer.Relational => default(T) is IRelation;

    readonly int IBuffer.RowSize => TypeRegistry.RowSize<T>();

    readonly int IBuffer.Layout => TypeRegistry.RowSize<T>() != 0 ? TypeRegistry.RawLayout<T>.Hash : 0;

    readonly void IBuffer.Write(ref Writer writer, BufferStore store) => BufferStore<T>.Write(ref writer, Unsafe.As<BufferStore<T>>(store));

    readonly BufferStore IBuffer.Read(ref Reader reader, BufferStore reuse) => BufferStore<T>.Read(ref reader, reuse as BufferStore<T>);

    readonly void IBuffer.Remap(ref BufferStore store, IdMap map)
    {
        if (store is not BufferStore<T> typed) return;
        bool ids = TypeRegistry.EntityIdOffsets<T>.Value.Length != 0;
        // The arrived row's list is made its own when its ids are rewritten, and when a row of the world it came from
        // (or a snapshot of that world) counts it: counted lists never cross worlds, whose owner threads would count
        // them at once. A prefab's list is shared uncounted by every world: it stays shared.
        if (typed.Shares > 0 || ids && typed.Shares != 0) store = typed = typed.Unshare();
        if (ids) map.Apply(typed.Items.AsSpan(0, typed.Count));
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
    // Every write reaches the list through Own (the list, made this buffer's first when it is shared) or Take (Own, or a
    // list from the pool for a buffer without one): one load and compare inlined, the copy out of line.

    /// <summary>The elements, writable.</summary>
    public static Span<T> AsSpan<T>(this ref DynamicBuffer<T> buffer) =>
        Own(ref buffer) is { } store ? new Span<T>(store.Items, 0, store.Count) : default;

    /// <summary>Append <paramref name="item"/>. O(1) amortized; allocates only when the list grows.</summary>
    public static void Add<T>(this ref DynamicBuffer<T> buffer, in T item)
    {
        var store = Take(ref buffer);
        if (store.Count == store.Items.Length) store.Grow(store.Count + 1);
        store.Items[store.Count++] = item;
    }

    /// <summary>
    /// The list of <paramref name="buffer"/> to write (null when it has none): a list it shares (a prefab instance, a
    /// clone, a snapshot) is replaced by a copy of its own first (<see cref="BufferStore{T}.Unshare"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static BufferStore<T> Own<T>(ref DynamicBuffer<T> buffer)
    {
        var store = buffer.Store;
        if (store != null && store.Shares != 0) store = buffer.Store = store.Unshare();
        return store;
    }

    /// <summary><see cref="Own{T}"/>, with a list from the pool for a buffer that has none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static BufferStore<T> Take<T>(ref DynamicBuffer<T> buffer)
    {
        var store = buffer.Store;
        return store != null && store.Shares == 0 ? store : Taken(ref buffer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BufferStore<T> Taken<T>(ref DynamicBuffer<T> buffer) => buffer.Store = buffer.Store?.Unshare() ?? BufferStore<T>.Rent();

    /// <summary>Append <paramref name="items"/> (a list: <c>CollectionsMarshal.AsSpan(list)</c>, no copy). O(items).</summary>
    public static void AddRange<T>(this ref DynamicBuffer<T> buffer, params ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return; // an empty buffer stays without a list: its first element takes one from the pool
        var store = Take(ref buffer);
        if (store.Count + items.Length > store.Items.Length) store.Grow(store.Count + items.Length);
        items.CopyTo(store.Items.AsSpan(store.Count));
        store.Count += items.Length;
    }

    /// <summary>Insert <paramref name="item"/> at <paramref name="index"/>, keeping the order. O(count).</summary>
    public static void Insert<T>(this ref DynamicBuffer<T> buffer, int index, in T item)
    {
        var store = Take(ref buffer);
        if ((uint)index > (uint)store.Count) DynamicBuffer<T>.ThrowIndex();
        if (store.Count == store.Items.Length) store.Grow(store.Count + 1);
        Array.Copy(store.Items, index, store.Items, index + 1, store.Count - index);
        store.Items[index] = item;
        store.Count++;
    }

    /// <summary>Remove the element at <paramref name="index"/>, keeping the order. O(count).</summary>
    public static void RemoveAt<T>(this ref DynamicBuffer<T> buffer, int index)
    {
        var store = Own(ref buffer);
        if (store == null || (uint)index >= (uint)store.Count) DynamicBuffer<T>.ThrowIndex();
        int count = --store.Count;
        Array.Copy(store.Items, index + 1, store.Items, index, count - index);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) store.Items[count] = default;
    }

    /// <summary>Remove the element at <paramref name="index"/> by moving the last one into its place. O(1).</summary>
    public static void RemoveAtSwapBack<T>(this ref DynamicBuffer<T> buffer, int index)
    {
        var store = Own(ref buffer);
        if (store == null || (uint)index >= (uint)store.Count) DynamicBuffer<T>.ThrowIndex();
        int count = --store.Count;
        store.Items[index] = store.Items[count];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) store.Items[count] = default;
    }

    /// <summary>Remove every element; the capacity stays. O(1), or O(count) for elements holding references.</summary>
    public static void Clear<T>(this ref DynamicBuffer<T> buffer)
    {
        if (buffer.Store is not { } store) return;
        if (store.Shares != 0)
        {
            // A shared list is left to its other owners, uncopied: the buffer holds none, as one never written.
            buffer.Store = null;
            store.Release();
            return;
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(store.Items, 0, store.Count);
        store.Count = 0;
    }

    /// <summary>
    /// Shrink the capacity to the count, so a list that once grew large stops holding its peak (the list's array is
    /// reallocated). An empty buffer gives its list back to the pool its first writes take lists from, so a buffer
    /// used only now and then (scratch space while an entity is active) holds no memory in between and the next
    /// first write of any buffer of <typeparamref name="T"/> reuses it. O(count).
    /// </summary>
    public static void TrimExcess<T>(this ref DynamicBuffer<T> buffer)
    {
        if (buffer.Store is not { } store) return;
        if (store.Count == 0)
        {
            buffer.Store = null;
            store.Recycle();
        }
        else if (store.Shares != 0) buffer.Store = store.Unshare(); // the copy holds the count, no more
        else if (store.Count < store.Items.Length) store.Resize(store.Count);
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
        if (Own(ref buffer) is not { } store)
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
        var store = Take(ref buffer);
        if (capacity > store.Items.Length) store.Resize(capacity);
    }
}

/// <summary>Marks <see cref="DynamicBuffer{T}"/> for the column operations that copy its elements.</summary>
internal interface IBuffer
{
    /// <summary>Its elements are <see cref="IRelation"/>s: the buffer is indexed by their targets.</summary>
    bool Relational { get; }

    /// <summary>How a save writes the elements (<see cref="TypeRegistry.RowSize{T}"/>).</summary>
    int RowSize { get; }

    /// <summary>The layout hash of raw elements (<see cref="TypeRegistry.Raw{T}"/>), else 0.</summary>
    int Layout { get; }

    /// <summary>Write the elements of <paramref name="store"/> (a buffer of this type's; null: none) for a save.</summary>
    void Write(ref Writer writer, BufferStore store);

    /// <summary>
    /// Read elements written by <see cref="Write"/> into <paramref name="reuse"/> (a store of this type's from an
    /// earlier Load, grown when it lacks room), else into one from the pool; null for none and no store to reuse.
    /// </summary>
    BufferStore Read(ref Reader reader, BufferStore reuse);

    /// <summary>
    /// Point the <see cref="EntityId"/> fields of the elements of <paramref name="store"/> at a migration's entities,
    /// first replacing a shared store by a copy of its own when it writes them or when the store is counted (see
    /// <see cref="BufferStore.Shares"/>).
    /// </summary>
    void Remap(ref BufferStore store, IdMap map);
}

/// <summary>
/// The list of a <see cref="DynamicBuffer{T}"/>, typed away so a column can be copied without its <c>T</c>. Buffers
/// share one list until one of them writes (copy-on-write, vLLM's shared blocks): a prefab's instances, an entity's
/// clones and a snapshot's rows hold the list of the row they copy, and the first write of each makes a copy of its
/// own (<see cref="BufferStore{T}.Unshare"/>), so sharing is never seen.
/// </summary>
internal abstract class BufferStore
{
    /// <summary>The value of <see cref="Shares"/> of a prefab's lists (<see cref="Prefab"/>): shared by its instances
    /// in every world, on any thread, so never counted and never given back to the pool (the GC takes it).</summary>
    public const int Uncounted = int.MinValue;

    /// <summary>
    /// How many buffers hold this list besides one: 0 when it is its buffer's own (written in place), more when it is
    /// shared (the next write of each holder copies it), <see cref="Uncounted"/> (any negative value) for a prefab's.
    /// Counted lists stay in one world (its rows and its snapshots: an arrival copies them, IBuffer.Remap): counted
    /// up by structural changes on the owner thread, down by them and by first writes, plain on the owner thread and
    /// atomic inside a parallel pass, whose chunks may each copy a list they share (<see cref="Release"/>).
    /// </summary>
    public int Shares;

    /// <summary>
    /// Parallel passes running now, in any world (World.RunChunks): while one runs, a first write counts its list down
    /// atomically, since other chunks may count the same list down at once.
    /// </summary>
    public static int ParallelPasses;

    /// <summary>This list, held by one more buffer (a clone, an instance, a snapshot's row); null for an empty one (a buffer with no list).</summary>
    public abstract BufferStore Share();

    /// <summary>A store with copies of the elements, taken from the pool, its own; null for an empty one.</summary>
    public abstract BufferStore Copy();

    /// <summary>
    /// A buffer holding this list died or let it go: one holder fewer, and the last one's list is emptied and kept for
    /// the next first write. A prefab's list is left to the GC.
    /// </summary>
    public abstract void Recycle();

    /// <summary>A holder that copied this shared list or let it go: one holder fewer (never the last one).</summary>
    public void Release()
    {
        if (Shares < 0) return; // a prefab's: uncounted
        // Two chunks of a parallel pass that share a list may both copy it and count it down at once (it then reaches
        // -1, held by nobody: a prefab's value, harmless): the decrement is atomic only while a pass runs.
        if (Volatile.Read(ref ParallelPasses) == 0) Shares--;
        else Interlocked.Decrement(ref Shares);
    }

    /// <summary>Remove the elements; the store stays its owner's, with its capacity (a singleton's: never shared).</summary>
    public abstract void Empty();

    /// <summary>
    /// <paramref name="target"/> (a store its owner replaces, or null) made a copy of <paramref name="source"/>: it shares
    /// the source's list, and its old list loses a holder (back to the pool when it was the last). Capturing or
    /// restoring twice without a write in between changes nothing.
    /// </summary>
    public static void Assign(ref BufferStore target, BufferStore source)
    {
        if (target == source) return;
        target?.Recycle();
        target = source?.Share();
    }

    /// <summary>
    /// Hold copies of the elements of <paramref name="source"/> (of the same element type, or null); false, the store
    /// untouched, when it has none. For a store that is its owner's (a singleton's).
    /// </summary>
    public abstract bool CopyFrom(BufferStore source);

    /// <summary>
    /// Mark the lists of rows [0, <paramref name="count"/>) of a buffer column <see cref="Uncounted"/>: a prefab's, which
    /// worlds on several threads share without counting (<see cref="Prefab"/>). Each row of a buffer column is a
    /// DynamicBuffer, whose only field is its store: the column reads as an array of stores.
    /// </summary>
    public static void MarkUncounted(Array column, int count)
    {
        ref var first = ref Unsafe.As<byte, BufferStore>(ref MemoryMarshal.GetArrayDataReference(column));
        for (int i = 0; i < count; i++)
            if (Unsafe.Add(ref first, i) is { } store)
                store.Shares = Uncounted;
    }
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
    // were taken (the peak a table's capacity keeps too), each with at most MaxPooledLength elements (a list that grew
    // larger is left to the GC; DynamicBuffer<T>.MaxPooledLength sets it).
    internal static int MaxPooledLength = 4096 / Unsafe.SizeOf<T>();

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

    /// <summary>Add stores of MaxPooledLength elements to the shared pool until the pools hold <paramref name="count"/> (cold).</summary>
    public static void Fill(int count)
    {
        if (MaxPooledLength == 0) return;
        _ = t_free ?? Own(); // this thread's own list (and every helper thread's): a first refill allocates nothing
        lock (s_gate)
        {
            int missing = count - s_freeCount - t_freeCount;
            if (missing <= 0) return;
            if (s_freeCount + missing > s_free.Length) Array.Resize(ref s_free, Math.Max(s_freeCount + missing, s_free.Length * 2));
            for (int i = 0; i < missing; i++) s_free[s_freeCount++] = new BufferStore<T>(MaxPooledLength);
        }
    }

    private static int _ownSetUp; // the ThreadSetup step that gives every helper thread its own list

    /// <summary>This thread's own list of stores, created (on every helper thread at once: <see cref="ThreadSetup"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BufferStore<T>[] Own()
    {
        ThreadSetup.Add(ref _ownSetUp, static () => t_free ??= new BufferStore<T>[2 * Batch], typeof(BufferStore<T>));
        return t_free ??= new BufferStore<T>[2 * Batch];
    }

    // This thread's stores ran out: take up to Batch from the shared pool.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Refill()
    {
        if (Volatile.Read(ref s_freeCount) == 0) return false;
        var free = t_free ?? Own();
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
        if (Shares != 0)
        {
            Release(); // others hold it (or a prefab does): they keep it
            return;
        }

        Empty();
        if (Items.Length > MaxPooledLength) return;
        var free = t_free ?? Own();
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

    public override void Empty()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(Items, 0, Count);
        Count = 0;
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

    public override bool CopyFrom(BufferStore source)
    {
        var from = Unsafe.As<BufferStore<T>>(source);
        if (from == null || from.Count == 0) return false;
        if (Items.Length < from.Count) Resize(from.Count);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && Count > from.Count) Array.Clear(Items, from.Count, Count - from.Count);
        Array.Copy(from.Items, Items, from.Count);
        Count = from.Count;
        return true;
    }

    public override BufferStore Share()
    {
        if (Count == 0) return null;
        if (Shares >= 0) Shares++; // structural changes, on the owner thread: a prefab's list is never counted
        return this;
    }

    public override BufferStore Copy() => Count == 0 ? null : Copied();

    /// <summary>
    /// A copy of this shared list for the holder about to write it, which lets this one go (<see cref="BufferStore.Release"/>):
    /// the copy first, so a holder that sees the count reach 0 and writes in place finds the elements copied.
    /// O(count); a list from the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public BufferStore<T> Unshare()
    {
        var copy = Copied();
        Release();
        return copy;
    }

    private BufferStore<T> Copied()
    {
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
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) TypeRegistry.WriteRows<T>(ref writer, Bytes(items)); // raw or [NinoType]
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
        int count = reader.ReadLength(Math.Max(1, TypeRegistry.RowSize<T>()));
        if (count < 0) throw new System.IO.InvalidDataException("A damaged save: a buffer longer than its bytes");
        if (reuse is { Shares: not 0 })
        {
            reuse.Release(); // a list others hold (a snapshot's, a prefab's) is never refilled: theirs stays
            reuse = null;
        }

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
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) TypeRegistry.ReadRows<T>(ref reader, Bytes(items));
        else for (int i = 0; i < items.Length; i++) NinoDeserializer.Deserialize(ref items[i], ref reader);
        return store;
    }
}
