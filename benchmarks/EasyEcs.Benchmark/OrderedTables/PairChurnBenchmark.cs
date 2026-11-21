using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// The generated pair method over a table whose rows come and go every tick (items dropped and picked up, shots,
/// spawns): each tick <see cref="ChurnPerMille"/> rows of the moving crowd (<see cref="Crowd"/>) are destroyed and as
/// many created at random places, then an update (the ordered tables' cadence: a sort every 10 updates, or sooner as
/// the core decides) and the pass, one direction (Generated) or writing both sides (Both). A row created lands past the
/// sorted rows, then in the place of a destroyed one (the table's swap-remove): between sorts it widens a cluster's box
/// across the map. Each invocation runs 20 ticks (OperationsPerInvoke). Setup checks the pass against the
/// double-precision reference after a run of churned ticks.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class PairChurnBenchmark
{
    private const int Ticks = 20;

    /// <summary>The rows and how many of each thousand are replaced per tick.</summary>
    [ParamsSource(nameof(Cases))] public (int Entities, int ChurnPerMille) Case;

    /// <summary>A small table at 0.1, 1 and 10 % a tick, a large one at 0.1 and 1 %.</summary>
    public static (int, int)[] Cases => [(10_000, 1), (10_000, 10), (10_000, 100), (100_000, 1), (100_000, 10)];

    private int Entities => Case.Entities;
    private int ChurnPerMille => Case.ChurnPerMille;

    private World _world;
    private Entity[] _live;
    private Random _random;
    private float _extent;
    private int _tick;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _world = Crowd.Create(Entities, Density.Crowd);
        _extent = _world.GetSingleton<Crowd.Map>().Extent;
        _live = new Entity[Entities];
        int k = 0;
        foreach (var item in _world.Query<Crowd.Position>()) _live[k++] = item.Entity;
        _random = new Random(7);
        Check(both: false);
        Check(both: true);
    }

    private void Check(bool both)
    {
        for (int t = 0; t < Ticks; t++) Tick(both);
        var expected = Crowd.Reference(_world);
        int at = Crowd.FirstMismatch(Crowd.Pushes(_world), expected);
        if (at >= 0) throw new InvalidOperationException($"{(both ? "Both" : "Generated")} differs from the reference at row {at}");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>Replace the churned rows, step the crowd (forth, then back), update, run the pass.</summary>
    private void Tick(bool both)
    {
        int churn = (int)((long)Entities * ChurnPerMille / 1000);
        _world.Reserve<Crowd.Position, Crowd.Velocity, Crowd.Push, Crowd.Neighbors>(churn);
        for (int i = 0; i < churn; i++)
        {
            int at = _random.Next(_live.Length);
            _live[at].Destroy();
            _live[at] = _world.CreateEntity(
                new Crowd.Position(_random.NextSingle() * _extent, _random.NextSingle() * _extent),
                new Crowd.Velocity(_random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1),
                new Crowd.Push(), new Crowd.Neighbors());
        }

        if (_tick++ % Ticks >= Ticks / 2) CrowdPairs.StepBack(_world);
        else CrowdPairs.StepForward(_world);
        _world.Update();
        if (both) CrowdPairs.TickBoth(_world);
        else CrowdPairs.Tick(_world);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Ticks)]
    public void Generated()
    {
        for (int t = 0; t < Ticks; t++) Tick(both: false);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Both()
    {
        for (int t = 0; t < Ticks; t++) Tick(both: true);
    }
}
