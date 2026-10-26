using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// The cost of the core's re-sort (L1, <see cref="World.SortRows{T}"/>): the keys and their radix sort, one permutation
/// of every column, presence bits, change ticks and slots, the boxes. On a narrow table (the ordered component alone)
/// and a wide one (six more components, one tracked, one managed), 1k to 1M rows: <c>Reverse</c> turns the order around
/// (every row moves: the worst case), <c>Churn1</c> changes 1 % of the keys (a tick of a moving crowd), <c>Sorted</c>
/// finds the rows in order (the keys and the boxes only). Churn1 includes the walk that writes the keys. Setup checks
/// the order after each.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public partial class RowOrderBenchmark
{
    public enum Width { Narrow, Wide }

    [Params(1_000, 10_000, 100_000, 1_000_000)] public int Rows;
    [Params(Width.Narrow, Width.Wide)] public Width Columns;

    /// <summary>XORed into every key: flipping it reverses the order of every row.</summary>
    public static int Flip;

    public partial record struct Ranked(int Key, float X, float Y) : IComponent
    {
        [OrderBy(nameof(X), nameof(Y), Interval = int.MaxValue)]
        public readonly int Order => Key ^ Flip;
    }

    public record struct Extra1(long A, long B) : IComponent;
    public record struct Extra2(float A, float B, float C) : IComponent;
    public record struct Extra3(int A) : IComponent;
    public record struct Extra4(double A, double B) : IComponent;
    public record struct Tracked(int A) : IComponent, ITracked;
    [NotSaved] public record struct Label(string Text) : IComponent;

    private World _world;
    private Random _random;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        Flip = 0;
        _random = new Random(42);
        _world = new World(new WorldOptions { InitialEntityCapacity = Rows + 16 });
        var all = new Entity[Rows];
        if (Columns == Width.Narrow) _world.CreateEntities(all, new Ranked());
        else _world.CreateEntities(all, new Ranked(), new Extra1(), new Extra2(), new Extra3(), new Extra4(), new Tracked(), new Label("x"));
        foreach (var item in _world.Query<Ranked>())
            item.Component1 = new Ranked(_random.Next(), _random.NextSingle() * 1000, _random.NextSingle() * 1000);
        _world.SortRows<Ranked>();
        Check();
        Reverse();
        Check();
        Churn1();
        Check();
    }

    private void Check()
    {
        int previous = int.MinValue;
        foreach (var item in _world.Query<Ranked>())
        {
            if (item.Component1.Order < previous) throw new InvalidOperationException("The rows are out of order");
            previous = item.Component1.Order;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Reverse()
    {
        Flip = ~Flip;
        _world.SortRows<Ranked>();
    }

    [Benchmark]
    public void Churn1()
    {
        // 1 % of the rows get a new key (spread over the table), then the sort.
        int step = 100;
        int k = _random.Next(step);
        foreach (var item in _world.Query<Ranked>())
            if (--k < 0)
            {
                item.Component1 = item.Component1 with { Key = _random.Next() };
                k = step;
            }

        _world.SortRows<Ranked>();
    }

    [Benchmark(Baseline = true)]
    public void Sorted() => _world.SortRows<Ranked>();
}
