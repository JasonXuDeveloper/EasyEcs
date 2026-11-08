using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A round reset: 100,000 fighters are all stunned, then the stun is lifted from everyone; and a status is disabled
/// for a whole zone, then given back. Each pair once in bulk (<c>SetAll</c> / <c>RemoveAll</c>,
/// <c>DisableAll</c> / <c>EnableAll</c>) and once entity by entity, the way it is written without bulk calls. The
/// stun keeps its column after the first round, so a round flips presence bits (no row moves). Steady state allocates
/// 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class BulkBenchmark
{
    [Params(100_000)] public int Entities;

    private World _world;
    private Entity[] _fighters;
    private static readonly QueryFilter Fighters = QueryFilter.Create().With<Health>();

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _fighters = new Entity[Entities];
        _world.CreateEntities(_fighters, new Health(100), new Armor(5));
        StunAndLift();
        StunAndLiftEach();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int StunAndLift() => _world.SetAll(new Stun(3), in Fighters) + _world.RemoveAll<Stun>();

    [Benchmark]
    public int StunAndLiftEach()
    {
        foreach (var fighter in _fighters) fighter.Set(new Stun(3));
        foreach (var fighter in _fighters) fighter.Remove<Stun>();
        return _fighters.Length * 2;
    }

    [Benchmark]
    public int DisableAndEnable() => _world.DisableAll<Armor>() + _world.EnableAll<Armor>();

    [Benchmark]
    public int DisableAndEnableEach()
    {
        foreach (var fighter in _fighters) fighter.Disable<Armor>();
        foreach (var fighter in _fighters) fighter.Enable<Armor>();
        return _fighters.Length * 2;
    }

    public record struct Health(int Value) : IComponent;
    public record struct Armor(int Value) : IComponent;
    public record struct Stun(int Ticks) : IComponent;
}
