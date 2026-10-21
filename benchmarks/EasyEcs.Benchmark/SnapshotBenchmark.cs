using BenchmarkDotNet.Attributes;
using Nino.Core;

namespace EasyEcs.Benchmark;

/// <summary>
/// In-memory snapshots of a world of 100,000 created entities (a third destroyed: recycled ids) in two tables, with a
/// singleton: <c>Capture</c> copies every table and singleton into a new snapshot; <c>Restore</c> puts a snapshot back in place.
/// <c>Save</c> writes the world as bytes into a reused buffer; <c>Load</c> replaces the world with those bytes.
/// <c>SaveWithout</c> leaves a component and the singleton out (a restart's save); <c>LoadKeeping</c> loads but keeps
/// this world's singleton (a restart's load).
/// </summary>
[MemoryDiagnoser]
public class SnapshotBenchmark
{
    [Params(100_000)] public int Entities;

    private World _world;
    private WorldSnapshot _snapshot;
    private readonly NinoArrayBufferWriter _bytes = new();
    private byte[] _save;

    private static readonly ComponentSet Excluded = ComponentSet.Create().With<Health>().WithSingleton<Clock>();
    private static readonly ComponentSet Kept = ComponentSet.Create().WithSingleton<Clock>();

    [GlobalSetup]
    public void Setup()
    {
        // Rows move to a second table or are destroyed as they are made: the entity storage is what grows large.
        _world = new World();
        _world.EnsureEntityCapacity(Entities);
        for (int i = 0; i < Entities; i++)
        {
            var e = _world.CreateEntity(new Position { X = i }, new Velocity { Y = i });
            if (i % 3 == 0) e.GetOrAdd<Health>().Value = i;
            if (i % 3 == 1) e.Destroy();                                       // recycled ids, as in a running world
        }

        _world.AddSingleton<Clock>();
        _snapshot = _world.Capture();
        _world.Save(_bytes);
        _save = _bytes.WrittenSpan.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int Capture() => _world.Capture().EntityCount;

    [Benchmark]
    public void Restore() => _world.Restore(_snapshot);

    [Benchmark]
    public int Save()
    {
        _bytes.ResetWrittenCount();
        _world.Save(_bytes);
        return _bytes.WrittenCount;
    }

    [Benchmark]
    public int SaveWithout()
    {
        _bytes.ResetWrittenCount();
        _world.SaveWithout(_bytes, in Excluded);
        return _bytes.WrittenCount;
    }

    [Benchmark]
    public void Load() => _world.Load(_save);

    [Benchmark]
    public void LoadKeeping() => _world.LoadKeeping(_save, in Kept);
}
