using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Showcase;

namespace EasyEcs.Benchmark;

/// <summary>
/// The battle server example (doc/example.md) (<see cref="BattleServer"/>), which uses every feature; the tick runs a
/// parallel [ForEach], parallel systems, batch methods, immediate structural changes, disabled components, cleanup
/// components, a hierarchy, WithAny, change tracking (replication every third tick), groups, an ordered index, a
/// relation, Maybe, system groups, update intervals and phases, ordering, singletons, timings. One operation is one
/// server tick. Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class BattleServerBenchmark
{
    [Params(100_000)] public int Units;

    private BattleServer _server;

    [GlobalSetup]
    public void Setup()
    {
        _server = new BattleServer(Units);
        for (int i = 0; i < 1_200; i++) _server.Tick();                     // tables at their peak populations
        var battle = _server.Battle;
        if (battle.Hits == 0 || battle.Deaths == 0 || battle.Shots == 0 || battle.Replicated == 0 || battle.Afflicted == 0)
            throw new System.InvalidOperationException($"The battle is idle: {battle}");
    }

    [GlobalCleanup]
    public void Cleanup() => _server.Dispose();

    [Benchmark]
    public void Tick() => _server.Tick();
}
