using BenchmarkDotNet.Attributes;
using Nino.Core;

namespace EasyEcs.Benchmark;

/// <summary>
/// Players changing worlds: 10 players, each with 99 items (<see cref="ChildOf"/> the player), move from one world to
/// another and back, in warm worlds. Once through bytes (<c>SaveEntities</c>, <c>LoadEntities</c>, then destroying
/// the originals: how a group crosses processes) and once in memory (<c>MoveEntities</c>: rows copied, no encoding).
/// </summary>
[MemoryDiagnoser]
public class MigrationBenchmark
{
    [Params(1_000)] public int Entities;

    private World _a, _b;
    private EntityId[] _ids;
    private Entity[] _handles;
    private readonly NinoArrayBufferWriter _bytes = new();

    [GlobalSetup]
    public void Setup()
    {
        _a = new World();
        _b = new World();
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
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _a.Dispose();
        _b.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int SaveAndLoad() => SaveAndLoad(_a, _b) + SaveAndLoad(_b, _a);

    [Benchmark]
    public int Move() => Move(_a, _b) + Move(_b, _a);

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
