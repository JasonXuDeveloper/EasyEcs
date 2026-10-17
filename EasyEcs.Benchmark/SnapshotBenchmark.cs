using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// In-memory snapshots of a world of 100,000 created entities (a third destroyed: recycled ids) in two tables, with a
/// singleton: <c>Capture</c> copies every table and singleton into a new snapshot; <c>Restore</c> puts a snapshot back in place.
/// </summary>
[MemoryDiagnoser]
public class SnapshotBenchmark
{
    [Params(100_000)] public int Entities;

    private World _world;
    private WorldSnapshot _snapshot;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        for (int i = 0; i < Entities; i++)
        {
            var e = _world.CreateEntity(new Position { X = i }, new Velocity { Y = i });
            if (i % 3 == 0) e.GetOrAdd<Health>().Value = i;
            if (i % 3 == 1) e.Destroy();                                       // recycled ids, as in a running world
        }

        _world.AddSingleton<Clock>();
        _snapshot = _world.Capture();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int Capture() => _world.Capture().EntityCount;

    [Benchmark]
    public void Restore() => _world.Restore(_snapshot);
}
