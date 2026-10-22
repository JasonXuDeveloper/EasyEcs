using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A histogram of 100,000 rows into 1,024 to 65,536 bins (units per map cell, players per zone), the keyed reduction a
/// dedicated API would serve: <see cref="Reduce{T}"/> over a singleton of <c>[InlineArray]</c> bins (combined per
/// chunk: bins × chunks), against what it would be built on: bins per thread merged once per thread when the pass
/// ends (threads × bins, the candidate API), a shared array with an <see cref="Interlocked"/> increment per row, and
/// one thread's plain loop. From 16,384 bins (64 KB) on, a Reduce&lt;T&gt; cannot hold them: the generated job keeps a copy
/// per chunk in an array, and the runtime refuses arrays of value types of 64 KB or more (TypeLoadException).
/// </summary>
[MemoryDiagnoser]
public class HistogramBenchmark
{
    [Params(1_024, 4_096, 16_384, 65_536)] public int Bins;

    private const int Rows = 100_000;
    private World _world;
    private int[] _shared = [];
    private int[] _result = [];

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Rows });
        var entities = new Entity[Rows];
        _world.CreateEntities(entities, new Cell(0));
        var random = new Random(9);
        for (int i = 0; i < entities.Length; i++) entities[i].Set(new Cell(random.Next(Bins)));
        _shared = new int[Bins];
        _result = new int[Bins];
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int Serial()
    {
        Array.Clear(_shared);
        Histograms.Count(_world, _shared);
        return _shared[0];
    }

    [Benchmark]
    public int ReduceInlineArray()
    {
        switch (Bins)
        {
            case 1_024:
                Histograms.Reduce1K(_world);
                return _world.GetSingleton<Bins1K>().Counts[0];
            case 4_096:
                Histograms.Reduce4K(_world);
                return _world.GetSingleton<Bins4K>().Counts[0];
            default:
                return -1; // 64 KB or more: no array of the job's per-chunk copies can be created
        }
    }

    [Benchmark]
    public int InterlockedShared()
    {
        Array.Clear(_shared);
        var job = new Shared { Counts = _shared };
        _world.ForEachParallel<Cell, Shared>(ref job);
        return _shared[0];
    }

    [Benchmark]
    public int PerThreadBins()
    {
        PerThread.Bins = Bins;
        var job = new PerThread();
        _world.ForEachBatchParallel<Cell, PerThread>(ref job);
        PerThread.MergeInto(_result);
        return _result[0];
    }

    public record struct Cell(int Bin) : IComponent;

    [InlineArray(1_024)]
    public struct Counts1K
    {
        private int _first;
    }

    [InlineArray(4_096)]
    public struct Counts4K
    {
        private int _first;
    }

    public struct Bins1K : ISingletonComponent, IReduction<Bins1K>
    {
        public Counts1K Counts;

        public static Bins1K Identity => default;

        public static Bins1K Combine(Bins1K left, Bins1K right)
        {
            Span<int> into = left.Counts;
            ReadOnlySpan<int> from = right.Counts;
            for (int i = 0; i < into.Length; i++) into[i] += from[i];
            return left;
        }
    }

    public struct Bins4K : ISingletonComponent, IReduction<Bins4K>
    {
        public Counts4K Counts;

        public static Bins4K Identity => default;

        public static Bins4K Combine(Bins4K left, Bins4K right)
        {
            Span<int> into = left.Counts;
            ReadOnlySpan<int> from = right.Counts;
            for (int i = 0; i < into.Length; i++) into[i] += from[i];
            return left;
        }
    }

    public static class Histograms
    {
        [ForEach]
        public static void Count(in Cell cell, int[] counts) => counts[cell.Bin]++;

        [ForEach(Parallel = true)]
        public static void Reduce1K(in Cell cell, ref Reduce<Bins1K> bins) => bins.Value.Counts[cell.Bin]++;

        [ForEach(Parallel = true)]
        public static void Reduce4K(in Cell cell, ref Reduce<Bins4K> bins) => bins.Value.Counts[cell.Bin]++;
    }

    private struct Shared : IForEach<Cell>, INoStructuralChanges
    {
        public int[] Counts;

        public void Execute(ref Cell cell) => Interlocked.Increment(ref Counts[cell.Bin]);

        static int IForEach<Cell>.WriteMask => 0;
    }

    /// <summary>The candidate: each thread counts into its own bins (found once per run of rows), merged once per thread.</summary>
    private struct PerThread : IForEachBatch<Cell>, INoStructuralChanges
    {
        public static int Bins;
        [ThreadStatic] private static int[] t_counts;
        private static readonly List<int[]> All = new();
        private static readonly Lock Gate = new();

        public void Execute(EntitySpan entities, Span<Cell> cells)
        {
            var counts = t_counts;
            if (counts == null || counts.Length != Bins) counts = Register();
            for (int i = 0; i < cells.Length; i++) counts[cells[i].Bin]++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int[] Register()
        {
            var counts = t_counts = new int[Bins];
            lock (Gate) All.Add(counts);
            return counts;
        }

        /// <summary>The sum of every thread's bins, which start again at 0. Owner thread, after the pass.</summary>
        public static void MergeInto(int[] result)
        {
            Array.Clear(result);
            lock (Gate)
                foreach (var counts in All)
                {
                    if (counts.Length != result.Length) continue;
                    for (int i = 0; i < counts.Length; i++) result[i] += counts[i];
                    Array.Clear(counts);
                }
        }

        static int IForEachBatch<Cell>.WriteMask => 0;
    }
}
