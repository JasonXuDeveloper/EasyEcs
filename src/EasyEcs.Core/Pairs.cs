using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// Marks a parameter of a <see cref="ForEachAttribute">[ForEach]</see> method as the other entity of a pair: the
/// method then runs for every pair of its entities and the entities holding the <c>[Neighbor]</c> components (the
/// row's own entity included when it holds them, as a loop over both would), and the generator writes the search that
/// skips the pairs the method's distance test turns away. A <c>[Neighbor]</c> parameter is a component taken by
/// <c>in</c>, one taken by <c>ref</c> to write it (a pickup marking its item taken; its pairs then run one at a time,
/// in the order a loop over both tables would visit them, and no component of the row's is one it writes), or the
/// neighbor's <see cref="Entity"/>.
/// <code>
/// [ForEach]
/// static void Separate(in Position p, ref Push push, [Neighbor] in Position other)
/// {
///     float dx = p.X - other.X, dy = p.Y - other.Y, d2 = dx * dx + dy * dy;
///     if (d2 &gt;= Range * Range || d2 == 0) return;          // the cutoff: farther pairs never run
///     push.X += (Range * Range - d2) * dx;
///     push.Y += (Range * Range - d2) * dy;
/// }
/// </code>
/// The cutoff may be a value of the row (<c>if (d2 &gt;= z.Vision * z.Vision) return;</c>), and the neighbors may be
/// other entities than the method's (the <c>[Neighbor]</c> components say which). The tables holding the compared
/// component keep their rows in space order (inferred from the test: <see cref="World.OrderRows{T, TOrder}"/>), and
/// the pass reads a cluster of rows against the rows within the cutoff only, a vector of rows per instruction where the
/// method qualifies. The pairs of a row run in the order of the tables' rows (not of the entities): a method that keeps
/// the nearest neighbor breaks ties by the entity (the lower <see cref="EntityId.Index"/>), so its result is the same
/// whatever the order.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class NeighborAttribute : Attribute;

/// <summary>
/// The job of a pair method (<see cref="NeighborAttribute">[Neighbor]</see> parameters), written by the generator:
/// the cutoff its distance test compares against and the method over one table's rows against another's (or its own),
/// cluster by cluster. <see cref="World.ForPairs{T, TOrder, TJob}"/> runs it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPairJob
{
    /// <summary>
    /// The method's cutoff depends on the row (its test compares against a value of the row's components): the pass
    /// reads <see cref="CutoffsSquared"/> instead of <see cref="CutoffSquared"/>.
    /// </summary>
    bool CutoffPerRow { get; }

    /// <summary>
    /// The square of the distance the method's test lets pairs through within, read once per pass: a pair of rows
    /// farther apart than its root on some axis never runs. NaN or positive infinity: every pair may run; 0 or
    /// negative: only pairs at one point do (and rows whose position is NaN, which every test lets through).
    /// </summary>
    double CutoffSquared { get; }

    /// <summary>
    /// The square of each row's cutoff (as <see cref="CutoffSquared"/>) for the rows of <paramref name="table"/>'s
    /// table (<see cref="PairRun.Rows{T}"/>; it has no clusters): row r into <paramref name="into"/>[r]. Read once per
    /// pass when <see cref="CutoffPerRow"/>.
    /// </summary>
    void CutoffsSquared(in PairRun table, Span<double> into);

    /// <summary>
    /// The rows of the method's table one step of <see cref="Run"/> handles at once when it runs cluster by cluster:
    /// <c>Vector&lt;float&gt;.Count</c> where its vector loop runs, else 1. The pass weighs lists of each row's neighbors
    /// (<see cref="PairRun.Listed"/>) against it.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// The rows of <see cref="PairRun"/>'s table against the rows of its other table, clusters
    /// <see cref="PairRun.First"/> to <see cref="PairRun.End"/> - 1 (all of them, or one chunk's of a parallel pass, which
    /// runs on several threads: each writes its clusters' rows only).
    /// </summary>
    void Run(in PairRun run);

    /// <summary>
    /// Stamp the rows of the method's table the run's clusters ran for in the tracked components the method writes of
    /// its row, after <see cref="Run"/> (after every chunk of a parallel pass, on the calling thread).
    /// </summary>
    void Stamp(in PairRun run);
}

/// <summary>
/// One table's rows (the method's own) against one table's rows (the neighbors': the same table or another) for a pair
/// job (<see cref="IPairJob.Run"/>): clusters of <see cref="ClusterSize"/> rows of the first, each with the rows of the
/// other to visit (<see cref="Ranges"/>), every other row farther than the cutoff from all of the cluster's.
/// Valid during the call it is passed to.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly ref struct PairRun
{
    private readonly World _world;
    private readonly Archetype _table, _other;
    private readonly ulong[] _rows, _neighbors;
    private readonly int[] _starts, _ranges, _nearStarts, _near;
    private readonly float[] _reach;
    private readonly int _first, _end, _size;

    internal PairRun(World world, Archetype table, Archetype other, ulong[] rows, ulong[] neighbors, int[] starts,
        int[] ranges, float[] reach, int first, int end, int size, int[] nearStarts = null, int[] near = null)
    {
        _nearStarts = nearStarts;
        _near = near;
        _reach = reach;
        _world = world;
        _table = table;
        _other = other;
        _rows = rows;
        _neighbors = neighbors;
        _starts = starts;
        _ranges = ranges;
        _first = first;
        _end = end;
        _size = size;
    }

    /// <summary>Rows per cluster: a vector of floats (8 where 256-bit vectors are hardware, 4 on ARM64).</summary>
    public int ClusterSize => _size;

    /// <summary>The first cluster of the method's table this run covers: row <c>w * ClusterSize</c> starts cluster w.</summary>
    public int First => _first;

    /// <summary>The cluster after the last this run covers (the table's cluster count, or a chunk's end).</summary>
    public int End => _end;

    /// <summary>The rows of the method's table (rows of the last cluster past it are none).</summary>
    public int Count => _table.Count;

    /// <summary>The neighbors' table is the method's own: a row meets itself in its own cluster.</summary>
    public bool Same => ReferenceEquals(_table, _other);

    /// <summary>
    /// The rows of cluster <paramref name="cluster"/> the method runs for, one bit per row from its first (row
    /// <c>cluster * ClusterSize</c>): rows of the table that pass the method's filter. A whole cluster is
    /// <c>(1 &lt;&lt; ClusterSize) - 1</c>. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Lanes(int cluster)
    {
        int row = cluster * _size;
        // _rows holds a word per 64 rows of every cluster (ClusterSize divides 64): in bounds for every cluster.
        ulong word = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_rows), row >> 6);
        return (word >> (row & 63)) & (ulong.MaxValue >> (64 - _size));
    }

    /// <summary>
    /// The rows of the other table cluster <paramref name="cluster"/> meets, as (first, end) pairs in row order; every
    /// row outside them is farther than the cutoff from each of the cluster's. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> Ranges(int cluster)
    {
        int start = _starts[cluster];
        return _ranges.AsSpan(start, _starts[cluster + 1] - start);
    }

    /// <summary>
    /// The run carries a list of each row's neighbors (<see cref="Near"/>): the pass found them fewer than the rows of
    /// the clusters' ranges by enough that visiting them one row at a time costs less (the density pick). The rows of
    /// <see cref="Ranges"/> each row's list leaves out are farther than the cutoff from it.
    /// </summary>
    public bool Listed => _nearStarts != null;

    /// <summary>
    /// The rows of the other table near row <paramref name="row"/> of the method's table, ascending (only when
    /// <see cref="Listed"/>): a subset of its cluster's <see cref="Ranges"/> that holds every row within the cluster's
    /// <see cref="Cutoff"/> of it. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> Near(int row)
    {
        // _nearStarts holds Count + 1 starts of the method's table, rising within _near: in bounds for every row.
        ref int starts = ref MemoryMarshal.GetArrayDataReference(_nearStarts);
        int start = Unsafe.Add(ref starts, row);
        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_near), start), Unsafe.Add(ref starts, row + 1) - start);
    }

    /// <summary>
    /// The cutoff of cluster <paramref name="cluster"/> this pass: the largest cutoff of its rows, a little over it (never
    /// below: a pair farther than it fails the method's test), infinity without a cutoff. The method per pair skips the
    /// neighbors past it without a call. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Cutoff(int cluster) => _reach[cluster];

    /// <summary>Some rows of the other table fail the neighbors' filter: <see cref="Skip"/> tells which.</summary>
    public bool Skips => _neighbors != null;

    /// <summary>Row <paramref name="row"/> of the other table fails the neighbors' filter (only when <see cref="Skips"/>). O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Skip(int row) =>
        // _neighbors holds a word per 64 rows of the other table and the ranges end at its count: in bounds.
        ((Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_neighbors), row >> 6) >> row) & 1) == 0; // shift is mod 64

    /// <summary>Row 0 of the method's table's column of <typeparamref name="T"/> (a component of its filter). O(1).</summary>
    public ref T Rows<T>() where T : struct, IComponent => ref _table.Data<T>(_table.ColumnIndex(ComponentType<T>.Bit));

    /// <summary>Row 0 of the other table's column of <typeparamref name="T"/> (a component of the neighbors' filter). O(1).</summary>
    public ref T Others<T>() where T : struct, IComponent => ref _other.Data<T>(_other.ColumnIndex(ComponentType<T>.Bit));

    /// <summary>What a chunk of a parallel pass rebuilds the run from (its own clusters).</summary>
    internal (World, Archetype, Archetype, ulong[], ulong[], int[], int[], float[], int[], int[]) Parts() =>
        (_world, _table, _other, _rows, _neighbors, _starts, _ranges, _reach, _nearStarts, _near);

    /// <summary>The entity of row <paramref name="row"/> of the method's table. O(1).</summary>
    public Entity Entity(int row) => new(_world, _table.Slots[row]);

    /// <summary>The entity of row <paramref name="row"/> of the other table. O(1).</summary>
    public Entity Other(int row) => new(_world, _other.Slots[row]);

    /// <summary>
    /// Stamp the rows the method runs for in the run's clusters of the method's table as written now (a tracked
    /// <typeparamref name="T"/> it writes; nothing for another). O(rows / 64). Calling thread only.
    /// </summary>
    public void Stamp<T>() where T : struct, IComponent
    {
        if (default(T) is not ITracked) return;
        ulong tick = _world.ChangeTick;
        int from = _first * _size, to = Math.Min(_end * _size, _table.Count);
        for (int w = from >> 6, words = (to + 63) >> 6; w < words; w++)
        {
            ulong rows = _rows[w] & RowFilter.RowMask(w, to);
            if (w == from >> 6) rows &= ulong.MaxValue << (from & 63);
            if (rows != 0) _table.StampWord<T>(w, rows, tick);
        }
    }

    /// <summary>
    /// Stamp row <paramref name="row"/> of the other table as written now (a tracked <typeparamref name="T"/> the
    /// method writes of the neighbor, <c>[Neighbor] ref</c>; nothing for another). O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampOther<T>(int row) where T : struct, IComponent
    {
        if (default(T) is ITracked) _other.Stamp<T>(row, _world.ChangeTick);
    }
}

public sealed partial class World
{
    // The state of each pair job type this world ran (PairJobs<TJob>.Id): its clusters' boxes and lists.
    private PairState[] _pairStates = [];

    /// <summary>
    /// Run a pair job (a generated <see cref="NeighborAttribute">[Neighbor]</see> method): the rows matching
    /// <paramref name="rows"/> against the rows matching <paramref name="neighbors"/>, both holding
    /// <typeparamref name="T"/>, whose point <typeparamref name="TOrder"/> reads (<paramref name="axes"/> axes). The
    /// tables of <typeparamref name="T"/> are ordered by <typeparamref name="TOrder"/>'s key the first time (sorted at
    /// once outside a parallel stage); clusters of rows get boxes over their points, checked every pass and widened by
    /// a margin, so the lists of clusters within the cutoff hold until a row leaves its box, a table changes or a
    /// row's cutoff grows. Owner thread (or the system's), outside other passes; allocation-free in steady state.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ForPairs<T, TOrder, TJob>(ref TJob job, in QueryFilter rows, in QueryFilter neighbors, int axes)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T> where TJob : struct, IPairJob, allows ref struct
    {
        var state = BeginPairs<T, TOrder, TJob>(ref job, in rows, in neighbors, axes, out float reach);
        bool froze = Gate.FreezeAlone();
        try
        {
            while (state.Next<T, TOrder, TJob>(this, ref job, axes, reach, out var run))
            {
                job.Run(in run);
                job.Stamp(in run);
            }
        }
        finally
        {
            if (froze) Gate.ThawAlone();
        }
    }

    /// <summary>
    /// <see cref="ForPairs{T, TOrder, TJob}"/> on several threads (a <c>[ForEach(Parallel = true)]</c> pair method that
    /// writes no neighbor): the boxes and lists are drawn on the calling thread as there, then each table's clusters are
    /// cut into chunks of whole 64-row words (about four per thread, or by the clusters' measured cost) that the chunk
    /// runner's threads claim, each running a copy of the job over its clusters' rows: a row is written by its own cluster
    /// only, so the result is the serial pass's bit for bit. Stamps follow on the calling thread. Owner thread, outside
    /// other passes; allocation-free after the first call per job type.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ForPairsParallel<T, TOrder, TJob>(ref TJob job, in QueryFilter rows, in QueryFilter neighbors, int axes)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T> where TJob : struct, IPairJob
    {
        var state = BeginPairs<T, TOrder, TJob>(ref job, in rows, in neighbors, axes, out float reach);
        var work = WorkOf<PairChunks<TJob>>();
        bool froze = Gate.FreezeAlone();
        try
        {
            while (state.Next<T, TOrder, TJob>(this, ref job, axes, reach, out var run))
            {
                // Chunks of whole words of rows: a chunk's rows never share a presence or tick word with another's.
                int perWord = 64 / run.ClusterSize, clusters = run.End;
                int size = ChunkRows(work, (long)clusters * run.ClusterSize, 0) / run.ClusterSize;
                size = (size + perWord - 1) / perWord * perWord;
                work.Set(job, in run, size);
                try
                {
                    RunChunks(work, (clusters + size - 1) / size); // one chunk runs here, timed now and then as any pass
                }
                finally
                {
                    work.Release();
                }

                job.Stamp(in run);
            }
        }
        finally
        {
            if (froze) Gate.ThawAlone();
        }
    }

    /// <summary>The pair state of <typeparamref name="TJob"/>, its tables ordered (requested once), its pass begun.</summary>
    private PairState BeginPairs<T, TOrder, TJob>(ref TJob job, in QueryFilter rows, in QueryFilter neighbors, int axes, out float reach)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T> where TJob : struct, IPairJob, allows ref struct
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        var state = PairStateOf(PairJobs<TJob>.Id);
        reach = job.CutoffPerRow ? float.NaN : PairState.Reach(job.CutoffSquared);
        // Every pair runs without a cutoff: no order helps.
        if (!state.Ordered) state.Ordered = reach == float.PositiveInfinity || OrderPairs<T, TOrder>(axes, reach);
        state.Begin(this, in rows, in neighbors);
        return state;
    }

    /// <summary>The chunks of a parallel pair pass over one table against one: chunk i is clusters [i × Size, (i + 1) × Size).</summary>
    private sealed class PairChunks<TJob> : ParallelWork where TJob : struct, IPairJob
    {
        private TJob _job;
        private World _world;
        private Archetype _table, _other;
        private ulong[] _rows, _neighbors;
        private int[] _starts, _ranges, _nearStarts, _near;
        private float[] _reach;
        private int _end, _size, _clusters;

        public void Set(TJob job, in PairRun run, int clusters)
        {
            _job = job;
            (_world, _table, _other, _rows, _neighbors, _starts, _ranges, _reach, _nearStarts, _near) = run.Parts();
            _end = run.End;
            _size = run.ClusterSize;
            _clusters = clusters;
        }

        public override void Run(int index)
        {
            var job = _job;
            int first = index * _clusters;
            job.Run(new PairRun(_world, _table, _other, _rows, _neighbors, _starts, _ranges, _reach, first, Math.Min(first + _clusters, _end), _size, _nearStarts, _near));
        }

        /// <summary>Drop the references of the last call until the next one.</summary>
        public void Release()
        {
            _job = default;
            _world = null;
            _table = _other = null;
            _rows = _neighbors = null;
            _starts = _ranges = _nearStarts = _near = null;
            _reach = null;
        }
    }

    /// <summary>
    /// Ask for the order of the tables of <typeparamref name="T"/> a pair method needs (the Morton code of the point
    /// quantized by the cutoff) and sort them now: false when another pass or a parallel stage is running, so the
    /// request waits for the next pass (the pairs are right either way, only slower).
    /// </summary>
    private bool OrderPairs<T, TOrder>(int axes, float reach) where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        if (RequestedOrder(ComponentType<T>.Bit) != null || ComponentInfo.Of(ComponentType<T>.Bit).Defaults?.Order != null) return true;
        if (Volatile.Read(ref PassDepth) != 0 || !Gate.Free) return false;
        float cell = reach is > 0 and < float.PositiveInfinity ? reach : 1; // a cutoff per row: cells of 1
        OrderRows<T, TOrder>(axes, cell, PairState.Interval, cell * PairState.MarginShare, Vector<float>.Count);
        SortRows<T>();
        return true;
    }

    private PairState PairStateOf(int id)
    {
        var states = _pairStates;
        if ((uint)id < (uint)states.Length && states[id] is { } state) return state;
        return NewPairState(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PairState NewPairState(int id)
    {
        if (id >= _pairStates.Length) Array.Resize(ref _pairStates, Math.Max(id + 1, _pairStates.Length * 2));
        return _pairStates[id] = new PairState();
    }
}

/// <summary>A dense id per pair job type: its slot in each world's pair states.</summary>
internal static class PairJobs<TJob> where TJob : allows ref struct
{
    public static readonly int Id = PairJobs.Next();
}

internal static class PairJobs
{
    private static int _next = -1;

    public static int Next() => Interlocked.Increment(ref _next);
}

/// <summary>
/// What one pair job keeps in one world between passes: per table, the boxes of its clusters of rows (each widened by
/// the margin when kept) and the reach of each cluster (the largest cutoff of its rows), and per pair of tables, the
/// clusters of the other that each cluster's box widened by its reach meets (as row ranges). A pass draws every cluster's
/// box again and compares it with the kept one: while every box stays inside and no reach grows, the lists hold (no
/// pair within the cutoff can be missing); a box that left, a table that grew, shrank or was sorted, or a reach that
/// grew draws them again. Owner thread only (the job's pass); memory bounded by the tables.
/// </summary>
internal sealed class PairState
{
    /// <summary>Updates between sorts of the ordered tables (GROMACS's nstlist).</summary>
    public const int Interval = 10;

    /// <summary>The margin a box is widened by at first, as a share of the reach (GROMACS's buffer: rlist = 1.1 rc).</summary>
    public const float MarginShare = 0.1f;

    /// <summary>The tables of the component are ordered for this job (or by another request or [OrderBy]).</summary>
    public bool Ordered;

    private Clusters[] _tables = new Clusters[4];
    private int _tableCount;
    private PairList[] _lists = new PairList[4];
    private int _listCount;
    private int _pass;

    // The margin boxes are kept with: a share of the largest finite reach, grown when boxes escape soon after they were
    // kept (the rows move fast), shrunk when a sort keeps them again before any escaped.
    private float _margin = float.NaN, _largest;

    // Scratch of one pass: the method's rows that pass (a word per 64 rows), the neighbors' that pass, the rows' cutoffs.
    private ulong[] _rows = new ulong[64], _neighbors = new ulong[64];
    private double[] _cutoffs = new double[64];

    // Scratch of the sweep that finds the clusters within reach.
    private float[] _keys = new float[64], _sides = new float[64 * 5];
    private int[] _ids = new int[64];
    private int[] _pairA = new int[64], _pairB = new int[64];

    /// <summary>
    /// The distance on one axis beyond which no pair passes a test of <paramref name="squared"/>, widened a little
    /// against the rounding of the method's float arithmetic (a pair whose distance on one axis is past the reach has a
    /// squared distance past the cutoff whatever the rounding): infinity for NaN or infinity, 0 for 0 or less.
    /// </summary>
    public static float Reach(double squared)
    {
        if (!(squared < double.PositiveInfinity)) return float.PositiveInfinity; // NaN, +inf
        if (squared <= 0) return 0;
        float reach = (float)(Math.Sqrt(squared) * (1 + 1e-4) + 1e-30);
        return float.IsFinite(reach) ? reach : float.PositiveInfinity;
    }

    // The pass's cursor: the tables of each side, the method's table being run (its clusters) and the next neighbors' one.
    private QueryMatch _mine, _theirs;
    private Clusters _own;
    private int _a, _b;

    /// <summary>A pass begins: its tables, the cursor before the first pair of tables (<see cref="Next"/>).</summary>
    public void Begin(World world, in QueryFilter rows, in QueryFilter neighbors)
    {
        _pass++;
        _mine = world.GetQueryEntry(default, in rows).Match;
        _theirs = world.GetQueryEntry(default, in neighbors).Match;
        _own = null;
        _a = 0;
    }

    /// <summary>
    /// The next pair of tables of the pass (a table of rows, then each table of neighbors), its clusters checked and
    /// its lists drawn: all its clusters into <paramref name="run"/>; false after the last.
    /// </summary>
    public bool Next<T, TOrder, TJob>(World world, scoped ref TJob job, int axes, float reach, out PairRun run)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T> where TJob : struct, IPairJob, allows ref struct
    {
        int bit = ComponentType<T>.Bit;
        int size = Vector<float>.Count;
        while (true)
        {
            if (_own == null)
            {
                if (_a == _mine.Archetypes.Length)
                {
                    _mine = _theirs = null; // nothing held between passes
                    run = default;
                    return false;
                }

                var table = _mine.Archetypes[_a];
                var filter = new RowFilter(_mine.Filters[_a++], 0);
                int mode = filter.Mode(table);
                if (table.AliveCount == 0 || mode == RowFilter.None) continue;
                Passing(table, filter, mode, ref _rows);
                _own = Refresh<T, TOrder>(table, bit, axes, size);
                if (Reaches(world, ref job, _own, reach)) Keep(_own); // a cutoff grew past the one the lists were drawn for
                _b = 0;
            }

            if (_b == _theirs.Archetypes.Length)
            {
                _own = null;
                continue;
            }

            var own = _own;
            var other = _theirs.Archetypes[_b];
            var otherFilter = new RowFilter(_theirs.Filters[_b++], 0);
            int otherMode = otherFilter.Mode(other);
            if (other.AliveCount == 0 || otherMode == RowFilter.None) continue;
            var their = ReferenceEquals(other, own.Table) ? own : Refresh<T, TOrder>(other, bit, axes, size);
            bool skips = otherMode == RowFilter.Test || other.Count != other.AliveCount;
            if (skips) Passing(other, otherFilter, otherMode, ref _neighbors);
            var list = ListOf(own, their);
            bool listed = Listed<T, TOrder>(list, bit, job.Width);
            run = new PairRun(world, own.Table, other, _rows, skips ? _neighbors : null, list.Starts, list.Ranges, own.Cutoff, 0,
                own.ClusterCount, size, listed ? list.NearStarts : null, listed ? list.Near : null);
            return true;
        }
    }

    /// <summary>
    /// The rows of <paramref name="table"/> that pass <paramref name="filter"/>, a word per 64 rows into
    /// <paramref name="into"/> (tombstones and rows past the table's count clear).
    /// </summary>
    private static void Passing(Archetype table, RowFilter filter, int mode, ref ulong[] into)
    {
        int count = table.Count, words = (count + 63) >> 6;
        if (into.Length < words) into = new ulong[Math.Max(words, into.Length * 2)]; // warm-up only
        bool tombstones = count != table.AliveCount;
        ref EntityId slots = ref MemoryMarshal.GetArrayDataReference(table.Slots);
        for (int w = 0; w < words; w++)
        {
            ulong bits = RowFilter.RowMask(w, count);
            if (mode == RowFilter.Test) bits &= filter.Word(table, w);
            if (tombstones && bits != 0) bits &= World.AliveRows(ref Unsafe.Add(ref slots, w << 6), Math.Min(64, count - (w << 6)));
            into[w] = bits;
        }
    }

    /// <summary>
    /// Each cluster's reach of <paramref name="clusters"/> (the largest of its passing rows' cutoffs; every row's
    /// <paramref name="reach"/> unless NaN: a cutoff per row), into <see cref="Clusters.Cutoff"/>; true when one grew past
    /// its kept reach. O(rows).
    /// </summary>
    private bool Reaches<TJob>(World world, ref TJob job, Clusters clusters, float reach) where TJob : struct, IPairJob, allows ref struct
    {
        var table = clusters.Table;
        int count = table.Count, size = clusters.Size, n = clusters.ClusterCount;
        var reaches = clusters.Cutoff;
        float largest = 0;
        if (!float.IsNaN(reach))
        {
            reaches.AsSpan(0, n).Fill(reach);
            largest = reach;
        }
        else
        {
            if (_cutoffs.Length < count) _cutoffs = new double[Math.Max(count, _cutoffs.Length * 2)]; // warm-up only
            job.CutoffsSquared(new PairRun(world, table, table, _rows, null, null, null, null, 0, 0, size), _cutoffs.AsSpan(0, count));
            for (int w = 0; w < n; w++)
            {
                int first = w * size;
                ulong lanes = (_rows[first >> 6] >> (first & 63)) & (ulong.MaxValue >> (64 - size));
                float most = 0;
                for (; lanes != 0; lanes &= lanes - 1)
                    most = Math.Max(most, Reach(_cutoffs[first + BitOperations.TrailingZeroCount(lanes)]));
                reaches[w] = most;
                largest = Math.Max(largest, most);
            }
        }

        if (float.IsFinite(largest) && largest > _largest)
        {
            _largest = largest;
            if (float.IsNaN(_margin)) _margin = largest * MarginShare;
        }

        var kept = clusters.KeptCutoff;
        for (int w = 0; w < n; w++)
            if (reaches[w] > kept[w]) return true;
        return false;
    }

    /// <summary>
    /// The clusters of <paramref name="table"/>, their boxes checked against the rows' points now (once per pass): kept
    /// again (and the lists of the table drawn again with them) when the table was sorted, grew or shrank, or a
    /// cluster's rows left its kept box.
    /// </summary>
    private Clusters Refresh<T, TOrder>(Archetype table, int bit, int axes, int size)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        Clusters clusters = null;
        for (int i = 0; i < _tableCount; i++)
            if (ReferenceEquals(_tables[i].Table, table))
            {
                clusters = _tables[i];
                break;
            }

        if (clusters == null)
        {
            if (_tableCount == _tables.Length) Array.Resize(ref _tables, _tableCount * 2);
            _tables[_tableCount++] = clusters = new Clusters(table);
        }

        if (clusters.Pass == _pass) return clusters;
        clusters.Pass = _pass;
        int count = table.Count, n = (count + size - 1) / size;
        long version = table.Order?.Version ?? 0;
        clusters.Reserve(n);
        bool changed = !clusters.Kept || clusters.Count != count || clusters.Version != version || clusters.Size != size;
        clusters.Size = size;
        clusters.ClusterCount = n;
        clusters.Axes = axes;
        bool escaped = clusters.Draw<T, TOrder>(table, bit, !changed);
        clusters.Passes++;
        if (float.IsFinite(_margin))
        {
            if (escaped && clusters.Passes < Interval / 2) _margin = Math.Min(_margin * 1.5f, _largest); // the rows move fast
            else if (changed && clusters.Kept && !clusters.Escapes) _margin = Math.Max(_margin * 0.9f, _largest * 0.02f); // a sort came first
        }

        if (changed || escaped)
        {
            clusters.Count = count;
            clusters.Version = version;
            clusters.Passes = 0;
            clusters.Escapes = escaped;
            Keep(clusters);
        }

        return clusters;
    }

    private void Keep(Clusters clusters) => clusters.Keep(float.IsFinite(_margin) ? _margin : 0);

    /// <summary>The clusters of <paramref name="other"/> each cluster of <paramref name="table"/> meets, drawn again when either was kept again.</summary>
    private PairList ListOf(Clusters table, Clusters other)
    {
        PairList list = null;
        for (int i = 0; i < _listCount; i++)
            if (ReferenceEquals(_lists[i].Table, table) && ReferenceEquals(_lists[i].Other, other))
            {
                list = _lists[i];
                break;
            }

        if (list == null)
        {
            if (_listCount == _lists.Length) Array.Resize(ref _lists, _listCount * 2);
            _lists[_listCount++] = list = new PairList(table, other);
        }

        if (list.Builds != table.Builds || list.OtherBuilds != other.Builds) Draw(list);
        return list;
    }

    /// <summary>
    /// The pairs (cluster of the table, cluster of the other) where the table's kept box widened by the cluster's kept
    /// reach meets the other's kept box on every axis (a sweep over both sets of boxes sorted by their low side on axis
    /// 0, as <see cref="RowClusters.Pairs(float, Span{ClusterPair})"/>), then per cluster of the table the other's clusters
    /// in row order, as row ranges (neighboring clusters merged). One table against itself sweeps its boxes twice, once
    /// widened, once not, so a cluster meets itself and each direction of a pair is tested with its own reach.
    /// O(clusters log clusters + pairs).
    /// </summary>
    private void Draw(PairList list)
    {
        var a = list.Table;
        var b = list.Other;
        int na = a.ClusterCount, nb = b.ClusterCount, axes = a.Axes;
        int m = 0, n = na + nb;
        if (_keys.Length < n)
        {
            _keys = new float[n * 2]; // warm-up only
            _ids = new int[n * 2];
            _sides = new float[n * 2 * 5];
        }

        for (int i = 0; i < na; i++)
            if (a.Low[i] <= a.High[i]) // an inverted box holds no row
            {
                _keys[m] = a.Low[i] - a.KeptCutoff[i];
                _ids[m++] = i;
            }

        for (int i = 0; i < nb; i++)
            if (b.Low[i] <= b.High[i])
            {
                _keys[m] = b.Low[i];
                _ids[m++] = ~i;
            }

        Array.Sort(_keys, _ids, 0, m);
        var high0 = _sides.AsSpan(0, m);
        var low1 = _sides.AsSpan(m, m);
        var high1 = _sides.AsSpan(2 * m, m);
        var low2 = _sides.AsSpan(3 * m, m);
        var high2 = _sides.AsSpan(4 * m, m);
        for (int k = 0; k < m; k++)
        {
            int id = _ids[k];
            var w = id >= 0 ? a : b;
            int i = id >= 0 ? id : ~id, c = w.Capacity;
            float r = id >= 0 ? a.KeptCutoff[i] : 0;
            high0[k] = w.High[i] + r;
            low1[k] = axes > 1 ? w.Low[c + i] - r : 0;
            high1[k] = axes > 1 ? w.High[c + i] + r : 0;
            low2[k] = axes > 2 ? w.Low[2 * c + i] - r : 0;
            high2[k] = axes > 2 ? w.High[2 * c + i] + r : 0;
        }

        int found = 0;
        for (int i = 0; i < m; i++)
        {
            int p = _ids[i];
            float high = high0[i], l1 = low1[i], h1 = high1[i], l2 = low2[i], h2 = high2[i];
            for (int j = i + 1; j < m && _keys[j] <= high; j++)
            {
                int q = _ids[j];
                if ((q >= 0) == (p >= 0)) continue; // two boxes of one side
                if (low1[j] > h1 || l1 > high1[j] || low2[j] > h2 || l2 > high2[j]) continue;
                if (p >= 0) Add(ref found, p, ~q);
                else Add(ref found, q, ~p);
            }
        }

        // Per cluster of a: its clusters of b in row order (a counting sort by cluster of a, then each list sorted).
        list.Reserve(na, found);
        var starts = list.Starts;
        Array.Clear(starts, 0, na + 2);
        for (int k = 0; k < found; k++) starts[_pairA[k] + 2]++;
        for (int i = 0; i < na; i++) starts[i + 2] += starts[i + 1];
        var order = list.Order;
        for (int k = 0; k < found; k++) order[starts[_pairA[k] + 1]++] = _pairB[k];
        // starts[i + 1] is now the end of cluster i's clusters, starts[i] its start.
        int size = b.Size, rows = b.Count, at = 0;
        var ranges = list.Ranges;
        for (int i = 0; i < na; i++)
        {
            int from = starts[i], to = starts[i + 1];
            starts[i] = at;
            order.AsSpan(from, to - from).Sort();
            for (int k = from; k < to; k++)
            {
                int first = order[k] * size, end = Math.Min(first + size, rows);
                if (at > starts[i] && ranges[at - 1] == first) ranges[at - 1] = end; // the cluster after the last: one range
                else
                {
                    ranges[at++] = first;
                    ranges[at++] = end;
                }
            }
        }

        starts[na] = at;
        list.Builds = a.Builds;
        list.OtherBuilds = b.Builds;
        list.Draws++;
    }

    /// <summary>How a pass picks the lists of each row's neighbors: by their cost (Auto), never, or whenever they hold (On: tests, benchmarks).</summary>
    internal enum ListMode { Auto, Off, On }

    /// <summary>The pick of every pair pass of the process (benchmarks and tests set it).</summary>
    internal static ListMode Lists = ListMode.Auto;

    /// <summary>
    /// What a list's entry costs, in steps of the clusters' ranges (a neighbor against a cluster): the entry's load, the
    /// neighbor's row out of stride, the distance test (calibrated with PairsBenchmark).
    /// </summary>
    internal const float ListCost = 2;

    /// <summary>A pick of the lists must save this share of the ranges' cost at least, against the error of its estimate.</summary>
    internal const float ListGain = 0.8f;

    /// <summary>About how many clusters of a table the estimate of a list's entries tests (<see cref="Estimate"/>).</summary>
    private const int Samples = 64;

    /// <summary>The draws of a list's ranges between two counts of the share of its candidates within its radius.</summary>
    private const int Resample = 16;

    /// <summary>
    /// Whether this pass of <paramref name="list"/>'s tables visits each row's list of neighbors (Verlet's list: the
    /// rows of the ranges within the reach plus the skin, the boxes' margin) instead of the clusters' ranges: the list
    /// built (O(candidates)) or kept (O(rows): it holds while the rows of both tables together moved less than the skin
    /// since it was built and the ranges were not drawn again). Auto builds one only where it costs less per pass than
    /// the ranges for a method of <paramref name="width"/> rows per step, its build spread over the passes the last
    /// one held: dense neighborhoods and wide vectors keep the ranges.
    /// </summary>
    private bool Listed<T, TOrder>(PairList list, int bit, int width) where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        var mode = Lists;
        if (mode == ListMode.Off) return false;
        Clusters own = list.Table, their = list.Other;
        float skin = float.IsFinite(_margin) ? _margin : 0;
        if (list.Listed)
        {
            if (list.ListedDraw == list.Draws && list.SaveA == own.Saves && list.SaveB == their.Saves && list.Skin == skin &&
                Moved<T, TOrder>(own, bit) + Moved<T, TOrder>(their, bit) + list.Slack <= skin * 0.99f)
                return true;
            list.Listed = false;
            list.Life = Math.Max(1, _pass - list.BuiltPass);
        }

        if (mode == ListMode.Auto)
        {
            if (list.EstimatedDraw != list.Draws) Estimate<T, TOrder>(list, bit, skin);
            // Per pass: the ranges' steps against the list's entries, its build (a test per candidate) spread over the
            // passes the last one held, and the check of both tables' rows.
            float ranges = list.Candidates / width;
            float lists = list.Entries * ListCost + list.Candidates / list.Life + own.Table.Count + their.Table.Count;
            if (!(lists < ranges * ListGain)) return false;
        }

        return Build<T, TOrder>(list, bit, skin);
    }

    /// <summary>
    /// The candidates of <paramref name="list"/>'s ranges (each cluster's rows times the rows of its ranges, O(clusters))
    /// and its entries: the candidates times the share of them within the list's radius, counted on 64 clusters spread
    /// over the table (each one of a smaller table) at the first draw and every <see cref="Resample"/>th one after
    /// (O(the candidates of 64 clusters)). Counting on every eighth cluster at each draw cost a moving crowd of 100,000
    /// rows 27 % of its pass, and on 64 clusters at each draw 8 % of a sparse one of 10,000 (PairListsBenchmark).
    /// </summary>
    private void Estimate<T, TOrder>(PairList list, int bit, float skin) where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        list.EstimatedDraw = list.Draws;
        bool sample = list.Estimates++ % Resample == 0;
        list.Candidates = list.Entries = 0;
        Clusters own = list.Table, their = list.Other;
        var table = own.Table;
        int a = table.ColumnIndex(bit), b = their.Table.ColumnIndex(bit);
        if (a < 0 || b < 0) return;
        ref T rows = ref table.Data<T>(a);
        ref T others = ref their.Table.Data<T>(b);
        int count = table.Count, size = own.Size, n = own.ClusterCount, step = sample ? Math.Max(1, n / Samples) : int.MaxValue;
        double candidates = 0, sampled = 0, entries = 0;
        for (int w = 0; w < n; w++)
        {
            int first = w * size, end = Math.Min(first + size, count), at = list.Starts[w], to = list.Starts[w + 1];
            long span = 0;
            for (int k = at; k < to; k += 2) span += list.Ranges[k + 1] - list.Ranges[k];
            candidates += (double)span * (end - first);
            if (w % step != 0) continue;
            float reach = own.KeptCutoff[w];
            if (!float.IsFinite(reach)) // every pair of the cluster runs: no list
            {
                list.Share = float.NaN;
                return;
            }

            sampled += (double)span * (end - first);
            float r2 = Radius(reach, skin);
            for (int i = first; i < end; i++)
            {
                var point = TOrder.Point(in Unsafe.Add(ref rows, i));
                for (int k = at; k < to; k += 2)
                    for (int j = list.Ranges[k], stop = list.Ranges[k + 1]; j < stop; j++)
                        if (!(Vector3.DistanceSquared(point, TOrder.Point(in Unsafe.Add(ref others, j))) > r2)) entries++;
            }
        }

        if (sample) list.Share = sampled > 0 ? (float)(entries / sampled) : 0;
        list.Candidates = (float)candidates;
        list.Entries = list.Share * list.Candidates; // NaN until a sample: no list
    }

    /// <summary>The square of a list's radius: the cutoff plus the skin, widened against the rounding of the method's own test.</summary>
    private static float Radius(float reach, float skin)
    {
        float radius = reach + skin;
        return radius * radius * (1 + 1e-3f);
    }

    /// <summary>
    /// Each row's list of <paramref name="list"/>: the rows of its cluster's ranges within the cluster's kept cutoff plus
    /// <paramref name="skin"/> (a NaN distance too: the method's test lets it through), ascending, after saving the
    /// points of a table whose rows moved past a quarter of the skin since its last save. False when a cluster has no
    /// finite cutoff or a table no point. O(candidates).
    /// </summary>
    private bool Build<T, TOrder>(PairList list, int bit, float skin) where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        Clusters own = list.Table, their = list.Other;
        var table = own.Table;
        int a = table.ColumnIndex(bit), b = their.Table.ColumnIndex(bit);
        if (a < 0 || b < 0) return false;
        float movedA = Moved<T, TOrder>(own, bit);
        if (!(movedA <= skin * 0.25f)) movedA = Save<T, TOrder>(own, bit);
        float movedB = Moved<T, TOrder>(their, bit); // the save of one table against itself counts twice, as checked
        if (!(movedB <= skin * 0.25f)) movedB = Save<T, TOrder>(their, bit);
        ref T rows = ref table.Data<T>(a);
        ref T others = ref their.Table.Data<T>(b);
        int count = table.Count, size = own.Size, n = own.ClusterCount, found = 0;
        if (list.NearStarts.Length < count + 1) list.NearStarts = new int[Math.Max(count + 1, list.NearStarts.Length * 2)];
        var starts = list.NearStarts;
        var near = list.Near;
        for (int w = 0; w < n; w++)
        {
            float reach = own.KeptCutoff[w];
            if (!float.IsFinite(reach)) return false;
            float r2 = Radius(reach, skin);
            int first = w * size, end = Math.Min(first + size, count), at = list.Starts[w], to = list.Starts[w + 1];
            for (int i = first; i < end; i++)
            {
                starts[i] = found;
                var point = TOrder.Point(in Unsafe.Add(ref rows, i));
                for (int k = at; k < to; k += 2)
                    for (int j = list.Ranges[k], stop = list.Ranges[k + 1]; j < stop; j++)
                        if (!(Vector3.DistanceSquared(point, TOrder.Point(in Unsafe.Add(ref others, j))) > r2))
                        {
                            if (found == near.Length) Array.Resize(ref near, Math.Max(64, found * 2)); // grows to the densest list
                            near[found++] = j;
                        }
            }
        }

        starts[count] = found;
        list.Near = near;
        list.Listed = true;
        list.ListedDraw = list.Draws;
        list.SaveA = own.Saves;
        list.SaveB = their.Saves;
        list.Slack = movedA + movedB;
        list.Skin = skin;
        list.BuiltPass = _pass;
        return true;
    }

    /// <summary>Save the points of <paramref name="clusters"/>' rows now, which later moves are measured from: 0, the move since. O(rows).</summary>
    private float Save<T, TOrder>(Clusters clusters, int bit) where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        var table = clusters.Table;
        int count = table.Count;
        if (clusters.Saved.Length < count) clusters.Saved = new Vector3[Math.Max(count, clusters.Saved.Length * 2)];
        ref T rows = ref table.Data<T>(table.ColumnIndex(bit));
        var saved = clusters.Saved;
        for (int i = 0; i < count; i++) saved[i] = TOrder.Point(in Unsafe.Add(ref rows, i));
        clusters.SavedCount = count;
        clusters.Saves++;
        clusters.MovedPass = _pass;
        return clusters.Moved = 0;
    }

    /// <summary>
    /// The farthest any row of <paramref name="clusters"/>' table moved since its points were saved (a little over it),
    /// once per pass: infinity when the table's count changed, nothing was saved or a point is NaN. Absent and dead rows
    /// are measured too: a list covers every row, whichever passes. O(rows).
    /// </summary>
    private float Moved<T, TOrder>(Clusters clusters, int bit) where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        if (clusters.MovedPass == _pass) return clusters.Moved;
        clusters.MovedPass = _pass;
        var table = clusters.Table;
        int count = table.Count, column = table.ColumnIndex(bit);
        if (clusters.SavedCount != count || column < 0) return clusters.Moved = float.PositiveInfinity;
        ref T rows = ref table.Data<T>(column);
        ref Vector3 saved = ref MemoryMarshal.GetArrayDataReference(clusters.Saved); // SavedCount points
        float most = 0;
        for (int i = 0; i < count; i++)
        {
            float d2 = Vector3.DistanceSquared(TOrder.Point(in Unsafe.Add(ref rows, i)), Unsafe.Add(ref saved, i));
            if (d2 > most) most = d2;
            else if (float.IsNaN(d2)) return clusters.Moved = float.PositiveInfinity;
        }

        return clusters.Moved = MathF.Sqrt(most) * (1 + 1e-4f);
    }

    private void Add(ref int found, int first, int second)
    {
        if (found == _pairA.Length)
        {
            Array.Resize(ref _pairA, found * 2); // warm-up only
            Array.Resize(ref _pairB, found * 2);
        }

        _pairA[found] = first;
        _pairB[found++] = second;
    }

    /// <summary>
    /// One table's clusters: the boxes kept (the margin added when kept) and the boxes of this pass, axis-major: axis k
    /// of cluster w at <c>k * Capacity + w</c>, and each cluster's reach (this pass's and the one kept). A cluster whose
    /// rows hold a NaN point gets a box over everything, so it meets every cluster (a NaN distance passes a test of the
    /// form <c>if (d2 &gt;= r2) return</c>).
    /// </summary>
    private sealed class Clusters(Archetype table)
    {
        public readonly Archetype Table = table;
        public bool Kept, Escapes;
        public int Count, Size, ClusterCount, Builds, Passes, Pass, Axes, Capacity;
        public long Version;
        public float[] Low = [], High = [], Cutoff = [], KeptCutoff = [];

        // The points a list's moves are measured from (Saves counts the saves), and the farthest move this pass.
        public Vector3[] Saved = [];
        public int SavedCount = -1, Saves, MovedPass = -1;
        public float Moved;
        private float[] _low = [], _high = [];

        public void Reserve(int clusters)
        {
            if (clusters <= Capacity) return;
            int capacity = Math.Max(clusters, Capacity * 2);
            // Growing means the count changed: the clusters are kept again this pass, nothing to copy.
            Low = new float[3 * capacity];
            High = new float[3 * capacity];
            _low = new float[3 * capacity];
            _high = new float[3 * capacity];
            Cutoff = new float[capacity];
            KeptCutoff = new float[capacity];
            Capacity = capacity;
            Kept = false;
        }

        /// <summary>
        /// The box of each cluster over the points of its rows that hold the component, into this pass's boxes; when
        /// <paramref name="check"/>, whether some cluster's box left the kept one (its rows moved past the margin).
        /// O(rows).
        /// </summary>
        public bool Draw<T, TOrder>(Archetype table, int bit, bool check)
            where T : struct, IComponent where TOrder : struct, IRowOrder<T>
        {
            int column = table.ColumnIndex(bit), count = table.Count, c = Capacity, size = Size, axes = Axes;
            if (column < 0) return Everywhere(clusters: ClusterCount); // no point: a method without a cutoff test, every pair runs
            ref T row0 = ref table.Data<T>(column);
            var present = table.Bits(column);
            bool escaped = false;
            var infinity = Vector128.Create(float.PositiveInfinity);
            for (int w = 0, clusters = ClusterCount; w < clusters; w++)
            {
                var low = infinity;
                var high = -infinity;
                var nan = Vector128<float>.Zero;
                for (int row = w * size, end = Math.Min(row + size, count); row < end; row++)
                {
                    if (present != null && ((present[row >> 6] >> row) & 1) == 0) continue; // shift is mod 64
                    var point = TOrder.Point(in Unsafe.Add(ref row0, row)).AsVector128();
                    low = Vector128.MinNative(low, point);
                    high = Vector128.MaxNative(high, point);
                    nan |= ~Vector128.Equals(point, point);
                }

                if (nan != Vector128<float>.Zero)
                {
                    low = -infinity;
                    high = infinity;
                }

                for (int k = 0; k < axes; k++)
                {
                    float l = low[k], h = high[k];
                    _low[k * c + w] = l;
                    _high[k * c + w] = h;
                    escaped |= check && l <= h && (l < Low[k * c + w] || h > High[k * c + w]);
                }
            }

            return escaped;
        }

        /// <summary>Boxes over everything for every cluster (their rows have no point): never left.</summary>
        private bool Everywhere(int clusters)
        {
            for (int k = 0, c = Capacity; k < Axes; k++)
                for (int w = 0; w < clusters; w++)
                {
                    _low[k * c + w] = float.NegativeInfinity;
                    _high[k * c + w] = float.PositiveInfinity;
                }

            return false;
        }

        /// <summary>Keep this pass's boxes, widened by <paramref name="margin"/> on every side, and the reaches.</summary>
        public void Keep(float margin)
        {
            int c = Capacity, clusters = ClusterCount;
            for (int k = 0; k < Axes; k++)
                for (int w = 0; w < clusters; w++)
                {
                    Low[k * c + w] = _low[k * c + w] - margin;
                    High[k * c + w] = _high[k * c + w] + margin;
                }

            Array.Copy(Cutoff, KeptCutoff, clusters);
            Kept = true;
            Builds++;
        }
    }

    /// <summary>Per cluster of a table, the row ranges of another table (or its own) to visit, and the keeps they were drawn from.</summary>
    private sealed class PairList(Clusters table, Clusters other)
    {
        public readonly Clusters Table = table, Other = other;
        public int Builds = -1, OtherBuilds = -1, Draws;
        public int[] Starts = [], Ranges = [], Order = [];

        // The list of each row's neighbors (Verlet's): its rows, the draw and saves it was built from, the moves it
        // allows, how long the last one held, and the estimate of its cost for the current draw.
        public int[] NearStarts = [], Near = [];
        public bool Listed;
        public int ListedDraw = -1, SaveA, SaveB, BuiltPass, Life = PairState.Interval, EstimatedDraw = -1;
        public int Estimates;
        public float Slack, Skin, Candidates, Entries, Share = float.NaN;

        public void Reserve(int clusters, int pairs)
        {
            if (Starts.Length < clusters + 2) Starts = new int[Math.Max(clusters + 2, Starts.Length * 2)];
            if (Order.Length < pairs) Order = new int[Math.Max(pairs, Order.Length * 2)];
            if (Ranges.Length < 2 * pairs) Ranges = new int[Math.Max(2 * pairs, Ranges.Length * 2)];
        }
    }
}
