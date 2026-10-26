using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/locality.md 4.4, phase 0: a condition over a column turned into a row list (<see cref="Selection"/>) at
/// 10 / 50 / 90 % selectivity: a branch per row (the baseline), a store per row without a branch, a permutation table
/// (AVX2, else 128-bit byte shuffles: NEON's TBL on ARM64), an AVX-512 compressing store (the permutation table
/// without AVX-512), and the core's Lanes.Compact over 64-row masks. Setup checks every form writes the same list and
/// prints the ISA; the ISA column names it.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class CompressBenchmark
{
    [Params(4_096, 262_144)] public int Rows;
    [Params(10, 50, 90)] public int Percent;

    private float[] _values;
    private int[] _rows;
    private int[] _rowNumbers;
    private float _limit;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _values = Selection.Column(Rows);
        _rows = Selection.RowList(Rows);
        _rowNumbers = new int[Rows];
        for (int i = 0; i < Rows; i++) _rowNumbers[i] = i;
        _limit = Percent / 100f;
        int n = Selection.Branchy(_values, _limit, _rows);
        var expected = _rows.AsSpan(0, n).ToArray();
        foreach (var form in new Func<int>[] { Branchless, PermutationTable, ShuffleTable, Compress512, CoreCompact })
        {
            Array.Clear(_rows);
            if (form() != n || !_rows.AsSpan(0, n).SequenceEqual(expected))
                throw new InvalidOperationException($"{form.Method.Name} writes another row list");
        }
    }

    [Benchmark(Baseline = true)]
    public int Branchy() => Selection.Branchy(_values, _limit, _rows);

    [Benchmark]
    public int Branchless() => Selection.Branchless(_values, _limit, _rows);

    [Benchmark]
    public int PermutationTable() => Selection.PermutationTable(_values, _limit, _rows);

    [Benchmark]
    public int ShuffleTable() => Selection.ShuffleTable(_values, _limit, _rows);

    [Benchmark]
    public int Compress512() => Selection.Compress512(_values, _limit, _rows);

    [Benchmark]
    public int CoreCompact() => Selection.CoreCompact(_values, _limit, _rowNumbers, _rows);
}
