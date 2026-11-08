using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// Random lookups across a large world: every hunter chases another hunter picked at random, so each read lands on a
/// random entity record and a random row (a TLB miss per lookup on small pages once the arrays outgrow the TLB's reach).
/// <see cref="Lookup{T}"/> on one thread and in parallel, and <c>world.TryGetEntity</c> from a <see cref="World"/>
/// parameter. 1,000 hunters (in L1 / L2), 1,000,000 (16 MiB of records, 8 MiB per column: past L2 and the TLB's
/// reach) and 16,000,000 (256 MiB of records, 128 MiB per column: past the last-level cache). Measures what changes
/// the cost of a random lookup: page sizes (decision D123) and prefetching the targets of later rows
/// (borrowed-ideas.md item 1, <see cref="Chase"/>: the job row by row and D131's vector loop, each without and with
/// prefetches <see cref="Distance"/> rows ahead).
/// </summary>
[MemoryDiagnoser]
public partial class LookupChaseBenchmark
{
    [Params(1_000, 1_000_000, 16_000_000)] public int Entities;

    /// <summary>How far ahead the prefetching jobs prefetch: in rows (the vector loop prefetches the 8 rows that many rows past its vector).</summary>
    [Params(8, 16)] public int Distance;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _world = Chase.Create(Entities);
        Chase.Hunt(_world);
        var expected = Chase.Headings(_world);
        foreach (var form in new Action[] { ChaseWorld, ChaseParallel, ChaseRows, ChasePrefetched, ChaseGathered, ChaseGatheredHand, ChaseGatheredPrefetched })
        {
            Hunting.Clear(_world);
            form();
            if (!Chase.Headings(_world).AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"{form.Method.Name} computes other headings");
        }

        for (int i = 0; i < 3; i++) ChaseParallel();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void ChaseWorld() => Hunting.ByWorld(_world);

    /// <summary>The generated job of the plain method (<see cref="Chase.Hunt"/>), row by row.</summary>
    [Benchmark]
    public void ChaseSerial() => Chase.Hunt(_world);

    [Benchmark]
    public void ChaseParallel() => Hunting.ByLookupParallel(_world);

    /// <summary>The method row by row over each run, by hand, no prefetch: what <see cref="ChasePrefetched"/> adds to.</summary>
    [Benchmark]
    public void ChaseRows() => Chase.Rows(_world, 0);

    /// <summary>Row by row, the record of row i + 2k and the row of row i + k prefetched.</summary>
    [Benchmark]
    public void ChasePrefetched() => Chase.Rows(_world, Distance);

    /// <summary>The generated vector loop (D131's gathers, <c>SimdMode.Require</c>).</summary>
    [Benchmark]
    public void ChaseGathered() => Chase.HuntGathered(_world);

    /// <summary>The same vector loop by hand, no prefetch: what <see cref="ChaseGatheredPrefetched"/> adds to.</summary>
    [Benchmark]
    public void ChaseGatheredHand() => Chase.Gathered(_world, 0);

    /// <summary>The vector loop, the records of the rows 2k past its vector and the rows k past it prefetched.</summary>
    [Benchmark]
    public void ChaseGatheredPrefetched() => Chase.Gathered(_world, Distance);

    public static class Hunting
    {
        [ForEach]
        public static void Clear(ref Chase.Heading heading) => heading = default;

        [ForEach]
        public static void ByWorld(World world, in Chase.Position position, in Chase.Prey prey, ref Chase.Heading heading) =>
            heading = world.TryGetEntity(prey.Target, out var target) && target.TryGet<Chase.Position>(out var at)
                ? new Chase.Heading(at.X - position.X, at.Y - position.Y)
                : default;

        [ForEach(Parallel = true)]
        public static void ByLookupParallel(in Chase.Position position, in Chase.Prey prey, ref Chase.Heading heading, Lookup<Chase.Position> positions)
        {
            var at = positions[prey.Target];
            heading = at.HasValue ? new Chase.Heading(at.Value.X - position.X, at.Value.Y - position.Y) : default;
        }
    }
}

/// <summary>
/// The size gate of the generated jobs' prefetches (borrowed-ideas.md item 1): the plain method's job row by row
/// (<see cref="Chase.Hunt"/>; past the cache its twin job prefetching), in parallel (its twin a batch job over each
/// chunk's runs of rows), and as D131's vector loop (<see cref="Chase.HuntGathered"/>), with the gates as this
/// machine's caches decide them (<see cref="PrefetchGate.Gate.Auto"/>: the row forms past the L2, the vector loop past
/// the last-level cache), forced off (today's loop) and forced on (a world in the cache prefetching all the same: what
/// the gate saves). The owner runs it on machines with other caches.
/// </summary>
[MemoryDiagnoser]
public class LookupChaseGateBenchmark
{
    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Sizes))] public int Entities;

    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Gates))] public PrefetchGate.Gate Prefetch;

    private World _world;
    private PrefetchGate _gate;

    [GlobalSetup]
    public void Setup()
    {
        _gate = PrefetchGate.Set(Prefetch);
        _world = Chase.Create(Entities);
        for (int i = 0; i < 3; i++) Hunting();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _gate.Dispose();
        _world.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Hunting() => Chase.Hunt(_world);

    [Benchmark]
    public void HuntingParallel() => LookupChaseBenchmark.Hunting.ByLookupParallel(_world);

    [Benchmark]
    public void HuntingGathered() => Chase.HuntGathered(_world);
}

/// <summary>
/// The distance of <see cref="LookupChaseBenchmark"/>'s prefetches, chosen by measurement: the prefetching jobs at
/// distances 2 to 64 (rows for the row job; for the vector loop the screen of 2026-10-26 counted vectors of 8, now rows), and the row job prefetching records only
/// (stage 1 of 2).
/// </summary>
[MemoryDiagnoser]
public class LookupChaseDistanceBenchmark
{
    [Params(1_000_000, 16_000_000)] public int Entities;

    [Params(2, 4, 8, 16, 32, 64)] public int Distance;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _world = Chase.Create(Entities);
        for (int i = 0; i < 3; i++) Chase.Rows(_world, Distance);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void Rows() => Chase.Rows(_world, 0);

    [Benchmark]
    public void RecordsOnly() => Chase.Rows(_world, Distance, rows: false);

    [Benchmark]
    public void Prefetched() => Chase.Rows(_world, Distance);

    [Benchmark]
    public void Gathered() => Chase.Gathered(_world, Distance);
}

/// <summary>
/// Loads issued ahead against prefetches (borrowed-ideas.md item 1 on ARM64 without a prefetch intrinsic): the
/// method's job over each run of rows plain, prefetching the records 2k and the rows k ahead (nothing where no prefetch
/// exists: Apple silicon), and loading the record of row i + k and touching the row of row i + k / 2 into rings row i
/// consumes (Locality/Chase.cs, LoadAhead). The generated code ships the load-ahead form only where this pair proves it.
/// </summary>
[MemoryDiagnoser]
public class LookupChaseLoadAheadBenchmark
{
    [Params(1_000, 1_000_000, 16_000_000)] public int Entities;

    [Params(4, 8, 16)] public int Distance;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _world = Chase.Create(Entities);
        for (int i = 0; i < 3; i++) Chase.LoadAhead(_world, Distance);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void Rows() => Chase.Rows(_world, 0);

    [Benchmark]
    public void Prefetched() => Chase.Rows(_world, Distance);

    [Benchmark]
    public int LoadAhead() => Chase.LoadAhead(_world, Distance);
}

/// <summary>
/// #164: the chase, a plain pass and the allocations of a world with the large columns and entity records on 4 KiB
/// pages (HugePages.Enabled forced off) against 2 MiB pages (their pinned arrays advised onto Linux's
/// transparent huge pages, the default where THP is on). Setup prints the process's AnonHugePages, so a run shows the
/// pages were huge.
/// </summary>
[MemoryDiagnoser]
public class LookupChaseHugePagesBenchmark
{
    [Params(1_000_000, 16_000_000)] public int Entities;

    [Params(false, true)] public bool HugePages;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        typeof(World).Assembly.GetType("EasyEcs.HugePages")!
            .GetField("Enabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, HugePages);
        _world = Chase.Create(Entities);
        foreach (var line in System.IO.File.ReadAllLines("/proc/self/smaps_rollup"))
            if (line.StartsWith("AnonHugePages", StringComparison.Ordinal)) Console.WriteLine($"// {line}");
        for (int i = 0; i < 3; i++) Chase.Hunt(_world);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void ChaseWorld() => LookupChaseBenchmark.Hunting.ByWorld(_world);

    /// <summary>The generated job (its prefetches as the gate decides).</summary>
    [Benchmark]
    public void ChaseSerial() => Chase.Hunt(_world);

    /// <summary>Row by row, no prefetch: the TLB's cost bare.</summary>
    [Benchmark]
    public void ChaseRows() => Chase.Rows(_world, 0);

    [Benchmark]
    public void ChaseGathered() => Chase.HuntGathered(_world);

    /// <summary>A plain pass in row order over two columns: what huge pages do to a walk that never chases.</summary>
    [Benchmark]
    public void Drift() => Drifting.Drift(_world);

    /// <summary>A world of this size made and disposed: what advising the pages costs its allocations.</summary>
    [Benchmark]
    public void Create() => Chase.Create(Entities).Dispose();

    /// <summary>
    /// A world grown to this size without a reservation, 4,096 entities a call: every doubling of a column past a huge
    /// page allocates and advises a new array.
    /// </summary>
    [Benchmark]
    public void Grow()
    {
        using var world = new World();
        var batch = new Entity[4096];
        for (int made = 0; made < Entities; made += batch.Length)
            world.CreateEntities(batch, new Chase.Position(1, 2), new Chase.Prey(), new Chase.Heading());
    }

    public static class Drifting
    {
        [ForEach]
        public static void Drift(ref Chase.Position position, in Chase.Heading heading) =>
            position = new Chase.Position(position.X + heading.X * 0.01f, position.Y + heading.Y * 0.01f);
    }
}
