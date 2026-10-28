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

/// <summary>
/// Rollback netcode over <see cref="Entities"/> bodies: a tick writes <see cref="Writes"/> of them (the players' inputs
/// and what they hit) and captures the world into a ring of <see cref="Ring"/> snapshots, one per tick (<c>Tick</c>); a
/// late input restores the snapshot of <see cref="Depth"/> ticks ago and replays those ticks, capturing each again
/// (<c>Rollback</c>). The state is tracked (<see cref="ITracked"/>): a capture or restore copies the rows written since,
/// where an untracked column would be copied whole.
/// </summary>
[MemoryDiagnoser]
public class RollbackBenchmark
{
    public const int Ring = 8;
    public const int Depth = 4;

    [Params(100_000)] public int Entities;
    [Params(16, 256)] public int Writes;

    private World _world;
    private readonly WorldSnapshot[] _ring = new WorldSnapshot[Ring];
    private Entity[] _entities;
    private int _tick = Ring;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _entities = new Entity[Entities];
        _world.CreateEntities(_entities, new Body(), new Life(100));
        for (int i = 0; i < Ring; i++) _ring[i] = _world.Capture();
        for (int i = 0; i < 2 * Ring; i++)
        {
            Tick();
            Rollback();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int Tick() => Step(_tick++);

    [Benchmark]
    public int Rollback()
    {
        _world.Restore(_ring[(_tick - Depth) & (Ring - 1)]);
        int alive = 0;
        for (int t = _tick - Depth + 1; t <= _tick; t++) alive += Step(t);
        return alive;
    }

    private int Step(int tick)
    {
        for (int i = 0; i < Writes; i++)
        {
            var e = _entities[(int)(((long)tick * 7_919 + (long)i * 104_729) % Entities)];
            e.Get<Body>().X += 1;
            e.Get<Life>().Value -= 1;
        }

        return _world.Capture(_ring[tick & (Ring - 1)]).EntityCount;
    }

    public record struct Body(float X, float Y, float VX, float VY) : IComponent, ITracked;

    public record struct Life(int Value) : IComponent, ITracked;
}
