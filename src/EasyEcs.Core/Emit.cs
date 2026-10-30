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
    /// Hand the records of a chunk's list <paramref name="chunk"/> to <paramref name="into"/>, after those it holds:
    /// the list itself, not a copy of its records (a segment the singleton's list joins only when it is read as one span
    /// or written, <see cref="EmitStore{T}"/>). Called in chunk order when the pass ends, on the thread that started it.
    /// O(1). Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Join(SingletonRef<Emitted<T>> into, ref DynamicBuffer<T> chunk)
    {
        if (chunk.Store is not { } part) return;
        chunk.Store = null;
        ref var items = ref into.Value.Items;
        if (items.Store == null) items.Store = part; // the singleton's first records: the chunk's list becomes its list
        else EmitStore<T>.Of(ref items).Append((EmitStore<T>)part); // chunks' lists come from EmitPool
    }
}

/// <summary>
/// The records the passes with an <see cref="Emit{T}"/> parameter appended, in row order of each pass and in the order
/// the passes ran: a singleton, added by the first pass that emits. The system that applies the records reads
/// <see cref="Items"/> (<c>foreach</c>, <c>AsReadOnlySpan</c>, <see cref="World.ParallelFor{T, TJob}"/>) and clears it.
/// A parallel pass hands over its chunks' lists, not copies of their records: <c>Count</c>, <c>Clear</c>, a
/// <see cref="DrainAttribute"/> pass and <see cref="World.ParallelFor{T, TJob}"/> take the records where they
/// are, and the first read as one span (<c>foreach</c>, <c>AsReadOnlySpan</c>, the indexer) or write joins them into one
/// list, O(records) once. Snapshots and saves copy the records like a buffer component's.
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

    /// <summary>
    /// Runs <paramref name="job"/> on each record of <paramref name="world"/>'s <see cref="Emitted{T}"/> (none when it has
    /// no such singleton), in the order they were emitted, then clears them: a <see cref="DrainAttribute"/>
    /// method's pass. The records walked are those present when it starts: one the job adds is cleared unhandled. A job
    /// that throws leaves every record in place. O(records), no allocation. Owner thread. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Consume<TJob>(World world, ref TJob job) where TJob : IDrain<T>, allows ref struct
    {
        if (!world.HasSingleton<Emitted<T>>()) return;
        ref var items = ref world.GetSingleton<Emitted<T>>().Items; // a box of its own: the ref outlives other singletons' adds
        if (items.Store is not { } store) return;
        // A local copy: the JIT keeps its refs in registers instead of reloading them through 'job' for every record.
        var local = job;
        // The records side by side in runs: the list's own, then those of the chunks' lists parallel passes handed it,
        // walked where they are. Each run is found again from the records walked, so a job that makes the list join its
        // runs or grow (a write of Emitted<T>) leaves the walk on the same records: a list that grows is a new array.
        int total = store.Count;
        for (int done = 0; done < total;)
        {
            var records = store.Run(done, total);
            if (records.IsEmpty) break; // the job removed records
            for (int i = 0; i < records.Length; i++) local.Execute(in records[i]);
            done += records.Length;
        }

        job = local; // what the job keeps (the records of its Emit<T> parameters) goes back to the pass
        items.Clear();
    }

    private static readonly IBuffer Buffer = default(DynamicBuffer<T>);

    readonly bool IBuffer.Relational => false;

    readonly int IBuffer.RowSize => Buffer.RowSize;

    readonly int IBuffer.Layout => Buffer.Layout;

    readonly void IBuffer.Write(ref Writer writer, BufferStore store) => Buffer.Write(ref writer, store);

    readonly BufferStore IBuffer.Read(ref Reader reader, BufferStore reuse) => Buffer.Read(ref reader, reuse);

    readonly void IBuffer.FillPool(int lists) => Buffer.FillPool(lists);

    readonly void IBuffer.Remap(ref BufferStore store, IdMap map) => Buffer.Remap(ref store, map);
}

/// <summary>
/// The job of a <see cref="DrainAttribute"/> method, which the generator writes: the method called on one
/// record with the values its pass resolved (singletons, lookups). Run by <see cref="Emitted{T}.Consume{TJob}"/>.
/// </summary>
/// <typeparam name="T">The record.</typeparam>
public interface IDrain<T> where T : struct
{
    /// <summary>Handle <paramref name="record"/>.</summary>
    void Execute(in T record);
}

/// <summary>
/// The list of an <see cref="Emitted{T}"/> singleton, or of a chunk of a pass emitting <typeparamref name="T"/>
/// (<see cref="EmitPool{T}"/>), which becomes the singleton's when it holds none. A parallel pass hands it each chunk's
/// list in chunk order (<see cref="Append"/>) instead of copying their records in (MPI's gather without the copy, a
/// rope's segments): its records are then its own (<c>Items[0, head)</c>) followed by those of its segments in order,
/// <see cref="BufferStore{T}.Count"/> counts them all, and <see cref="BufferStore.Shares"/> reads
/// <see cref="BufferStore.Segmented"/>. Count, Clear (the segments back to the pool), a [Drain] pass and
/// <see cref="World.ParallelFor{T, TJob}"/> take the records where they are (<see cref="BufferStore{T}.Run"/>); a read
/// as one span, a write, a pass resolving the singleton (<see cref="World.SingletonRef{T}"/>, which an emitting pass on
/// one thread does too), a capture and a save join them first (<see cref="Settle"/>), once: O(records), the copy the
/// pass no longer makes. The list keeps the largest size the joined records needed; the segments, those their chunks needed.
/// </summary>
internal sealed class EmitStore<T>(int capacity) : BufferStore<T>(capacity)
{
    // Past this many segments an append joins them first: a list nobody reads or clears holds a bounded number of
    // segments (its records grow as a list's would), and a read walks at most this many.
    private const int MaxSegments = 64;

    private EmitStore<T>[] _segments = [];
    private int _segmentCount;
    private int _head;     // the records in Items while Segmented; Count counts the segments' too
    private SpinGate _gate; // a join reached from several threads at once (a hand-written parallel job reading the records)

    /// <summary>
    /// The list of <paramref name="items"/> (an <see cref="Emitted{T}"/>'s, which has one), as one that takes segments:
    /// a list the singleton got elsewhere (a write, a load, a restore) trades its array for one of the pool's (no copy),
    /// once. Owner thread.
    /// </summary>
    public static EmitStore<T> Of(ref DynamicBuffer<T> items)
    {
        if (items.Store is EmitStore<T> store) return store;
        return Adopt(ref items);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EmitStore<T> Adopt(ref DynamicBuffer<T> items)
    {
        var plain = items.Store;
        if (plain.Shares != 0) plain = plain.Unshare(); // a singleton's list is its own; never wrong to make sure
        var store = EmitPool<T>.Rent();
        (store.Items, plain.Items) = (plain.Items, store.Items);
        (store.Count, plain.Count) = (plain.Count, 0);
        items.Store = store;
        plain.Recycle();
        return store;
    }

    /// <summary>
    /// Hold the records of <paramref name="segment"/> (a chunk's list) after the ones held: the list itself, given
    /// back to the pool when the records are joined or cleared. O(1) (an empty one goes straight back). Owner thread.
    /// </summary>
    public void Append(EmitStore<T> segment)
    {
        if (segment.Count == 0)
        {
            EmitPool<T>.Return(segment);
            return;
        }

        if (Shares != Segmented)
        {
            _head = Count;
            Shares = Segmented;
        }

        if (_segmentCount == _segments.Length) Room();

        _segments[_segmentCount++] = segment;
        Count += segment.Count;
    }

    // No room for a segment: a larger array (warm-up), or past MaxSegments the records joined first.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Room()
    {
        if (_segments.Length < MaxSegments)
        {
            Array.Resize(ref _segments, Math.Max(16, _segments.Length * 2));
            return;
        }

        Settle();
        _head = Count;
        Shares = Segmented;
    }

    /// <summary>The records from <paramref name="start"/> on that lie side by side in one segment, up to <paramref name="end"/>.</summary>
    public Span<T> SegmentRun(int start, int end)
    {
        int first = _head; // the index of the segment's first record
        if (start < first) return Items.AsSpan(start, Math.Min(first, end) - start);
        for (int i = 0; i < _segmentCount; i++)
        {
            var segment = _segments[i];
            int next = first + segment.Count;
            if (start < next) return segment.Items.AsSpan(start - first, Math.Min(next, end) - start);
            first = next;
        }

        return default;
    }

    /// <summary>
    /// Copy the segments' records after the list's own and give the segments back to the pool: the records side by side
    /// in <c>Items</c>, the list no longer <see cref="BufferStore.Segmented"/>. O(records); allocates only when the list
    /// grows past its largest size. Under a gate: two threads reading the records at once join them once.
    /// </summary>
    public override void Settle()
    {
        using (new SpinScope(ref _gate))
        {
            if (Shares != Segmented) return;
            int count = Count;
            if (Items.Length < count)
            {
                var grown = new T[Math.Max(count, Items.Length * 2)];
                Array.Copy(Items, grown, _head);
                Items = grown;
            }

            int at = _head;
            for (int i = 0; i < _segmentCount; i++)
            {
                var segment = _segments[i];
                _segments[i] = null;
                segment.Items.AsSpan(0, segment.Count).CopyTo(Items.AsSpan(at));
                at += segment.Count;
                EmitPool<T>.Return(segment);
            }

            _segmentCount = 0;
            Volatile.Write(ref Shares, 0); // after the records: a thread that sees 0 reads them in place
        }
    }

    /// <summary>Remove the records: the segments back to the pool, uncopied; the list keeps its capacity.</summary>
    public override void Empty()
    {
        if (Shares == Segmented)
        {
            for (int i = 0; i < _segmentCount; i++)
            {
                EmitPool<T>.Return(_segments[i]);
                _segments[i] = null;
            }

            _segmentCount = 0;
            Count = _head;
            Shares = 0;
        }

        base.Empty();
    }

    public override void Recycle()
    {
        Empty();
        if (Shares != 0) Release(); // shared by a row that copied it by hand (EECS012): the other holder keeps it
        else EmitPool<T>.Return(this);
    }

    public override bool CopyFrom(BufferStore source)
    {
        if (Shares == Segmented) Empty(); // the records it copies replace these
        return base.CopyFrom(source);
    }

    public override BufferStore Share()
    {
        Settle();
        return base.Share();
    }

    public override BufferStore Copy()
    {
        Settle();
        return base.Copy();
    }
}

/// <summary>
/// The lists of the chunks of passes emitting <typeparamref name="T"/>: taken by a chunk's first record (on any thread),
/// given back when the records the chunk's list holds as a segment of an <see cref="Emitted{T}"/> are joined or cleared
/// (on the owner thread). Unlike the buffer pool, a list keeps its size whatever it grew to: a chunk emits about as many
/// records each tick. Bounded: the pool holds at most as many lists as segments held at once, each of the largest size a
/// chunk needed. One lock per list taken or given back, once per chunk of at least 1,024 rows.
/// </summary>
internal static class EmitPool<T>
{
    private static SpinGate Gate; // chunks on several threads rent and return at once (D128)
    private static EmitStore<T>[] _free = [];
    private static int _count;

    public static EmitStore<T> Rent()
    {
        using (new SpinScope(ref Gate))
        {
            if (_count == 0) return new EmitStore<T>(16);
            var store = _free[--_count];
            _free[_count] = null;
            return store;
        }
    }

    public static void Return(EmitStore<T> store)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(store.Items, 0, store.Count);
        store.Count = 0;
        using (new SpinScope(ref Gate))
        {
            if (_count == _free.Length) Array.Resize(ref _free, Math.Max(8, _count * 2));
            _free[_count++] = store;
        }
    }
}
