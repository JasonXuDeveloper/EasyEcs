using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Scenario;

namespace EasyEcs.Benchmark;

/// <summary>
/// Headline benchmark: one server tick of a realistic world (movement, AOI, combat, status effects with
/// component add/remove, projectile spawn/expire, NPC death/respawn). Steady state must allocate 0 bytes per tick.
/// </summary>
[MemoryDiagnoser]
public class WorldSimulationBenchmark
{
    [Params(10_000, 100_000)] public int Entities;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = Simulation.Create(Entities);
        // Let projectile counts, status effects and archetypes reach steady state (and assert the world is busy).
        Simulation.WarmUp(_world);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>One full world tick.</summary>
    [Benchmark]
    public void Tick() => _world.Update();
}

/// <summary>
/// Many worlds per process: one world per core, each ticked by its own thread (the recommended layout).
/// Measures scaling and contention on process-wide state (component registry, pools).
/// </summary>
[MemoryDiagnoser]
public class MultiWorldBenchmark
{
    [Params(100_000)] public int TotalEntities;

    private World[] _worlds;
    private Thread[] _threads;
    private Barrier _start, _done;
    private volatile bool _stop;

    [GlobalSetup]
    public void Setup()
    {
        int worlds = Environment.ProcessorCount;
        _worlds = new World[worlds];
        for (int i = 0; i < worlds; i++)
        {
            _worlds[i] = Simulation.Create(TotalEntities / worlds);
            Simulation.WarmUp(_worlds[i]);
        }

        // Long-lived owner threads, like a real server (no thread pool hops per tick).
        _start = new Barrier(worlds + 1);
        _done = new Barrier(worlds + 1);
        _threads = new Thread[worlds];
        for (int i = 0; i < worlds; i++)
        {
            var world = _worlds[i];
            _threads[i] = new Thread(() =>
            {
                while (true)
                {
                    _start.SignalAndWait();
                    if (_stop) return;
                    world.Update();
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
        foreach (var w in _worlds) w.Dispose();
    }

    /// <summary>One tick of every world, each on its own thread.</summary>
    [Benchmark]
    public void TickAllWorlds()
    {
        _start.SignalAndWait();
        _done.SignalAndWait();
    }
}
