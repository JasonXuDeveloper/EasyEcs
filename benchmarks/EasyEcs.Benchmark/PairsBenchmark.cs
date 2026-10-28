using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/locality.md 4.1, phase 0: the sum over neighbors within a cutoff (<see cref="Crowd"/>), per tick, four
/// ways: the row loop over a hand-written grid reading neighbors through lookups (what users write today, the
/// baseline), the grid with copies of the positions and velocities, per-entity neighbor lists rebuilt every 10 ticks,
/// and a hand-written cluster-pair kernel over Morton-sorted rows in clusters of 8 (kernel only, the side arrays standing
/// for the sorted columns, 8 lanes with hardware 256-bit vectors, else 4: ARM64's NEON; with the sort every 1, 10 and
/// 20 ticks; and with the copies a user would add today), and that kernel on the core's order (L1: the table's own
/// columns kept sorted, clusters of 8 or 16, the core's sort every 10 ticks), and the generated pair method (L2: Generated,
/// GeneratedRows one pair at a time; GeneratedBoth writing both sides of each unordered pair once, one at a time). Each
/// invocation runs 20 ticks (OperationsPerInvoke), so a sort every k ticks is amortized exactly. One thread. Setup checks
/// every version against a double-precision reference.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class PairsBenchmark
{
    private const int Ticks = 20;

    [Params(1_000, 10_000, 100_000)] public int Entities;
    [Params(Density.Crowd, Density.Sparse)] public Density Density;

    private World _world;

    // (c) on the core's order: a world of the same crowd whose Position table the core keeps sorted (L1).
    private World _core;
    private readonly CoreClusters _clusters = new();

    // The pair method (L2): a world of the same crowd the generated pass orders and pairs on its own.
    private World _pairs;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _world = Crowd.Create(Entities, Density);
        var expected = Crowd.Reference(_world);
        Check(expected, nameof(GridLookups), () => Crowd.GridTick(_world));
        Check(expected, nameof(GridCopies), () => Crowd.CopiedGridTick(_world));
        Check(expected, nameof(NeighborLists10), () =>
        {
            Crowd.BuildLists(_world);
            Crowd.Interact(_world);
        });
        Check(expected, nameof(ClustersWithCopies10), () =>
        {
            Crowd.Sort(_world);
            Crowd.ClusterTickWithCopies(_world);
        });
        Check(expected, nameof(Clusters), () =>
        {
            Crowd.Sort(_world);
            _world.GetSingleton<Crowd.Clusters>().Rows.Interact();
            Crowd.WriteBack(_world);
        });

        _core = Crowd.Create(Entities, Density);
        Locality.CoreClusters.Order(_core, int.MaxValue); // sorted now; the benchmark sorts on its own cadence
        var sorted = Crowd.Reference(_core);
        CheckCore(sorted, nameof(CoreClusters), () => _clusters.Interact(_core));
        CheckCore(sorted, nameof(CoreClusters16), () => _clusters.Interact16(_core));

        _pairs = Crowd.Create(Entities, Density);
        CrowdPairs.Tick(_pairs);                              // the first pass sorts the tables: the reference after it
        var paired = Crowd.Reference(_pairs);
        CheckPairs(paired, nameof(Generated), () => CrowdPairs.Tick(_pairs));
        CheckPairs(paired, nameof(GeneratedRows), () => CrowdPairs.TickRows(_pairs));
        CheckPairs(paired, nameof(GeneratedParallel), () => CrowdPairs.TickParallel(_pairs));
        CheckPairs(paired, nameof(GeneratedBoth), () => CrowdPairs.TickBoth(_pairs));
    }

    private void CheckPairs((double, double, double)[] expected, string name, Action tick)
    {
        tick();
        tick();
        int at = Crowd.FirstMismatch(Crowd.Pushes(_pairs), expected);
        if (at >= 0) throw new InvalidOperationException($"{name} differs from the reference at row {at}");
    }

    private void CheckCore((double, double, double)[] expected, string name, Action tick)
    {
        tick();
        tick();
        int at = Crowd.FirstMismatch(Crowd.Pushes(_core), expected);
        if (at >= 0) throw new InvalidOperationException($"{name} differs from the reference at row {at}");
    }

    private void Check((double, double, double)[] expected, string name, Action tick)
    {
        tick();
        tick();                                               // the second run sees warm, grown arrays
        int at = Crowd.FirstMismatch(Crowd.Pushes(_world), expected);
        if (at >= 0) throw new InvalidOperationException($"{name} differs from the reference at row {at}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        _core.Dispose();
        _pairs.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Ticks)]
    public void GridLookups()
    {
        for (int t = 0; t < Ticks; t++) Crowd.GridTick(_world);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void GridCopies()
    {
        for (int t = 0; t < Ticks; t++) Crowd.CopiedGridTick(_world);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void NeighborLists10()
    {
        for (int t = 0; t < Ticks; t++)
        {
            if (t % 10 == 0) Crowd.BuildLists(_world);
            Crowd.Interact(_world);
        }
    }

    /// <summary>The kernel alone: the sort done in setup, the side arrays standing for sorted columns.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Clusters()
    {
        var rows = _world.GetSingleton<Crowd.Clusters>().Rows;
        for (int t = 0; t < Ticks; t++) rows.Interact();
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void ClustersSort1() => SortEvery(1);

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void ClustersSort10() => SortEvery(10);

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void ClustersSort20() => SortEvery(20);

    /// <summary>What a user can write today: sort every 10 ticks, and every tick copy the rows in and the pushes out.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void ClustersWithCopies10()
    {
        for (int t = 0; t < Ticks; t++)
        {
            if (t % 10 == 0) Crowd.Sort(_world);
            Crowd.ClusterTickWithCopies(_world);
        }
    }

    /// <summary>The kernel on the core's order: the table's own columns, sorted in setup; the pair lists cached.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void CoreClusters()
    {
        for (int t = 0; t < Ticks; t++) _clusters.Interact(_core);
    }

    /// <summary>The same in clusters of 16 (512-bit vectors; the 8-wide kernel where they are not hardware).</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void CoreClusters16()
    {
        for (int t = 0; t < Ticks; t++) _clusters.Interact16(_core);
    }

    /// <summary>The core's sort every 10 ticks (keys, the sortedness check, boxes) and the pair lists drawn again.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void CoreClustersSort10()
    {
        for (int t = 0; t < Ticks; t++)
        {
            if (t % 10 == 0) _core.SortRows<Crowd.Position>();
            _clusters.Interact(_core);
        }
    }

    /// <summary>The pair method: the generated pass, its own order (re-sorted when rows leave their clusters' boxes) and
    /// pair lists (drawn again on its cadence), a vector of rows against each neighbor.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Generated()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.Tick(_pairs);
    }

    /// <summary>The same pass one pair at a time (the method takes the neighbor's entity).</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void GeneratedRows()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickRows(_pairs);
    }

    /// <summary>The pair method on every thread the world allows (the machine's cores): chunks of clusters.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void GeneratedParallel()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickParallel(_pairs);
    }

    /// <summary>The pair method writing both sides: each unordered pair once, one at a time.</summary>
    [Benchmark(OperationsPerInvoke = Ticks)]
    public void GeneratedBoth()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickBoth(_pairs);
    }

    private void SortEvery(int ticks)
    {
        var rows = _world.GetSingleton<Crowd.Clusters>().Rows;
        for (int t = 0; t < Ticks; t++)
        {
            if (t % ticks == 0) Crowd.Sort(_world);
            rows.Interact();
        }
    }
}
