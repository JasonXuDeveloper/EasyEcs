using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace EasyEcs;

// Parallel batches (WorldOptions.Parallel): the systems of one batch have non-conflicting SystemAccess, so they run at
// the same time. Allocation-free, unlike Parallel.For (closure + state per call): the calling thread works through the
// batch together with up to MaxDegreeOfParallelism - 1 reusable thread-pool work items.
public sealed partial class World
{
    private BatchRunner _batchRunner;

    private void RunBatch(EcsSystem[] batch, long update)
    {
        var runner = _batchRunner ??= new BatchRunner(this, Math.Max(1, _options.MaxDegreeOfParallelism) - 1);
        runner.Run(batch, update);
    }

    /// <summary>
    /// Runs one batch at a time; systems are claimed through <see cref="_cursor"/>: the batch's epoch in the high
    /// 32 bits, the next system index in the low 32. A helper that starts late (after its batch finished) sees another
    /// epoch and leaves without touching the next batch's state. The caller always takes part, so a batch completes even
    /// when the thread pool is busy and no helper starts.
    /// </summary>
    private sealed class BatchRunner
    {
        private readonly World _world;
        private readonly Helper[] _helpers;
        private readonly ManualResetEventSlim _done = new(false);

        // Written by the caller before the epoch is published, read by participants after claiming in that epoch.
        private EcsSystem[] _batch = [];
        private long _update;
        private long _cursor;
        private int _remaining;
        private Exception _error;

        public BatchRunner(World world, int helpers)
        {
            _world = world;
            _helpers = new Helper[helpers];
            for (int i = 0; i < helpers; i++) _helpers[i] = new Helper(this);
        }

        public void Run(EcsSystem[] batch, long update)
        {
            _batch = batch;
            _update = update;
            _error = null;
            _remaining = batch.Length;
            _done.Reset();
            int epoch = (int)(_cursor >> 32) + 1;
            // Publishes the fields above: a participant reads them only after a successful claim in this epoch.
            Interlocked.Exchange(ref _cursor, (long)epoch << 32);

            int wanted = Math.Min(_helpers.Length, batch.Length - 1);
            for (int i = 0; i < _helpers.Length && wanted > 0; i++)
            {
                // A helper still queued from an earlier batch is skipped (it will exit on its stale epoch).
                if (Interlocked.CompareExchange(ref _helpers[i].Queued, 1, 0) != 0) continue;
                _helpers[i].Epoch = epoch;
                ThreadPool.UnsafeQueueUserWorkItem(_helpers[i], preferLocal: false);
                wanted--;
            }

            Work(epoch);
            if (Volatile.Read(ref _remaining) != 0) _done.Wait();
            if (_error != null) ExceptionDispatchInfo.Throw(_error);
        }

        private void Work(int epoch)
        {
            while (true)
            {
                long cursor = Volatile.Read(ref _cursor);
                int index = (int)cursor;
                if ((int)(cursor >> 32) != epoch || index >= _batch.Length) return;
                if (Interlocked.CompareExchange(ref _cursor, cursor + 1, cursor) != cursor) continue;

                try
                {
                    _world.RunSystem(_batch[index], _update);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _error, e, null);
                }

                if (Interlocked.Decrement(ref _remaining) == 0) _done.Set();
            }
        }

        private sealed class Helper(BatchRunner runner) : IThreadPoolWorkItem
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
