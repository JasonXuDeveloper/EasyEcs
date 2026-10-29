using System;

namespace EasyEcs;

/// <summary>
/// A job over a range of indexes, run in chunks by <see cref="World.ForParallel{TJob}"/> (Kokkos' <c>RangePolicy</c>,
/// Unity's <c>IJobParallelForBatch</c>): each call gets one chunk.
/// </summary>
public interface IForRange
{
    /// <summary>Run indexes <paramref name="start"/> to <paramref name="end"/> - 1, one chunk.</summary>
    void Execute(int start, int end);
}

/// <summary>
/// A job over the elements of a list, run in chunks by <see cref="World.ForParallel{T, TJob}"/>: each call gets one
/// chunk's elements, writable.
/// </summary>
/// <typeparam name="T">The element.</typeparam>
public interface IForSpan<T>
{
    /// <summary>Run <paramref name="items"/>, the elements from index <paramref name="start"/> on, one chunk.</summary>
    void Execute(int start, Span<T> items);
}

// Parallel loops over a range or a list: the runner and chunk claiming of parallel [ForEach] passes, for data that is
// not rows (an Emitted<T> singleton's records, a grid, the cells of a map).
public sealed partial class World
{
    /// <summary>
    /// Run <paramref name="job"/> over indexes 0 to <paramref name="count"/> - 1 on several threads: the range is cut
    /// into chunks (a multiple of 64 indexes: about four per thread, between 1,024 and <see cref="MaxChunkRows"/>,
    /// until a call has measured the items' cost, then chunks of about 25 µs for heavy items and fewer for a short loop;
    /// or <paramref name="chunkSize"/> rounded up to 64) that the calling thread and up to
    /// <see cref="WorldOptions.MaxDegreeOfParallelism"/> - 1 helper threads claim one at a time, each running a copy
    /// of the job (what Execute writes into its fields is not merged back). The same runner as the parallel
    /// <c>[ForEach]</c> passes: helpers are woken only while they pay, and fewer than two chunks, or a runner already
    /// busy, run on the calling thread. The job must not change the world's structure (a structural change throws) nor
    /// write what another chunk reads. Owner thread. Allocation-free after the first call per job type.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void ForParallel<TJob>(int count, ref TJob job, int chunkSize = 0) where TJob : struct, IForRange
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0) return;
        var work = WorkOf<RangeChunks<TJob>>();
        work.Job = job;
        RunRange(work, count, chunkSize);
    }

    /// <summary>
    /// <see cref="ForParallel{TJob}"/> over the elements of <paramref name="items"/> (an <see cref="Emitted{T}"/>
    /// singleton's records, a buffer of a singleton): each chunk gets its elements as a span, which it may write. The
    /// list must not change while the call runs. The records of an <see cref="Emitted{T}"/> that parallel passes left in
    /// their chunks' lists are run where they are, in order, not joined first: a chunk that spans two of those lists
    /// gets one call per list, each with its own <c>start</c>.
    /// </summary>
    public void ForParallel<T, TJob>(ref DynamicBuffer<T> items, ref TJob job, int chunkSize = 0) where TJob : struct, IForSpan<T>
    {
        var store = items.Store;
        if (store is not { Shares: BufferStore.Segmented }) // an Emitted<T>'s list in segments is its own already
            if ((store = DynamicBufferExtensions.Own(ref items)) is not { Count: > 0 }) return; // the chunks write it: its own
        var work = WorkOf<SpanChunks<T, TJob>>();
        work.Job = job;
        work.Store = store;
        RunRange(work, store.Count, chunkSize);
    }

    /// <summary>
    /// <see cref="ForParallel{TJob}"/> in chunks of exactly <paramref name="size"/> indexes, for heavy items that are no
    /// rows of a table (no presence-bit word to keep whole): a stepping tile pass's woken tiles
    /// (<see cref="TileSteps{TCell, TTile, TGrid, TKey, TColors}.StepWokenParallel{TJob}"/>), where chunks of 64 tiles
    /// would leave most threads idle.
    /// </summary>
    internal void ForParallelChunks<TJob>(int count, ref TJob job, int size) where TJob : struct, IForRange
    {
        if (count == 0) return;
        var work = WorkOf<RangeChunks<TJob>>();
        work.Job = job;
        work.Items = 0; // cut by the caller's size, never by cost
        work.Count = count;
        work.Size = size;
        try
        {
            RunChunks(work, (count + size - 1) / size);
        }
        finally
        {
            work.Release();
        }
    }

    private void RunRange(RangeWork work, int count, int chunkSize)
    {
        int size = ChunkRows(work, count, chunkSize);
        work.Count = count;
        work.Size = size;
        int chunks = (int)((count + (long)size - 1) / size);
        try
        {
            RunChunks(work, chunks);
        }
        finally
        {
            work.Release();
        }
    }

    /// <summary>The chunks of a range: chunk <c>i</c> is indexes [i × Size, min((i + 1) × Size, Count)).</summary>
    private abstract class RangeWork : ParallelWork
    {
        public int Count, Size;

        public override void Run(int index)
        {
            int start = index * Size;
            Execute(start, Math.Min(start + Size, Count));
        }

        protected abstract void Execute(int start, int end);

        /// <summary>Drop the references of the last call (the job's own) until the next one.</summary>
        public abstract void Release();
    }

    private sealed class RangeChunks<TJob> : RangeWork where TJob : struct, IForRange
    {
        public TJob Job;

        protected override void Execute(int start, int end)
        {
            var job = Job;
            job.Execute(start, end);
        }

        public override void Release() => Job = default;
    }

    private sealed class SpanChunks<T, TJob> : RangeWork where TJob : struct, IForSpan<T>
    {
        public TJob Job;
        public BufferStore<T> Store;

        protected override void Execute(int start, int end)
        {
            var job = Job;
            var store = Store;
            if (store.Shares != BufferStore.Segmented)
            {
                job.Execute(start, new Span<T>(store.Items, start, end - start));
                return;
            }

            while (start < end) // the chunk's runs, one per segment it spans
            {
                var run = store.Run(start, end);
                if (run.IsEmpty) return; // the list changed under the call
                job.Execute(start, run);
                start += run.Length;
            }
        }

        public override void Release()
        {
            Job = default;
            Store = null;
        }
    }
}
