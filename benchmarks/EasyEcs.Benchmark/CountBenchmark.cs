using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Counting rows that must be tested one by one: 100,000 fighters in one table, half of them with <c>Npc</c> disabled
/// and two thirds with <c>Dead</c> disabled, so two columns' rows differ and no per-column count answers the query. 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class CountBenchmark
{
    public struct Npc : IComponent;
    public struct Dead : IComponent;

    [Params(100_000)] public int Entities;

    private World _world;
    private static readonly QueryFilter LivingNpcs = QueryFilter.Create().With<Npc>().Without<Dead>();

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        for (int i = 0; i < Entities; i++)
        {
            // Disabled first, then enabled again: the rows share one table and only their presence bits differ.
            var e = _world.CreateEntity(new Health { Value = i }, new Npc(), new Dead());
            e.Disable<Npc>();
            e.Disable<Dead>();
            if (i % 2 != 0) e.Enable<Npc>();
            if (i % 3 == 0) e.Enable<Dead>();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int Count() => _world.Count<Health>(LivingNpcs);
}
