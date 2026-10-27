using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/locality.md 4.2, phase 0: samples/VoxelServer's ZombieTargetSystem.Pick (<see cref="ZombieHunt"/>), every
/// zombie once (a full look: the sample spreads it over 10 ticks with Slices). The ring walk through the players' chunk
/// index and position lookups (the sample's form, the baseline) against a search of the players' sorted clusters, with the
/// sort once per look and with it 10 times per look (the players move every tick and the sample looks every tick).
/// 3 zombies per player, at the sample's sizes (50 players; 2,000 for its crowd run), on its spawn grid or spread out.
/// Setup checks that every zombie finds a player at the same distance.
/// </summary>
[MemoryDiagnoser]
public class NearestZombieBenchmark
{
    [Params(50, 2_000)] public int Players;
    [Params(Layout.Packed, Layout.Spread)] public Layout Layout;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = ZombieHunt.Create(Players, Layout);
        ZombieHunt.Pick(_world);
        var expected = ZombieHunt.Targets(_world);
        ClustersSortOnce();
        var got = ZombieHunt.Targets(_world);
        for (int i = 0; i < got.Length; i++)
            if (got[i].Distance != expected[i].Distance)
                throw new InvalidOperationException($"zombie {i}: the clusters find {got[i].Distance}, the ring walk {expected[i].Distance}");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void RingWalk() => ZombieHunt.Pick(_world);

    [Benchmark]
    public void ClustersSortOnce()
    {
        ZombieHunt.SortPlayers(_world);
        ZombieHunt.PickInClusters(_world);
    }

    [Benchmark]
    public void ClustersSortPerTick()
    {
        for (int t = 0; t < 10; t++) ZombieHunt.SortPlayers(_world);
        ZombieHunt.PickInClusters(_world);
    }
}
