using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A running total kept in a singleton across updates: every pass adds its rows to it. By hand (<c>HandWritten</c>) the
/// method takes the singleton by <c>ref</c> and adds row by row, so the pass stays on one thread and scalar (the rows
/// share one value); <c>Accumulated</c> takes a <c>Reduce&lt;T&gt;</c> of an <c>[Accumulate]</c> singleton, whose pass
/// sums into a value of its own and adds it to the world's total once; <c>Parallel</c> is the same method with
/// <c>Parallel = true</c>. The totals agree (checked in setup). Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class AccumulateBenchmark
{
    [Params(1_000, 100_000)] public int Rows;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _world.Reserve<Coins>(Rows);
        for (int i = 0; i < Rows; i++) _world.CreateEntity(new Coins(i % 1000 - 300));
        _world.GetOrAddSingleton<HandPurse>();
        HandWritten();
        Accumulated();
        Parallel();
        var hand = _world.GetSingleton<HandPurse>();
        var purse = _world.GetSingleton<Purse>();
        if (purse.Total != 2 * hand.Total || purse.Count != 2 * hand.Count)
            throw new System.InvalidOperationException($"{purse.Total} / {purse.Count} instead of twice {hand.Total} / {hand.Count}");
        for (int i = 0; i < 10; i++)
        {
            HandWritten();
            Accumulated();
            Parallel();
        }
    }

    [GlobalCleanup]
    public void Dispose() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public long HandWritten()
    {
        PurseTotals.AddByHand(_world);
        return _world.GetSingleton<HandPurse>().Total;
    }

    [Benchmark]
    public long Accumulated()
    {
        PurseTotals.Add(_world);
        return _world.GetSingleton<Purse>().Total;
    }

    [Benchmark]
    public long Parallel()
    {
        PurseTotals.AddParallel(_world);
        return _world.GetSingleton<Purse>().Total;
    }
}

public record struct Coins(int Value) : IComponent;

/// <summary>The total: [Accumulate] adds each pass to the world's value, [Sum] sums both fields.</summary>
[Accumulate, Sum]
public partial struct Purse : ISingletonComponent
{
    public long Total;
    public long Count;
}

/// <summary>The same total written by hand.</summary>
public struct HandPurse : ISingletonComponent
{
    public long Total;
    public long Count;
}

public static class PurseTotals
{
    [ForEach]
    public static void AddByHand(in Coins coins, ref HandPurse purse)
    {
        purse.Total += coins.Value;
        purse.Count++;
    }

    [ForEach]
    public static void Add(in Coins coins, ref Reduce<Purse> purse)
    {
        purse.Value.Total += coins.Value;
        purse.Value.Count++;
    }

    [ForEach(Parallel = true)]
    public static void AddParallel(in Coins coins, ref Reduce<Purse> purse)
    {
        purse.Value.Total += coins.Value;
        purse.Value.Count++;
    }
}
