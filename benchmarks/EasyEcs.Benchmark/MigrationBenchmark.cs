using BenchmarkDotNet.Attributes;
using Nino.Core;

namespace EasyEcs.Benchmark;

/// <summary>
/// Players changing worlds: 10 players, each with 99 items (<see cref="ChildOf"/> the player), move from one world to
/// another and back, in warm worlds. Once through bytes (<c>SaveEntities</c>, <c>LoadEntities</c>, then destroying
/// the originals: how a group crosses processes) and once in memory (<c>MoveEntities</c>: rows copied, no encoding).
/// <c>SaveAndLoadOne</c> sends one entity of four components through bytes (a save's fixed costs: the types by id).
/// <c>SaveRecord</c> saves the persistent part of one entity (two of its four components, a <see cref="ComponentSet"/>)
/// and <c>LoadRecord</c> loads that record onto an entity that already holds runtime state (<c>LoadEntitiesInto</c>):
/// what a persistence layer does per actor.
/// </summary>
[MemoryDiagnoser]
public class MigrationBenchmark
{
    [Params(1_000)] public int Entities;

    private World _a, _b, _c, _d;
    private readonly EntityId[] _one = new EntityId[1];
    private readonly Entity[] _oneHandle = new Entity[1];
    private EntityId[] _ids;
    private Entity[] _handles;
    private readonly NinoArrayBufferWriter _bytes = new();
    private readonly NinoArrayBufferWriter _record = new();
    private static readonly ComponentSet Persistent = ComponentSet.Create().With<Health>().With<Energy>();
    private readonly Entity[] _actor = new Entity[1];

    [GlobalSetup]
    public void Setup()
    {
        _a = new World();
        _b = new World();
        _c = new World();
        _d = new World();
        _one[0] = _c.CreateEntity(new Health { Value = 100 }, new Position { X = 1 }, new Velocity { Y = 1 }, new Energy { Value = 5 }).Id;
        _ids = new EntityId[Entities];
        _handles = new Entity[Entities];
        for (int i = 0; i < Entities; i += 100)
        {
            var player = _a.CreateEntity(new Health { Value = 100 }, new Position { X = i });
            _ids[i] = player.Id;
            for (int j = 1; j < 100; j++) _ids[i + j] = _a.CreateEntity(new ChildOf(player), new Energy { Value = j }).Id;
        }

        SaveAndLoad();
        Move();
        SaveAndLoadOne();
        _actor[0] = _d.CreateEntity(new Position { X = 2 }, new Velocity { Y = 2 });
        SaveRecord();
        LoadRecord();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _a.Dispose();
        _b.Dispose();
        _c.Dispose();
        _d.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int SaveAndLoad() => SaveAndLoad(_a, _b) + SaveAndLoad(_b, _a);

    [Benchmark]
    public int Move() => Move(_a, _b) + Move(_b, _a);

    [Benchmark]
    public int SaveAndLoadOne() => SaveAndLoadOne(_c, _d) + SaveAndLoadOne(_d, _c);

    [Benchmark]
    public int SaveRecord()
    {
        _record.ResetWrittenCount();
        _c.SaveEntitiesWith(_record, _one, Persistent);
        return _record.WrittenCount;
    }

    [Benchmark]
    public int LoadRecord() => _d.LoadEntitiesInto(_record.WrittenSpan, _actor);

    private int SaveAndLoadOne(World from, World to)
    {
        _bytes.ResetWrittenCount();
        from.SaveEntities(_bytes, _one);
        int moved = to.LoadEntities(_bytes.WrittenSpan, _oneHandle);
        from.GetEntity(_one[0]).Destroy();
        _one[0] = _oneHandle[0].Id;
        return moved;
    }

    private int SaveAndLoad(World from, World to)
    {
        _bytes.ResetWrittenCount();
        from.SaveEntities(_bytes, _ids);
        int moved = to.LoadEntities(_bytes.WrittenSpan, _handles);
        for (int i = 0; i < _ids.Length; i += 100) from.GetEntity(_ids[i]).Destroy(); // the items follow their player
        Remember();
        return moved;
    }

    private int Move(World from, World to)
    {
        int moved = from.MoveEntities(to, _ids, _handles);
        Remember();
        return moved;
    }

    private void Remember()
    {
        for (int i = 0; i < _ids.Length; i++) _ids[i] = _handles[i].Id;
    }
}
