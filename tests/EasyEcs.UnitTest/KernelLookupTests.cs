using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Beacon(float X, float Y) : IComponent;
public record struct Tracker(EntityId Target) : IComponent;
public record struct Course(float X, float Y) : IComponent;
public partial record struct Band([Group] int Id) : IComponent;

/// <summary>
/// Reads of other entities through a Lookup&lt;T&gt; in vector loops (Kernel.Lookups.cs, D131): each form required as a
/// vector loop (SimdMode.Require: the build fails otherwise) and its twin row by row (SimdMode.Off).
/// </summary>
public static class LookupKernels
{
    [ForEach(SimdMode = SimdMode.Require)]
    public static void Steer(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        var target = beacons[tracker.Target];
        course = target.HasValue ? new Course(target.Value.X - beacon.X, target.Value.Y - beacon.Y) : default;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void SteerRows(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        var target = beacons[tracker.Target];
        course = target.HasValue ? new Course(target.Value.X - beacon.X, target.Value.Y - beacon.Y) : default;
    }

    // The indexer read directly, a field of it under its HasValue, and Has.
    [ForEach(SimdMode = SimdMode.Require)]
    public static void Probe(in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        course.X = beacons[tracker.Target].HasValue ? beacons[tracker.Target].Value.Y * 2 : -1;
        if (beacons.Has(tracker.Target)) course.Y += 1;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void ProbeRows(in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        course.X = beacons[tracker.Target].HasValue ? beacons[tracker.Target].Value.Y * 2 : -1;
        if (beacons.Has(tracker.Target)) course.Y += 1;
    }

    // Read, which throws for a target without Beacon: its field and its whole value.
    [ForEach(SimdMode = SimdMode.Require)]
    public static void Follow(in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        var at = beacons.Read(tracker.Target);
        course = new Course(at.X + at.Y, beacons.Read(tracker.Target).Y);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void FollowRows(in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        var at = beacons.Read(tracker.Target);
        course = new Course(at.X + at.Y, beacons.Read(tracker.Target).Y);
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    public static void SteerParallel(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        var target = beacons[tracker.Target];
        course = target.HasValue ? new Course(target.Value.X - beacon.X, target.Value.Y - beacon.Y) : default;
    }

    [ForEach]
    public static void Clear(ref Course course) => course = default;
}

public class KernelLookupTests
{
    /// <summary>
    /// Trackers aiming at beacons in <paramref name="tables"/> tables (runs of <paramref name="run"/> trackers into one
    /// table, or each a random one when 0); with <paramref name="odd"/>, also targets that are destroyed (their ids
    /// recycled), null, of another world (past this one's records), in a table without Beacon, or with Beacon removed
    /// (its table then holds it for some rows only).
    /// </summary>
    private static World Build(int trackers, int tables, int run, bool odd, int threads = 1)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        world.GroupBy<Band>();
        var random = new Random(trackers * 31 + tables * 7 + run + (odd ? 1 : 0));
        var byTable = new Entity[tables][];
        for (int t = 0; t < tables; t++)
        {
            byTable[t] = new Entity[64 + random.Next(64)];
            world.CreateEntities(byTable[t], new Beacon(0, 0), new Band(t));
            foreach (var target in byTable[t]) target.Get<Beacon>() = new Beacon(random.NextSingle() * 100 - 50, random.Next(-1000, 1000));
        }

        var bare = world.CreateEntity(new Band(tables)); // no Beacon
        var all = new Entity[trackers];
        world.CreateEntities(all, new Beacon(0, 0), new Tracker(), new Course());
        for (int i = 0; i < all.Length; i++)
        {
            var row = byTable[run > 0 ? i / run % tables : random.Next(tables)];
            all[i].Get<Beacon>() = new Beacon(i % 13 - 6.5f, i % 7);
            var target = row[random.Next(row.Length)].Id;
            if (odd)
                target = random.Next(40) switch
                {
                    0 => default,
                    1 => new EntityId(1 << 28, 1),
                    2 => new EntityId(-5, 1),
                    3 => bare.Id,
                    _ => target,
                };
            all[i].Get<Tracker>() = new Tracker(target);
        }

        if (odd)
        {
            // Destroyed targets, their ids recycled by new entities (another version), and one Beacon removed.
            for (int t = 0; t < tables; t++)
            {
                byTable[t][1].Destroy();
                byTable[t][3].Remove<Beacon>();
            }
            world.CreateEntities(new Entity[tables], new Band(0));
        }

        return world;
    }

    private static Course[] Courses(World world)
    {
        var courses = new Course[world.EntityCount];
        int n = 0;
        foreach (var item in world.Query<Course>()) courses[n++] = item.Course;
        return courses.AsSpan(0, n).ToArray();
    }

    private static void AssertSame(Course[] expected, Course[] actual, string form)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
            if (BitConverter.SingleToInt32Bits(expected[i].X) != BitConverter.SingleToInt32Bits(actual[i].X)
                || BitConverter.SingleToInt32Bits(expected[i].Y) != BitConverter.SingleToInt32Bits(actual[i].Y))
                Assert.Fail($"{form}: row {i} is {actual[i]}, the method row by row gives {expected[i]}");
    }

    private static void Compare(World world, Action<World> vector, Action<World> rows, string form)
    {
        LookupKernels.Clear(world);
        rows(world);
        var expected = Courses(world);
        LookupKernels.Clear(world);
        vector(world);
        AssertSame(expected, Courses(world), form);
    }

    [TestCase(0, 1, 0, false)]     // an empty table
    [TestCase(7, 1, 0, false)]     // fewer rows than a vector
    [TestCase(1_003, 1, 0, false)] // one table: every vector gathers, a tail runs the method
    [TestCase(1_003, 4, 256, false)] // runs of 256 into one of 4 tables
    [TestCase(1_003, 4, 3, false)]  // runs shorter than a vector: vectors of two tables run the method
    [TestCase(1_003, 64, 0, false)] // every lane a random table
    [TestCase(2_000, 1, 0, true)]   // dead, recycled, null, foreign ids, a table without Beacon, presence bits
    [TestCase(2_000, 3, 512, true)]
    public void VectorLoopsReadOtherEntitiesAsTheMethodDoes(int trackers, int tables, int run, bool odd)
    {
        using var world = Build(trackers, tables, run, odd);
        Compare(world, w => LookupKernels.Steer(w), w => LookupKernels.SteerRows(w), "Steer");
        Compare(world, w => LookupKernels.Probe(w), w => LookupKernels.ProbeRows(w), "Probe");
        if (!odd) Compare(world, w => LookupKernels.Follow(w), w => LookupKernels.FollowRows(w), "Follow");
    }

    [Test]
    public void AReadOfAMissingTargetThrowsAsTheMethodDoes()
    {
        using var world = Build(100, 1, 0, false);
        var bare = world.CreateEntity(new Band(9)); // a target without Beacon: Read throws
        world.CreateEntity(new Beacon(0, 0), new Tracker(bare.Id), new Course());
        var rows = Assert.Catch(() => LookupKernels.FollowRows(world));
        var vector = Assert.Catch(() => LookupKernels.Follow(world));
        Assert.That(vector.GetType(), Is.EqualTo(rows.GetType()));
        Assert.That(vector.Message, Is.EqualTo(rows.Message));
    }

    [TestCase(1, 0, false)]
    [TestCase(4, 256, false)]
    [TestCase(64, 0, true)]
    public void AParallelVectorLoopReadsOtherEntitiesBitForBitOnOneAndFourThreads(int tables, int run, bool odd)
    {
        using var one = Build(20_000, tables, run, odd, threads: 1);
        using var four = Build(20_000, tables, run, odd, threads: 4);
        LookupKernels.SteerRows(one);
        var expected = Courses(one);
        LookupKernels.Clear(one);
        LookupKernels.SteerParallel(one);
        AssertSame(expected, Courses(one), "1 thread");
        LookupKernels.SteerParallel(four);
        AssertSame(expected, Courses(four), "4 threads");
    }

    [Test]
    public void AVectorLoopReadingOtherEntitiesAllocatesNothing()
    {
        using var world = Build(5_000, 4, 256, true, threads: 4);
        void Frame()
        {
            LookupKernels.Steer(world);
            LookupKernels.Probe(world);
            LookupKernels.SteerParallel(world);
        }

        for (int i = 0; i < 50; i++) Frame(); // warm-up: the query cache, JIT tiers
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) Frame(); }), Is.Zero);
    }
}
