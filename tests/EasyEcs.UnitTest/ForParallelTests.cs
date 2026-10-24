using System;
using System.Threading;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary><see cref="World.ForParallel{TJob}"/>: a range or a list's elements in chunks, on the world's runner.</summary>
public class ForParallelTests
{
    /// <summary>Each index written by exactly one chunk.</summary>
    private struct Fill : IForRange
    {
        public int[] Cells;

        public void Execute(int start, int end)
        {
            for (int i = start; i < end; i++) Cells[i] += i + 1;
        }
    }

    /// <summary>Counts the chunks and the threads that ran them (Interlocked: shared on purpose).</summary>
    private struct Visits : IForRange
    {
        public int[] Chunks;
        public int[] Smallest;

        public void Execute(int start, int end)
        {
            Interlocked.Increment(ref Chunks[0]);
            int length = end - start;
            int seen;
            while (length < (seen = Volatile.Read(ref Smallest[0])) && Interlocked.CompareExchange(ref Smallest[0], length, seen) != seen) { }
        }
    }

    /// <summary>The scenario of the records a parallel pass emits: each hit's amount scaled in place, chunk by chunk.</summary>
    private struct Scale : IForSpan<EmitTests.Hit>
    {
        public int Factor;

        public void Execute(int start, Span<EmitTests.Hit> items)
        {
            for (int i = 0; i < items.Length; i++) items[i] = items[i] with { Amount = items[i].Amount * Factor + start + i };
        }
    }

    private struct Structural : IForRange
    {
        public World World;

        public void Execute(int start, int end) => World.CreateEntity(new Health(start));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(1023)]
    [TestCase(1025)]
    [TestCase(100_003)]
    public void EveryIndexRunsOnce(int count)
    {
        foreach (int threads in new[] { 1, 4 })
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            var job = new Fill { Cells = new int[count] };
            for (int run = 1; run <= 20; run++)
            {
                world.ForParallel(count, ref job);
                for (int i = 0; i < count; i++)
                    if (job.Cells[i] != run * (i + 1)) Assert.Fail($"{threads} threads, run {run}: index {i} holds {job.Cells[i]}");
            }
        }
    }

    [Test]
    public void ChunksFollowChunkRows()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var census = new Visits { Chunks = new int[1], Smallest = [int.MaxValue] };
        world.ForParallel(10_000, ref census, chunkRows: 100); // rounded up to 128
        Assert.That(census.Chunks[0], Is.EqualTo((10_000 + 127) / 128));
        Assert.That(census.Smallest[0], Is.EqualTo(10_000 % 128));
        census = new Visits { Chunks = new int[1], Smallest = [int.MaxValue] };
        world.ForParallel(500, ref census); // below the smallest default chunk: one chunk, on the caller
        Assert.That(census.Chunks[0], Is.EqualTo(1));
    }

    [Test]
    public void AListsElementsAreWrittenInPlace()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        ref var hits = ref world.GetOrAddSingleton<Emitted<EmitTests.Hit>>().Items;
        for (int i = 0; i < 50_000; i++) hits.Add(new EmitTests.Hit(default, i));
        var job = new Scale { Factor = 3 };
        world.ForParallel(ref hits, ref job);
        for (int i = 0; i < hits.Count; i++) Assert.That(hits[i].Amount, Is.EqualTo(i * 3 + i));
        hits.Clear();
        world.ForParallel(ref hits, ref job); // no elements: nothing to run
        using var fresh = new World();
        ref var none = ref fresh.GetOrAddSingleton<Emitted<EmitTests.Hit>>().Items;
        Assert.That(none.Capacity, Is.Zero);
        fresh.ForParallel(ref none, ref job); // no list at all
    }

    [Test]
    public void AStructuralChangeInsideThrows()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var job = new Structural { World = world };
        Assert.Throws<InvalidOperationException>(() => world.ForParallel(10_000, ref job));
        Assert.That(world.EntityCount, Is.Zero);
        var fill = new Fill { Cells = new int[10] };
        world.ForParallel(10, ref fill); // the world works after the throw
        Assert.That(fill.Cells[9], Is.EqualTo(10));
    }

    [Test]
    public void ANegativeCountThrows()
    {
        using var world = new World();
        var job = new Fill { Cells = [] };
        Assert.Throws<ArgumentOutOfRangeException>(() => world.ForParallel(-1, ref job));
    }

    [Test]
    public void ForParallelDoesNotAllocateAfterItsFirstCall()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var fill = new Fill { Cells = new int[100_000] };
        world.GetOrAddSingleton<Emitted<EmitTests.Hit>>().Items.SetCount(100_000);
        var scale = new Scale { Factor = 1 };
        void Calls(int count)
        {
            ref var hits = ref world.GetSingleton<Emitted<EmitTests.Hit>>().Items;
            for (int i = 0; i < count; i++)
            {
                world.ForParallel(fill.Cells.Length, ref fill);
                world.ForParallel(ref hits, ref scale);
            }
        }

        Calls(20);
        Assert.That(Allocations.Of(() => Calls(200)), Is.Zero);
    }
}
