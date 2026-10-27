using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Records handled once: per operation, <see cref="Records"/> records are appended to <c>Emitted&lt;Hit&gt;</c> (what
/// passes taking an <c>Emit&lt;Hit&gt;</c> leave), then handled in order into a singleton and cleared. <c>HandWritten</c>
/// is a system's update as EECS106 finds it: the singleton and the list taken by ref, the walk, the Clear;
/// <c>ForEachRecord</c> is the generated pass of a <c>[ForEachRecord]</c> method with the same body, taking the singleton
/// by ref. Both leave the same tally (checked in setup). Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class RecordBenchmark
{
    [Params(1_000, 100_000)] public int Records;

    private World _world;
    private Hit[] _hits;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _hits = new Hit[Records];
        for (int i = 0; i < _hits.Length; i++) _hits[i] = new Hit(i, i % 97);
        _world.GetOrAddSingleton<Tally>();
        long hand = HandWritten();
        _world.GetSingleton<Tally>() = default;
        if (hand != ForEachRecord()) throw new System.InvalidOperationException("the two passes differ");
        for (int i = 0; i < 10; i++)
        {
            HandWritten();
            ForEachRecord();
        }
    }

    [GlobalCleanup]
    public void Dispose() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public long HandWritten()
    {
        _world.GetOrAddSingleton<Emitted<Hit>>().Items.AddRange(_hits);
        ref var tally = ref _world.GetSingleton<Tally>();
        ref var hits = ref _world.GetOrAddSingleton<Emitted<Hit>>().Items;
        foreach (ref readonly var hit in hits.AsReadOnlySpan())
        {
            tally.Total += hit.Amount;
            tally.Order = tally.Order * 31 + hit.Target;
        }
        hits.Clear();
        return tally.Total ^ tally.Order;
    }

    [Benchmark]
    public long ForEachRecord()
    {
        _world.GetOrAddSingleton<Emitted<Hit>>().Items.AddRange(_hits);
        HitRecords.Book(_world);
        ref readonly var tally = ref _world.GetSingleton<Tally>();
        return tally.Total ^ tally.Order;
    }

    public record struct Hit(int Target, int Amount);

    public struct Tally : ISingletonComponent
    {
        public long Total;
        public long Order;
    }
}

public static class HitRecords
{
    [ForEachRecord]
    public static void Book(in RecordBenchmark.Hit hit, ref RecordBenchmark.Tally tally)
    {
        tally.Total += hit.Amount;
        tally.Order = tally.Order * 31 + hit.Target;
    }
}
