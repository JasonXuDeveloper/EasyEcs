using System;
using System.Buffers;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A per-row working list in a parallel [ForEach]: each entity gathers the 24 candidate cells around it (a neighbour
/// search's shape), keeps those its rule accepts, and stores how many and their spread. Where the list lives:
/// <see cref="Scratch{T}"/> (one list per method and thread), a new list per row (the obvious code), a rented array per
/// row (<c>ArrayPool&lt;int&gt;.Shared</c>), a user's own <c>[ThreadStatic]</c> list, and <c>stackalloc</c> (only for a
/// small bound known up front: the baseline a list cannot beat). On one thread (the chunks run one after another on
/// the calling thread: the per-row cost alone, steady on a busy machine) and on four.
/// </summary>
[MemoryDiagnoser]
public class ScratchBenchmark
{
    [Params(10_000, 100_000)] public int Entities;
    [Params(1, 4)] public int Threads;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities, MaxDegreeOfParallelism = Threads });
        _world.CreateEntities(new Entity[Entities], new Cell(0), new Spread());
        int row = 0;
        foreach (var item in _world.Query<Cell>()) item.Component1 = new Cell(row++ * 7919);
        for (int i = 0; i < 20; i++) Searches.WithScratch(_world); // every helper thread has made its list
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void Stackalloc() => Searches.WithStackalloc(_world);

    [Benchmark]
    public void Scratch() => Searches.WithScratch(_world);

    [Benchmark]
    public void NewList() => Searches.WithNewList(_world);

    [Benchmark]
    public void ArrayPoolRent() => Searches.WithArrayPool(_world);

    [Benchmark]
    public void ThreadStaticList() => Searches.WithThreadStatic(_world);

    public record struct Cell(int Key) : IComponent;
    public record struct Spread(int Count, int Range) : IComponent;

    public static class Searches
    {
        private const int Candidates = 24;

        // The accepted candidates of a cell: a cheap stand-in for a distance or line-of-sight test.
        private static int Candidate(int key, int i) => (key ^ (i * 40_503)) & 1023;
        private static bool Accepted(int value) => (value & 3) != 0;

        private static Spread Measure(ReadOnlySpan<int> kept)
        {
            int min = int.MaxValue, max = int.MinValue;
            for (int i = 0; i < kept.Length; i++)
            {
                min = Math.Min(min, kept[i]);
                max = Math.Max(max, kept[i]);
            }

            return new Spread(kept.Length, kept.Length == 0 ? 0 : max - min);
        }

        private static Spread Measure(List<int> kept)
        {
            int min = int.MaxValue, max = int.MinValue;
            for (int i = 0; i < kept.Count; i++)
            {
                min = Math.Min(min, kept[i]);
                max = Math.Max(max, kept[i]);
            }

            return new Spread(kept.Count, kept.Count == 0 ? 0 : max - min);
        }

        [ForEach(Parallel = true)]
        public static void WithStackalloc(in Cell cell, ref Spread spread)
        {
            Span<int> kept = stackalloc int[Candidates];
            int count = 0;
            for (int i = 0; i < Candidates; i++)
                if (Accepted(Candidate(cell.Key, i))) kept[count++] = Candidate(cell.Key, i);
            spread = Measure(kept[..count]);
        }

        [ForEach(Parallel = true)]
        public static void WithScratch(in Cell cell, ref Spread spread, Scratch<List<int>> scratch)
        {
            var kept = scratch.Value ??= new List<int>(Candidates);
            kept.Clear();
            for (int i = 0; i < Candidates; i++)
                if (Accepted(Candidate(cell.Key, i))) kept.Add(Candidate(cell.Key, i));
            spread = Measure(kept);
        }

#pragma warning disable EECS028 // the allocation per row is what this variant measures
        [ForEach(Parallel = true)]
        public static void WithNewList(in Cell cell, ref Spread spread)
        {
            var kept = new List<int>(Candidates);
            for (int i = 0; i < Candidates; i++)
                if (Accepted(Candidate(cell.Key, i))) kept.Add(Candidate(cell.Key, i));
            spread = Measure(kept);
        }
#pragma warning restore EECS028

        [ForEach(Parallel = true)]
        public static void WithArrayPool(in Cell cell, ref Spread spread)
        {
            int[] kept = ArrayPool<int>.Shared.Rent(Candidates);
            int count = 0;
            for (int i = 0; i < Candidates; i++)
                if (Accepted(Candidate(cell.Key, i))) kept[count++] = Candidate(cell.Key, i);
            spread = Measure(kept.AsSpan(0, count));
            ArrayPool<int>.Shared.Return(kept);
        }

        [ForEach(Parallel = true)]
        public static void WithThreadStatic(in Cell cell, ref Spread spread)
        {
            var kept = PerThread.List ??= new List<int>(Candidates);
            kept.Clear();
            for (int i = 0; i < Candidates; i++)
                if (Accepted(Candidate(cell.Key, i))) kept.Add(Candidate(cell.Key, i));
            spread = Measure(kept);
        }
    }

    /// <summary>What a user writes without <see cref="Scratch{T}"/>: a thread-static field of their own.</summary>
    private static class PerThread
    {
        [ThreadStatic] public static List<int> List;
    }
}
