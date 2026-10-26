using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/locality.md 4.2, phase 0: BattleServer's NearestPlayer (<see cref="PlayerSearch"/>) over its 100,000 units
/// (33,333 players), per query: the sample's scan of the players (the baseline) against a search of their sorted
/// blocks, sorted once in setup, and with the sort paid by 64 queries. Setup checks every query finds a player at the
/// same distance.
/// </summary>
[MemoryDiagnoser]
public class NearestPlayerBenchmark
{
    private const int Queries = 64;

    [Params(100_000)] public int Units;

    private World _world;
    private readonly PlayerSearch.Position[] _at = new PlayerSearch.Position[Queries];

    [GlobalSetup]
    public void Setup()
    {
        _world = PlayerSearch.Create(Units);
        var random = new Random(7);
        for (int q = 0; q < Queries; q++) _at[q] = new PlayerSearch.Position(random.Next(4096), random.Next(4096));
        PlayerSearch.Sort(_world);
        foreach (var at in _at)
        {
            float scan = PlayerSearch.DistanceTo(_world, PlayerSearch.Scan(_world, at).Id, at);
            float blocks = PlayerSearch.DistanceTo(_world, PlayerSearch.Search(_world, at), at);
            if (scan != blocks) throw new InvalidOperationException($"at {at}: the blocks find {blocks}, the scan {scan}");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = Queries)]
    public int Scan()
    {
        int sum = 0;
        foreach (var at in _at) sum += PlayerSearch.Scan(_world, at).Id.Index;
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Queries)]
    public int Blocks()
    {
        int sum = 0;
        foreach (var at in _at) sum += PlayerSearch.Search(_world, at).Index;
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Queries)]
    public int BlocksWithSort()
    {
        PlayerSearch.Sort(_world);
        return Blocks();
    }
}
