using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="Reduce{T}"/>: a [ForEach] pass accumulates into a partial result per chunk (per pass on one thread),
/// combined in chunk order into its singleton when the pass ends.
/// </summary>
public class ReduceTests
{
    public record struct Points(int Value) : IComponent;
    public record struct Spot(float X, float Y) : IComponent;
    public record struct Weight(float Value) : IComponent;

    /// <summary>A count and a sum: integers, exact whatever the chunks.</summary>
    public struct Scores : ISingletonComponent, IReduction<Scores>
    {
        public int Count;
        public long Sum;

        public static Scores Identity => default;
        public static Scores Combine(Scores left, Scores right) => new() { Count = left.Count + right.Count, Sum = left.Sum + right.Sum };
    }

    /// <summary>A bounding box: min and max per axis.</summary>
    public struct Bounds2 : ISingletonComponent, IReduction<Bounds2>
    {
        public float MinX, MinY, MaxX, MaxY;

        public static Bounds2 Identity => new() { MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };

        public static Bounds2 Combine(Bounds2 left, Bounds2 right) => new()
        {
            MinX = MathF.Min(left.MinX, right.MinX), MinY = MathF.Min(left.MinY, right.MinY),
            MaxX = MathF.Max(left.MaxX, right.MaxX), MaxY = MathF.Max(left.MaxY, right.MaxY),
        };
    }

    /// <summary>A float sum: rounded per chunk.</summary>
    public struct WeightSum : ISingletonComponent, IReduction<WeightSum>
    {
        public float Total;

        public static WeightSum Identity => default;
        public static WeightSum Combine(WeightSum left, WeightSum right) => new() { Total = left.Total + right.Total };
    }

    /// <summary>The first and last value visited: not commutative, so it shows the order partial results combine in.</summary>
    public struct Ends2 : ISingletonComponent, IReduction<Ends2>
    {
        public int First, Last;
        public bool Any;

        public static Ends2 Identity => default;
        public static Ends2 Combine(Ends2 left, Ends2 right) =>
            !left.Any ? right : !right.Any ? left : new Ends2 { First = left.First, Last = right.Last, Any = true };
    }

    public class SerialScores : EcsSystem
    {
        [ForEach]
        static void Add(in Points points, ref Reduce<Scores> tally)
        {
            ref var t = ref tally.Value;
            t.Count++;
            t.Sum += points.Value;
        }
    }

    public class ParallelScores : EcsSystem
    {
        [ForEach(Parallel = true)]
        static void Add(in Points points, ref Reduce<Scores> tally) => tally.Combine(new Scores { Count = 1, Sum = points.Value });
    }

    public class ParallelBounds : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 1024)]
        static void Grow(in Spot spot, ref Reduce<Bounds2> box)
        {
            ref var b = ref box.Value;
            b.MinX = MathF.Min(b.MinX, spot.X);
            b.MinY = MathF.Min(b.MinY, spot.Y);
            b.MaxX = MathF.Max(b.MaxX, spot.X);
            b.MaxY = MathF.Max(b.MaxY, spot.Y);
        }
    }

    public class FixedChunkMass : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 4096)]
        static void Add(in Weight weight, ref Reduce<WeightSum> mass) => mass.Value.Total += weight.Value;
    }

    public class ParallelEnds : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Visit(in Points points, ref Reduce<Ends2> ends) =>
            ends.Combine(new Ends2 { First = points.Value, Last = points.Value, Any = true });
    }

    public class SerialEnds : EcsSystem
    {
        [ForEach]
        static void Visit(in Points points, ref Reduce<Ends2> ends) =>
            ends.Combine(new Ends2 { First = points.Value, Last = points.Value, Any = true });
    }

    /// <summary>An optional component and a reduction: the serial job walks runs of rows, the partial result in a local.</summary>
    public class ScoresWithBonus : EcsSystem
    {
        [ForEach]
        static void Add(in Points points, ReadOnlyMaybe<Weight> bonus, ref Reduce<Scores> tally)
        {
            ref var t = ref tally.Value;
            t.Count++;
            t.Sum += points.Value + (bonus.HasValue ? (long)bonus.Value.Value : 0);
        }
    }

    public class ReadsScores : EcsSystem
    {
        [ForEach]
        static void Read(ref Weight weight, in Scores scores) => weight.Value = scores.Count;
    }

    public static class Runners
    {
        [ForEach(Parallel = true)]
        public static void Score(in Points points, ref Reduce<Scores> tally)
        {
            ref var t = ref tally.Value;
            t.Count++;
            t.Sum += points.Value;
        }
    }

    private static World Pointed(int count, int parallelism = 4)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = parallelism });
        var entities = new Entity[count];
        world.CreateEntities(entities, new Points(0));
        for (int i = 0; i < count; i++) entities[i].Points = new Points(i);
        return world;
    }

    /// <summary>The armor of the rows written since the last run: rows picked by change ticks, a chunk's runs at a time.</summary>
    public class ChangedArmor : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 64), Changed<Armor>]
        static void Add(in Armor armor, ref Reduce<Scores> tally) => tally.Combine(new Scores { Count = 1, Sum = armor.Value });
    }

    [Test]
    public void AParallelReductionVisitsOnlyTheRowsItsFilterPasses()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var entities = new Entity[20_000];
        world.CreateEntities(entities, new Armor(0), new Points(0));
        world.AddSystem<ChangedArmor>();
        world.Update(); // every row is new
        Assert.That(world.Scores.Count, Is.EqualTo(entities.Length));
        for (int run = 1; run <= 50; run++)
        {
            long sum = 0;
            int count = 0;
            for (int i = run % 7; i < entities.Length; i += 7 + run % 5) // scattered rows, runs of one row
            {
                entities[i].Set(new Armor(i));
                sum += i;
                count++;
            }

            for (int i = 0; i < entities.Length; i += 997) entities[i].Disable<Points>(); // presence bits: rows tested
            world.Update();
            Assert.That((world.Scores.Count, world.Scores.Sum), Is.EqualTo((count, sum)), $"run {run}");
            for (int i = 0; i < entities.Length; i += 997) entities[i].Enable<Points>();
        }
    }

    [Test]
    public void ASerialPassReplacesItsSingleton()
    {
        using var world = Pointed(1000);
        world.GetOrAddScores() = new Scores { Count = 7, Sum = 7 };
        world.AddSystem<SerialScores>();
        world.Update();
        Assert.That((world.Scores.Count, world.Scores.Sum), Is.EqualTo((1000, 999L * 1000 / 2)));
        world.Update(); // not accumulated across passes: each pass's result replaces the last
        Assert.That((world.Scores.Count, world.Scores.Sum), Is.EqualTo((1000, 999L * 1000 / 2)));
    }

    [Test]
    public void APassAddsAMissingSingletonAndAnEmptyPassWritesTheIdentity()
    {
        using var world = new World();
        world.AddSystem<ParallelBounds>();
        world.Update();
        Assert.That(world.HasBounds2, Is.True);
        Assert.That(world.Bounds2, Is.EqualTo(Bounds2.Identity));
    }

    [Test]
    public void ParallelIntegerReductionsAreExactOnEveryRun()
    {
        const int count = World.ParallelChunkRows * 5 + 123;
        using var world = Pointed(count);
        world.AddSystem<ParallelScores>();
        for (int run = 0; run < 300; run++)
        {
            world.Update();
            if (world.Scores.Count != count || world.Scores.Sum != (long)(count - 1) * count / 2)
                Assert.Fail($"run {run}: {world.Scores.Count} rows, sum {world.Scores.Sum}");
        }
    }

    [Test]
    public void AParallelBoundingBoxMatchesASerialScan()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var random = new Random(5);
        var entities = new Entity[50_000];
        world.CreateEntities(entities, new Spot(0, 0));
        var expected = Bounds2.Identity;
        foreach (var e in entities)
        {
            var spot = new Spot(random.NextSingle() * 2000 - 1000, random.NextSingle() * 500 - 250);
            e.Spot = spot;
            expected = Bounds2.Combine(expected, new Bounds2 { MinX = spot.X, MaxX = spot.X, MinY = spot.Y, MaxY = spot.Y });
        }

        world.AddSystem<ParallelBounds>();
        for (int run = 0; run < 100; run++)
        {
            world.Update();
            Assert.That(world.Bounds2, Is.EqualTo(expected));
        }
    }

    [Test]
    public void PartialResultsCombineInRowOrder()
    {
        // Several tables: chunks of 64 rows each, run by any thread, combined as the rows come.
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var first = new Entity[20_000];
        var second = new Entity[30_000];
        world.CreateEntities(first, new Points(0));
        world.CreateEntities(second, new Points(0), new Spot(0, 0));
        int n = 0;
        foreach (var e in first) e.Points = new Points(n++);
        foreach (var e in second) e.Points = new Points(n++);
        world.AddSystem<SerialEnds>();
        world.Update();
        var serial = world.Ends2;
        world.RemoveSystem<SerialEnds>();
        world.AddSystem<ParallelEnds>();
        for (int run = 0; run < 200; run++)
        {
            world.Update();
            Assert.That(world.Ends2, Is.EqualTo(serial));
        }
    }

    [Test]
    public void AFloatReductionWithFixedChunksDoesNotDependOnTheThreads()
    {
        float? expected = null;
        foreach (int threads in new[] { 1, 2, 3, 4, 8 })
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            var random = new Random(11);
            var entities = new Entity[100_000];
            world.CreateEntities(entities, new Weight(0));
            foreach (var e in entities) e.Weight = new Weight(random.NextSingle() * 1000);
            world.AddSystem<FixedChunkMass>();
            for (int run = 0; run < 50; run++)
            {
                world.Update();
                expected ??= world.WeightSum.Total;
                Assert.That(world.WeightSum.Total, Is.EqualTo(expected.Value), $"{threads} threads, run {run}");
            }
        }
    }

    [Test]
    public void AReductionNextToAnOptionalComponentRunsOverRuns()
    {
        using var world = Pointed(5000);
        var bonus = new Entity[100];
        world.CreateEntities(bonus, new Points(1), new Weight(2));
        world.AddSystem<ScoresWithBonus>();
        world.Update();
        Assert.That((world.Scores.Count, world.Scores.Sum), Is.EqualTo((5100, 4999L * 5000 / 2 + 300)));
    }

    [Test]
    public void ARunnerReducesIntoItsSingleton()
    {
        using var world = Pointed(World.ParallelChunkRows * 3);
        for (int run = 0; run < 20; run++)
        {
            Runners.Score(world);
            Assert.That(world.Scores.Sum, Is.EqualTo((long)(World.ParallelChunkRows * 3 - 1) * World.ParallelChunkRows * 3 / 2));
        }
    }

    [Test]
    public void AReductionWritesItsSingletonForTheScheduler()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        world.AddSystem<ParallelScores>();
        world.AddSystem<ReadsScores>();
        world.AddSystem<ParallelBounds>();
        var schedule = world.DescribeSchedule().ToString();
        // The reader of Scores waits for its reduction; the box shares the reduction's stage.
        Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(2), schedule);
    }

    [Test]
    public void AParallelReductionDoesNotAllocateAfterItsFirstPass()
    {
        using var world = Pointed(World.ParallelChunkRows * 4);
        world.AddSystem<ParallelScores>();
        for (int i = 0; i < 10; i++) world.Update();
        long least = long.MaxValue;
        for (int window = 0; window < 3 && least != 0; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300; i++) world.Update();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.That(least, Is.Zero);
    }
}
