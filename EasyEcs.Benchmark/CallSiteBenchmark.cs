using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The cost of a query call itself: counting a few entities with a filter built at the call, a filter built once,
/// and no filter (the memo's first way). 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class CallSiteBenchmark
{
    public record struct Health(int Value) : IComponent;
    public struct Dead : IComponent;
    public struct Frozen : IComponent;
    public struct Npc : IComponent;

    private World _world;
    private static readonly QueryFilter Ready = QueryFilter.Create().With<Npc>().Without<Dead>().Without<Frozen>();

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        for (int i = 0; i < 16; i++)
        {
            var e = _world.CreateEntity(new Health(i));
            if (i % 2 == 0) e.Set(new Npc());
            if (i % 3 == 0) e.Set(new Dead());
            if (i % 5 == 0) e.Set(new Frozen());
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int FilterAtCall() => _world.Count<Health>(QueryFilter.Create().With<Npc>().Without<Dead>().Without<Frozen>());

    [Benchmark]
    public int FilterOnce() => _world.Count<Health>(Ready);

    [Benchmark]
    public int NoFilter() => _world.Count<Health>();
}
