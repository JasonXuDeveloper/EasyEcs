using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Records handled once: per operation, <see cref="Records"/> records are appended to <c>Emitted&lt;Hit&gt;</c> (what
/// passes taking an <c>Emit&lt;Hit&gt;</c> leave), then handled in order and cleared. <c>HandWritten</c> gets the list,
/// walks it and clears it (what EECS106 reports); <c>ForEachRecord</c> is the generated pass of a <c>[ForEachRecord]</c>
/// method with the same body. Both return the same value (checked in setup). Steady state allocates 0 bytes.
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
        if (HandWritten() != ForEachRecord()) throw new System.InvalidOperationException("the two passes differ");
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
        long total = 0, order = 0;
        ref var hits = ref _world.GetOrAddSingleton<Emitted<Hit>>().Items;
        foreach (ref readonly var hit in hits.AsReadOnlySpan())
        {
            total += hit.Amount;
            order = order * 31 + hit.Target;
        }
        hits.Clear();
        return total ^ order;
    }

    [Benchmark]
    public long ForEachRecord()
    {
        _world.GetOrAddSingleton<Emitted<Hit>>().Items.AddRange(_hits);
        long total = 0, order = 0;
        HitRecords.Book(_world, ref total, ref order);
        return total ^ order;
    }

    public record struct Hit(int Target, int Amount);
}

public static class HitRecords
{
    [ForEachRecord]
    public static void Book(in RecordBenchmark.Hit hit, ref long total, ref long order)
    {
        total += hit.Amount;
        order = order * 31 + hit.Target;
    }
}
