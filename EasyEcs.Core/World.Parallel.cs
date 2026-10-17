using System;
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
    private SystemStage _systemStage;

    // Chunked jobs, one cached instance per job type (TypeIndex<ParallelWork, TWork>): allocated on first use only.
    private ParallelWork[] _parallelWork = [];
    private readonly Lock _parallelWorkLock = new();

    /// <summary>Rows per chunk of a parallel [ForEach] (a multiple of 64, so presence-bit words never straddle chunks).</summary>
    public const int ParallelChunkRows = 16384;

    /// <summary>Thread-pool work items a runner keeps: the calling thread is the other one.</summary>
    private int Helpers => Math.Max(1, _options.MaxDegreeOfParallelism) - 1;

    private void RunStage(EcsSystem[] stage, long update)
    {
        var work = _systemStage ??= new SystemStage(this);
        work.Stage = stage;
        work.Update = update;
        var runner = _systemRunner ??= new Runner(Helpers);
        Gate.Freeze(); // the stage's systems never change structure (they do not run alone): a change throws
        try
        {
            if (runner.TryRun(work, stage.Length)) return;
            for (int i = 0; i < stage.Length; i++) RunSystem(stage[i], update); // an update from inside a running stage
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
            Interlocked.CompareExchange(ref _chunkRunner, new Runner(helpers), null);
            runner = _chunkRunner;
        }

        return runner.TryRun(work, count);
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

    /// <summary>Items that can run on any thread, in any order.</summary>
    internal abstract class ParallelWork
    {
        public abstract void Run(int index);
    }

    private sealed class SystemStage(World world) : ParallelWork
    {
        public EcsSystem[] Stage = [];
        public long Update;

        public override void Run(int index) => world.RunSystem(Stage[index], Update);
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
        private int _busy;

        // Written by the caller before the epoch is published, read by participants after claiming in that epoch.
        private ParallelWork _work;
        private int _count;
        private long _cursor;
        private int _remaining;
        private Exception _error;

        public Runner(int helpers)
        {
            _helpers = new Helper[helpers];
            for (int i = 0; i < helpers; i++) _helpers[i] = new Helper(this);
        }

        public bool TryRun(ParallelWork work, int count)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
            try
            {
                Run(work, count);
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
            int epoch = (int)(_cursor >> 32) + 1;
            // Publishes the fields above: a participant reads them only after a successful claim in this epoch.
            Interlocked.Exchange(ref _cursor, (long)epoch << 32);

            int wanted = Math.Min(_helpers.Length, count - 1);
            for (int i = 0; i < _helpers.Length && wanted > 0; i++)
            {
                // A helper still queued from an earlier run is skipped (it will exit on its stale epoch).
                if (Interlocked.CompareExchange(ref _helpers[i].Queued, 1, 0) != 0) continue;
                _helpers[i].Epoch = epoch;
                ThreadPool.UnsafeQueueUserWorkItem(_helpers[i], preferLocal: false);
                wanted--;
            }

            Work(epoch);
            // Every item is claimed: at most one item per helper is still running. Spin and yield (never sleep, never
            // block): a blocking event allocates its lock the first time it waits.
            var spinner = new SpinWait();
            while (Volatile.Read(ref _remaining) != 0) spinner.SpinOnce(sleep1Threshold: -1);
            if (_error != null) ExceptionDispatchInfo.Throw(_error);
        }

        private void Work(int epoch)
        {
            while (true)
            {
                long cursor = Volatile.Read(ref _cursor);
                int index = (int)cursor;
                if ((int)(cursor >> 32) != epoch || index >= _count) return;
                if (Interlocked.CompareExchange(ref _cursor, cursor + 1, cursor) != cursor) continue;

                try
                {
                    _work.Run(index);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _error, e, null);
                }

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
                runner.Work(epoch);
            }
        }
    }
}
