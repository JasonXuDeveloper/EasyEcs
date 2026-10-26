using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/locality.md 4.1, phase 0: the sum over neighbors within a cutoff (<see cref="Crowd"/>), per tick, four
/// ways: the row loop over a hand-written grid reading neighbors through lookups (what users write today, the
/// baseline), the grid with copies of the positions and velocities, per-entity neighbor lists rebuilt every 10 ticks,
/// and a hand-written cluster-pair kernel over Morton-sorted rows in blocks of 8 (kernel only, the side arrays standing
/// for the sorted columns, 8 lanes with hardware 256-bit vectors, else 4: ARM64's NEON; with the sort every 1, 10 and
/// 20 ticks; and with the copies a user would add today). Each
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
    }

    private void Check((double, double, double)[] expected, string name, Action tick)
    {
        tick();
        tick();                                               // the second run sees warm, grown arrays
        int at = Crowd.FirstMismatch(Crowd.Pushes(_world), expected);
        if (at >= 0) throw new InvalidOperationException($"{name} differs from the reference at row {at}");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

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
