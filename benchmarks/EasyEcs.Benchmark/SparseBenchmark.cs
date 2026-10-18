using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A rare state in a large table: 100,000 units, 10 of them burning (a tag held in the units' own table, so only their
/// presence bits differ). A pass over the burning, a query over them, a count of them and a pass over the rest: the
/// walks skip the words without a passing row, and span the words where every row passes, 4 words per compare. 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class SparseBenchmark
{
    public struct Burning : IComponent;

    public struct BurnPass : ISystemGroup;

    public struct CoolPass : ISystemGroup;

    /// <summary>The burning lose health.</summary>
    [UpdateInGroup<BurnPass>]
    public class Burn : EcsSystem
    {
        [ForEach, With<Burning>]
        private static void Hurt(ref Health health) => health.Value--;
    }

    /// <summary>The others heal.</summary>
    [UpdateInGroup<CoolPass>]
    public class Heal : EcsSystem
    {
        [ForEach, Without<Burning>]
        private static void Mend(ref Health health) => health.Value++;
    }

    [Params(100_000)] public int Units;

    private World _world;
    private static readonly QueryFilter Burnt = QueryFilter.Create().With<Burning>();

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Units });
        var units = new Entity[Units];
        _world.CreateEntities(units, new Health { Value = 100 }, new Burning());
        for (int i = 0; i < Units; i++)
            if (i % (Units / 10) != 7) units[i].Remove<Burning>();
        _world.AddSystem<Burn>();
        _world.AddSystem<Heal>();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void PassOverTheFew() => _world.Update<BurnPass>();

    [Benchmark]
    public int QueryTheFew()
    {
        int total = 0;
        foreach (var item in _world.Query<Health>(Burnt)) total += item.Component1.Value;
        return total;
    }

    [Benchmark]
    public int CountTheFew() => _world.Count<Health>(Burnt);

    [Benchmark]
    public void PassOverTheRest() => _world.Update<CoolPass>();
}
