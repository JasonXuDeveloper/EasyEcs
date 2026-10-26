using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A pass reading other entities' components through a <see cref="Lookup{T}"/>: seekers read their target's spot. The
/// method as written, row by row (<see cref="SimdMode.Off"/>), against the vector loop the generator writes (AVX2
/// gathers of the targets' records and fields, D131), on one thread and in parallel, with the same method in the same
/// run. Where the targets live decides what each lane resolves: random targets of one table, runs of 256 seekers aiming
/// into one of 4 tables, every seeker a random target of 64 or of 1,000 tables, 64 tables with a quarter of the targets
/// dead or without the component, and 64 tables where a quarter of the targets had it removed (the tables then hold it
/// for some rows only: presence bits). The body: one field read and one written, the bearing (two of each), or a
/// heavier one (a normalized, rotated bearing: a square root and four divisions).
/// </summary>
[MemoryDiagnoser]
public partial class LookupGatherBenchmark
{
    public enum Spread { OneTable, FourTables, Tables64, Tables1000, Dead, Presence }

    public enum Work { Trivial, Bearing, Heavy }

    [Params(1_000, 100_000, 1_000_000)] public int Rows;
    [Params(Spread.OneTable, Spread.FourTables, Spread.Tables64, Spread.Tables1000, Spread.Dead, Spread.Presence)] public Spread Targets;
    [Params(Work.Trivial, Work.Bearing, Work.Heavy)] public Work Body;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        int targetCount = Math.Max(1_000, Rows / 4);
        int tables = Targets switch { Spread.OneTable => 1, Spread.FourTables => 4, Spread.Tables1000 => 1_000, _ => 64 };
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

        var bare = _world.CreateEntity(new Cluster(tables)); // a target without Spot
        var seekers = new Entity[Rows];
        _world.CreateEntities(seekers, new Spot(0, 0), new Seeker(), new Bearing());
        for (int i = 0; i < seekers.Length; i++)
        {
            int t = Targets switch { Spread.OneTable => 0, Spread.FourTables => i / 256 % tables, _ => random.Next(tables) };
            var row = byTable[t];
            var target = row[random.Next(row.Length)].Id;
            if (Targets == Spread.Dead && random.Next(8) == 0) target = bare.Id;
            seekers[i].Get<Spot>() = new Spot(i % 7, i % 11);
            seekers[i].Get<Seeker>() = new Seeker(target);
        }

        // A quarter of the targets destroyed (their ids then find nothing), or with Spot removed in place.
        if (Targets is Spread.Dead or Spread.Presence)
            foreach (var row in byTable)
                for (int i = 0; i < row.Length; i += 4)
                    if (Targets == Spread.Dead) row[i].Destroy();
                    else row[i].Remove<Spot>();

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
    public void PerRow()
    {
        switch (Body)
        {
            case Work.Trivial: Seeking.TrivialRows(_world); break;
            case Work.Bearing: Seeking.BearingRows(_world); break;
            default: Seeking.HeavyRows(_world); break;
        }
    }

    [Benchmark]
    public void Vector()
    {
        switch (Body)
        {
            case Work.Trivial: Seeking.Trivial(_world); break;
            case Work.Bearing: Seeking.Bearing(_world); break;
            default: Seeking.Heavy(_world); break;
        }
    }

    [Benchmark]
    public void ParallelPerRow()
    {
        switch (Body)
        {
            case Work.Trivial: Seeking.ParallelTrivialRows(_world); break;
            case Work.Bearing: Seeking.ParallelBearingRows(_world); break;
            default: Seeking.ParallelHeavyRows(_world); break;
        }
    }

    [Benchmark]
    public void ParallelVector()
    {
        switch (Body)
        {
            case Work.Trivial: Seeking.ParallelTrivial(_world); break;
            case Work.Bearing: Seeking.ParallelBearing(_world); break;
            default: Seeking.ParallelHeavy(_world); break;
        }
    }

    public record struct Spot(float X, float Y) : IComponent;
    public record struct Seeker(EntityId Target) : IComponent;
    public record struct Bearing(float X, float Y) : IComponent;
    public partial record struct Cluster([Group] int Id) : IComponent;

    /// <summary>Each body four times: the vector loop and the method row by row, on one thread and in parallel.</summary>
    public static class Seeking
    {
        [ForEach]
        public static void Clear(ref Bearing bearing) => bearing = default;

        [ForEach(SimdMode = SimdMode.Require)]
        public static void Trivial(in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots) =>
            bearing.X = spots[seeker.Target].HasValue ? spots[seeker.Target].Value.X : 0;

        [ForEach(SimdMode = SimdMode.Off)]
        public static void TrivialRows(in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots) =>
            bearing.X = spots[seeker.Target].HasValue ? spots[seeker.Target].Value.X : 0;

        [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
        public static void ParallelTrivial(in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots) =>
            bearing.X = spots[seeker.Target].HasValue ? spots[seeker.Target].Value.X : 0;

        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        public static void ParallelTrivialRows(in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots) =>
            bearing.X = spots[seeker.Target].HasValue ? spots[seeker.Target].Value.X : 0;

        [ForEach(SimdMode = SimdMode.Require)]
        public static void Bearing(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }

        [ForEach(SimdMode = SimdMode.Off)]
        public static void BearingRows(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }

        [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
        public static void ParallelBearing(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }

        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        public static void ParallelBearingRows(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            bearing = target.HasValue ? new Bearing(target.Value.X - spot.X, target.Value.Y - spot.Y) : default;
        }

        [ForEach(SimdMode = SimdMode.Require)]
        public static void Heavy(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            float dx = target.HasValue ? target.Value.X - spot.X : 0, dy = target.HasValue ? target.Value.Y - spot.Y : 0;
            float d = MathF.Sqrt(dx * dx + dy * dy) + 1;
            bearing = new Bearing(dx / d * 3 + dy / d, dy / d * 3 - dx / d);
        }

        [ForEach(SimdMode = SimdMode.Off)]
        public static void HeavyRows(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            float dx = target.HasValue ? target.Value.X - spot.X : 0, dy = target.HasValue ? target.Value.Y - spot.Y : 0;
            float d = MathF.Sqrt(dx * dx + dy * dy) + 1;
            bearing = new Bearing(dx / d * 3 + dy / d, dy / d * 3 - dx / d);
        }

        [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
        public static void ParallelHeavy(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            float dx = target.HasValue ? target.Value.X - spot.X : 0, dy = target.HasValue ? target.Value.Y - spot.Y : 0;
            float d = MathF.Sqrt(dx * dx + dy * dy) + 1;
            bearing = new Bearing(dx / d * 3 + dy / d, dy / d * 3 - dx / d);
        }

        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        public static void ParallelHeavyRows(in Spot spot, in Seeker seeker, ref Bearing bearing, Lookup<Spot> spots)
        {
            var target = spots[seeker.Target];
            float dx = target.HasValue ? target.Value.X - spot.X : 0, dy = target.HasValue ? target.Value.Y - spot.Y : 0;
            float d = MathF.Sqrt(dx * dx + dy * dy) + 1;
            bearing = new Bearing(dx / d * 3 + dy / d, dy / d * 3 - dx / d);
        }
    }
}
