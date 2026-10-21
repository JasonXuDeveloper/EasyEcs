using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The cost of the destroy log on the destroys themselves: 10 rounds of creating and destroying 1,000 entities (one by
/// one, then a whole table at once with <c>DestroyAll</c>) in a warm world, with the log off (<c>DestroyLogCapacity</c> 0,
/// the default) and on (a ring of 4,096 records), and with the log on plus the replication read of 16 clients that
/// acknowledged one of the last four rounds each (<see cref="World.DestroyedSince"/>). Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class DestroyLogBenchmark
{
    private const int Entities = 1_000, Clients = 16;

    [Params(0, 4_096)] public int DestroyLog;

    private World _world;
    private Entity[] _entities;
    private EntityId[] _destroyed;
    private readonly ulong[] _marks = new ulong[4]; // the change tick at the start of the last four rounds
    private long _round;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { DestroyLogCapacity = DestroyLog, InitialEntityCapacity = Entities * 2 });
        _entities = new Entity[Entities];
        _destroyed = new EntityId[Math.Max(1, DestroyLog)];
        for (int i = 0; i < 10; i++) DestroyOneByOne(); // warm: tables, ids, edges
        DestroyInBulk();
        DestroyAndReplicate(); // every mark taken
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>Per-entity destroys: one record each when the log is on.</summary>
    [Benchmark(Baseline = true)]
    public int DestroyOneByOne()
    {
        var entities = _entities;
        for (int round = 0; round < 10; round++)
        {
            _world.CreateEntities(entities, new Position(), new Health());
            for (int i = 0; i < entities.Length; i++) entities[i].Destroy();
        }

        return _world.EntityCount;
    }

    /// <summary>A whole table at once (the freed-table path of DestroyEntities).</summary>
    [Benchmark]
    public int DestroyInBulk()
    {
        for (int round = 0; round < 10; round++)
        {
            _world.CreateEntities(_entities, new Position(), new Health());
            _world.DestroyAll<Position>();
        }

        return _world.EntityCount;
    }

    /// <summary>
    /// Per-entity destroys, one tick per round, then the read of 16 clients, client c behind by c % 4 rounds (log on
    /// only; with the log off it does the destroys alone).
    /// </summary>
    [Benchmark]
    public int DestroyAndReplicate()
    {
        var entities = _entities;
        var marks = _marks;
        int sent = 0;
        for (int round = 0; round < 10; round++)
        {
            long now = ++_round;
            marks[now & 3] = _world.ChangeTick;
            _world.CreateEntities(entities, new Position(), new Health());
            for (int i = 0; i < entities.Length; i++) entities[i].Destroy();
            _world.Update();
            if (DestroyLog == 0) continue;
            for (int c = 0; c < Clients; c++)
                if (_world.DestroyedSince(marks[(now - (c & 3)) & 3], _destroyed, out int count)) sent += count;
        }

        return sent;
    }
}
