using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// An optional component: 100,000 units, half of them with a <c>Shield</c>; every unit regenerates, a shielded one
/// more. One [ForEach] method taking <c>Maybe&lt;Shield&gt;</c> (a lookup per entity) against two methods split by
/// <c>[With&lt;Shield&gt;]</c> / <c>[Without&lt;Shield&gt;]</c> (no lookup), both as runners. Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class MaybeBenchmark
{
    [Params(100_000)] public int Units;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Units });
        for (int i = 0; i < Units; i++)
        {
            var unit = _world.CreateEntity(new Stamina(0));
            if (i % 2 == 0) unit.Set(new Shield(2));
        }

        OneMethod();
        TwoMethods();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void OneMethod() => Regeneration.WithMaybe(_world);

    [Benchmark]
    public void TwoMethods()
    {
        Regeneration.Shielded(_world);
        Regeneration.Plain(_world);
    }

    public record struct Stamina(int Value) : IComponent;
    public record struct Shield(int Bonus) : IComponent;
}

public static class Regeneration
{
    [ForEach]
    static void WithMaybe(ref MaybeBenchmark.Stamina stamina, Maybe<MaybeBenchmark.Shield> shield) =>
        stamina.Value += shield.HasValue ? 1 + shield.Value.Bonus : 1;

    [ForEach]
    static void Shielded(ref MaybeBenchmark.Stamina stamina, in MaybeBenchmark.Shield shield) => stamina.Value += 1 + shield.Bonus;

    [ForEach, Without<MaybeBenchmark.Shield>]
    static void Plain(ref MaybeBenchmark.Stamina stamina) => stamina.Value += 1;
}
