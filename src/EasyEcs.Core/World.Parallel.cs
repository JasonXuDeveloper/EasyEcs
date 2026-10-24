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
    /// The most rows per chunk of a parallel [ForEach] that does not set its own (<c>chunkRows</c>). Chunks hold about
    /// a quarter of each thread's share of the matching rows (at least 1,024): equal chunks finish together, and four
    /// per thread even out uneven rows.
    /// </summary>
    public const int ParallelChunkRows = 16384;

    /// <summary>
    /// Rows per chunk for a pass over <paramref name="tables"/> (over one of <paramref name="slices"/> slices of each,
    /// <see cref="QueryFilter.Slice"/>): <paramref name="chunkRows"/> rounded up to 64 when given (the caller knows its
    /// rows are heavy, OpenMP's <c>schedule(dynamic, n)</c>); else about four chunks per thread, between 1,024 and
    /// <see cref="ParallelChunkRows"/>. Always a multiple of 64: presence-bit words and change-tick words never straddle
    /// chunks. With fixed 16,384-row chunks, 100,000 rows made 7 chunks for 4 threads: the second round ran 3 of them
    /// and left a thread idle. A pass that <paramref name="stamps"/> a tracked component gets a multiple of 4,096 rows
    /// unless it sets its own: each block of 4,096 rows shares one change-tick summary per column
    /// (<see cref="Archetype.Blocks"/>), which chunks splitting a block share through <see cref="Archetype.OpenBlocks{T}"/>.
    /// A pass that <paramref name="merges"/> partial results per chunk cuts <see cref="MergedChunks"/> chunks whatever the
    /// threads, so its result does not depend on them.
    /// </summary>
    internal int ChunkRows(Archetype[] tables, bool stamps, bool merges, int slices = 0, int chunkRows = 0)
    {
        if (chunkRows > 0) return ChunkRows(0, chunkRows);
        long rows = 0;
        for (int i = 0; i < tables.Length; i++) rows += tables[i].Count;
        if (slices > 1) rows /= slices; // a sliced pass visits about 1 / slices of them
        long chunks = merges ? MergedChunks : (Helpers + 1) * 4L;
        if (!stamps) return ChunkRows(rows, 0, chunks);
        long share = rows / chunks;
        return (int)Math.Clamp((share + 4095) & ~4095L, 4096, ParallelChunkRows);
    }

    /// <summary>
    /// The chunks a pass that merges per chunk (a <see cref="Reduce{T}"/>, an <see cref="Emit{T}"/>) cuts its rows into
    /// when it does not set <c>ChunkRows</c>: a constant, so its chunk boundaries follow the row count alone and a
    /// floating-point result is the same bits at any <see cref="WorldOptions.MaxDegreeOfParallelism"/> (the threads only
    /// share the chunks out). Four per thread at four threads; tables of 262,144 rows or more get chunks of
    /// <see cref="ParallelChunkRows"/> rows, more than enough for any thread count.
    /// </summary>
    private const int MergedChunks = 16;

    /// <summary>
    /// Items per chunk for <paramref name="items"/> items (rows of a pass, elements of <see cref="ForParallel{TJob}"/>):
    /// <paramref name="chunkRows"/> rounded up to 64 when given, else about four chunks per thread, a multiple of 64
    /// between 1,024 and <see cref="ParallelChunkRows"/>.
    /// </summary>
    private int ChunkRows(long items, int chunkRows, long chunks = 0)
    {
        if (chunkRows > 0) return (int)Math.Min((chunkRows + 63L) & ~63L, int.MaxValue & ~63);
        long share = items / (chunks > 0 ? chunks : (Helpers + 1) * 4L);
        return (int)Math.Clamp((share + 63) & ~63L, 1024, ParallelChunkRows);
    }

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
                segment.Set.Run(this, systems.AsSpan(segment.Start, segment.Count), update, default);
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
                if (chunks == 1) work.Run(0);
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
            if (row.Version == slot.Version && row.Code is { } code) code.Set.Run(World, Systems.AsSpan(index, 1), Update, default);
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
