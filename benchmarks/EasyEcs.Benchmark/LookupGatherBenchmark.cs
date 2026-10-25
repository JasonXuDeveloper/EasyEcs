using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A pass reading other entities' components through a <see cref="Lookup{T}"/>: seekers take the bearing to their
/// target's spot. The method as written, row by row (<see cref="SimdMode.Off"/>), against the vector loop the generator
/// writes (AVX2 gathers of the targets' records and fields, D131), on one thread and in parallel. Where the targets live
/// decides what the gathers can do: random targets of one table, runs of 256 seekers aiming into one of 4 tables, and
/// every seeker a random target of 64 tables (a vector of 8 lanes then almost never shares a table, and runs the method).
/// </summary>
[MemoryDiagnoser]
public partial class LookupGatherBenchmark
{
    public enum Spread { OneTable, FewTables, ManyTables }

    [Params(1_000, 100_000, 1_000_000)] public int Rows;
    [Params(Spread.OneTable, Spread.FewTables, Spread.ManyTables)] public Spread Targets;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        int targetCount = Math.Max(1_000, Rows / 4);
        int tables = Targets switch { Spread.OneTable => 1, Spread.FewTables => 4, _ => 64 };
        _world = new World(new WorldOptions { InitialEntityCapacity = Rows + targetCount + 1_000 });
        _world.GroupBy<Cluster>();
        var random = new Random(42);
        var byTable = new Entity[tables][];
        for (int t = 0; t < tables; t++)
        {
            byTable[t] = new Entity[targetCount / tables];
            _world.CreateEntities(byTable[t], new Spot(0, 0), new Cluster(t));
            foreach (var target in byTable[t]) target.Get<Spot>() = new Spot(random.Next(1_000), -random.Next(1_000));
        }

        var seekers = new Entity[Rows];
        _world.CreateEntities(seekers, new Spot(0, 0), new Seeker(), new Bearing());
        for (int i = 0; i < seekers.Length; i++)
        {
            int t = Targets switch { Spread.OneTable => 0, Spread.FewTables => i / 256 % tables, _ => random.Next(tables) };
            var row = byTable[t];
            seekers[i].Get<Spot>() = new Spot(i % 7, i % 11);
            seekers[i].Get<Seeker>() = new Seeker(row[random.Next(row.Length)].Id);
        }

        // Every form computes the same bearings, bit for bit.
        PerRow();
        var expected = Bearings();
        foreach (var run in new Action[] { Vector, ParallelPerRow, ParallelVector })
        {
            Seeking.Clear(_world);
            run();
            var got = Bearings();
            for (int i = 0; i < got.Length; i++)
                if (BitConverter.SingleToInt32Bits(got[i].X) != BitConverter.SingleToInt32Bits(expected[i].X)
                    || BitConverter.SingleToInt32Bits(got[i].Y) != BitConverter.SingleToInt32Bits(expected[i].Y))
                    throw new InvalidOperationException($"{run.Method.Name} differs from the method row by row at row {i}");
        }
    }

    private Bearing[] Bearings()
    {
        var all = new Bearing[Rows];
        int n = 0;
        foreach (var item in _world.Query<Bearing>()) all[n++] = item.Bearing;
        return all;
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void PerRow() => Seeking.PerRow(_world);

    [Benchmark]
    public void Vector() => Seeking.Steer(_world);

    [Benchmark]
    public void ParallelPerRow() => Seeking.ParallelPerRow(_world);

    [Benchmark]
    public void ParallelVector() => Seeking.ParallelSteer(_world);

    public record struct Spot(float X, float Y) : IComponent;
    public record struct Seeker(EntityId Target) : IComponent;
    public record struct Bearing(float X, float Y) : IComponent;
    public partial record struct Cluster([Group] int Id) : IComponent;

    public static class Seeking
    {
        [ForEach]
        public static void Clear(ref Bearing bearing) => bearing = default;

        [ForEach]
        public static void Steer(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }

        [ForEach(SimdMode = SimdMode.Off)]
        public static void PerRow(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }

        [ForEach(Parallel = true)]
        public static void ParallelSteer(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }

        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        public static void ParallelPerRow(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }
    }
}
