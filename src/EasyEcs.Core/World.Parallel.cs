using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace EasyEcs;

// Parallel work: the systems of one stage (ParallelSystems; non-conflicting SystemAccess) and the row chunks of a
// parallel [ForEach] (ForEachParallel). Allocation-free, unlike Parallel.For (closure + state per call): the calling
// thread works through the items together with up to MaxDegreeOfParallelism - 1 reusable thread-pool work items.
public sealed partial class World
{
    private Runner _systemRunner;
    private Runner _chunkRunner;

    /// <summary>
    /// Bytes the helper threads of this thread's parallel [ForEach] passes allocated, summed (only with
    /// <see cref="WorldOptions.MeasureSystems"/>): a measured run adds what grew during it to its system's
    /// <see cref="SystemStats.LastAllocated"/>. Per thread: two systems of a stage may run passes at once.
    /// </summary>
    [ThreadStatic] private static long HelperAllocated;

    // Chunked jobs, one cached instance per job type (TypeIndex<ParallelWork, TWork>): allocated on first use only.
    private ParallelWork[] _parallelWork = [];
    private readonly Lock _parallelWorkLock = new();

    /// <summary>
    /// The most rows per chunk of a parallel [ForEach]. Chunks hold about a quarter of each thread's share of the
    /// matching rows (at least 1,024): equal chunks finish together, and four per thread even out uneven rows.
    /// </summary>
    public const int ParallelChunkRows = 16384;

    /// <summary>
    /// Rows per chunk for a pass over <paramref name="tables"/> (over one of <paramref name="slices"/> slices of each,
    /// <see cref="QueryFilter.Slice"/>): a multiple of 64 (presence-bit words never straddle
    /// chunks), about four chunks per thread, between 1,024 and <see cref="ParallelChunkRows"/>. With fixed 16,384-row
    /// chunks, 100,000 rows made 7 chunks for 4 threads: the second round ran 3 of them and left a thread idle. A pass
    /// that <paramref name="stamps"/> a tracked component gets a multiple of 4,096 rows: each block of 4,096 rows shares
    /// one change-tick summary per column (<see cref="Archetype.Blocks"/>), whose plain read-modify-write two chunks of
    /// one block would race on.
    /// </summary>
    internal int ChunkRows(Archetype[] tables, bool stamps, int slices = 0)
    {
        long rows = 0;
        for (int i = 0; i < tables.Length; i++) rows += tables[i].Count;
        if (slices > 1) rows /= slices; // a sliced pass visits about 1 / slices of them
        long share = rows / ((Helpers + 1) * 4L);
        if (stamps) return (int)Math.Clamp((share + 4095) & ~4095L, 4096, ParallelChunkRows);
        return (int)Math.Clamp((share + 63) & ~63L, 1024, ParallelChunkRows);
    }

    /// <summary>Thread-pool work items a runner keeps: the calling thread is the other one.</summary>
    private int Helpers => Math.Max(1, _options.MaxDegreeOfParallelism) - 1;

    private void RunStage(SystemStage stage, long update)
    {
        var systems = stage.Systems;
        stage.World = this;
        stage.Update = update;
        // Each system of the stage measures its own allocations (RunMeasured, on the thread that runs it).
        var runner = _systemRunner ??= new Runner(Helpers, false);
        Gate.Freeze(); // the stage's systems never change structure (they do not run alone): a change throws
        try
        {
            if (runner.TryRun(stage, systems.Length, out _)) return;
            for (int i = 0; i < systems.Length; i++) RunSystem(systems[i], update); // an update from inside a running stage
        }
        finally
        {
            Gate.Thaw();
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> items 0 .. <paramref name="count"/> - 1 on the chunk runner. False when it is busy (a
    /// parallel pass inside another one's item, or two systems of a stage at once) or the world allows one thread
    /// only: the caller then runs the items itself.
    /// </summary>
    internal bool TryRunChunks(ParallelWork work, int count)
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
        /// the work outlasts their start: a parked pool thread takes from a few to a few hundred microseconds to start
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

    /// <summary>One stage of a schedule: systems that run at once (a schedule rebuilds its stages when it changes).</summary>
    internal sealed class SystemStage(EcsSystem[] systems) : ParallelWork
    {
        public readonly EcsSystem[] Systems = systems;
        public World World;
        public long Update;

        public override void Run(int index) => World.RunSystem(Systems[index], Update);
    }

    /// <summary>
    /// Runs one piece of work at a time; items are claimed through <see cref="_cursor"/>: the run's epoch in the high
    /// 32 bits, the next item index in the low 32. A helper that starts late (after its run finished) sees another
    /// epoch and leaves without touching the next run's state. The caller always takes part, so a run completes even
    /// when the thread pool is busy and no helper starts.
    /// </summary>
    private sealed class Runner
    {
        private readonly Helper[] _helpers;
        private readonly bool _measure; // helpers sum what they allocate running items (_allocated)
        private int _busy;

        // Written by the caller before the epoch is published, read by participants after claiming in that epoch.
        private ParallelWork _work;
        private int _count;
        private long _cursor;
        private int _remaining;
        private long _spent; // the items' summed thread time
        private long _allocated; // bytes helpers allocated running items (_measure)
        private Exception _error;

        public Runner(int helpers, bool measure)
        {
            _measure = measure;
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
            try
            {
                Run(work, count);
                allocated = _allocated; // read while the runner is still this run's
            }
            finally
            {
                _work = null;
                Volatile.Write(ref _busy, 0);
            }

            return true;
        }

        private void Run(ParallelWork work, int count)
        {
            _work = work;
            _count = count;
            _error = null;
            _remaining = count;
            _spent = 0;
            _allocated = 0;
            int epoch = (int)(_cursor >> 32) + 1;
            // Publishes the fields above: a participant reads them only after a successful claim in this epoch.
            Interlocked.Exchange(ref _cursor, (long)epoch << 32);

            bool wide = work.Wide(out bool sample);
            long start = Stopwatch.GetTimestamp();
            int wanted = wide ? Math.Min(_helpers.Length, count - 1) : 0;
            for (int i = 0; i < _helpers.Length && wanted > 0; i++)
            {
                // A helper still queued from an earlier run is skipped (it will exit on its stale epoch).
                if (Interlocked.CompareExchange(ref _helpers[i].Queued, 1, 0) != 0) continue;
                _helpers[i].Epoch = epoch;
                ThreadPool.UnsafeQueueUserWorkItem(_helpers[i], preferLocal: false);
                wanted--;
            }

            Work(epoch, false);
            // Every item is claimed: at most one item per helper is still running. Spin and yield (never sleep, never
            // block): a blocking event allocates its lock the first time it waits.
            var spinner = new SpinWait();
            while (Volatile.Read(ref _remaining) != 0) spinner.SpinOnce(sleep1Threshold: -1);
            if (sample) work.Took(wide, Stopwatch.GetTimestamp() - start, Volatile.Read(ref _spent));
            if (_error != null) ExceptionDispatchInfo.Throw(_error);
        }

        private void Work(int epoch, bool helper)
        {
            bool measure = helper && _measure;
            while (true)
            {
                long cursor = Volatile.Read(ref _cursor);
                int index = (int)cursor;
                if ((int)(cursor >> 32) != epoch || index >= _count) return;
                if (Interlocked.CompareExchange(ref _cursor, cursor + 1, cursor) != cursor) continue;

                long start = Stopwatch.GetTimestamp();
                long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                try
                {
                    _work.Run(index);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _error, e, null);
                }

                // Added before the item counts as done: the caller reads the sums once the last one is.
                Interlocked.Add(ref _spent, Stopwatch.GetTimestamp() - start);
                if (measure) Interlocked.Add(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - allocated);
                Interlocked.Decrement(ref _remaining);
            }
        }

        private sealed class Helper(Runner runner) : IThreadPoolWorkItem
        {
            public int Queued;
            public int Epoch;

            public void Execute()
            {
                int epoch = Epoch;
                Volatile.Write(ref Queued, 0);
                runner.Work(epoch, true);
            }
        }
    }
}
