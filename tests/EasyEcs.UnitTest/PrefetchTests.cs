using System;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Methods chasing other entities (Lookup reads at an id the row holds) whose row form prefetches.</summary>
public static class ChaseKernels
{
    // The row form: past the cache, a twin job over runs of rows prefetching the records 16 rows ahead and the rows 8 ahead.
    [ForEach]
    public static void Chase(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        var target = beacons[tracker.Target];
        course = target.HasValue ? new Course(target.Value.X - beacon.X, target.Value.Y - beacon.Y) : default;
    }

    // Has and Read of one id: one chase, both stages.
    [ForEach]
    public static void Check(in Tracker tracker, ref Course course, Lookup<Beacon> beacons) =>
        course = beacons.Has(tracker.Target) ? new Course(beacons.Read(tracker.Target).X, 1) : new Course(-1, 0);

    // In parallel the row form keeps today's job (ForEachParallel); only a parallel vector loop prefetches.
    [ForEach(Parallel = true)]
    public static void ChaseParallel(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        var target = beacons[tracker.Target];
        course = target.HasValue ? new Course(target.Value.X - beacon.X, target.Value.Y - beacon.Y) : default;
    }

    // An id of a component the method writes: no chase (a row ahead may not hold the id its own row reads).
    [ForEach]
    public static void Retarget(ref Tracker tracker, ref Course course, Lookup<Beacon> beacons)
    {
        course = beacons.Has(tracker.Target) ? new Course(beacons.Read(tracker.Target).Y, 0) : default;
        if (!beacons.Has(tracker.Target)) tracker = default;
    }
}

public class PrefetchTests
{
    /// <summary>Runs <paramref name="body"/> with the last-level cache taken as <paramref name="bytes"/>, then restores it.</summary>
    private static void WithCache(long bytes, Action body)
    {
        long was = LastLevelCache.Bytes;
        LastLevelCache.Bytes = bytes;
        try { body(); }
        finally { LastLevelCache.Bytes = was; }
    }

    private static Course[] Run(World world, Action<World> pass)
    {
        LookupKernels.Clear(world);
        pass(world);
        return KernelLookupTests.Courses(world);
    }

    /// <summary>
    /// The prefetching jobs compute what today's do, bit for bit: the gate on (a cache of 1 byte) against off (unknown)
    /// and against the method row by row, with dead, recycled, null and foreign targets, targets without the component,
    /// runs no longer than the prefetch distance (7 and 16 rows, runs of a few rows) and an empty world.
    /// </summary>
    [TestCase(0, 1, 0, false)]
    [TestCase(7, 1, 0, false)]
    [TestCase(16, 1, 0, false)]
    [TestCase(17, 1, 0, false)]
    [TestCase(1_003, 1, 0, false)]
    [TestCase(1_003, 64, 0, false)]
    [TestCase(2_000, 1, 0, true)]
    [TestCase(2_000, 3, 512, true)]
    [TestCase(3_001, 1_000, 0, true)]
    public void PrefetchingJobsComputeWhatTodaysDo(int trackers, int tables, int run, bool odd)
    {
        foreach (var (name, pass, rows) in new (string, Action<World>, Action<World>)[]
                 {
                     ("Chase", w => ChaseKernels.Chase(w), w => LookupKernels.SteerRows(w)),
                     ("ChaseParallel", w => ChaseKernels.ChaseParallel(w), w => LookupKernels.SteerRows(w)),
                     ("Check", w => ChaseKernels.Check(w), null),
                     ("Retarget", w => ChaseKernels.Retarget(w), null),
                     ("Steer", w => LookupKernels.Steer(w), w => LookupKernels.SteerRows(w)),
                     ("SteerParallel", w => LookupKernels.SteerParallel(w), w => LookupKernels.SteerRows(w)),
                     ("Probe", w => LookupKernels.Probe(w), w => LookupKernels.ProbeRows(w)),
                     ("Orient", w => LookupKernels.Orient(w), w => LookupKernels.OrientRows(w)),
                 })
        {
            // A world per form: Retarget clears the targets it finds dead.
            using var world = KernelLookupTests.Build(trackers, tables, run, odd, threads: 4);
            Course[] off = null, on = null;
            WithCache(0, () => off = Run(world, pass));
            WithCache(1, () => on = Run(world, pass));
            KernelLookupTests.AssertSame(off, on, name + " prefetching");
            if (rows != null) KernelLookupTests.AssertSame(Run(world, rows), on, name);
        }
    }

    [Test]
    public void APrefetchingPassAllocatesNothing()
    {
        using var world = KernelLookupTests.Build(5_000, 4, 256, true, threads: 4);
        WithCache(1, () =>
        {
            void Frame()
            {
                ChaseKernels.Chase(world);
                ChaseKernels.Check(world);
                ChaseKernels.ChaseParallel(world);
                LookupKernels.Steer(world);
            }

            for (int i = 0; i < 50; i++) Frame();
            Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) Frame(); }), Is.Zero);
        });
    }

    /// <summary>
    /// The gate: a pass prefetches only where a prefetch exists (x86) and when every live entity's record and row of T
    /// (16 + size bytes each) are past the last-level cache; an unknown cache (0) never.
    /// </summary>
    [Test]
    public void ThePassPrefetchesOnlyPastTheLastLevelCache()
    {
        using var world = new World();
        world.CreateEntities(new Entity[1_000], new Beacon(1, 2));
        int bytes = 1_000 * (16 + 8);
        WithCache(bytes - 1, () => Assert.That(Lookup<Beacon>.Prefetches(world), Is.EqualTo(Sse.IsSupported), "past the cache"));
        WithCache(bytes, () => Assert.That(Lookup<Beacon>.Prefetches(world), Is.False, "in the cache"));
        WithCache(0, () => Assert.That(Lookup<Beacon>.Prefetches(world), Is.False, "an unknown cache"));
        Assert.That(LookupLanes.PastCache(1L << 40, 0), Is.False);
        Assert.That(LookupLanes.PastCache(33L << 20, 32L << 20), Is.True);
        Assert.That(LookupLanes.PastCache(16L << 20, 32L << 20), Is.False);
    }

    /// <summary>
    /// What the generator writes: the row form's twin job prefetching its chases (the row stage only for a read, not for
    /// Has), picked by the pass past the cache; the vector loop's prefetches per vector behind the gate its job reads once
    /// per pass; none for an id the method may write.
    /// </summary>
    [Test]
    public void TheJobsPrefetchTheirChasesBehindTheGate()
    {
        var compilation = GeneratorTests.Compile("""
            namespace Game;
            public struct Spot : IComponent { public float X, Y; }
            public struct Aim : IComponent { public EntityId Target; }
            public struct Way : IComponent { public float X; }
            public static class K
            {
                [ForEach] public static void Row(in Spot s, in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots[a.Target].HasValue ? spots[a.Target].Value.X - s.X : 0;
                [ForEach] public static void Seen(in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots.Has(a.Target) ? 1 : 0;
                [ForEach(SimdMode = SimdMode.Require)] public static void Vector(in Spot s, in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots[a.Target].HasValue ? spots[a.Target].Value.X - s.X : 0;
                [ForEach] public static void Written(ref Aim a, ref Way w, Lookup<Spot> spots) { w.X = spots.Has(a.Target) ? 1 : 0; a.Target = default; }
            }
            """);
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string text = string.Concat(output.SyntaxTrees.Where(GeneratorTests.IsCode).Select(t => t.ToString()));
        string Job(string name) => text.Split("internal ref struct ").Single(j => j.StartsWith(name + " "));
        const string add = "global::System.Runtime.CompilerServices.Unsafe.Add";

        // The row form: the call site picks the twin past the cache, today's job otherwise.
        Assert.That(text, Does.Contain("if (global::EasyEcs.Lookup<global::Game.Spot>.Prefetches(world))")
            .And.Contain("new __ForEach.RowJob0Chases { World = world }").And.Contain("new __ForEach.RowJob0 { World = world }"));
        Assert.That(Job("RowJob0Chases"), Does.Contain($".Prefetch({add}(ref r2, i + 16).Target);").And.Contain($".PrefetchRow({add}(ref r2, i + 8).Target);")
            .And.Contain("if (rows > 16)"));
        Assert.That(Job("RowJob0"), Does.Not.Contain("Prefetch"));
        Assert.That(Job("SeenJob1Chases"), Does.Contain($".Prefetch({add}(ref r1, i + 16).Target);").And.Not.Contain(".PrefetchRow("));
        // The vector loop: per vector, behind the gate the job's initializer reads once per pass.
        Assert.That(text, Does.Contain("f3 = global::EasyEcs.Lookup<global::Game.Spot>.Prefetches(world)"));
        Assert.That(Job("VectorJob2"), Does.Contain("if (f3) __p3.PrefetchAhead(ref e2, 2, 1, i, rows, 16, "));
        Assert.That(text, Does.Not.Contain("WrittenJob3Chases"));
        Assert.That(Job("WrittenJob3"), Does.Not.Contain("Prefetch").And.Not.Contain("public bool f"));
    }

    /// <summary>The readers of the cache size on this machine's operating system, and their parsing.</summary>
    [Test]
    public void TheOperatingSystemsCacheSizeIsRead()
    {
        Assert.That(LastLevelCache.ParseSize("266240K\n"), Is.EqualTo(266240L << 10));
        Assert.That(LastLevelCache.ParseSize("32M"), Is.EqualTo(32L << 20));
        Assert.That(LastLevelCache.ParseSize("1G"), Is.EqualTo(1L << 30));
        Assert.That(LastLevelCache.ParseSize("4096"), Is.EqualTo(4096));
        Assert.That(LastLevelCache.ParseSize(""), Is.Zero);
        Assert.That(LastLevelCache.ParseSize("big"), Is.Zero);
        if (!OperatingSystem.IsLinux()) Assert.Ignore("the readers checked here are Linux's");
        long sysfs = LastLevelCache.FromSysfs("/sys/devices/system/cpu/cpu0/cache");
        long sysconf = LastLevelCache.FromSysconf();
        TestContext.Out.WriteLine($"sysfs {sysfs} bytes, sysconf {sysconf} bytes, read {LastLevelCache.Read()} bytes");
        Assert.That(sysfs, Is.GreaterThan(0));
        if (sysconf > 0) Assert.That(sysconf, Is.EqualTo(sysfs));
        Assert.That(LastLevelCache.Read(), Is.EqualTo(sysfs));
    }

    /// <summary>Unknown means off: descriptions that are missing or say no number read as 0, and 0 never prefetches.</summary>
    [Test]
    public void AnUnknownCacheMeansNoPrefetch()
    {
        Assert.That(LastLevelCache.FromSysfs("/nonexistent/cache"), Is.Zero);
        var empty = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(empty.FullName, "index0"));
            Assert.That(LastLevelCache.FromSysfs(empty.FullName), Is.Zero, "an index without a size");
            File.WriteAllText(Path.Combine(empty.FullName, "index0", "size"), "unknown");
            Assert.That(LastLevelCache.FromSysfs(empty.FullName), Is.Zero, "a size that is no number");
        }
        finally { empty.Delete(true); }

        using var world = new World();
        world.CreateEntities(new Entity[100_000], new Beacon(1, 2));
        WithCache(0, () => Assert.That(Lookup<Beacon>.Prefetches(world), Is.False));
    }
}
