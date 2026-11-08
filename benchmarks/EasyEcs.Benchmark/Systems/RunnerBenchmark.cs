using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A pass outside any system, counting the wounded of 100,000 fighters among which a quarter are dead: a static
/// [ForEach] method run through its generated entry point, the struct job it replaces (written by hand) and a Query
/// loop. Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class RunnerBenchmark
{
    [Params(100_000)] public int Entities;

    public record struct Health(int Value, int Max) : IComponent;
    public struct Dead : IComponent;

    private World _world;
    private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        int dead = (Entities + 3) / 4; // every fourth fighter, in a table of its own
        _world.Reserve<Health>(Entities - dead);
        _world.Reserve<Health, Dead>(dead);
        for (int i = 0; i < Entities; i++)
        {
            if (i % 4 == 0) _world.CreateEntity(new Health(i % 100, 100), new Dead());
            else _world.CreateEntity(new Health(i % 100, 100));
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [ForEach, Without<Dead>]
    private static void CountWounded(in Health hp, ref int wounded)
    {
        if (hp.Value < hp.Max) wounded++;
    }

    [Benchmark(Baseline = true)]
    public int Runner()
    {
        int wounded = 0;
        RunnerBenchmark.CountWounded(_world, ref wounded); // inside the type, the entry point is named with it
        return wounded;
    }

    private struct WoundedJob : IForEach<Health>
    {
        public int Wounded;

        public void Execute(ref Health hp)
        {
            if (hp.Value < hp.Max) Wounded++;
        }
    }

    [Benchmark]
    public int HandWrittenJob()
    {
        var job = new WoundedJob();
        _world.ForEach<Health, WoundedJob>(ref job, in Alive);
        return job.Wounded;
    }

    [Benchmark]
    public int Query()
    {
        int wounded = 0;
        foreach (var item in _world.Query<Health>(Alive))
            if (item.Component1.Value < item.Component1.Max) wounded++;
        return wounded;
    }
}
