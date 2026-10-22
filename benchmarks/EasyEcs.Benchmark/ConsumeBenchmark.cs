using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Events applied once: per operation, <see cref="Events"/> entities are created and one pass applies and destroys
/// them. [Consume] on a method without structural changes frees the visited tables in bulk after the pass
/// (<c>Bulk</c>); a method that may change structure destroys each row after the method (<c>EachRow</c>, the same
/// [Consume] taking the world); the baseline destroys each entity by hand in the method. Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class ConsumeBenchmark
{
    [Params(1_000, 100_000)] public int Events;

    private World _world;
    private Entity[] _created;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Events * 2 });
        _created = new Entity[Events];
        for (int i = 0; i < 10; i++)
        {
            HandWritten();
            Bulk();
            EachRow();
        }
    }

    [GlobalCleanup]
    public void Dispose() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int HandWritten()
    {
        _world.CreateEntities(_created, new Damage(1));
        int total = 0;
        Hits.DestroyEach(_world, ref total);
        return total;
    }

    [Benchmark]
    public int Bulk()
    {
        _world.CreateEntities(_created, new Damage(1));
        int total = 0;
        Hits.Consume(_world, ref total);
        return total;
    }

    [Benchmark]
    public int EachRow()
    {
        _world.CreateEntities(_created, new Damage(1));
        int total = 0;
        Hits.ConsumeEach(_world, ref total);
        return total;
    }

    public record struct Damage(int Amount) : IComponent;
}

public static class Hits
{
    [ForEach]
    static void DestroyEach(Entity hit, in ConsumeBenchmark.Damage damage, ref int total)
    {
        total += damage.Amount;
        hit.Destroy();
    }

    [ForEach, Consume]
    static void Consume(in ConsumeBenchmark.Damage damage, ref int total) => total += damage.Amount;

    // The world taken (a method that creates entities on some rows): the method may change structure, so each row is
    // destroyed after it runs.
    [ForEach, Consume]
    static void ConsumeEach(in ConsumeBenchmark.Damage damage, ref int total, World world)
    {
        total += damage.Amount;
        if (damage.Amount < 0) world.CreateEntity(damage);
    }
}
