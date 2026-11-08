using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The gate between a sleeping pass's two walks: Life plates with a blinker on <see cref="Awake"/> percent of them (the
/// others asleep), stepped by ref with the gate as shipped (Auto), forced to walk the woken tiles (Woken: a lookup per
/// woken tile) or every tile of the grid (EveryTile: the table in order, a test per sleeping tile), against the same
/// field slept by hand (Awake tags, a commit per plate). The share where EveryTile overtakes Woken is the gate.
/// </summary>
[MemoryDiagnoser]
public class TileWokenBenchmark
{
    [Params(1, 4)] public int Threads;

    [Params(Plot.Default, 256)] public int Side;

    /// <summary>The percent of the plates that hold a blinker (and stay awake), spread evenly.</summary>
    [Params(2, 6, 25, 50, 75)] public int Awake;

    /// <summary>The gate EasyEcs.Core reads (internal): set per invocation without allocating.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Share")]
    private static extern ref int Share([UnsafeAccessorType("EasyEcs.TileWalk, EasyEcs.Core")] object walk);

    private int _shipped;
    private World _hand, _auto, _woken, _everyTile;

    [GlobalSetup]
    public void Setup()
    {
        _shipped = Share(null);
        Plot.Side = Side;
        _hand = TileWorlds.Blinkers(Threads, awake: true, percent: Awake);
        _hand.AddSystem<TileSystems.SparseLifeSystem>();
        _hand.AddSystem<TileSystems.CommitAwakeSystem>();
        _hand.AddSystem<TileSystems.SleepSystem>();
        _hand.AddSystem<TileSystems.WakeSystem>();
        _auto = PingPongWorld();
        _woken = PingPongWorld();
        _everyTile = PingPongWorld();
        for (int i = 0; i < 4; i++)
        {
            Hand();
            Auto();
            Woken();
            EveryTile();
        }
        Share(null) = _shipped;
        if (_hand.Count<EasyEcs.Benchmark.Awake>(QueryFilter.Create()) != Side * Side * Awake / 100) throw new InvalidOperationException("the plates with a blinker stay awake");
        TileWorlds.SameSeeds(_hand, _auto);
        TileWorlds.SameSeeds(_hand, _woken);
        TileWorlds.SameSeeds(_hand, _everyTile);
    }

    private World PingPongWorld()
    {
        var world = TileWorlds.Blinkers(Threads, awake: false, next: false, percent: Awake);
        world.AddSystem<TileSystems.PingLifeSystem>();
        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Share(null) = _shipped;
        _hand.Dispose();
        _auto.Dispose();
        _woken.Dispose();
        _everyTile.Dispose();
    }

    [Benchmark(Baseline = true)] public void Hand() => _hand.Update();

    [Benchmark]
    public void Auto()
    {
        Share(null) = _shipped;
        _auto.Update();
    }

    [Benchmark]
    public void Woken()
    {
        Share(null) = 0;
        _woken.Update();
    }

    [Benchmark]
    public void EveryTile()
    {
        Share(null) = int.MaxValue;
        _everyTile.Update();
    }
}
