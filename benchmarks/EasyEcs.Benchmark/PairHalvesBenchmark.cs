using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// The crowd's pair method writing both sides (<see cref="CrowdPairs.SeparateBoth"/>) on 1 and 4 threads: the serial pass
/// (Serial, the baseline), the parallel pass of chunks kept apart by colors (Parallel), and the parallel
/// pass of the method writing its row only over every ordered pair (Full: twice the pairs, no writes of neighbors). Each
/// invocation runs 20 ticks (OperationsPerInvoke). Setup checks every pass against the double-precision reference.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class PairHalvesBenchmark
{
    private const int Ticks = 20;

    [Params(10_000, 100_000)] public int Entities;
    [Params(Density.Crowd, Density.Sparse)] public Density Density;
    [Params(1, 4)] public int Threads;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _world = Crowd.Create(Entities, Density, threads: Threads);
        CrowdPairs.TickBoth(_world); // the first pass sorts the tables: the reference after it
        var expected = Crowd.Reference(_world);
        Check(expected, nameof(Serial), () => CrowdPairs.TickBoth(_world));
        Check(expected, nameof(Parallel), () => CrowdPairs.TickBothParallel(_world));
        Check(expected, nameof(Full), () => CrowdPairs.TickParallel(_world));
    }

    private void Check((double, double, double)[] expected, string name, Action tick)
    {
        tick();
        tick();
        int at = Crowd.FirstMismatch(Crowd.Pushes(_world), expected);
        if (at >= 0) throw new InvalidOperationException($"{name} ({Threads} threads) differs from the reference at row {at}");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>The serial pass writing both sides: each unordered pair once, one at a time.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Ticks)]
    public void Serial()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickBoth(_world);
    }

    /// <summary>The same method in parallel: chunks of a fixed number of rows, colored.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Parallel()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickBothParallel(_world);
    }

    /// <summary>The method writing its row only, every ordered pair, in parallel (vector loop).</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Full()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickParallel(_world);
    }
}
