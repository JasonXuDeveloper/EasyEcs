using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Requests on one entity at a time: 1,000 calls, each on another of 10,000 units (a tenth of them dead), applying a
/// hit to <c>Health</c> (tracked) and counting it in <c>Hits</c>, refused for a dead unit. <c>Handwritten</c> is the
/// struct job and <c>static readonly</c> filter a request handler writes for <c>entity.Run</c>; <c>EntityMethod</c> is
/// the same method declared with <c>[EntityMethod, Without&lt;Dead&gt;]</c>, run through its generated entry point;
/// <c>EntityMethodChanged</c> adds <c>[Changed&lt;Health&gt;]</c> (every unit passes: tick 1). 0 bytes every way.
/// </summary>
[MemoryDiagnoser]
public class EntityMethodBenchmark
{
    private const int Count = 10_000, Calls = 1_000;

    private World _world;
    private Entity[] _units;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _units = new Entity[Count];
        _world.CreateEntities(_units, new Health(1_000_000), new Hits(0));
        for (int i = 0; i < Count; i += 10) _units[i].Set(new Dead());
        for (int i = 0; i < 10; i++) { Handwritten(); EntityMethod(); }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int Handwritten()
    {
        int done = 0;
        for (int i = 0; i < Calls; i++)
        {
            var hit = new HitJob { Damage = 1 };
            if (_units[i * 7 % Count].Run<Health, Hits, HitJob>(ref hit, Alive)) done++;
        }

        return done;
    }

    [Benchmark]
    public int EntityMethod()
    {
        int done = 0;
        for (int i = 0; i < Calls; i++)
            if (UnitRequests.Hit(_units[i * 7 % Count], 1)) done++;
        return done;
    }

    /// <summary>The method with <c>[Changed&lt;Health&gt;]</c> too: its entry point takes the tick to look back from.</summary>
    [Benchmark]
    public int EntityMethodChanged()
    {
        int done = 0;
        for (int i = 0; i < Calls; i++)
            if (UnitRequests.HitIfHit(_units[i * 7 % Count], 1, 1)) done++;
        return done;
    }

    private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();

    public record struct Health(int Value) : IComponent, ITracked;
    public record struct Hits(int Count) : IComponent;
    public struct Dead : IComponent;

    private struct HitJob : IForEachEntity<Health, Hits>, INoStructuralChanges
    {
        public int Damage;

        public void Execute(Entity unit, ref Health hp, ref Hits hits)
        {
            hp.Value -= Damage;
            hits.Count++;
        }
    }
}

public static class UnitRequests
{
    [EntityMethod, Without<EntityMethodBenchmark.Dead>]
    static void Hit(ref EntityMethodBenchmark.Health hp, ref EntityMethodBenchmark.Hits hits, int damage)
    {
        hp.Value -= damage;
        hits.Count++;
    }

    [EntityMethod, Without<EntityMethodBenchmark.Dead>, Changed<EntityMethodBenchmark.Health>]
    static void HitIfHit(ref EntityMethodBenchmark.Health hp, ref EntityMethodBenchmark.Hits hits, int damage)
    {
        hp.Value -= damage;
        hits.Count++;
    }
}
