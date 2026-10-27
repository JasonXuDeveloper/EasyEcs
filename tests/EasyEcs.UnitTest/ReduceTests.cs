using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="Reduce{T}"/>: a [ForEach] pass accumulates into a partial result per chunk (per pass on one thread),
/// combined in chunk order into its singleton when the pass ends: replacing it, or added to it for an [Accumulate] total.
/// </summary>
public partial class ReduceTests
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

    /// <summary>A reduction declared by its fields' attributes: the generator writes Identity and Combine.</summary>
    public partial struct Census : ISingletonComponent
    {
        [Sum] public int Count;
        [Sum] public long Total;
        [Min] public int Lowest;
        [Max] public float Highest;
        [Product] public double Odds;
        [All] public bool AllEven;
        [Any] public bool AnyTen;
    }

    [EcsSystem]
    public ref struct ParallelCensus
    {
        [ForEach(Parallel = true, ChunkRows = 256)]
        static void Count(in Points points, ref Reduce<Census> census)
        {
            ref var c = ref census.Value;
            c.Count++;
            c.Total += points.Value;
            c.Lowest = Math.Min(c.Lowest, points.Value);
            c.Highest = MathF.Max(c.Highest, points.Value);
            c.Odds *= points.Value % 100 == 0 ? 0.5 : 1;
            c.AllEven &= points.Value % 2 == 0;
            c.AnyTen |= points.Value == 10;
        }
    }

    [EcsSystem]
    public ref struct SerialScores
    {
        [ForEach]
        static void Add(in Points points, ref Reduce<Scores> tally)
        {
            tally.Value.Count++;
            tally.Value.Sum += points.Value;
        }
    }

    [EcsSystem]
    public ref struct ParallelScores
    {
        [ForEach(Parallel = true)]
        static void Add(in Points points, ref Reduce<Scores> tally) => tally.Combine(new Scores { Count = 1, Sum = points.Value });
    }

    [EcsSystem]
    public ref struct ParallelBounds
    {
        [ForEach(Parallel = true, ChunkRows = 1024)]
        static void Grow(in Spot spot, ref Reduce<Bounds2> box)
        {
            box.Value.MinX = MathF.Min(box.Value.MinX, spot.X);
            box.Value.MinY = MathF.Min(box.Value.MinY, spot.Y);
            box.Value.MaxX = MathF.Max(box.Value.MaxX, spot.X);
            box.Value.MaxY = MathF.Max(box.Value.MaxY, spot.Y);
        }
    }

    [EcsSystem]
    public ref struct FixedChunkMass
    {
        [ForEach(Parallel = true, ChunkRows = 4096)]
        static void Add(in Weight weight, ref Reduce<WeightSum> mass) => mass.Value.Total += weight.Value;
    }

    /// <summary>The same sum in chunks the runtime chooses: from the row count alone, never from the threads.</summary>
    [EcsSystem]
    public ref struct DefaultChunkMass
    {
        [ForEach(Parallel = true)]
        static void Add(in Weight weight, ref Reduce<WeightSum> mass) => mass.Value.Total += weight.Value;
    }

    [EcsSystem]
    public ref struct ParallelEnds
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Visit(in Points points, ref Reduce<Ends2> ends) =>
            ends.Combine(new Ends2 { First = points.Value, Last = points.Value, Any = true });
    }

    [EcsSystem]
    public ref struct SerialEnds
    {
        [ForEach]
        static void Visit(in Points points, ref Reduce<Ends2> ends) =>
            ends.Combine(new Ends2 { First = points.Value, Last = points.Value, Any = true });
    }

    /// <summary>An optional component and a reduction: the serial job walks runs of rows, the partial result in a local.</summary>
    [EcsSystem]
    public ref struct ScoresWithBonus
    {
        [ForEach]
        static void Add(in Points points, ReadOnlyMaybe<Weight> bonus, ref Reduce<Scores> tally)
        {
            tally.Value.Count++;
            tally.Value.Sum += points.Value + (bonus.HasValue ? (long)bonus.Value.Value : 0);
        }
    }

    [EcsSystem]
    public ref struct ReadsScores
    {
        [ForEach]
        static void Read(ref Weight weight, in Scores scores) => weight.Value = scores.Count;
    }

    public static class Runners
    {
        [ForEach(Parallel = true)]
        public static void Score(in Points points, ref Reduce<Scores> tally)
        {
            tally.Value.Count++;
            tally.Value.Sum += points.Value;
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

    /// <summary>
    /// Histograms of 10,000 buckets (40 KB each): a job holding two of them is over 64 KB, too large to be the element of
    /// an array (a single value that large cannot be passed by value at all).
    /// </summary>
    public struct Buckets : ISingletonComponent, IReduction<Buckets>
    {
        public Counts Values;

        public static Buckets Identity => default;

        public static Buckets Combine(Buckets left, Buckets right)
        {
            for (int i = 0; i < Counts.Length; i++) left.Values[i] += right.Values[i];
            return left;
        }
    }

    public struct OddBuckets : ISingletonComponent, IReduction<OddBuckets>
    {
        public Counts Values;

        public static OddBuckets Identity => default;

        public static OddBuckets Combine(OddBuckets left, OddBuckets right)
        {
            for (int i = 0; i < Counts.Length; i++) left.Values[i] += right.Values[i];
            return left;
        }
    }

    [System.Runtime.CompilerServices.InlineArray(Length)]
    public struct Counts
    {
        public const int Length = 10_000;
        private int _first;
    }

    [EcsSystem]
    public ref struct ParallelBuckets
    {
        [ForEach(Parallel = true, ChunkRows = 1024)]
        static void Add(in Points points, ref Reduce<Buckets> all, ref Reduce<OddBuckets> odd)
        {
            all.Value.Values[points.Value % Counts.Length]++;
            if (points.Value % 2 != 0) odd.Value.Values[points.Value % Counts.Length]++;
        }
    }

    /// <summary>The armor of the rows written since the last run: rows picked by change ticks, a chunk's runs at a time.</summary>
    [EcsSystem]
    public ref struct ChangedArmor
    {
        [ForEach(Parallel = true, ChunkRows = 64), Changed<Armor>]
        static void Add(in Armor armor, ref Reduce<Scores> tally) => tally.Combine(new Scores { Count = 1, Sum = armor.Value });
    }

    [Test]
    public void AJobOfReductionsOver64KilobytesRunsInParallel()
    {
        using var world = Pointed(25_000);
        world.AddSystem<ParallelBuckets>();
        for (int run = 0; run < 2; run++)
        {
            world.Update();
            ref var all = ref world.GetSingleton<Buckets>();
            ref var odd = ref world.GetSingleton<OddBuckets>();
            for (int i = 0; i < Counts.Length; i++)
            {
                Assert.That(all.Values[i], Is.EqualTo(i < 5_000 ? 3 : 2), $"bucket {i}");
                Assert.That(odd.Values[i], Is.EqualTo(i % 2 == 0 ? 0 : i < 5_000 ? 3 : 2), $"odd bucket {i}");
            }
        }
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
    public void AReductionDeclaredByAttributesCombinesEachFieldItsWay()
    {
        Assert.That(Census.Identity, Is.EqualTo(new Census { Lowest = int.MaxValue, Highest = float.NegativeInfinity, Odds = 1, AllEven = true }));
        using var world = Pointed(10_000);
        world.AddSystem<ParallelCensus>();
        world.Update();
        var census = world.Census;
        Assert.That((census.Count, census.Total, census.Lowest, census.Highest), Is.EqualTo((10_000, 9_999L * 10_000 / 2, 0, 9_999f)));
        Assert.That((census.Odds, census.AllEven, census.AnyTen), Is.EqualTo((Math.Pow(0.5, 100), false, true)));

        using var empty = new World(); // no rows: the identity
        empty.AddSystem<ParallelCensus>();
        empty.Update();
        Assert.That(empty.Census, Is.EqualTo(Census.Identity));
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
        const int count = World.MaxChunkRows * 5 + 123;
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
    public void AFloatReductionWithFixedChunksDoesNotDependOnTheThreads() => SameMassOnAnyThreads<FixedChunkMass>(100_000);

    /// <summary>
    /// Without ChunkRows too: a pass that merges cuts its chunks from the row count alone, so a float sum is the same
    /// bits at 1 to 8 threads, on every run, whether the runner wakes helpers or runs alone. Table sizes below, at and
    /// above the cap of a chunk (16 chunks of 1,024 to 16,384 rows).
    /// </summary>
    [TestCase(5_000)]
    [TestCase(100_000)]
    [TestCase(400_000)]
    public void AFloatReductionDoesNotDependOnTheThreads(int rows) => SameMassOnAnyThreads<DefaultChunkMass>(rows);

    private static void SameMassOnAnyThreads<TSystem>(int rows) where TSystem : struct, allows ref struct
    {
        float? expected = null;
        foreach (int threads in new[] { 1, 2, 3, 4, 8 })
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            var random = new Random(11);
            var entities = new Entity[rows];
            world.CreateEntities(entities, new Weight(0));
            foreach (var e in entities) e.Weight = new Weight(random.NextSingle() * 1000);
            world.AddSystem<TSystem>();
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
        using var world = Pointed(World.MaxChunkRows * 3);
        for (int run = 0; run < 20; run++)
        {
            Runners.Score(world);
            Assert.That(world.Scores.Sum, Is.EqualTo((long)(World.MaxChunkRows * 3 - 1) * World.MaxChunkRows * 3 / 2));
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
        using var world = Pointed(World.MaxChunkRows * 4);
        world.AddSystem<ParallelScores>();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 300; i++) world.Update(); }), Is.Zero);
    }

    /// <summary>
    /// A total: [Accumulate] combines each pass's result into the world's value, and [Sum] on the struct sums every
    /// field without an attribute of its own.
    /// </summary>
    [Accumulate, Sum]
    public partial struct Grand : ISingletonComponent
    {
        public long Count;
        public long Sum;
        [Max] public int Highest;
    }

    /// <summary>The same total written by hand: one value every row adds to (what [Accumulate] replaces).</summary>
    public struct HandTotals : ISingletonComponent
    {
        public long Count;
        public long Sum;
        public int Highest;
    }

    /// <summary>A total that is not commutative: the world's value comes first, then the pass's.</summary>
    [Accumulate]
    public struct EndsTotal : ISingletonComponent, IReduction<EndsTotal>
    {
        public int First, Last;
        public bool Any;

        public static EndsTotal Identity => default;
        public static EndsTotal Combine(EndsTotal left, EndsTotal right) =>
            !left.Any ? right : !right.Any ? left : new EndsTotal { First = left.First, Last = right.Last, Any = true };
    }

    [EcsSystem]
    public ref struct SerialTotals
    {
        [ForEach]
        static void Add(in Points points, ref Reduce<Grand> totals)
        {
            totals.Value.Count++;
            totals.Value.Sum += points.Value;
            totals.Value.Highest = Math.Max(totals.Value.Highest, points.Value);
        }
    }

    [EcsSystem]
    public ref struct ParallelTotals
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Add(in Points points, ref Reduce<Grand> totals)
        {
            totals.Value.Count++;
            totals.Value.Sum += points.Value;
            totals.Value.Highest = Math.Max(totals.Value.Highest, points.Value);
        }
    }

    [EcsSystem]
    public ref struct HandWrittenTotals
    {
        [ForEach]
        static void Add(in Points points, ref HandTotals totals)
        {
            totals.Count++;
            totals.Sum += points.Value;
            totals.Highest = Math.Max(totals.Highest, points.Value);
        }
    }

    [EcsSystem]
    public ref struct ParallelEndsTotal
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Visit(in Points points, ref Reduce<EndsTotal> ends) =>
            ends.Combine(new EndsTotal { First = points.Value, Last = points.Value, Any = true });
    }

    public static class TotalRunners
    {
        [ForEach(Parallel = true)]
        public static void Add(in Points points, ref Reduce<Grand> totals) => totals.Value.Sum += points.Value;
    }

    [Test]
    public void AStructAttributeCombinesTheFieldsWithoutTheirOwn()
    {
        Assert.That(Grand.Identity, Is.EqualTo(new Grand { Highest = int.MinValue }));
        Assert.That(Grand.Combine(new Grand { Count = 1, Sum = 2, Highest = 9 }, new Grand { Count = 3, Sum = 4, Highest = 5 }),
            Is.EqualTo(new Grand { Count = 4, Sum = 6, Highest = 9 }));
    }

    [TestCase(1, false)]
    [TestCase(4, false)]
    [TestCase(1, true)]
    [TestCase(4, true)]
    public void AnAccumulatedTotalIsTheHandWrittenOneBitForBit(int threads, bool parallel)
    {
        using var world = Pointed(World.MaxChunkRows * 2 + 77, threads);
        var more = new Entity[300];
        world.CreateEntities(more, new Points(0), new Spot(0, 0)); // a second table
        for (int i = 0; i < more.Length; i++) more[i].Points = new Points(-i * 3);
        world.GetOrAddSingleton<HandTotals>() = new HandTotals { Highest = int.MinValue };
        if (parallel) world.AddSystem<ParallelTotals>();
        else world.AddSystem<SerialTotals>();
        world.AddSystem<HandWrittenTotals>();
        Assert.That(world.HasGrand, Is.False);
        for (int run = 0; run < 20; run++)
        {
            world.Update();
            var hand = world.GetSingleton<HandTotals>();
            Assert.That(world.Grand, Is.EqualTo(new Grand { Count = hand.Count, Sum = hand.Sum, Highest = hand.Highest }), $"run {run}");
        }
    }

    [Test]
    public void AnAccumulatedTotalStartsFromTheWorldsValue()
    {
        using var world = Pointed(1000);
        world.GetOrAddGrand() = new Grand { Count = 7, Sum = 7, Highest = 5000 };
        world.AddSystem<SerialTotals>();
        world.Update();
        world.Update();
        Assert.That(world.Grand, Is.EqualTo(new Grand { Count = 2007, Sum = 7 + 999L * 1000, Highest = 5000 }));

        using var empty = new World(); // no rows: the identity, added to nothing
        empty.AddSystem<ParallelTotals>();
        empty.Update();
        Assert.That(empty.Grand, Is.EqualTo(Grand.Identity));
    }

    [Test]
    public void AnAccumulatedTotalCombinesTheWorldsValueFirst()
    {
        using var world = Pointed(10_000);
        world.GetOrAddSingleton<EndsTotal>() = new EndsTotal { First = -5, Last = -5, Any = true };
        world.AddSystem<ParallelEndsTotal>();
        for (int run = 0; run < 50; run++)
        {
            world.Update();
            Assert.That(world.GetSingleton<EndsTotal>(), Is.EqualTo(new EndsTotal { First = -5, Last = 9_999, Any = true }), $"run {run}");
        }
    }

    [Test]
    public void RunnersAndLambdaPassesAccumulateToo()
    {
        using var world = Pointed(World.MaxChunkRows * 3);
        long pass = (long)(World.MaxChunkRows * 3 - 1) * World.MaxChunkRows * 3 / 2;
        TotalRunners.Add(world);
        TotalRunners.Add(world);
        Assert.That(world.Grand.Sum, Is.EqualTo(2 * pass));
        world.ForEachParallel(static (in Points points, ref Reduce<Grand> totals) => totals.Value.Sum += points.Value);
        Assert.That(world.Grand.Sum, Is.EqualTo(3 * pass));
    }

    [Test]
    public void AnAccumulatedTotalDoesNotAllocateAfterItsFirstPass()
    {
        using var world = Pointed(World.MaxChunkRows * 4);
        world.AddSystem<ParallelTotals>();
        world.AddSystem<SerialTotals>();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 300; i++) world.Update(); }), Is.Zero);
    }
}
