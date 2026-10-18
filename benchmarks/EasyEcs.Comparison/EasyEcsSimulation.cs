using EasyEcs;
using EasyEcs.Benchmark.Scenario;

namespace EcsComparison;

/// <summary>
/// EasyEcs: the headline scenario (benchmarks/EasyEcs.Benchmark/WorldSimulation.cs, compiled in unchanged): [ForEach] systems,
/// immediate structural changes during iteration, counters in a singleton.
/// </summary>
public sealed class EasyEcsSimulation(int entities) : ISimulation
{
    private readonly World _world = Simulation.Create(entities);

    public string Library => "EasyEcs";

    public void Tick() => _world.Update();

    public Counters Snapshot()
    {
        ref var s = ref _world.GetSingleton<WorldStats>();
        return new Counters
        {
            Tick = s.Tick, CellChanges = s.CellChanges, Hits = s.Hits, Deaths = s.Deaths, NpcKills = s.NpcKills,
            Spawns = s.Spawns, Entities = _world.EntityCount,
        };
    }

    public void Dispose() => _world.Dispose();
}
