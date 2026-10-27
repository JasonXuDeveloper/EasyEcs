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
/// <c>in</c>, or the neighbor's <see cref="Entity"/>.
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
/// the pass reads a window of rows against the rows within the cutoff only, a vector of rows per instruction where the
/// method qualifies. The pairs of a row run in the order of the tables' rows (not of the entities): a method that keeps
/// the nearest neighbor breaks ties by the entity (the lower <see cref="EntityId.Index"/>), so its result is the same
/// whatever the order.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class NeighborAttribute : Attribute;

/// <summary>
/// The job of a pair method (<see cref="NeighborAttribute">[Neighbor]</see> parameters), written by the generator:
/// the cutoff its distance test compares against and the method over one table's rows against another's (or its own),
/// window by window. <see cref="World.ForPairs{T, TOrder, TJob}"/> runs it.
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
    /// table (<see cref="PairRun.Rows{T}"/>; it has no windows): row r into <paramref name="into"/>[r]. Read once per
    /// pass when <see cref="CutoffPerRow"/>.
    /// </summary>
    void CutoffsSquared(in PairRun table, Span<double> into);

    /// <summary>The rows of <see cref="PairRun"/>'s table against the rows of its other table, window by window.</summary>
    void Run(in PairRun run);
}

/// <summary>
/// One table's rows (the method's own) against one table's rows (the neighbors': the same table or another) for a pair
/// job (<see cref="IPairJob.Run"/>): windows of <see cref="Size"/> rows of the first, each with the rows of the
/// other to visit (<see cref="Ranges"/>), every other row farther than the cutoff from all of the window's.
/// Valid during the call it is passed to.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly ref struct PairRun
{
    private readonly World _world;
    private readonly Archetype _table, _other;
    private readonly ulong[] _rows, _neighbors;
    private readonly int[] _starts, _ranges;
    private readonly float[] _reach;
    private readonly int _windows, _size;

    internal PairRun(World world, Archetype table, Archetype other, ulong[] rows, ulong[] neighbors, int[] starts,
        int[] ranges, float[] reach, int windows, int size)
    {
        _reach = reach;
        _world = world;
        _table = table;
        _other = other;
        _rows = rows;
        _neighbors = neighbors;
        _starts = starts;
        _ranges = ranges;
        _windows = windows;
        _size = size;
    }

    /// <summary>Rows per window: a vector of floats (8 where 256-bit vectors are hardware, 4 on ARM64).</summary>
    public int Size => _size;

    /// <summary>Windows of the method's table: row <c>w * Size</c> starts window w.</summary>
    public int Windows => _windows;

    /// <summary>The rows of the method's table (rows of the last window past it are none).</summary>
    public int Count => _table.Count;

    /// <summary>The neighbors' table is the method's own: a row meets itself in its own window.</summary>
    public bool Same => ReferenceEquals(_table, _other);

    /// <summary>
    /// The rows of window <paramref name="window"/> the method runs for, one bit per row from its first (row
    /// <c>window * Size</c>): rows of the table that pass the method's filter. A whole window is
    /// <c>(1 &lt;&lt; Size) - 1</c>. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Lanes(int window)
    {
        int row = window * _size;
        // _rows holds a word per 64 rows of every window (Size divides 64): in bounds for every window.
        ulong word = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_rows), row >> 6);
        return (word >> (row & 63)) & (ulong.MaxValue >> (64 - _size));
    }

    /// <summary>
    /// The rows of the other table window <paramref name="window"/> meets, as (first, end) pairs in row order; every
    /// row outside them is farther than the cutoff from each of the window's. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> Ranges(int window)
    {
        int start = _starts[window];
        return _ranges.AsSpan(start, _starts[window + 1] - start);
    }

    /// <summary>
    /// The reach of window <paramref name="window"/> this pass: the largest cutoff of its rows, a little over it (never
    /// below: a pair farther than it fails the method's test), infinity without a cutoff. The method per pair skips the
    /// neighbors past it without a call. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Reach(int window) => _reach[window];

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

    /// <summary>The entity of row <paramref name="row"/> of the method's table. O(1).</summary>
    public Entity Entity(int row) => new(_world, _table.Slots[row]);

    /// <summary>The entity of row <paramref name="row"/> of the other table. O(1).</summary>
    public Entity Other(int row) => new(_world, _other.Slots[row]);

    /// <summary>
    /// Stamp the rows the method runs for in the method's table as written now (a tracked <typeparamref name="T"/> it
    /// writes; nothing for another). O(rows / 64).
    /// </summary>
    public void Stamp<T>() where T : struct, IComponent
    {
        if (default(T) is not ITracked) return;
        ulong tick = _world.ChangeTick;
        for (int w = 0, words = (_table.Count + 63) >> 6; w < words; w++)
            if (_rows[w] != 0) _table.StampWord<T>(w, _rows[w], tick);
    }
}

public sealed partial class World
{
    // The state of each pair job type this world ran (PairJobs<TJob>.Id): its windows' boxes and lists.
    private PairState[] _pairStates = [];

    /// <summary>
    /// Run a pair job (a generated <see cref="NeighborAttribute">[Neighbor]</see> method): the rows matching
    /// <paramref name="rows"/> against the rows matching <paramref name="neighbors"/>, both holding
    /// <typeparamref name="T"/>, whose point <typeparamref name="TOrder"/> reads (<paramref name="axes"/> axes). The
    /// tables of <typeparamref name="T"/> are ordered by <typeparamref name="TOrder"/>'s key the first time (sorted at
    /// once outside a parallel stage); windows of rows get boxes over their points, checked every pass and widened by
    /// a margin, so the lists of windows within the cutoff hold until a row leaves its box, a table changes or a
    /// row's cutoff grows. Owner thread (or the system's), outside other passes; allocation-free in steady state.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ForPairs<T, TOrder, TJob>(ref TJob job, in QueryFilter rows, in QueryFilter neighbors, int axes)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T> where TJob : struct, IPairJob, allows ref struct
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        var state = PairStateOf(PairJobs<TJob>.Id);
        float reach = job.CutoffPerRow ? float.NaN : PairState.Reach(job.CutoffSquared);
        // Every pair runs without a cutoff: no order helps.
        if (!state.Ordered) state.Ordered = reach == float.PositiveInfinity || OrderPairs<T, TOrder>(axes, reach);
        bool froze = Gate.FreezeAlone();
        try
        {
            state.Run<T, TOrder, TJob>(this, ref job, in rows, in neighbors, axes, reach);
        }
        finally
        {
            if (froze) Gate.ThawAlone();
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
/// What one pair job keeps in one world between passes: per table, the boxes of its windows of rows (each widened by
/// the margin when kept) and the reach of each window (the largest cutoff of its rows), and per pair of tables, the
/// windows of the other that each window's box widened by its reach meets (as row ranges). A pass draws every window's
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

    private Windows[] _tables = new Windows[4];
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

    // Scratch of the sweep that finds the windows within reach.
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

    public void Run<T, TOrder, TJob>(World world, ref TJob job, in QueryFilter rows, in QueryFilter neighbors, int axes, float reach)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T> where TJob : struct, IPairJob, allows ref struct
    {
        _pass++;
        var mine = world.GetQueryEntry(default, in rows).Match;
        var theirs = world.GetQueryEntry(default, in neighbors).Match;
        int bit = ComponentType<T>.Bit;
        int size = Vector<float>.Count;
        for (int a = 0; a < mine.Archetypes.Length; a++)
        {
            var table = mine.Archetypes[a];
            var filter = new RowFilter(mine.Filters[a], 0);
            int mode = filter.Mode(table);
            if (table.AliveCount == 0 || mode == RowFilter.None) continue;
            Passing(table, filter, mode, ref _rows);
            var own = Refresh<T, TOrder>(table, bit, axes, size);
            if (Reaches(world, ref job, own, reach)) Keep(own); // a cutoff grew past the one the lists were drawn for
            for (int b = 0; b < theirs.Archetypes.Length; b++)
            {
                var other = theirs.Archetypes[b];
                var otherFilter = new RowFilter(theirs.Filters[b], 0);
                int otherMode = otherFilter.Mode(other);
                if (other.AliveCount == 0 || otherMode == RowFilter.None) continue;
                var their = ReferenceEquals(other, table) ? own : Refresh<T, TOrder>(other, bit, axes, size);
                bool skips = otherMode == RowFilter.Test || other.Count != other.AliveCount;
                if (skips) Passing(other, otherFilter, otherMode, ref _neighbors);
                var list = ListOf(own, their);
                job.Run(new PairRun(world, table, other, _rows, skips ? _neighbors : null, list.Starts, list.Ranges, own.Reach, own.WindowCount, size));
            }
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
    /// Each window's reach of <paramref name="windows"/> (the largest of its passing rows' cutoffs; every row's
    /// <paramref name="reach"/> unless NaN: a cutoff per row), into <see cref="Windows.Reach"/>; true when one grew past
    /// its kept reach. O(rows).
    /// </summary>
    private bool Reaches<TJob>(World world, ref TJob job, Windows windows, float reach) where TJob : struct, IPairJob, allows ref struct
    {
        var table = windows.Table;
        int count = table.Count, size = windows.Size, n = windows.WindowCount;
        var reaches = windows.Reach;
        float largest = 0;
        if (!float.IsNaN(reach))
        {
            reaches.AsSpan(0, n).Fill(reach);
            largest = reach;
        }
        else
        {
            if (_cutoffs.Length < count) _cutoffs = new double[Math.Max(count, _cutoffs.Length * 2)]; // warm-up only
            job.CutoffsSquared(new PairRun(world, table, table, _rows, null, null, null, null, 0, size), _cutoffs.AsSpan(0, count));
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

        var kept = windows.KeptReach;
        for (int w = 0; w < n; w++)
            if (reaches[w] > kept[w]) return true;
        return false;
    }

    /// <summary>
    /// The windows of <paramref name="table"/>, their boxes checked against the rows' points now (once per pass): kept
    /// again (and the lists of the table drawn again with them) when the table was sorted, grew or shrank, or a
    /// window's rows left its kept box.
    /// </summary>
    private Windows Refresh<T, TOrder>(Archetype table, int bit, int axes, int size)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        Windows windows = null;
        for (int i = 0; i < _tableCount; i++)
            if (ReferenceEquals(_tables[i].Table, table))
            {
                windows = _tables[i];
                break;
            }

        if (windows == null)
        {
            if (_tableCount == _tables.Length) Array.Resize(ref _tables, _tableCount * 2);
            _tables[_tableCount++] = windows = new Windows(table);
        }

        if (windows.Pass == _pass) return windows;
        windows.Pass = _pass;
        int count = table.Count, n = (count + size - 1) / size;
        long version = table.Order?.Version ?? 0;
        windows.Reserve(n);
        bool changed = !windows.Kept || windows.Count != count || windows.Version != version || windows.Size != size;
        windows.Size = size;
        windows.WindowCount = n;
        windows.Axes = axes;
        bool escaped = windows.Draw<T, TOrder>(table, bit, !changed);
        windows.Passes++;
        if (float.IsFinite(_margin))
        {
            if (escaped && windows.Passes < Interval / 2) _margin = Math.Min(_margin * 1.5f, _largest); // the rows move fast
            else if (changed && windows.Kept && !windows.Escapes) _margin = Math.Max(_margin * 0.9f, _largest * 0.02f); // a sort came first
        }

        if (changed || escaped)
        {
            windows.Count = count;
            windows.Version = version;
            windows.Passes = 0;
            windows.Escapes = escaped;
            Keep(windows);
        }

        return windows;
    }

    private void Keep(Windows windows) => windows.Keep(float.IsFinite(_margin) ? _margin : 0);

    /// <summary>The windows of <paramref name="other"/> each window of <paramref name="table"/> meets, drawn again when either was kept again.</summary>
    private PairList ListOf(Windows table, Windows other)
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
    /// The pairs (window of the table, window of the other) where the table's kept box widened by the window's kept
    /// reach meets the other's kept box on every axis (a sweep over both sets of boxes sorted by their low side on axis
    /// 0, as <see cref="RowBlocks.Pairs(float, Span{BlockPair})"/>), then per window of the table the other's windows
    /// in row order, as row ranges (neighboring windows merged). One table against itself sweeps its boxes twice, once
    /// widened, once not, so a window meets itself and each direction of a pair is tested with its own reach.
    /// O(windows log windows + pairs).
    /// </summary>
    private void Draw(PairList list)
    {
        var a = list.Table;
        var b = list.Other;
        int na = a.WindowCount, nb = b.WindowCount, axes = a.Axes;
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
                _keys[m] = a.Low[i] - a.KeptReach[i];
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
            float r = id >= 0 ? a.KeptReach[i] : 0;
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

        // Per window of a: its windows of b in row order (a counting sort by window of a, then each list sorted).
        list.Reserve(na, found);
        var starts = list.Starts;
        Array.Clear(starts, 0, na + 2);
        for (int k = 0; k < found; k++) starts[_pairA[k] + 2]++;
        for (int i = 0; i < na; i++) starts[i + 2] += starts[i + 1];
        var order = list.Order;
        for (int k = 0; k < found; k++) order[starts[_pairA[k] + 1]++] = _pairB[k];
        // starts[i + 1] is now the end of window i's windows, starts[i] its start.
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
                if (at > starts[i] && ranges[at - 1] == first) ranges[at - 1] = end; // the window after the last: one range
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
    /// One table's windows: the boxes kept (the margin added when kept) and the boxes of this pass, axis-major: axis k
    /// of window w at <c>k * Capacity + w</c>, and each window's reach (this pass's and the one kept). A window whose
    /// rows hold a NaN point gets a box over everything, so it meets every window (a NaN distance passes a test of the
    /// form <c>if (d2 &gt;= r2) return</c>).
    /// </summary>
    private sealed class Windows(Archetype table)
    {
        public readonly Archetype Table = table;
        public bool Kept, Escapes;
        public int Count, Size, WindowCount, Builds, Passes, Pass, Axes, Capacity;
        public long Version;
        public float[] Low = [], High = [], Reach = [], KeptReach = [];
        private float[] _low = [], _high = [];

        public void Reserve(int windows)
        {
            if (windows <= Capacity) return;
            int capacity = Math.Max(windows, Capacity * 2);
            // Growing means the count changed: the windows are kept again this pass, nothing to copy.
            Low = new float[3 * capacity];
            High = new float[3 * capacity];
            _low = new float[3 * capacity];
            _high = new float[3 * capacity];
            Reach = new float[capacity];
            KeptReach = new float[capacity];
            Capacity = capacity;
            Kept = false;
        }

        /// <summary>
        /// The box of each window over the points of its rows that hold the component, into this pass's boxes; when
        /// <paramref name="check"/>, whether some window's box left the kept one (its rows moved past the margin).
        /// O(rows).
        /// </summary>
        public bool Draw<T, TOrder>(Archetype table, int bit, bool check)
            where T : struct, IComponent where TOrder : struct, IRowOrder<T>
        {
            int column = table.ColumnIndex(bit), count = table.Count, c = Capacity, size = Size, axes = Axes;
            if (column < 0) return Everywhere(windows: WindowCount); // no point: a method without a cutoff test, every pair runs
            ref T row0 = ref table.Data<T>(column);
            var present = table.Bits(column);
            bool escaped = false;
            var infinity = Vector128.Create(float.PositiveInfinity);
            for (int w = 0, windows = WindowCount; w < windows; w++)
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

        /// <summary>Boxes over everything for every window (their rows have no point): never left.</summary>
        private bool Everywhere(int windows)
        {
            for (int k = 0, c = Capacity; k < Axes; k++)
                for (int w = 0; w < windows; w++)
                {
                    _low[k * c + w] = float.NegativeInfinity;
                    _high[k * c + w] = float.PositiveInfinity;
                }

            return false;
        }

        /// <summary>Keep this pass's boxes, widened by <paramref name="margin"/> on every side, and the reaches.</summary>
        public void Keep(float margin)
        {
            int c = Capacity, windows = WindowCount;
            for (int k = 0; k < Axes; k++)
                for (int w = 0; w < windows; w++)
                {
                    Low[k * c + w] = _low[k * c + w] - margin;
                    High[k * c + w] = _high[k * c + w] + margin;
                }

            Array.Copy(Reach, KeptReach, windows);
            Kept = true;
            Builds++;
        }
    }

    /// <summary>Per window of a table, the row ranges of another table (or its own) to visit, and the keeps they were drawn from.</summary>
    private sealed class PairList(Windows table, Windows other)
    {
        public readonly Windows Table = table, Other = other;
        public int Builds = -1, OtherBuilds = -1;
        public int[] Starts = [], Ranges = [], Order = [];

        public void Reserve(int windows, int pairs)
        {
            if (Starts.Length < windows + 2) Starts = new int[Math.Max(windows + 2, Starts.Length * 2)];
            if (Order.Length < pairs) Order = new int[Math.Max(pairs, Order.Length * 2)];
            if (Ranges.Length < 2 * pairs) Ranges = new int[Math.Max(2 * pairs, Ranges.Length * 2)];
        }
    }
}
