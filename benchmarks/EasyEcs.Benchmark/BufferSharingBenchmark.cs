using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Buffers copied from a filled one: <see cref="Copies"/> crates with <see cref="Items"/> items each, spawned from a
/// prefab (<c>Instantiate</c>) or cloned from one crate (<c>Clone</c>), then destroyed. With <see cref="Written"/>,
/// every copy changes one item before it goes (the copy-on-write cost); without, the copies only read theirs.
/// </summary>
[MemoryDiagnoser]
public class BufferSharingBenchmark
{
    public const int Items = 16;

    [Params(10_000)] public int Copies;
    [Params(false, true)] public bool Written;

    private World _world;
    private Prefab _crate;
    private Entity[] _copies;
    private int[] _stock;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Copies + 1 });
        _stock = new int[Items];
        for (int i = 0; i < Items; i++) _stock[i] = i;
        _crate = new Prefab().Set(new Crate()).SetBuffer<int>(_stock);
        _copies = new Entity[Copies];
        for (int i = 0; i < 5; i++)
        {
            Instantiate();
            Clone();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int Instantiate()
    {
        _world.Instantiate(_crate, _copies);
        return Finish();
    }

    [Benchmark]
    public int Clone()
    {
        var original = _world.CreateEntity(new Crate(), new DynamicBuffer<int>());
        original.Get<DynamicBuffer<int>>().AddRange(_stock);
#pragma warning disable EECS031 // the clones of one entity: Clone per copy is what this case measures
        for (int i = 0; i < _copies.Length; i++) _copies[i] = original.Clone();
#pragma warning restore EECS031
        return Finish();
    }

    private int Finish()
    {
        if (Written)
            for (int i = 0; i < _copies.Length; i++)
                _copies[i].Get<DynamicBuffer<int>>().AsSpan()[0] = i;
        return _world.DestroyAll<Crate>();
    }

    public record struct Crate(int Owner) : IComponent;
}

/// <summary>
/// A rollback ring over <see cref="Entities"/> inventories of <see cref="Items"/> items: <c>Capture</c> into a reused
/// snapshot, <c>Restore</c> it, and <c>Rollback</c>: capture, <see cref="WrittenRows"/> inventories changed (a tick's input),
/// restore.
/// </summary>
[MemoryDiagnoser]
public class BufferSnapshotBenchmark
{
    public const int Items = 16;

    [Params(10_000)] public int Entities;
    [Params(1_000)] public int WrittenRows;

    private World _world;
    private WorldSnapshot _snapshot;
    private Entity[] _entities;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _entities = new Entity[Entities];
        _world.CreateEntities(_entities, new Owner(), new DynamicBuffer<int>());
        foreach (var e in _entities)
            for (int i = 0; i < Items; i++) e.Get<DynamicBuffer<int>>().Add(i);
        _snapshot = _world.Capture();
        for (int i = 0; i < 5; i++) Rollback();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int Capture() => _world.Capture(_snapshot).EntityCount;

    [Benchmark]
    public void Restore() => _world.Restore(_snapshot);

    [Benchmark]
    public void Rollback()
    {
        _world.Capture(_snapshot);
        for (int i = 0; i < WrittenRows; i++)
        {
            _next = (_next + 97) % _entities.Length;
            _entities[_next].Get<DynamicBuffer<int>>().AsSpan()[0] = _next;
        }

        _world.Restore(_snapshot);
    }

    public record struct Owner(int Player) : IComponent;
}
