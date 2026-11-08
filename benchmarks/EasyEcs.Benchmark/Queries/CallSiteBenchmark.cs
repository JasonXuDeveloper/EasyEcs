using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The cost of a query call itself: counting a few entities with a filter built at the call, a filter built once,
/// no filter (the memo's first way), and a filter past the memo's ways (the query cache's dictionary). 0 bytes.
/// Every case counts the same 16 entities in the same 8 tables (the filters rule none of them out), so the cases
/// differ only in the call: a filter that matched fewer tables would look cheaper by the tables it skips.
/// </summary>
[MemoryDiagnoser]
public class CallSiteBenchmark
{
    public record struct Health(int Value) : IComponent;
    public struct Dead : IComponent;
    public struct Frozen : IComponent;
    public struct Npc : IComponent;
    public struct Elite : IComponent;
    public struct Ranged : IComponent;
    public struct Flying : IComponent;

    private World _world;
    private static readonly QueryFilter Ready = QueryFilter.Create().With<Npc>().Without<Dead>().Without<Frozen>();
    private static readonly QueryFilter Living = QueryFilter.Create().Without<Dead>();

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        for (int i = 0; i < 16; i++)
        {
            // Every entity is a living, unfrozen NPC; the markers spread them over 8 tables.
            var e = _world.CreateEntity(new Health(i), new Npc());
            if (i % 2 == 0) e.Set(new Elite());
            if (i % 3 == 0) e.Set(new Ranged());
            if (i % 5 == 0) e.Set(new Flying());
        }

        // The signature's ways: no filter, Ready and two more; Living takes the dictionary.
        _world.Count<Health>();
        _world.Count<Health>(Ready);
        _world.Count<Health>(QueryFilter.Create().With<Dead>());
        _world.Count<Health>(QueryFilter.Create().With<Frozen>());
        if (FilterAtCall() != 16 || FilterOnce() != 16 || NoFilter() != 16 || PastTheMemo() != 16)
            throw new System.InvalidOperationException("every case must count the same 16 entities");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int FilterAtCall() => _world.Count<Health>(QueryFilter.Create().With<Npc>().Without<Dead>().Without<Frozen>());

    [Benchmark]
    public int FilterOnce() => _world.Count<Health>(Ready);

    [Benchmark]
    public int NoFilter() => _world.Count<Health>();

    [Benchmark]
    public int PastTheMemo() => _world.Count<Health>(Living);
}
