using System;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// Lookups whose targets are in random order against the same rows ordered by their target (L1's side effect,
/// docs/plans/locality.md 3.1): the readers' table sorted once by the target's id (<see cref="World.OrderRows{T, TOrder}"/>
/// with the id as the key). <c>Chase</c> is <see cref="LookupChaseBenchmark"/>'s pass (every hunter reads a random
/// hunter: ordered, the records are read in order but the rows they point to are not, since the sort moved the targets
/// too). <c>Gather</c> / <c>GatherPerRow</c> are <see cref="LookupGatherBenchmark"/>'s pass with targets in 64 tables
/// (the case D131's gathers lose: a vector of 8 lanes almost never shares a table; ordered, runs of seekers aim into
/// one table and near rows).
/// </summary>
[MemoryDiagnoser, IsaColumn]
public partial class LookupOrderBenchmark
{
    [Params(100_000, 1_000_000)] public int Rows;
    [Params(false, true)] public bool Ordered;

    private World _hunters;
    private World _seekers;

    /// <summary>A hunter's rows in the order of its prey's id.</summary>
    public readonly struct ByPrey : IRowOrder<LookupChaseBenchmark.Prey>
    {
        public static int KeyBytes => 4;
        public static ulong Key(in LookupChaseBenchmark.Prey row, float cell) => (uint)row.Target.Index;
        public static Vector3 Point(in LookupChaseBenchmark.Prey row) => default;
    }

    /// <summary>A seeker's rows in the order of its target's id.</summary>
    public readonly struct ByTarget : IRowOrder<LookupGatherBenchmark.Seeker>
    {
        public static int KeyBytes => 4;
        public static ulong Key(in LookupGatherBenchmark.Seeker row, float cell) => (uint)row.Target.Index;
        public static Vector3 Point(in LookupGatherBenchmark.Seeker row) => default;
    }

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        var random = new Random(42);

        _hunters = new World(new WorldOptions { InitialEntityCapacity = Rows });
        var hunters = new Entity[Rows];
        _hunters.CreateEntities(hunters, new LookupChaseBenchmark.Position(1, 2), new LookupChaseBenchmark.Prey(),
            new LookupChaseBenchmark.Heading());
        for (int i = 0; i < hunters.Length; i++)
            hunters[i].Get<LookupChaseBenchmark.Prey>() = new LookupChaseBenchmark.Prey(hunters[random.Next(hunters.Length)].Id);

        const int tables = 64;
        int targetCount = Math.Max(1_000, Rows / 4);
        _seekers = new World(new WorldOptions { InitialEntityCapacity = Rows + targetCount + 1_000 });
        _seekers.GroupBy<LookupGatherBenchmark.Cluster>();
        var byTable = new Entity[tables][];
        for (int t = 0; t < tables; t++)
        {
            byTable[t] = new Entity[targetCount / tables];
            _seekers.CreateEntities(byTable[t], new LookupGatherBenchmark.Spot(0, 0), new LookupGatherBenchmark.Cluster(t));
            foreach (var target in byTable[t])
                target.Get<LookupGatherBenchmark.Spot>() = new LookupGatherBenchmark.Spot(random.Next(1_000), -random.Next(1_000));
        }

        var seekers = new Entity[Rows];
        _seekers.CreateEntities(seekers, new LookupGatherBenchmark.Spot(0, 0), new LookupGatherBenchmark.Seeker(),
            new LookupGatherBenchmark.Bearing());
        for (int i = 0; i < seekers.Length; i++)
        {
            var row = byTable[random.Next(tables)];
            seekers[i].Get<LookupGatherBenchmark.Spot>() = new LookupGatherBenchmark.Spot(i % 7, i % 11);
            seekers[i].Get<LookupGatherBenchmark.Seeker>() = new LookupGatherBenchmark.Seeker(row[random.Next(row.Length)].Id);
        }

        if (Ordered)
        {
            _hunters.OrderRows<LookupChaseBenchmark.Prey, ByPrey>(1, 1, int.MaxValue, 0);
            _hunters.SortRows<LookupChaseBenchmark.Prey>();
            _seekers.OrderRows<LookupGatherBenchmark.Seeker, ByTarget>(1, 1, int.MaxValue, 0);
            _seekers.SortRows<LookupGatherBenchmark.Seeker>();
            int last = -1;
            foreach (var item in _seekers.Query<LookupGatherBenchmark.Seeker>())
            {
                if (item.Component1.Target.Index < last) throw new InvalidOperationException("the seekers are not in the order of their targets");
                last = item.Component1.Target.Index;
            }
        }

        Chase();
        Gather();
        GatherPerRow();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hunters.Dispose();
        _seekers.Dispose();
    }

    [Benchmark]
    public void Chase() => LookupChaseBenchmark.Hunting.ByLookup(_hunters);

    [Benchmark]
    public void Gather() => LookupGatherBenchmark.Seeking.Steer(_seekers);

    [Benchmark]
    public void GatherPerRow() => LookupGatherBenchmark.Seeking.PerRow(_seekers);
}
