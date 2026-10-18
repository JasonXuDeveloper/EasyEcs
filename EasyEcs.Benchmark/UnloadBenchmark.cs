using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Plugin;

namespace EasyEcs.Benchmark;

/// <summary>
/// Unloading game logic for a reload: a plugin (EasyEcs.Benchmark.Plugin: a tracked and an indexed component on every
/// player, a singleton, a system, a kept index) is removed from a world of <see cref="Players"/> players with
/// <c>RemoveTypes</c>. The entities stay; their tables with plugin columns are dropped and the rows move to tables
/// without them. One removal per iteration, on a world built for it.
/// </summary>
[MemoryDiagnoser]
public class UnloadBenchmark
{
    [Params(10_000, 100_000)] public int Players;

    private World _world;
    private Entity[] _players;

    [IterationSetup]
    public void Install()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Players });
        _players = new Entity[Players];
        _world.CreateEntities(_players, new Position(1, 2), new Velocity(1, 1));
        Plugin.Plugin.Install(_world, _players);
        _world.Update();
    }

    [IterationCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark, InvocationCount(1)]
    public int RemoveTypes()
    {
        _world.RemoveTypes(typeof(Score).Assembly);
        return _world.EntityCount;
    }

    public record struct Position(float X, float Y) : IComponent;
    public record struct Velocity(float X, float Y) : IComponent;
}
