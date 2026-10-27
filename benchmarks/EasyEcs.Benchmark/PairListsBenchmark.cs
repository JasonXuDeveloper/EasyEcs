using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>How a pair pass picks lists of each row's neighbors (the core's internal PairState.ListMode).</summary>
public enum ListPick
{
    /// <summary>By their cost against the clusters' ranges for the method's width (the default).</summary>
    Auto,

    /// <summary>Never: the clusters' ranges.</summary>
    Off,

    /// <summary>Wherever they hold.</summary>
    On,
}

/// <summary>
/// docs/plans/locality.md 6.6, the neighbor-list form: the generated pair method over a moving crowd (<see cref="Crowd"/>,
/// every row stepping 0.02 cutoffs per tick at full speed, forth then back), the lists of each row's neighbors picked by
/// their cost (Auto), never (Off: the clusters' ranges) or always (On). The vector loop (Generated: a cluster of rows
/// per step, 8 or 4 lanes) and one pair at a time (GeneratedRows: a row per step, as a method the vector loop cannot
/// take). ARM64's NEON is the case the lists are for (4 lanes); run with DOTNET_EnableAVX=0 to see 4 lanes on x86. Each
/// invocation runs 20 ticks (OperationsPerInvoke); the pick is process-wide, so each case sets it in setup. Setup checks
/// every pick against the double-precision reference.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class PairListsBenchmark
{
    private const int Ticks = 20;

    [Params(10_000, 100_000)] public int Entities;
    [Params(Density.Crowd, Density.Sparse)] public Density Density;
    [Params(ListPick.Auto, ListPick.Off, ListPick.On)] public ListPick Lists;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        Pick(Lists);
        _world = Crowd.Create(Entities, Density);
        Check(rows: false);
        Check(rows: true);
    }

    private void Check(bool rows)
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickMoving(_world, t >= Ticks / 2, rows);
        var expected = Crowd.Reference(_world);
        int at = Crowd.FirstMismatch(Crowd.Pushes(_world), expected);
        if (at >= 0) throw new InvalidOperationException($"{(rows ? "GeneratedRows" : "Generated")} with lists {Lists} differs from the reference at row {at}");
    }

    /// <summary>Set the core's pick (internal: through reflection, once per case).</summary>
    private static void Pick(ListPick pick)
    {
        var field = typeof(World).Assembly.GetType("EasyEcs.PairState")!.GetField("Lists", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        field.SetValue(null, Enum.ToObject(field.FieldType, (int)pick));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        Pick(ListPick.Auto);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Ticks)]
    public void Generated()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickMoving(_world, t >= Ticks / 2, rows: false);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void GeneratedRows()
    {
        for (int t = 0; t < Ticks; t++) CrowdPairs.TickMoving(_world, t >= Ticks / 2, rows: true);
    }
}
