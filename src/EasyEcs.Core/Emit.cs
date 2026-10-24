using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nino.Core;

namespace EasyEcs;

/// <summary>
/// Records a <c>[ForEach]</c> pass appends to (Unity's <c>NativeStream</c>, a GPU's stream compaction), taken by value:
/// <c>static void Strike(in Attack attack, in Target target, Emit&lt;Hit&gt; hits) =&gt; hits.Add(new Hit(target.Id,
/// attack.Power));</c>. Each chunk of a parallel pass (or the whole pass on one thread) appends to its own list, with no
/// lock and no atomic per record; when the pass ends, the lists are appended to the world's
/// <see cref="Emitted{T}"/> singleton (added when missing) in chunk order, which is row order: the records come out in
/// the same order on every run and for any thread count, the order of the same method with <c>Parallel = false</c>.
/// <para>
/// A pass appends, it does not replace: several systems may emit <typeparamref name="T"/> in one update (melee and
/// spells both emit damage), each in its turn, and the system that applies the records clears them
/// (<c>world.GetSingleton&lt;Emitted&lt;Hit&gt;&gt;().Items.Clear()</c>), so nothing is applied twice. A method that
/// emits must not also take <see cref="Emitted{T}"/> (EECS070): a parallel pass's records reach it only when the pass
/// ends.
/// </para>
/// The system writes <see cref="Emitted{T}"/> (once per pass, as a <see cref="Reduce{T}"/> writes its singleton). The
/// chunks' lists come from a pool of their own, kept at the largest size a chunk needed, so steady passes allocate
/// nothing. Valid for the call it is passed to (a ref struct).
/// </summary>
/// <typeparam name="T">The record: plain data, such as the target and amount of a hit.</typeparam>
public readonly ref struct Emit<T> where T : struct
{
    private readonly ref DynamicBuffer<T> _items;

    /// <summary>Appends to <paramref name="items"/>: the pass's list (a chunk's, or the singleton's on one thread). Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Emit(ref DynamicBuffer<T> items) => _items = ref items;

    /// <summary>Append <paramref name="record"/>. O(1) amortized; allocates only when this chunk's list grows past its largest size so far.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(in T record)
    {
        // The cold calls take what they need by value (the list's ref, the record): neither this nor the record has its
        // address taken, so the row loop builds no copy of either on the stack.
        var store = _items.Store ?? Start(ref _items);
        int count = store.Count;
        var items = store.Items;
        if ((uint)count < (uint)items.Length)
        {
            items[count] = record;
            store.Count = count + 1;
        }
        else Grow(store, record);
    }

    /// <summary>Append <paramref name="records"/>. O(records).</summary>
    public void AddRange(params ReadOnlySpan<T> records)
    {
        if (records.IsEmpty) return;
        var store = _items.Store ?? Start(ref _items);
        if (store.Count + records.Length > store.Items.Length) store.Grow(store.Count + records.Length);
        records.CopyTo(store.Items.AsSpan(store.Count));
        store.Count += records.Length;
    }

    /// <summary>
    /// Append a record per set bit of <paramref name="lanes"/> (bit j: row <paramref name="row"/> + j of the run), the id
    /// of its entity, in row order: the vector loop of a method that emits <c>new T(entity.Id)</c>, where
    /// <typeparamref name="T"/> holds the id alone. O(selected rows). Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddIds(in EntitySpan rows, int row, ulong lanes)
    {
        if (lanes != 0) AddSelected(rows.Slots.Slice(row), lanes);
    }

    /// <summary>
    /// Append a record per set bit of <paramref name="lanes"/>, lane j of <paramref name="values"/>, in lane order: the
    /// vector loop of a method that emits <c>new T(value)</c>, where <typeparamref name="T"/> holds that value alone.
    /// O(selected lanes). Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddLanes<TLane>(Vector512<TLane> values, ulong lanes) where TLane : unmanaged
    {
        if (lanes != 0) AddSelected(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Vector512<TLane>, TLane>(ref values), Vector512<TLane>.Count), lanes);
    }

    /// <inheritdoc cref="AddLanes{TLane}(Vector512{TLane}, ulong)"/>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddLanes<TLane>(Vector<TLane> values, ulong lanes) where TLane : unmanaged
    {
        if (lanes != 0) AddSelected(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Vector<TLane>, TLane>(ref values), Vector<TLane>.Count), lanes);
    }

    // The selected elements of source, side by side at the end of the list (Lanes.Compact): T is laid out as TLane.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddSelected<TLane>(ReadOnlySpan<TLane> source, ulong lanes) where TLane : unmanaged
    {
        if (Unsafe.SizeOf<T>() != Unsafe.SizeOf<TLane>()) ThrowLayout();
        var store = _items.Store ?? Start(ref _items);
        int count = BitOperations.PopCount(lanes);
        if (store.Count + count > store.Items.Length) store.Grow(store.Count + count);
        var into = MemoryMarshal.CreateSpan(ref Unsafe.As<T, TLane>(ref store.Items[store.Count]), store.Items.Length - store.Count);
        store.Count += Lanes.Compact(source, lanes, into);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLayout() => throw new ArgumentException($"{typeof(T).Name} is not laid out as one value of the lanes it is emitted from");

    // The pass's first record: a list from the pool (a chunk's, or the singleton's first one).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BufferStore<T> Start(ref DynamicBuffer<T> items) => items.Store = EmitPool<T>.Rent();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Grow(BufferStore<T> store, T record)
    {
        store.Grow(store.Count + 1);
        store.Items[store.Count++] = record;
    }

    /// <summary>
    /// Append the records of a chunk's list <paramref name="chunk"/> to <paramref name="into"/> and give the list back to
    /// the pool. Called in chunk order when the pass ends, on the thread that started it. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Join(SingletonRef<Emitted<T>> into, ref DynamicBuffer<T> chunk)
    {
        var part = chunk.Store;
        if (part == null) return;
        chunk.Store = null;
        ref var items = ref into.Value.Items;
        if (items.Store is not { } target)
        {
            // The singleton's first records: the chunk's list becomes its list. Later passes copy into that list, which
            // keeps the largest size the records needed (a list taken from a chunk each pass would grow every pass, and
            // every list of the pool would end up the size of all the records).
            items.Store = part;
            return;
        }

        if (part.Count != 0)
        {
            if (target.Count + part.Count > target.Items.Length) target.Grow(target.Count + part.Count);
            Array.Copy(part.Items, 0, target.Items, target.Count, part.Count);
            target.Count += part.Count;
        }

        EmitPool<T>.Return(part);
    }
}

/// <summary>
/// The records the passes with an <see cref="Emit{T}"/> parameter appended, in row order of each pass and in the order
/// the passes ran: a singleton, added by the first pass that emits. The system that applies the records reads
/// <see cref="Items"/> (<c>foreach</c>, <c>AsReadOnlySpan</c>, <see cref="World.ForParallel{T, TJob}"/>) and clears it.
/// Snapshots and saves copy the records like a buffer component's.
/// </summary>
/// <typeparam name="T">The record.</typeparam>
public struct Emitted<T> : ISingletonComponent, IBuffer where T : struct
{
    // The only field, and DynamicBuffer<T>'s only field is its store: singleton boxes read the value as a BufferStore,
    // so snapshots and saves copy the elements, not the list (EECS012's concern).
#pragma warning disable EECS012
    /// <summary>The records: read them, then <c>Clear()</c> them (a <see cref="DynamicBuffer{T}"/>'s members).</summary>
    public DynamicBuffer<T> Items;
#pragma warning restore EECS012

    /// <summary>
    /// <paramref name="world"/>'s <see cref="Emitted{T}"/>, added when missing: the target a parallel pass's chunks are
    /// joined into. Owner thread (or before the parallel stage). Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static SingletonRef<Emitted<T>> Joined(World world)
    {
        world.GetOrAddSingleton<Emitted<T>>();
        return world.SingletonRef<Emitted<T>>();
    }

    private static readonly IBuffer Buffer = default(DynamicBuffer<T>);

    readonly bool IBuffer.Relational => false;

    readonly int IBuffer.RowSize => Buffer.RowSize;

    readonly int IBuffer.Layout => Buffer.Layout;

    readonly void IBuffer.Write(ref Writer writer, BufferStore store) => Buffer.Write(ref writer, store);

    readonly BufferStore IBuffer.Read(ref Reader reader, BufferStore reuse) => Buffer.Read(ref reader, reuse);

    readonly void IBuffer.Remap(BufferStore store, IdMap map) => Buffer.Remap(store, map);
}

/// <summary>
/// The lists of the chunks of passes emitting <typeparamref name="T"/>: taken by a chunk's first record (on any thread),
/// given back when the pass joins it (on the thread that started the pass). Unlike the buffer pool, a list keeps its
/// size whatever it grew to: a chunk emits about as many records each tick. Bounded: the pool holds at most as many
/// lists as chunks emitted at once, each of the largest size a chunk needed. One lock per list taken or given back,
/// once per chunk of at least 1,024 rows.
/// </summary>
internal static class EmitPool<T>
{
    private static readonly Lock Gate = new();
    private static BufferStore<T>[] _free = [];
    private static int _count;

    public static BufferStore<T> Rent()
    {
        lock (Gate)
        {
            if (_count == 0) return new BufferStore<T>(16);
            var store = _free[--_count];
            _free[_count] = null;
            return store;
        }
    }

    public static void Return(BufferStore<T> store)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(store.Items, 0, store.Count);
        store.Count = 0;
        lock (Gate)
        {
            if (_count == _free.Length) Array.Resize(ref _free, Math.Max(8, _count * 2));
            _free[_count++] = store;
        }
    }
}
