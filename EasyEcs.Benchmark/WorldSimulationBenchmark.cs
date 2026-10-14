using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Simulation;
using EasyEcs.Core;

namespace EasyEcs.Benchmark;

/// <summary>
/// Headline benchmark: one server tick of a realistic world (movement, AOI, combat, status effects with
/// component add/remove, projectile spawn/expire, NPC death/respawn). Steady state must allocate 0 bytes per tick.
/// </summary>
[MemoryDiagnoser]
public class WorldSimulationBenchmark
{
    [Params(10_000, 100_000)] public int Entities;

    private Context _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = World.Create(Entities);
        // Let projectile counts, status effects and archetypes reach steady state (and assert the world is busy).
        World.WarmUp(_world);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>One full world tick.</summary>
    [Benchmark]
    public void Tick() => _world.Update().GetAwaiter().GetResult();
}

/// <summary>
/// Sharded server: one world per core, each ticked by its own thread (the recommended layout).
/// Measures scaling and contention on process-wide state (component registry, pools).
/// </summary>
[MemoryDiagnoser]
public class ShardedWorldBenchmark
{
    [Params(100_000)] public int TotalEntities;

    private Context[] _worlds;
    private Thread[] _threads;
    private Barrier _start, _done;
    private volatile bool _stop;

    [GlobalSetup]
    public void Setup()
    {
        int shards = Environment.ProcessorCount;
        _worlds = new Context[shards];
        for (int i = 0; i < shards; i++)
        {
            _worlds[i] = World.Create(TotalEntities / shards);
            World.WarmUp(_worlds[i]);
        }

        // Long-lived shard threads, like a real server (no thread pool hops per tick).
        _start = new Barrier(shards + 1);
        _done = new Barrier(shards + 1);
        _threads = new Thread[shards];
        for (int i = 0; i < shards; i++)
        {
            var world = _worlds[i];
            _threads[i] = new Thread(() =>
            {
                while (true)
                {
                    _start.SignalAndWait();
                    if (_stop) return;
                    world.Update().GetAwaiter().GetResult();
                    _done.SignalAndWait();
                }
            }) { IsBackground = true };
            _threads[i].Start();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stop = true;
        _start.SignalAndWait();
        foreach (var w in _worlds) w.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>One tick of every shard in parallel.</summary>
    [Benchmark]
    public void TickAllShards()
    {
        _start.SignalAndWait();
        _done.SignalAndWait();
    }
}
