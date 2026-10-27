using System;
using System.IO;
using System.Linq;
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

    // In parallel the twin is a batch job over each chunk's runs of rows (ForEachBatchParallel), today's job a per-row one.
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

    // Relation targets: a chase per element of a buffer of links (foreach, and the indexer).
    [ForEach]
    public static void Party(in DynamicBuffer<MemberOf> links, ref Course course, Lookup<Beacon> beacons)
    {
        float x = 0;
        foreach (var link in links)
        {
            var at = beacons[link.Target];
            if (at.HasValue) x += at.Value.X;
        }
        course.X = x;
    }

    [ForEach]
    public static void PartyIndexed(in DynamicBuffer<MemberOf> links, ref Course course, Lookup<Beacon> beacons)
    {
        float y = 0;
        for (int j = 0; j < links.Count; j++)
            if (beacons.Has(links[j].Target)) y += beacons.Read(links[j].Target).Y;
        course.Y = y;
    }

    // Index probes at a key of an in component, one per representation: a hashed unique key, a direct-indexed key, a
    // hashed key many entities share (a managed key: three stages) and a relation's target (its slot by index).
    [ForEach]
    public static void FindLogin(in Seeker seeker, ref Answer answer, Lookup<Login> logins) =>
        answer.A = logins.TryFind(seeker.Login, out var found) ? found.Id.Index : -1;

    [ForEach]
    public static void FindCell(in Seeker seeker, ref Answer answer, Lookup<GridCell> cells) =>
        answer.B = cells.TryFind(seeker.Cell, out var found) ? found.Id.Index : -1;

    [ForEach]
    public static void CountSigns(in Seeker seeker, ref Answer answer, Lookup<Callsign> signs) =>
        answer.C = signs.Count(seeker.Sign) * 1000 + (signs.TryFind(seeker.Sign, out var found) ? found.Id.Index : -1);

    [ForEach]
    public static void FindFollowers(in Seeker seeker, ref Answer answer, Lookup<Follows> follows)
    {
        int sum = 0;
        foreach (var follower in follows.Find(seeker.Of)) sum += follower.Id.Index;
        answer.D = sum + follows.Count(seeker.Of);
    }

    [ForEach(Parallel = true)]
    public static void FindLoginParallel(in Seeker seeker, ref Answer answer, Lookup<Login> logins) =>
        answer.A = logins.TryFind(seeker.Login, out var found) ? found.Id.Index + 1 : -2;

    [ForEach]
    public static void ClearAnswers(ref Answer answer) => answer = default;
}

/// <summary>The keys a seeker finds entities by, one per index representation.</summary>
public record struct Seeker(int Login, long Cell, string Sign, EntityId Of) : IComponent;
public record struct Answer(int A, int B, int C, int D) : IComponent;

/// <summary>A chase in a walk two methods share (fusion): the walk's twin runs the chasing method's twin job.</summary>
[EcsSystem]
public ref struct ChaseWalkSystem
{
    [ForEach]
    static void Aim(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons) =>
        course = beacons.Has(tracker.Target) ? new Course(beacons.Read(tracker.Target).X - beacon.X, 0) : default;

    [ForEach]
    static void Lean(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons) =>
        course.Y = beacons[tracker.Target].HasValue ? beacons[tracker.Target].Value.Y + course.X - beacon.Y : -1;
}

/// <summary>Two systems in one walk (declared one after the other): the pair's twin runs the chasing method's twin.</summary>
[EcsSystem]
public ref struct ChasePairFirstSystem
{
    [ForEach]
    static void Aim(in Beacon beacon, in Tracker tracker, ref Course course, Lookup<Beacon> beacons) =>
        course = beacons.Has(tracker.Target) ? new Course(beacons.Read(tracker.Target).X - beacon.X, 0) : default;
}

[EcsSystem]
public ref struct ChasePairSecondSystem
{
    [ForEach]
    static void Bump(in Beacon beacon, ref Course course) => course.Y += beacon.Y + 1;
}

public class PrefetchTests
{
    /// <summary>
    /// Runs <paramref name="body"/> with the last-level cache taken as <paramref name="bytes"/> and the L2 as
    /// <paramref name="l2"/> (the same when not given: 0 and 1 turn every form's gate off and on), then restores both.
    /// </summary>
    private static void WithCache(long bytes, Action body, long? l2 = null)
    {
        long was = CacheSizes.LastLevel, wasL2 = CacheSizes.L2;
        CacheSizes.LastLevel = bytes;
        CacheSizes.L2 = l2 ?? bytes;
        try { body(); }
        finally
        {
            CacheSizes.LastLevel = was;
            CacheSizes.L2 = wasL2;
        }
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
    /// The last level's gate (a vector loop's chases): a pass prefetches only where a prefetch exists (x86, ARM64 with
    /// SVE) and when every live entity's record and row of T (16 + size bytes each) are past the last-level cache; an
    /// unknown cache (0) never.
    /// </summary>
    [Test]
    public void AVectorLoopsChasesPrefetchOnlyPastTheLastLevelCache()
    {
        using var world = new World();
        world.CreateEntities(new Entity[1_000], new Beacon(1, 2));
        int bytes = 1_000 * (16 + 8);
        WithCache(bytes - 1, () => Assert.That(Lookup<Beacon>.PrefetchesPastLastLevel(world), Is.EqualTo(LookupLanes.CanPrefetch), "past the cache"));
        WithCache(bytes, () => Assert.That(Lookup<Beacon>.PrefetchesPastLastLevel(world), Is.False, "in the cache"));
        WithCache(0, () => Assert.That(Lookup<Beacon>.PrefetchesPastLastLevel(world), Is.False, "an unknown cache"));
        Assert.That(LookupLanes.PastCache(1L << 40, 0), Is.False);
        Assert.That(LookupLanes.PastCache(33L << 20, 32L << 20), Is.True);
    }

    /// <summary>
    /// A gate per form: index probes (parallel ones too) and vector loops' chases past the last-level cache, the forms
    /// that pay past the L2 (the row form's chases, DestroyEntities' id list) past one core's L2; an unknown L2
    /// falls back to the last-level gate (never more eager than it), an unknown last level with it keeps every form off.
    /// </summary>
    [Test]
    public void EachFormHasItsCache()
    {
        using var world = new World();
        world.CreateEntities(new Entity[1_000], new Beacon(1, 2));
        using var keys = Seekers(10, 1_000);
        bool can = LookupLanes.CanPrefetch;
        long bytes = 1_000 * (16 + 8);
        // Between the L2 and the last level: only the forms gated on the L2 prefetch.
        WithCache(1L << 40, () =>
        {
            Assert.That(Lookup<Beacon>.PrefetchesPastLastLevel(world), Is.False, "a vector loop's chase in the last level");
            Assert.That(Lookup<Beacon>.PrefetchesPastL2(world), Is.EqualTo(can), "a row form's chase past the L2");
            Assert.That(LookupExtensions.Probes<Login, int>(keys), Is.False, "a probe in the last level");
            Assert.That(LookupLanes.PrefetchesIds(world, 100), Is.EqualTo(can), "an id list past the L2");
            Assert.That(LookupLanes.PrefetchesIds(world, LookupLanes.IdsAhead), Is.False, "a list no longer than the distance");
        }, l2: 1);
        // The L2 gate compares with the L2's size: in it, off.
        WithCache(1L << 40, () => Assert.That(Lookup<Beacon>.PrefetchesPastL2(world), Is.False, "in the L2"), l2: bytes);
        WithCache(1L << 40, () => Assert.That(Lookup<Beacon>.PrefetchesPastL2(world), Is.EqualTo(can), "past the L2"), l2: bytes - 1);
        // An unknown L2: the last level's gate.
        WithCache(1L << 40, () =>
        {
            Assert.That(Lookup<Beacon>.PrefetchesPastL2(world), Is.False, "an unknown L2 in the last level");
            Assert.That(LookupLanes.PrefetchesIds(world, 100), Is.False);
        }, l2: 0);
        WithCache(1, () => Assert.That(Lookup<Beacon>.PrefetchesPastL2(world), Is.EqualTo(can), "an unknown L2 past the last level"), l2: 0);
        WithCache(0, () =>
        {
            Assert.That(Lookup<Beacon>.PrefetchesPastL2(world), Is.False, "both unknown");
            Assert.That(LookupLanes.PrefetchesIds(world, 100), Is.False);
        }, l2: 0);
        Assert.That(LookupLanes.PastCache(16L << 20, 32L << 20), Is.False);
    }

    /// <summary>
    /// What the generator writes: the row form's twin job prefetching its chases (the row stage only for a read, not for
    /// Has), picked by the pass past the cache; the vector loop's prefetches per vector behind the gate its job reads once
    /// per pass; none for an id the method may write; a method sharing a walk runs its twin in the walk's twin.
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
            [EcsSystem] public ref struct Walk
            {
                [ForEach(SimdMode = SimdMode.Require)] static void Gathered(in Spot s, in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots.Has(a.Target) ? spots.Read(a.Target).X - s.X : 0;
                [ForEach] static void Plain(in Spot s, in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots.Has(a.Target) ? spots.Read(a.Target).X - s.X : 0;
            }
            """);
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error || d.Id == "CS0649"), Is.Empty);
        string text = string.Concat(output.SyntaxTrees.Where(GeneratorTests.IsCode).Select(t => t.ToString()));
        string Job(string name) => text.Split("internal ref struct ").Single(j => j.StartsWith(name + " "));
        const string add = "global::System.Runtime.CompilerServices.Unsafe.Add";

        // The row form: the call site picks the twin past the L2 (its form's gate), today's job otherwise.
        Assert.That(text, Does.Contain("if (global::EasyEcs.Lookup<global::Game.Spot>.PrefetchesPastL2(world))")
            .And.Contain("new __ForEach.RowJob0Chases { World = world }").And.Contain("new __ForEach.RowJob0 { World = world }"));
        Assert.That(Job("RowJob0Chases"), Does.Contain($".Prefetch({add}(ref r2, i + 16).Target);").And.Contain($".PrefetchRow({add}(ref r2, i + 8).Target);")
            .And.Contain("if (rows > 16)"));
        Assert.That(Job("RowJob0"), Does.Not.Contain("Prefetch"));
        Assert.That(Job("SeenJob1Chases"), Does.Contain($".Prefetch({add}(ref r1, i + 16).Target);").And.Not.Contain(".PrefetchRow("));
        // The vector loop: per vector, behind the gate the job's initializer reads once per pass.
        Assert.That(text, Does.Contain("f3 = global::EasyEcs.Lookup<global::Game.Spot>.PrefetchesPastLastLevel(world)"));
        Assert.That(Job("VectorJob2"), Does.Contain("if (f3) __p3.PrefetchAhead(ref e2, 2, 1, i, rows, 16, "));
        Assert.That(text, Does.Not.Contain("WrittenJob3Chases"));
        Assert.That(Job("WrittenJob3"), Does.Not.Contain("Prefetch").And.Not.Contain("public bool f"));
        // A method sharing a walk with others (fusion) runs its twin in the walk's twin.
        Assert.That(text, Does.Contain("PlainJob1Chases").And.Contain("Gathered_Plain_Fused0Chases"));
    }

    /// <summary>
    /// Seekers holding keys of every index representation (a hashed unique key, a direct-indexed key, a string many
    /// entities share, a relation's target), some found, some missing, null, out of a direct index's range or of a
    /// destroyed entity; <paramref name="seekers"/> rows in runs shorter and longer than the prefetch distance.
    /// </summary>
    private static World Seekers(int seekers, int keys)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var random = new Random(seekers * 17 + keys);
        var targets = new Entity[Math.Max(keys, 1)];
        world.CreateEntities(targets, new Beacon(0, 0));
        for (int i = 0; i < keys; i++)
        {
            world.CreateEntity(new Login(i * 3));
            world.CreateEntity(new GridCell(i % 64, i / 64));
            world.CreateEntity(new Callsign("s" + (i % 37)));
            world.CreateEntity(new Follows(targets[random.Next(targets.Length)].Id));
        }

        targets[0].Destroy();
        for (int i = 0; i < seekers; i++)
        {
            var of = random.Next(10) == 0 ? default : targets[random.Next(targets.Length)].Id;
            string sign = random.Next(10) switch { 0 => null, 1 => "none", _ => "s" + random.Next(40) };
            long cell = random.Next(12) == 0 ? -1L << 32 : ((long)random.Next(70) << 32) | (uint)random.Next(keys / 64 + 2);
            world.CreateEntity(new Seeker(random.Next(keys * 3 + 5), cell, sign, of), new Answer());
        }

        return world;
    }

    private static Answer[] Answers(World world, Action<World> pass)
    {
        ChaseKernels.ClearAnswers(world);
        pass(world);
        var answers = new Answer[world.EntityCount];
        int n = 0;
        foreach (var item in world.Query<Answer>()) answers[n++] = item.Answer;
        return answers.AsSpan(0, n).ToArray();
    }

    private static void ProbeAll(World world)
    {
        ChaseKernels.FindLogin(world);
        ChaseKernels.FindCell(world);
        ChaseKernels.CountSigns(world);
        ChaseKernels.FindFollowers(world);
    }

    /// <summary>
    /// Prefetched probes find what today's do, for every representation, bit for bit with the gate on and off; after
    /// the index grows (its hash's arrays replaced between passes) too.
    /// </summary>
    [TestCase(0, 10)]
    [TestCase(7, 10)]
    [TestCase(17, 100)]
    [TestCase(1_003, 500)]
    [TestCase(5_000, 3_000)]
    public void PrefetchedProbesFindWhatTodaysDo(int seekers, int keys)
    {
        using var world = Seekers(seekers, keys);
        foreach (var (name, pass) in new (string, Action<World>)[]
                 {
                     ("probes", ProbeAll),
                     ("parallel", w => ChaseKernels.FindLoginParallel(w)),
                     ("lambda", w => w.ForEach(static (in Seeker s, ref Answer a, Lookup<Callsign> signs) =>
                         a.C = signs.TryFind(s.Sign, out var found) ? found.Id.Index : -1)),
                 })
        {
            Answer[] off = null, on = null;
            WithCache(0, () => off = Answers(world, pass));
            WithCache(1, () => on = Answers(world, pass));
            Assert.That(on, Is.EqualTo(off), name);
        }

        // The index grows: its dictionaries resize, the cached entries go with the old buckets.
        world.Reserve<Login, Callsign>(keys * 2);
        for (int i = 0; i < keys * 2; i++) world.CreateEntity(new Login(1_000_000 + i), new Callsign("g" + i));
        Answer[] grownOff = null, grownOn = null;
        WithCache(0, () => grownOff = Answers(world, ProbeAll));
        WithCache(1, () => grownOn = Answers(world, ProbeAll));
        Assert.That(grownOn, Is.EqualTo(grownOff), "after growth");
    }

    /// <summary>The probe gate: past the cache only (the index's slots, buckets, entries and lists of ids), never unknown.</summary>
    [Test]
    public void ProbesPrefetchOnlyPastTheLastLevelCache()
    {
        using var world = Seekers(10, 1_000);
        bool can = LookupLanes.CanPrefetch;
        WithCache(1, () =>
        {
            Assert.That(LookupExtensions.Probes<Login, int>(world), Is.EqualTo(can), "hashed");
            Assert.That(LookupExtensions.Probes<GridCell, long>(world), Is.EqualTo(can), "direct");
            Assert.That(LookupExtensions.Probes<Callsign, string>(world), Is.EqualTo(can), "shared keys");
            Assert.That(LookupExtensions.Probes<Follows, EntityId>(world), Is.EqualTo(can), "relation");
        });
        WithCache(64L << 20, () => Assert.That(LookupExtensions.Probes<Login, int>(world), Is.False, "in the cache"));
        WithCache(0, () => Assert.That(LookupExtensions.Probes<Callsign, string>(world), Is.False, "an unknown cache"));
    }

    /// <summary>
    /// A hashed probe's lines are the dictionary's own: stage 1 is the key's bucket, stage 2 the entry it names, which
    /// holds the key's value whenever the key heads its bucket's chain (most keys: a chain's newest entry heads it).
    /// The private layout read through UnsafeAccessor and reflection agrees with the runtime's dictionary.
    /// </summary>
    [TestCase(1_000)]
    [TestCase(70_001)]
    public unsafe void HashedProbesPrefetchTheDictionarysOwnLines(int keys)
    {
        using var world = new World();
        world.Reserve<Login, Callsign>(keys);
        for (int i = 0; i < keys; i++) world.CreateEntity(new Login(i * 7 - 3), new Callsign("k" + i % (keys / 3 + 1)));
        foreach (var (name, check) in new (string, Func<double>)[]
                 {
                     ("unique int keys", () => Heads<Login, int>(world, i => i * 7 - 3, keys)),
                     ("shared string keys", () => Heads<Callsign, string>(world, i => "k" + i, keys / 3 + 1)),
                 })
            Assert.That(check(), Is.GreaterThan(0.5), name);

        static double Heads<T, TKey>(World world, Func<int, TKey> key, int count) where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
        {
            var index = (ValueIndex<T, TKey>)LookupExtensions.Prepared<T, TKey>(world);
            var heads = (System.Collections.Generic.Dictionary<TKey, IndexHead>)typeof(ValueIndex<T, TKey>)
                .GetField("_heads", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(index)!;
            Assert.That(HashSlots<TKey, IndexHead>.Works, Is.True, "the dictionary's fields are where they are read");
            int stride = HashSlots<TKey, IndexHead>.Stride(heads, out var buckets, out var entries, out _);
            Assert.That(stride, Is.GreaterThanOrEqualTo(System.Runtime.CompilerServices.Unsafe.SizeOf<IndexHead>()));
            WithCache(1, () => index.Probes()); // reads the arrays the stages use
            var all = new TKey[count];
            for (int i = 0; i < count; i++) all[i] = key(i);
            int headed = 0, wrong = 0;
            for (int i = 0; i < count; i++) // nothing allocates here: no collection moves the arrays between reads
            {
                var k = all[i];
                byte* bucket = (byte*)index.Address(k, 1), entry = (byte*)index.Address(k, 2);
                fixed (int* first = buckets)
                    if (bucket < (byte*)first || bucket >= (byte*)(first + buckets.Length) || entry == null) wrong++; // stage 1 is a bucket naming an entry
                byte* value = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(heads, k));
                if (value >= entry && value < entry + stride) headed++;
            }

            GC.KeepAlive(entries);
            Assert.That(wrong, Is.Zero, "every key's bucket, naming an entry");
            return (double)headed / count;
        }
    }

    /// <summary>Links to beacons (some destroyed, some without Beacon): a chase per element, bit for bit.</summary>
    [TestCase(0)]
    [TestCase(9)]
    [TestCase(2_001)]
    public void PrefetchedBufferChasesComputeWhatTodaysDo(int members)
    {
        using var world = new World();
        var random = new Random(members);
        var beacons = new Entity[50];
        world.CreateEntities(beacons, new Beacon(0, 0));
        for (int i = 0; i < beacons.Length; i++) beacons[i].Get<Beacon>() = new Beacon(i * 1.5f, -i);
        var bare = world.CreateEntity();
        for (int i = 0; i < members; i++)
        {
            var member = world.CreateEntity(new Course());
            int links = random.Next(5);
            for (int j = 0; j < links; j++) member.Link(new MemberOf(random.Next(20) == 0 ? bare.Id : beacons[random.Next(beacons.Length)].Id));
        }
        beacons[3].Destroy();

        foreach (var (name, pass) in new (string, Action<World>)[] { ("foreach", w => ChaseKernels.Party(w)), ("indexer", w => ChaseKernels.PartyIndexed(w)) })
        {
            Course[] off = null, on = null;
            WithCache(0, () => off = Run(world, pass));
            WithCache(1, () => on = Run(world, pass));
            KernelLookupTests.AssertSame(off, on, name);
        }
    }

    /// <summary>
    /// Walks two methods share (fusion within a system, and a pair of systems in order) and a lambda pass run the
    /// chasing method's twin past the cache: bit for bit with the gate on and off.
    /// </summary>
    [TestCase(17, false)]
    [TestCase(3_001, true)]
    public void PrefetchingWalksAndLambdasComputeWhatTodaysDo(int trackers, bool odd)
    {
        foreach (var (name, setup, pass) in new (string, Action<World>, Action<World>)[]
                 {
                     ("fused", w => w.AddSystem<ChaseWalkSystem>(), w => w.Update()),
                     ("pair", w => { w.AddSystem<ChasePairFirstSystem>(); w.AddSystem<ChasePairSecondSystem>(); }, w => w.Update()),
                     ("lambda", null, w => w.ForEach(static (in Beacon b, in Tracker t, ref Course c, Lookup<Beacon> beacons) =>
                         c = beacons.Has(t.Target) ? new Course(beacons.Read(t.Target).X - b.X, b.Y) : new Course(-1, -1))),
                 })
        {
            using var world = KernelLookupTests.Build(trackers, 3, 0, odd, threads: 4);
            setup?.Invoke(world);
            Course[] off = null, on = null;
            WithCache(0, () => off = Run(world, pass));
            WithCache(1, () => on = Run(world, pass));
            KernelLookupTests.AssertSame(off, on, name);
        }
    }

    [Test]
    public void PrefetchingProbesBuffersWalksAndLambdasAllocateNothing()
    {
        using var seekers = Seekers(4_000, 2_000);
        using var walk = KernelLookupTests.Build(4_000, 3, 0, true, threads: 4);
        walk.AddSystem<ChaseWalkSystem>();
        WithCache(1, () =>
        {
            void Frame()
            {
                ProbeAll(seekers);
                ChaseKernels.FindLoginParallel(seekers);
                walk.Update();
                walk.ForEach(static (in Beacon b, in Tracker t, ref Course c, Lookup<Beacon> beacons) =>
                    c = beacons.Has(t.Target) ? new Course(beacons.Read(t.Target).X - b.X, b.Y) : default);
            }

            for (int i = 0; i < 50; i++) Frame();
            Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) Frame(); }), Is.Zero);
        });
    }

    /// <summary>
    /// What the generator writes for the new chases: probes staged by their representation, a chase per buffer element,
    /// the parallel twin as a batch job per chunk, the fused walk's twin and the lambda pass's gate; no unused twin
    /// (CS0649).
    /// </summary>
    [Test]
    public void ProbesBuffersParallelWalksAndLambdasPrefetchBehindTheGate()
    {
        var compilation = GeneratorTests.Compile("""
            namespace Game;
            public struct Spot : IComponent { public float X; }
            public struct Aim : IComponent { public EntityId Target; }
            public struct Way : IComponent { public float X; }
            public record struct Name(string Value) : IComponent, IIndexed<string> { public string Key => Value; }
            public record struct Code(int Value) : IComponent, IUniqueIndexed<int> { public int Key => Value; }
            public struct Wants : IComponent { public string Name; public int Code; }
            public record struct Link(EntityId Target) : IRelation;
            public static class K
            {
                [ForEach] public static void Shared(in Wants w, ref Way way, Lookup<Name> names) => way.X = names.Count(w.Name);
                [ForEach] public static void Unique(in Wants w, ref Way way, Lookup<Code> codes) => way.X = codes.TryFind(w.Code, out var e) ? e.Id.Index : 0;
                [ForEach] public static void Links(in DynamicBuffer<Link> links, ref Way way, Lookup<Spot> spots) { foreach (var l in links) if (spots.Has(l.Target)) way.X += spots.Read(l.Target).X; }
                [ForEach(Parallel = true)] public static void Wide(in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots.Has(a.Target) ? spots.Read(a.Target).X : 0;
                [ForEach(Parallel = true)] public static void UniqueParallel(in Wants w, ref Way way, Lookup<Code> codes) => way.X = codes.TryFind(w.Code, out var e) ? e.Id.Index : 0;
            }
            [EcsSystem] public ref struct Walk
            {
                [ForEach] static void First(in Spot s, in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots.Has(a.Target) ? spots.Read(a.Target).X - s.X : 0;
                [ForEach] static void Second(in Spot s, in Aim a, ref Way w) => w.X += s.X * a.Target.Index;
            }
            public static class L
            {
                public static void Run(World world)
                {
                    world.ForEach(static (in Aim a, ref Way w, Lookup<Spot> spots) => w.X = spots.Has(a.Target) ? 1 : 0);
                    world.ForEach(static (in Wants w, ref Way way, Lookup<Code> codes) => way.X = codes.TryFind(w.Code, out var e) ? e.Id.Index : 0);
                }
            }
            """);
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error || d.Id == "CS0649"), Is.Empty);
        string text = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        string Job(string name) => text.Split("internal ref struct ").Single(j => j.StartsWith(name + " "));

        // Probes, of a key many entities share and of a unique key: the bucket 16 rows ahead, its entry 8 ahead.
        Assert.That(text, Does.Contain("if (global::EasyEcs.LookupExtensions.Probes<global::Game.Name, string>(world))"));
        Assert.That(Job("SharedJob0Chases"), Does.Contain("PrefetchKey<global::Game.Name, string>(").And.Contain("i + 16).Name, 1);").And.Contain("i + 8).Name, 2);"));
        Assert.That(Job("UniqueJob1Chases"), Does.Contain("i + 16).Code, 1);").And.Contain("i + 8).Code, 2);").And.Not.Contain(", 3);"));
        // A chase per element of a buffer.
        Assert.That(Job("LinksJob2Chases"), Does.Contain("foreach (ref readonly var __link in "));
        // The parallel twin: a batch job over each chunk's runs of rows.
        Assert.That(text, Does.Contain("WideJob3Chases").And.Contain("ForEachBatchParallel"));
        // The fused walk's twin, picked past the cache before today's walk.
        Assert.That(text, Does.Contain("First_Second_Fused0Chases").And.Contain("FirstJob0Chases"));
        // A gate per form: probes (in parallel too) and a vector loop's chases past the last level; the row form's chases
        // (a buffer's, in parallel, in a fused walk) past the L2.
        Assert.That(text, Does.Contain("if (global::EasyEcs.LookupExtensions.Probes<global::Game.Code, int>(world))")
            .And.Contain("UniqueParallelJob4Chases"));
        Assert.That(text, Does.Contain("if (global::EasyEcs.Lookup<global::Game.Spot>.PrefetchesPastL2(world))"), "the row form's chases");
        Assert.That(text, Does.Not.Contain("if (global::EasyEcs.Lookup<global::Game.Spot>.PrefetchesPastLastLevel(world))"), "no row form on the last level's gate");
        // Lambda passes: the vector loop's gate in its job, the row form's twin picked at the interceptor.
        Assert.That(text, Does.Contain("f2 = global::EasyEcs.Lookup<global::Game.Spot>.PrefetchesPastLastLevel(__world)"));
        Assert.That(text, Does.Contain("if (global::EasyEcs.LookupExtensions.Probes<global::Game.Code, int>(__world))").And.Contain("LambdaJob1Chases"));
    }

    /// <summary>The readers of the cache size on this machine's operating system, and their parsing.</summary>
    [Test]
    public void TheOperatingSystemsCacheSizeIsRead()
    {
        Assert.That(CacheSizes.ParseSize("266240K\n"), Is.EqualTo(266240L << 10));
        Assert.That(CacheSizes.ParseSize("32M"), Is.EqualTo(32L << 20));
        Assert.That(CacheSizes.ParseSize("1G"), Is.EqualTo(1L << 30));
        Assert.That(CacheSizes.ParseSize("4096"), Is.EqualTo(4096));
        Assert.That(CacheSizes.ParseSize(""), Is.Zero);
        Assert.That(CacheSizes.ParseSize("big"), Is.Zero);
        if (!OperatingSystem.IsLinux()) Assert.Ignore("the readers checked here are Linux's");
        long sysfs = CacheSizes.FromSysfs("/sys/devices/system/cpu/cpu0/cache");
        long sysconf = CacheSizes.FromSysconf();
        long l2 = CacheSizes.FromSysfs("/sys/devices/system/cpu/cpu0/cache", 2), l2Sysconf = CacheSizes.FromSysconf(2);
        TestContext.Out.WriteLine($"sysfs {sysfs} bytes, sysconf {sysconf} bytes, read {CacheSizes.ReadLastLevel()} bytes; L2 sysfs {l2}, sysconf {l2Sysconf}, read {CacheSizes.ReadL2()}");
        Assert.That(sysfs, Is.GreaterThan(0));
        if (sysconf > 0) Assert.That(sysconf, Is.EqualTo(sysfs));
        Assert.That(CacheSizes.ReadLastLevel(), Is.EqualTo(sysfs));
        Assert.That(l2, Is.GreaterThan(0).And.LessThanOrEqualTo(sysfs), "an L2 no larger than the last level");
        if (l2Sysconf > 0) Assert.That(l2Sysconf, Is.EqualTo(l2));
        Assert.That(CacheSizes.ReadL2(), Is.EqualTo(l2));
    }

    /// <summary>
    /// The prefetch each machine issues: x86's PREFETCHT0, ARM64's SVE PRFB where SVE is hardware (the CI arm64 job's
    /// Neoverse runners; qemu's neoverse-n2 / v1), nothing on ARM64 without it (Apple silicon), so its gates stay off
    /// without reading the cache size. The gate forced on runs the SVE call in the tests above.
    /// </summary>
    [Test]
    public unsafe void ThePrefetchIsTheMachines()
    {
#pragma warning disable SYSLIB5003 // Sve: IsSupported only
        bool sve = System.Runtime.Intrinsics.Arm.Sve.IsSupported;
#pragma warning restore SYSLIB5003
        TestContext.Out.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}: SSE {System.Runtime.Intrinsics.X86.Sse.IsSupported}, SVE {sve}, prefetch {Prefetching.IsSupported}, last-level cache {CacheSizes.LastLevel} bytes, L2 {CacheSizes.L2} bytes");
        Assert.That(Prefetching.IsSupported, Is.EqualTo(System.Runtime.Intrinsics.X86.Sse.IsSupported || sve));
        Assert.That(CacheSizes.LastLevel == 0 && CacheSizes.L2 == 0 || Prefetching.IsSupported, Is.True, "the sizes are read only where a prefetch exists");
        long line = 0;
        Prefetching.Line(&line); // never faults, changes nothing
        Prefetching.Line(null);
        Assert.That(line, Is.Zero);
    }

    /// <summary>Unknown means off: descriptions that are missing or say no number read as 0, and 0 never prefetches.</summary>
    [Test]
    public void AnUnknownCacheMeansNoPrefetch()
    {
        Assert.That(CacheSizes.FromSysfs("/nonexistent/cache"), Is.Zero);
        Assert.That(CacheSizes.FromSysfs("/nonexistent/cache", 2), Is.Zero);
        var empty = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(empty.FullName, "index0"));
            Assert.That(CacheSizes.FromSysfs(empty.FullName), Is.Zero, "an index without a size");
            File.WriteAllText(Path.Combine(empty.FullName, "index0", "size"), "unknown");
            Assert.That(CacheSizes.FromSysfs(empty.FullName), Is.Zero, "a size that is no number");
            // The L2 is the level-2 data or unified cache: not the level-2 instruction cache, not one without a level.
            File.WriteAllText(Path.Combine(empty.FullName, "index0", "size"), "64K");
            File.WriteAllText(Path.Combine(empty.FullName, "index0", "level"), "2\n");
            File.WriteAllText(Path.Combine(empty.FullName, "index0", "type"), "Instruction\n");
            Assert.That(CacheSizes.FromSysfs(empty.FullName, 2), Is.Zero, "an instruction cache");
            Directory.CreateDirectory(Path.Combine(empty.FullName, "index1"));
            File.WriteAllText(Path.Combine(empty.FullName, "index1", "size"), "1024K");
            Assert.That(CacheSizes.FromSysfs(empty.FullName, 2), Is.Zero, "a cache without a level");
            File.WriteAllText(Path.Combine(empty.FullName, "index1", "level"), "2\n");
            File.WriteAllText(Path.Combine(empty.FullName, "index1", "type"), "Unified\n");
            Directory.CreateDirectory(Path.Combine(empty.FullName, "index2"));
            File.WriteAllText(Path.Combine(empty.FullName, "index2", "size"), "32M");
            File.WriteAllText(Path.Combine(empty.FullName, "index2", "level"), "3\n");
            Assert.That(CacheSizes.FromSysfs(empty.FullName, 2), Is.EqualTo(1L << 20), "the level-2 unified cache");
            Assert.That(CacheSizes.FromSysfs(empty.FullName), Is.EqualTo(32L << 20), "the last level: the largest");
        }
        finally { empty.Delete(true); }

        using var world = new World();
        world.CreateEntities(new Entity[100_000], new Beacon(1, 2));
        WithCache(0, () => Assert.That(Lookup<Beacon>.PrefetchesPastLastLevel(world), Is.False));
    }
}
