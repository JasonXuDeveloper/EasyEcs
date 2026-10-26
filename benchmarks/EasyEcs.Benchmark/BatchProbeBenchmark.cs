using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/locality.md 4.4, phase 0: 4,096 lookups in an open-addressing int to int table of 1k or 100k keys
/// (<see cref="ProbeTable"/>; 90 % of the keys present), per lookup: one key at a time (the baseline), vertically 8 keys
/// per AVX2 vector, and vertically with refill (Polychroniou). Setup checks every form finds the same values.
/// </summary>
[MemoryDiagnoser]
public class BatchProbeBenchmark
{
    private const int Lookups = 4_096;

    [Params(1_000, 100_000)] public int Keys;

    private ProbeTable _table;
    private int[] _queries;
    private readonly int[] _results = new int[Lookups];
    private readonly int[] _pairQuery = new int[Lookups + 8];
    private readonly int[] _pairValue = new int[Lookups + 8];

    [GlobalSetup]
    public void Setup()
    {
        (_table, _queries) = ProbeTable.Create(Keys, Lookups);
        OneAtATime();
        var expected = (int[])_results.Clone();
        foreach (var form in new Action[] { Vertical, VerticalRefill })
        {
            Array.Clear(_results);
            form();
            if (!_results.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"{form.Method.Name} finds other values");
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Lookups)]
    public void OneAtATime() => _table.OneAtATime(_queries, _results);

    [Benchmark(OperationsPerInvoke = Lookups)]
    public void Vertical() => _table.Vertical(_queries, _results);

    [Benchmark(OperationsPerInvoke = Lookups)]
    public void VerticalRefill() => _table.VerticalRefill(_queries, _results, _pairQuery, _pairValue);
}
