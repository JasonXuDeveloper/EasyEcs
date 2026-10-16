using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Showcase;

namespace EasyEcs.Benchmark;

/// <summary>
/// The battle server of the README (<see cref="BattleServer"/>), which uses every feature: a parallel [ForEach], batch
/// methods, immediate structural changes, WithAny, change tracking (replication every third tick), a value index and a
/// relation, system groups, an update interval, singletons. One operation is one server tick.
/// Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class BattleServerBenchmark
{
    [Params(100_000)] public int Units;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = BattleServer.Create(Units);
        for (int i = 0; i < 300; i++) BattleServer.Tick(_world);
        var battle = _world.GetSingleton<Battle>();
        if (battle.Hits == 0 || battle.Deaths == 0 || battle.Shots == 0 || battle.Replicated == 0 || battle.Afflicted == 0)
            throw new System.InvalidOperationException($"The battle is idle: {battle}");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Tick() => BattleServer.Tick(_world);
}
