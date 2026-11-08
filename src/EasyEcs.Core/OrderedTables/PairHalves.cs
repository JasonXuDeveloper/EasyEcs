using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EasyEcs;

public sealed partial class World
{
    /// <summary>
    /// A parallel pass of a pair job writing both sides (<see cref="IPairJob.Symmetric"/>): each table's clusters cut into
    /// chunks of a fixed number of rows (never by the threads: a pass gives the same result on any number of threads),
    /// each chunk running its rows in order. A table of fewer than <see cref="PairHalves.Least"/> chunks runs as the
    /// serial pass does. Owner thread, outside other passes; allocation-free after warm-up.
    /// </summary>
    private void ForPairHalves<T, TOrder, TJob>(ref TJob job, in QueryFilter rows, in QueryFilter neighbors, int axes)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T> where TJob : struct, IPairJob
    {
        var state = BeginPairs<T, TOrder, TJob>(ref job, in rows, in neighbors, axes, out float reach);
        var work = WorkOf<PairHalves<TJob>>();
        var start = job; // what each chunk's copy starts from: partial results at Identity
        bool froze = Gate.FreezeAlone();
        try
        {
            while (state.Next<T, TOrder, TJob>(this, ref job, axes, reach, out var run))
            {
                int clusters = PairHalves.ChunkClusters(run.ClusterSize), chunks = (run.End + clusters - 1) / clusters;
                if (chunks < PairHalves.Least)
                {
                    job.Run(in run); // a small table: the serial pass
                    job.Stamp(in run);
                    continue;
                }

                work.Set(start, in run, clusters, chunks);
                try
                {
                    // A barrier between colors: the next color's chunks reach the rows of this one's.
                    for (int phase = 0, phases = work.Color(); phase < phases; phase++) RunChunks(work, work.Phase(phase));

                    if (TJob.Merges) work.MergeInto(ref job, chunks); // in chunk order, after the pairs of tables before
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
}

/// <summary>The chunk size of a parallel pass writing both sides.</summary>
internal static class PairHalves
{
    /// <summary>Rows per chunk, a multiple of 64 (internal: the benchmarks set it).</summary>
    internal static int ChunkRows = 256;

    /// <summary>A table of fewer chunks runs as the serial pass does.</summary>
    internal const int Least = 4;

    /// <summary>The clusters of a chunk: <see cref="ChunkRows"/> rows of them (whole words of rows).</summary>
    public static int ChunkClusters(int size) => Math.Max(64, ChunkRows & ~63) / size;
}

/// <summary>
/// What a parallel pass writing both sides works out once per draw of a list (a table against one, its clusters' ranges
/// unchanged) and chunk size, and keeps until either changes: the chunks' colors in color order with each phase's end.
/// Owner thread.
/// </summary>
internal sealed class PairSchedule
{
    private int _draw = -1, _clusters;

    /// <summary>The colors are worked out (<see cref="Order"/>, <see cref="Ends"/>, <see cref="Phases"/>).</summary>
    public bool Colored;

    /// <summary>The chunks in color order, chunk order within a color; the end of each phase in it; the phases.</summary>
    public int[] Order = [], Ends = [];

    public int Phases;

    /// <summary>The list was drawn (<paramref name="draw"/>, its count of draws): what was worked out for another goes.</summary>
    public void Drawn(int draw)
    {
        if (draw == _draw) return;
        _draw = draw;
        Colored = false;
    }

    /// <summary>The pass cuts <paramref name="clusters"/> per chunk: what was worked out for another size goes.</summary>
    public void Cut(int clusters)
    {
        if (clusters == _clusters) return;
        _clusters = clusters;
        Colored = false;
    }
}

/// <summary>
/// The chunks of a parallel pass of a pair job writing both sides over one table against one (itself or one after it):
/// chunk i is clusters [i × Size, (i + 1) × Size) of the method's table. A phase runs the chunks of one color;
/// <see cref="Run"/> maps an index of the phase to its chunk.
/// </summary>
internal sealed class PairHalves<TJob> : World.ParallelWork where TJob : struct, IPairJob
{
    private TJob _job;
    private World _world;
    private Archetype _table, _other;
    private ulong[] _rows, _neighbors;
    private int[] _starts, _ranges, _nearStarts, _near;
    private float[] _reach;
    private PairSchedule _schedule;
    private int _end, _size, _clusters, _chunks, _phase;
    private TJob[] _parts = []; // each chunk's copy of the job when it keeps partial results (Merges)

    // Colors: each chunk's color, the colors each cluster's writers took (a bit per color), the chunks per color.
    private int[] _color = [], _counts = new int[65];
    private ulong[] _used = [];

    public void Set(TJob job, in PairRun run, int clusters, int chunks)
    {
        if (TJob.Merges && _parts.Length < chunks) _parts = new TJob[Math.Max(chunks, _parts.Length * 2)]; // warm-up only
        _job = job;
        (_world, _table, _other, _rows, _neighbors, _starts, _ranges, _reach, _nearStarts, _near) = run.Parts();
        _schedule = run.Schedule;
        _schedule.Cut(clusters);
        _end = run.End;
        _size = run.ClusterSize;
        _clusters = clusters;
        _chunks = chunks;
    }

    /// <summary>
    /// The phases of the colors: the chunks colored in order (greedy, distance two over the clusters they write: a chunk
    /// takes the lowest color none of the earlier chunks writing one of its clusters took) and put in color order, chunk
    /// order within a color: a phase per color, then one per chunk that found the 64 colors taken. Worked out once per
    /// draw of the list, O(clusters the chunks reach).
    /// </summary>
    public int Color()
    {
        var schedule = _schedule;
        if (schedule.Colored) return schedule.Phases;
        int chunks = _chunks, others = OtherClusters;
        if (_color.Length < chunks) Array.Resize(ref _color, Math.Max(chunks, _color.Length * 2));
        if (schedule.Order.Length < chunks) schedule.Order = new int[Math.Max(chunks, schedule.Order.Length * 2)];
        if (schedule.Ends.Length < chunks + 1) schedule.Ends = new int[Math.Max(chunks + 1, schedule.Ends.Length * 2)];
        if (_used.Length < others) Array.Resize(ref _used, Math.Max(others, _used.Length * 2));
        Array.Clear(_used, 0, others);
        Array.Clear(_counts);
        int colors = 0;
        for (int c = 0; c < chunks; c++)
        {
            ulong taken = Reached(c, 0, mark: false);
            int color = taken == ulong.MaxValue ? 64 : BitOperations.TrailingZeroCount(~taken);
            if (color < 64)
            {
                Reached(c, 1UL << color, mark: true);
                colors = Math.Max(colors, color + 1);
            }

            _color[c] = color;
            _counts[color]++;
        }

        // A counting sort by color (stable: chunk order within a color), the end of each phase in the order.
        int at = 0, phases = 0;
        var order = schedule.Order;
        var ends = schedule.Ends;
        for (int k = 0; k <= 64; k++) (_counts[k], at) = (at, at + _counts[k]);
        for (int c = 0; c < chunks; c++) order[_counts[_color[c]]++] = c; // _counts[k] ends at color k's end
        for (int k = 0; k < colors; k++) ends[phases++] = _counts[k];
        for (int i = _counts[63]; i < chunks; i++) ends[phases++] = i + 1; // past 64 colors: a chunk alone
        schedule.Phases = phases;
        schedule.Colored = true;
        return phases;
    }

    /// <summary>
    /// The colors the writers of chunk <paramref name="chunk"/>'s clusters took (its own, on a table against itself, and
    /// those its ranges reach), or with <paramref name="mark"/> their mark of <paramref name="color"/>.
    /// </summary>
    private ulong Reached(int chunk, ulong color, bool mark)
    {
        ulong taken = 0;
        var used = _used;
        bool same = ReferenceEquals(_table, _other);
        int first = chunk * _clusters, end = Math.Min(first + _clusters, _end), size = _size;
        for (int w = first; w < end; w++)
        {
            if (same)
            {
                if (mark) used[w] |= color;
                else taken |= used[w];
            }

            for (int k = _starts[w], to = _starts[w + 1]; k < to; k += 2)
                for (int x = _ranges[k] / size, last = (_ranges[k + 1] - 1) / size; x <= last; x++)
                    if (mark) used[x] |= color;
                    else taken |= used[x];
        }

        return taken;
    }

    /// <summary>The clusters of the other table (a cluster per <see cref="PairRun.ClusterSize"/> rows).</summary>
    private int OtherClusters => (_other.Count + _size - 1) / _size;

    /// <summary>Phase <paramref name="phase"/> of <see cref="Color"/>'s (its chunks next in the color order): its chunk count.</summary>
    public int Phase(int phase)
    {
        var ends = _schedule.Ends;
        _phase = phase == 0 ? 0 : ends[phase - 1];
        return ends[phase] - _phase;
    }

    public override void Run(int index)
    {
        var job = _job;
        int chunk = _schedule.Order[_phase + index];
        int first = chunk * _clusters, end = Math.Min(first + _clusters, _end);
        job.Run(new PairRun(_world, _table, _other, _rows, _neighbors, _starts, _ranges, _reach, first, end, _size, _nearStarts, _near));
        if (TJob.Merges) _parts[chunk] = job; // one chunk per index: no two threads write one element
    }

    /// <summary>Combine the chunks' partial results into <paramref name="job"/> in chunk order (row order).</summary>
    public void MergeInto(ref TJob job, int chunks)
    {
        for (int i = 0; i < chunks; i++) TJob.Merge(ref job, in _parts[i]);
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
        _schedule = null;
        if (TJob.Merges && RuntimeHelpers.IsReferenceOrContainsReferences<TJob>()) Array.Clear(_parts); // the copies hold the job's references
    }
}
