using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/locality.md 4.4, phase 0, and borrowed-ideas.md item 1: 4,096 lookups in an open-addressing int to int
/// table of 1k, 100k or 16M keys (<see cref="ProbeTable"/>; 90 % of the keys present), per lookup: one key at a time
/// (the baseline), one at a time with the slots of the key <c>distance</c> queries ahead prefetched (group prefetching),
/// vertically 8 keys per AVX2 vector, and vertically with refill (Polychroniou). At 16M keys (256 MiB of slots, past the
/// last-level cache) each invoke takes the next 4,096 of 4M queries, so the lines one invoke touches are not in cache
/// from the invokes before. Setup checks every form finds the same values.
/// </summary>
[MemoryDiagnoser]
public class BatchProbeBenchmark
{
    private const int Lookups = 4_096;

    [Params(1_000, 100_000, 16_000_000)] public int Keys;

    private ProbeTable _table;
    private int[] _queries;
    private int _at;
    private readonly int[] _results = new int[Lookups];
    private readonly int[] _pairQuery = new int[Lookups + 8];
    private readonly int[] _pairValue = new int[Lookups + 8];

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        (_table, _queries) = ProbeTable.Create(Keys, Keys >= 1_000_000 ? 4 << 20 : Lookups);
        OneAtATime();
        _at = 0;
        var expected = (int[])_results.Clone();
        foreach (var form in new Action[] { Vertical, VerticalRefill, Prefetched8, Prefetched16, Prefetched32 })
        {
            Array.Clear(_results);
            _at = 0;
            form();
            if (!_results.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"{form.Method.Name} finds other values");
        }
    }

    /// <summary>The next batch of queries (the same one when the table is small; the next 4,096 of 4M at 16M keys).</summary>
    private ReadOnlySpan<int> Batch()
    {
        var batch = _queries.AsSpan(_at, Lookups);
        _at = _at + Lookups >= _queries.Length ? 0 : _at + Lookups;
        return batch;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Lookups)]
    public void OneAtATime() => _table.OneAtATime(Batch(), _results);

    /// <summary>One at a time, the slots of the key 8 queries ahead prefetched.</summary>
    [Benchmark(OperationsPerInvoke = Lookups)]
    public void Prefetched8() => _table.Prefetched(Batch(), _results, 8);

    [Benchmark(OperationsPerInvoke = Lookups)]
    public void Prefetched16() => _table.Prefetched(Batch(), _results, 16);

    [Benchmark(OperationsPerInvoke = Lookups)]
    public void Prefetched32() => _table.Prefetched(Batch(), _results, 32);

    [Benchmark(OperationsPerInvoke = Lookups)]
    public void Vertical() => _table.Vertical(Batch(), _results);

    [Benchmark(OperationsPerInvoke = Lookups)]
    public void VerticalRefill() => _table.VerticalRefill(Batch(), _results, _pairQuery, _pairValue);
}
