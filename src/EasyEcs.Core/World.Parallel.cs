using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EasyEcs;

// Parallel work: the systems of one stage (ParallelSystems; non-conflicting SystemAccess) and the row chunks of a
// parallel [ForEach] (ForEachParallel). Allocation-free, unlike Parallel.For (closure + state per call): the calling
// thread works through the items together with up to MaxDegreeOfParallelism - 1 reusable helpers, run by the process's
// helper threads (Workers: a fixed set, not the thread pool, so steady state starts no thread).
public sealed partial class World
{
    private Runner _systemRunner;
    private Runner _chunkRunner;

    /// <summary>
    /// Bytes the helper threads of this thread's parallel [ForEach] passes allocated, summed (only with
    /// <see cref="WorldOptions.MeasureSystems"/>): a measured run adds what grew during it to its system's
    /// <see cref="SystemStats.LastAllocated"/>. Per thread: two systems of a stage may run passes at once.
    /// </summary>
    [ThreadStatic] internal static long HelperAllocated;

    // Chunked jobs, one cached instance per job type (TypeIndex<ParallelWork, TWork>): allocated on first use only.
    private ParallelWork[] _parallelWork = [];
    private readonly Lock _parallelWorkLock = new();

    /// <summary>
    /// The most rows per chunk of a parallel [ForEach] that does not set its own (<c>chunkRows</c>) before its rows' cost
    /// is measured. Chunks hold about a quarter of each thread's share of the matching rows (at least 1,024): equal
    /// chunks finish together, and four per thread even out uneven rows. Once a run has measured the rows' cost, chunks
    /// follow it instead: four per thread, unless that makes them longer than about 25 µs (heavy rows: more, smaller
    /// chunks) or shorter than 5 µs (a short pass: fewer chunks).
    /// </summary>
    public const int MaxChunkRows = 16384;

    /// <summary>
    /// Rows per chunk for <paramref name="work"/>'s pass over <paramref name="tables"/> (over one of
    /// <paramref name="slices"/> slices of each, <see cref="QueryFilter.Slice"/>): <paramref name="chunkRows"/> rounded up
    /// to 64 when given (the caller knows better, OpenMP's <c>schedule(dynamic, n)</c>); else what the rows' measured cost
    /// gives (<see cref="ParallelWork.Rows"/>), or before any run about four chunks per thread, between 1,024 and
    /// <see cref="MaxChunkRows"/>. Always a multiple of 64: presence-bit words and change-tick words never straddle
    /// chunks. A pass that <paramref name="stamps"/> a tracked component gets a multiple of 4,096 rows unless its chunks
    /// are smaller: each block of 4,096 rows shares one change-tick summary per column (<see cref="Archetype.Blocks"/>),
    /// which chunks splitting a block share through <see cref="Archetype.OpenBlocks{T}"/>. A pass that
    /// <paramref name="merges"/> partial results per chunk cuts <see cref="MergedChunks"/> chunks whatever the threads and
    /// the cost, so its result does not depend on them.
    /// </summary>
    internal int ChunkRows(ParallelWork work, Archetype[] tables, bool stamps, bool merges, int slices = 0, int chunkRows = 0)
    {
        if (chunkRows > 0) return ChunkRows(work, 0, chunkRows);
        long rows = 0;
        for (int i = 0; i < tables.Length; i++) rows += tables[i].Count;
        if (slices > 1) rows /= slices; // a sliced pass visits about 1 / slices of them
        if (merges)
        {
            work.Items = 0; // cut by count, never by cost: nothing to learn
            long share = rows / MergedChunks;
            return stamps
                ? (int)Math.Clamp((share + 4095) & ~4095L, 4096, MaxChunkRows)
                : (int)Math.Clamp((share + 63) & ~63L, 1024, MaxChunkRows);
        }

        int size = ChunkRows(work, rows, 0);
        if (!stamps) return size;
        if (work.Measured) return size >= 4096 ? (size + 4095) & ~4095 : size; // small chunks of heavy rows open their blocks
        long quarter = rows / ((Helpers + 1) * 4L);
        return (int)Math.Clamp((quarter + 4095) & ~4095L, 4096, MaxChunkRows);
    }

    /// <summary>
    /// The chunks a pass that merges per chunk (a <see cref="Reduce{T}"/>, an <see cref="Emit{T}"/>) cuts its rows into
    /// when it does not set <c>ChunkRows</c>: a constant, so its chunk boundaries follow the row count alone and a
    /// floating-point result is the same bits at any <see cref="WorldOptions.MaxDegreeOfParallelism"/> (the threads only
    /// share the chunks out). Four per thread at four threads; tables of 262,144 rows or more get chunks of
    /// <see cref="MaxChunkRows"/> rows, more than enough for any thread count.
    /// </summary>
    private const int MergedChunks = 16;

    /// <summary>
    /// Items per chunk for <paramref name="items"/> items of <paramref name="work"/> (rows of a pass, elements of
    /// <see cref="ForParallel{TJob}"/>): <paramref name="chunkRows"/> rounded up to 64 when given, else by the items'
    /// measured cost (<see cref="ParallelWork.Rows"/>), else about four chunks per thread, a multiple of 64 between
    /// 1,024 and <see cref="MaxChunkRows"/>.
    /// </summary>
    private int ChunkRows(ParallelWork work, long items, int chunkRows)
    {
        work.Items = chunkRows > 0 ? 0 : items;
        if (chunkRows > 0) return (int)Math.Min((chunkRows + 63L) & ~63L, int.MaxValue & ~63);
        int threads = Helpers + 1;
        if (work.Rows(items, threads) is var size and > 0) return size;
        long share = items / (threads * 4L);
        return (int)Math.Clamp((share + 63) & ~63L, 1024, MaxChunkRows);
    }

    /// <summary>
    /// The chunks a pass of heavy rows is cut into at most, per thread (<see cref="ParallelWork.Rows"/>): enough to even
    /// out uneven rows, and a bound on the chunk arrays of a pass, which <see cref="ChunkCapacity"/> sizes on the first
    /// call so that a measured cost never grows them later (no allocation after the first call).
    /// </summary>
    internal const int MostChunksPerThread = 64;

    /// <summary>The chunk slots a pass that does not merge reserves on its first call: one table's chunks at most, and a few tables more.</summary>
    internal int ChunkCapacity => (Helpers + 1) * MostChunksPerThread + 8;

    /// <summary>Helpers a runner keeps (each run by one of the process's <see cref="Workers"/>): the calling thread is the other one.</summary>
    private int Helpers => Math.Max(1, _options.MaxDegreeOfParallelism) - 1;

    private unsafe void RunStage(SystemStage stage, long update)
    {
        var systems = stage.Systems;
        stage.World = this;
        stage.Update = update;
        // Each system of the stage measures its own allocations (RunMeasured, on the thread that runs it).
        var runner = _systemRunner ??= new Runner(Helpers, false);
        // The indexes the stage's key lookups read, built here: an index builds (or is created) on first use, and the
        // stage's threads may only read it. Gathered once per schedule: the stage's systems are known when it is cut.
        var prepares = stage.Prepares;
        for (int i = 0; i < prepares.Length; i++) prepares[i](this);
        Gate.FreezeParallel(); // the stage's systems never change structure (they do not run alone): a change throws
        try
        {
            if (runner.TryRun(stage, systems.Length, out _)) return;
            foreach (var segment in stage.Segments) // an update from inside a running stage: one system after the other
                segment.Set.Run(this, systems.AsSpan(segment.Start, segment.Count), update, default, default);
        }
        finally
        {
            Gate.ThawParallel();
        }
    }

    /// <summary>
    /// Run the chunks 0 .. <paramref name="chunks"/> - 1 of <paramref name="work"/> (a parallel pass, a
    /// <see cref="ForParallel{TJob}"/>), structural changes refused meanwhile: on the chunk runner, or on this thread
    /// when there are fewer than two or the runner cannot take them (<see cref="TryRunChunks"/>).
    /// </summary>
    internal void RunChunks(ParallelWork work, int chunks)
    {
        if (chunks < 2)
        {
            // One chunk runs on this thread, as a serial pass does: the owner's plain freeze when nothing holds the gate
            // (D105), no parallel region (four locked instructions otherwise); a held gate refuses changes anyway.
            bool froze = Gate.FreezeAlone();
            try
            {
                if (chunks == 1 && work.Samples())
                {
                    long start = Stopwatch.GetTimestamp();
                    work.Run(0);
                    work.Cost(Stopwatch.GetTimestamp() - start); // a pass cut into one chunk keeps learning its cost
                }
                else if (chunks == 1)
                {
                    work.Run(0);
                }
            }
            finally
            {
                if (froze) Gate.ThawAlone();
            }

            return;
        }

        Gate.FreezeParallel(); // the chunks may run on several threads: a structural change throws
        Interlocked.Increment(ref BufferStore.ParallelPasses); // chunks sharing a buffer's list count it down atomically
        try
        {
            if (!TryRunChunks(work, chunks))
                for (int i = 0; i < chunks; i++) work.Run(i);
        }
        finally
        {
            Interlocked.Decrement(ref BufferStore.ParallelPasses);
            Gate.ThawParallel();
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> items 0 .. <paramref name="count"/> - 1 on the chunk runner. False when it is busy (a
    /// parallel pass inside another one's item, or two systems of a stage at once) or the world allows one thread
    /// only: the caller then runs the items itself.
    /// </summary>
    private bool TryRunChunks(ParallelWork work, int count)
    {
        int helpers = Helpers;
        if (helpers == 0) return false;
        var runner = _chunkRunner;
        if (runner == null)
        {
            // Measuring, the helpers count what they allocate for the system running the pass (HelperAllocated).
            Interlocked.CompareExchange(ref _chunkRunner, new Runner(helpers, _options.MeasureSystems), null);
            runner = _chunkRunner;
        }

        if (!runner.TryRun(work, count, out long allocated)) return false;
        if (allocated != 0) HelperAllocated += allocated;
        return true;
    }

    /// <summary>The cached instance of <typeparamref name="TWork"/> for this world.</summary>
    internal TWork WorkOf<TWork>() where TWork : ParallelWork, new()
    {
        int index = TypeIndex<ParallelWork, TWork>.Value;
        var all = Volatile.Read(ref _parallelWork);
        if (index < all.Length && all[index] is TWork work) return work;
        return CreateWork<TWork>(index);
    }

    private TWork CreateWork<TWork>(int index) where TWork : ParallelWork, new()
    {
        lock (_parallelWorkLock)
        {
            var all = _parallelWork;
            if (index < all.Length && all[index] is TWork existing) return existing;
            var grown = new ParallelWork[Math.Max(index + 1, all.Length * 2)];
            Array.Copy(all, grown, all.Length);
            var work = new TWork();
            grown[index] = work;
            Volatile.Write(ref _parallelWork, grown);
            return work;
        }
    }

    /// <summary>
    /// Items that can run on any thread, in any order, and how long a run takes on the caller alone and with helpers
    /// (see <see cref="Wide"/>).
    /// </summary>
    internal abstract class ParallelWork
    {
        public abstract void Run(int index);

        /// <summary>The items (rows) of the run being cut, when its chunks follow their cost; 0 otherwise (nothing learned).</summary>
        internal long Items;

        // The items' cost: a moving average of thread time (timestamp ticks) per item, from the runs that counted.
        private double _perItem;

        /// <summary>Whether a run has measured the items' cost (<see cref="Rows"/> cuts by it).</summary>
        internal bool Measured => _perItem > 0;

        /// <summary>
        /// Chunk time: at most about 1 % of a chunk goes to claiming it, timing it and copying the job (well under a
        /// microsecond together), so a chunk of heavy items lasts about this long; more chunks than four per thread then
        /// even out what uneven items and helpers that start late leave unbalanced (OpenMP's <c>guided</c> and TBB's
        /// auto partitioner go the same way from the other end: they split while threads are idle).
        /// </summary>
        private static readonly double TargetTicks = Stopwatch.Frequency / 40_000.0; // 25 µs

        /// <summary>The shortest chunk: below it, a pass of light items makes fewer chunks (one runs on the caller alone).</summary>
        private static readonly double FloorTicks = Stopwatch.Frequency / 200_000.0; // 5 µs

        /// <summary>
        /// Items per chunk for <paramref name="items"/> items on <paramref name="threads"/> threads from their measured
        /// cost, a multiple of 64; 0 before a run measured it. Four chunks per thread, unless that makes chunks longer
        /// than <see cref="TargetTicks"/> (heavy items: chunks of that time, at most <see cref="MostChunksPerThread"/> per thread) or shorter than <see cref="FloorTicks"/>
        /// (light items, a small pass: chunks of that time, one when the whole pass is shorter, run on the caller).
        /// Only the chunk boundaries follow the cost, never a result: a pass that merges per chunk is cut by count.
        /// </summary>
        internal int Rows(long items, int threads)
        {
            double perItem = _perItem;
            if (perItem <= 0 || items <= 0) return 0;
            double total = items * perItem;
            if (total < FloorTicks) return (int)Math.Min((items + 63) & ~63L, int.MaxValue & ~63); // one chunk, on the caller
            double chunks = threads * 4.0;
            if (total > chunks * TargetTicks) chunks = Math.Min(total / TargetTicks, threads * (double)MostChunksPerThread);
            else if (total < chunks * FloorTicks) chunks = Math.Max(1, total / FloorTicks);
            double size = Math.Ceiling(items / chunks);
            return (int)Math.Min(((long)size + 63) & ~63L, int.MaxValue & ~63);
        }

        // Runs on the caller alone since the last one timed (Samples).
        private int _untimed;

        /// <summary>
        /// Whether to time this run on the caller alone: until the cost is measured, then one run in 16. A short pass
        /// (a few hundred light rows, a fraction of a microsecond) would otherwise pay two clock reads every time.
        /// </summary>
        internal bool Samples()
        {
            if (Items <= 0) return false;
            if (_perItem > 0 && ++_untimed < 16) return false;
            _untimed = 0;
            return true;
        }

        // Whether a run has been timed and dropped: the first one runs cold (JIT tiers, cache misses, helpers starting).
        private bool _warm;

        /// <summary>
        /// Record the thread time <paramref name="ticks"/> the run's <see cref="Items"/> took (at most 1.5 times the
        /// average counts). The first run is dropped: cold, it would cut a light pass into chunks it does not need (and
        /// wake helpers for the first time after the first call, which allocates).
        /// </summary>
        internal void Cost(long ticks)
        {
            long items = Items;
            if (items <= 0 || ticks <= 0) return;
            if (!_warm)
            {
                _warm = true;
                return;
            }

            double perItem = (double)ticks / items, average = _perItem;
            _perItem = average == 0 ? perItem : (average * 3 + Math.Min(perItem, average * 1.5)) / 4;
        }

        // Moving averages (timestamp ticks) of a run with helpers, of its items' summed thread time (what the caller
        // alone would take), and of a run alone.
        private long _wide, _work, _alone;
        private long _settled;           // the average alone a few runs after the choice (0 until then)
        private int _run;                // runs alone since the last probe (or into the probe)
        private int _period = MinPeriod; // runs alone between probes
        private bool _single;            // the choice: run alone

        private const int Burst = 4, Settle = 8, MinPeriod = 16, MaxPeriod = 1024;

        /// <summary>
        /// Whether the next run wakes helpers, and whether its time counts (<see cref="Took"/>). Helpers pay only when
        /// the work outlasts their start: a parked helper thread takes from a few to a few hundred microseconds to start
        /// (bare metal vs. a small VM) and burns about as much CPU on the way, so a small pass between server ticks is as
        /// fast on the caller alone, while back-to-back passes find the threads awake. Measured time decides: a run with
        /// helpers yields both its time and its items' summed thread time, so helpers are kept while they make a run at
        /// least a fifth faster than its work, with no run alone to compare. Once alone, the caller probes again after a
        /// period: four runs with helpers (the first only wakes them, and does not count: back-to-back runs find them
        /// awake). A probe that confirms running alone doubles the period (16 to 1,024 runs), and runs alone turning
        /// half as slow again as they settled at (the work grew) probe at once: the choice follows the work and
        /// the machine as they change, and a settled one costs little.
        /// </summary>
        public bool Wide(out bool sample)
        {
            sample = true;
            if (!_single) return true;
            int run = ++_run;
            if (run < _period)
            {
                if (run == Settle) _settled = _alone;
                if (run <= Settle || _alone * 2 <= _settled * 3) return false;
                run = _run = _period; // the work grew: probe now
            }

            if (run == _period) sample = false; // wakes the helpers
            else if (run == _period + Burst)
            {
                _run = 0;
                if (Beats(_wide, _work)) _single = false;
                else _period = Math.Min(_period * 2, MaxPeriod);
                return !_single;
            }

            return true;
        }

        /// <summary>Whether the work has chosen to run alone (its runs wake no helper until the next probe).</summary>
        internal bool Alone => _single;

        /// <summary>Helpers pay when they make a run at least a fifth faster than its work.</summary>
        private static bool Beats(long wide, long work) => wide < work - work / 5;

        /// <summary>
        /// Record a run: its time and, with helpers, its items' summed thread time. The first sample of a probe restarts
        /// the averages; a later one counts at most 1.5 times the average (a preempted run says little). Helpers that no
        /// longer pay switch to running alone.
        /// </summary>
        public void Took(bool wide, long ticks, long work)
        {
            Cost(wide ? work : ticks);
            if (!wide)
            {
                _alone = Blend(_alone, ticks);
                return;
            }

            bool first = _single && _run == _period + 1;
            _wide = first ? ticks : Blend(_wide, ticks);
            _work = first ? work : Blend(_work, work);
            if (!_single && !Beats(_wide, _work))
            {
                _single = true;
                _alone = _work;
                _run = 0;
                _period = MinPeriod;
            }
        }

        private static long Blend(long average, long ticks) =>
            average == 0 ? ticks : (average * 3 + Math.Min(ticks, average * 3 / 2)) / 4;
    }

    /// <summary>
    /// One stage of a schedule: systems that run at once (a schedule rebuilds its stages when it changes), cut into
    /// segments of one set for a run on one thread. In parallel, each system is an item, run by its set with a span of one.
    /// </summary>
    internal sealed unsafe class SystemStage(SystemSlot[] systems, Segment[] segments, Action<World>[] prepares) : ParallelWork
    {
        public readonly SystemSlot[] Systems = systems;
        public readonly Segment[] Segments = segments;

        /// <summary>What builds the indexes its systems' key lookups read (<see cref="SystemAccess"/>), each once.</summary>
        public readonly Action<World>[] Prepares = prepares;
        public World World;
        public long Update;

        public override void Run(int index)
        {
            var slot = Systems[index];
            ref var row = ref World.SystemRows[slot.Index];
            if (row.Version == slot.Version && row.Code is { } code) code.Set.Run(World, Systems.AsSpan(index, 1), Update, default, default);
        }
    }

    /// <summary>
    /// Runs one piece of work at a time; items are claimed through <c>Claims.Cursor</c>: the run's epoch in the high
    /// 32 bits, the next item index in the low 32. A helper that starts late (after its run finished) sees another
    /// epoch and leaves without touching the next run's state. The caller always takes part, so a run completes even
    /// when the helper threads are busy and no helper starts.
    /// </summary>
    private sealed class Runner
    {
        private readonly Helper[] _helpers;
        private readonly bool _measure; // helpers sum what they allocate running items (Claims.Allocated)
        private int _busy;

        // Written by the caller before the epoch is published, read by participants after claiming in that epoch: a
        // read-mostly line, apart from the cursor every claim writes and the sums each participant adds to as it leaves.
        private ParallelWork _work;
        private int _count;
        private Exception _error;
        private Claims _claims;

        /// <summary>The cursor alone on its line (a compare-exchange per item), the sums on the next.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 3 * Archetype.CacheLine)]
        private struct Claims
        {
            [FieldOffset(Archetype.CacheLine)] public long Cursor;
            [FieldOffset(2 * Archetype.CacheLine)] public int Remaining;
            [FieldOffset(2 * Archetype.CacheLine + 8)] public long Spent; // the items' summed thread time
            [FieldOffset(2 * Archetype.CacheLine + 16)] public long Allocated; // bytes helpers allocated running items (_measure)
        }

        public Runner(int helpers, bool measure)
        {
            _measure = measure;
            Workers.Ensure(helpers);
            _helpers = new Helper[helpers];
            for (int i = 0; i < helpers; i++) _helpers[i] = new Helper(this);
        }

        /// <summary>
        /// Run items 0 .. <paramref name="count"/> - 1 of <paramref name="work"/>; false when the runner is busy.
        /// <paramref name="allocated"/>: the bytes helper threads allocated running items (a measuring runner only: the
        /// caller's own items allocate on its thread, which its system measures).
        /// </summary>
        public bool TryRun(ParallelWork work, int count, out long allocated)
        {
            allocated = 0;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
            ThreadSetup.CatchUp(); // the caller's per-thread state too, before it runs an item
            try
            {
                allocated = Run(work, count);
            }
            finally
            {
                _work = null;
                Volatile.Write(ref _busy, 0);
            }

            return true;
        }

        /// <summary>Run the items; returns the bytes helpers allocated (read while the runner is still this run's).</summary>
        private long Run(ParallelWork work, int count)
        {
            bool wide = work.Wide(out bool sample);
            if (!wide) return RunAlone(work, count);

            // Close the last run's claims first: a late participant of it that read its cursor before this can no longer
            // claim (its compare-exchange fails), and one reading after it finds no item. Without this, one that read
            // the cursor at its end and then the larger _count of this run would claim an item of this run under the
            // old epoch: that item would run twice and Remaining never reach zero.
            ref var claims = ref _claims;
            Interlocked.Exchange(ref claims.Cursor, (claims.Cursor & ~0xFFFFFFFFL) | int.MaxValue);
            _work = work;
            _count = count;
            _error = null;
            claims.Remaining = count;
            claims.Spent = 0;
            claims.Allocated = 0;
            int epoch = (int)(claims.Cursor >> 32) + 1;
            // Publishes the fields above: a participant reads them only after a successful claim in this epoch.
            Interlocked.Exchange(ref claims.Cursor, (long)epoch << 32);

            long start = Stopwatch.GetTimestamp();
            int wanted = Math.Min(_helpers.Length, count - 1), queued = 0;
            Helper first = null, last = null;
            for (int i = 0; i < _helpers.Length && queued < wanted; i++)
            {
                // A helper still queued from an earlier run is skipped (it will exit on its stale epoch).
                var helper = _helpers[i];
                if (Interlocked.CompareExchange(ref helper.Queued, 1, 0) != 0) continue;
                helper.Epoch = epoch;
                if (last == null) first = helper;
                else last.Next = helper;
                last = helper;
                queued++;
            }

            if (queued != 0) Workers.Post(first, last, queued);

            Work(epoch, false);
            // Every item is claimed: at most one item per helper is still running. Spin and yield (never sleep, never
            // block): a blocking event allocates its lock the first time it waits.
            var spinner = new SpinWait();
            while (Volatile.Read(ref claims.Remaining) != 0) spinner.SpinOnce(sleep1Threshold: -1);
            if (sample) work.Took(true, Stopwatch.GetTimestamp() - start, Volatile.Read(ref claims.Spent));
            if (_error != null) ExceptionDispatchInfo.Throw(_error);
            return claims.Allocated;
        }

        /// <summary>
        /// A run the work chose to make alone (<see cref="ParallelWork.Wide"/>): every item on the caller, in order, with
        /// no claim, no per-item timestamp and no epoch. The last run's claims are closed already (each participant
        /// claimed until its count), and its count stays, so a helper queued by it that starts now still finds no
        /// item; the next run with helpers closes them again before it publishes anything. An item that throws does
        /// not stop the others; the first exception is rethrown once all have run, as with helpers.
        /// </summary>
        private static long RunAlone(ParallelWork work, int count)
        {
            Exception error = null;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    work.Run(i);
                }
                catch (Exception e)
                {
                    error ??= e;
                }
            }

            work.Took(false, Stopwatch.GetTimestamp() - start, 0); // a run alone always counts
            if (error != null) ExceptionDispatchInfo.Throw(error);
            return 0;
        }

        private void Work(int epoch, bool helper)
        {
            bool measure = helper && _measure;
            ref var claims = ref _claims;
            // Summed here and added once on leaving, after the participant's last item: the caller sees Remaining at 0
            // only when every participant has left, so it reads complete sums.
            int done = 0;
            long spent = 0, allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            while (true)
            {
                long cursor = Volatile.Read(ref claims.Cursor);
                int index = (int)cursor;
                if ((int)(cursor >> 32) != epoch || index >= _count) break;
                if (Interlocked.CompareExchange(ref claims.Cursor, cursor + 1, cursor) != cursor) continue;

                long start = Stopwatch.GetTimestamp();
                try
                {
                    _work.Run(index);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _error, e, null);
                }

                spent += Stopwatch.GetTimestamp() - start;
                done++;
            }

            if (done == 0) return; // a late helper: it touches nothing of the run
            Interlocked.Add(ref claims.Spent, spent);
            if (measure) Interlocked.Add(ref claims.Allocated, GC.GetAllocatedBytesForCurrentThread() - allocated);
            Interlocked.Add(ref claims.Remaining, -done);
        }

        private sealed class Helper(Runner runner) : Workers.Item
        {
            public int Queued;
            public int Epoch;

            public override void Execute()
            {
                int epoch = Epoch;
                Volatile.Write(ref Queued, 0);
                runner.Work(epoch, true);
            }
        }
    }
}
