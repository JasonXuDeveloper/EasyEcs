using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Pass fusion: consecutive [ForEach] methods of a system that visit the same rows run as one walk. Each fused system has
/// a twin whose methods run apart (<see cref="FusionMode.Off"/>); both run on equal worlds and must leave equal values,
/// change ticks and per-pass results.
/// </summary>
public partial class FusionTests
{
    public record struct Locus(float X, float Y) : IComponent;
    public record struct Drift(float X, float Y) : IComponent;
    public record struct Stamina(int Value, int Max) : IComponent, ITracked;
    public record struct Venom(int Dose) : IComponent;
    public record struct Blows(int Count) : IComponent, ITracked;
    public record struct Quarry(EntityId Target) : IComponent;
    public struct Dozing : IComponent;

    public struct StaminaSum : ISingletonComponent, IReduction<StaminaSum>
    {
        public long Total;
        public int Rows;

        public static StaminaSum Identity => default;
        public static StaminaSum Combine(StaminaSum left, StaminaSum right) => new() { Total = left.Total + right.Total, Rows = left.Rows + right.Rows };
    }

    public record struct Fence(float Size) : ISingletonComponent;
    public record struct Faint(EntityId Who, int Value);

    // ---------------- fused systems and their twins ----------------

    /// <summary>Integrate, then bounce off the bounds: two vector loops over the same rows, per block.</summary>
    [EcsSystem]
    public ref struct Motion
    {
        [ForEach, Without<Dozing>]
        static void Integrate(ref Locus p, in Drift v) => p = new Locus(p.X + v.X, p.Y + v.Y);

        [ForEach, Without<Dozing>]
        static void Bounce(in Locus p, ref Drift v, in Fence bounds)
        {
            if (p.X < 0 || p.X > bounds.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > bounds.Size) v = v with { Y = -v.Y };
        }
    }

    [EcsSystem]
    public ref struct MotionApart
    {
        [ForEach, Without<Dozing>]
        static void Integrate(ref Locus p, in Drift v) => p = new Locus(p.X + v.X, p.Y + v.Y);

        [ForEach(FusionMode = FusionMode.Off), Without<Dozing>]
        static void Bounce(in Locus p, ref Drift v, in Fence bounds)
        {
            if (p.X < 0 || p.X > bounds.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > bounds.Size) v = v with { Y = -v.Y };
        }
    }

    /// <summary>Venom, then regenerate, then count: three scalar methods over the same rows, per row; a tracked write.</summary>
    [EcsSystem]
    public ref struct Vitals
    {
        [ForEach(SimdMode = SimdMode.Off), With<Venom>]
        static void Decay(ref Stamina stamina, Entity unit) => stamina = stamina with { Value = stamina.Value - (int)(unit.Id.Index % 3) };

        [ForEach, With<Venom>]
        static void Regenerate(ref Stamina stamina) => stamina = stamina with { Value = Math.Min(stamina.Max, stamina.Value + 2) };

        [ForEach]
        static void Count(in Stamina stamina, ref Venom poison) { if (stamina.Value < stamina.Max) poison = poison with { Dose = poison.Dose + 1 }; }
    }

    [EcsSystem]
    public ref struct VitalsApart
    {
        [ForEach(SimdMode = SimdMode.Off), With<Venom>]
        static void Decay(ref Stamina stamina, Entity unit) => stamina = stamina with { Value = stamina.Value - (int)(unit.Id.Index % 3) };

        [ForEach(FusionMode = FusionMode.Off), With<Venom>]
        static void Regenerate(ref Stamina stamina) => stamina = stamina with { Value = Math.Min(stamina.Max, stamina.Value + 2) };

        [ForEach(FusionMode = FusionMode.Off)]
        static void Count(in Stamina stamina, ref Venom poison) { if (stamina.Value < stamina.Max) poison = poison with { Dose = poison.Dose + 1 }; }
    }

    /// <summary>Rows written since the last run (a word of which some rows pass): a vector loop and a scalar method per word.</summary>
    [EcsSystem]
    public ref struct Watch
    {
        [ForEach, Changed<Stamina>, With<Venom>]
        static void Hit(in Stamina stamina, ref Blows hits) => hits = new Blows(hits.Count + stamina.Value);

        [ForEach, Changed<Stamina>]
        static void Note(in Stamina stamina, ref Venom poison, Mut<Blows> hits)
        {
            if (stamina.Value % 7 == 0) hits.Write() = new Blows(hits.Value.Count + 1000);
            poison = new Venom(poison.Dose ^ stamina.Value);
        }
    }

    [EcsSystem]
    public ref struct WatchApart
    {
        [ForEach, Changed<Stamina>, With<Venom>]
        static void Hit(in Stamina stamina, ref Blows hits) => hits = new Blows(hits.Count + stamina.Value);

        [ForEach(FusionMode = FusionMode.Off), Changed<Stamina>]
        static void Note(in Stamina stamina, ref Venom poison, Mut<Blows> hits)
        {
            if (stamina.Value % 7 == 0) hits.Write() = new Blows(hits.Value.Count + 1000);
            poison = new Venom(poison.Dose ^ stamina.Value);
        }
    }

    /// <summary>In parallel: a sum of what the first method leaves, records of the second, and a write.</summary>
    [EcsSystem]
    public ref struct Roster
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Weaken(ref Stamina stamina) => stamina = stamina with { Value = stamina.Value - 1 };

        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Sum(in Stamina stamina, ref Reduce<StaminaSum> sum) => sum.Value = new StaminaSum { Total = sum.Value.Total + stamina.Value, Rows = sum.Value.Rows + 1 };

        [ForEach(Parallel = true, ChunkRows = 64, SimdMode = SimdMode.Off)]
        static void Report(Entity unit, in Stamina stamina, Emit<Faint> low) { if (stamina.Value < 10) low.Add(new Faint(unit.Id, stamina.Value)); }
    }

    [EcsSystem]
    public ref struct CensusApart
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Weaken(ref Stamina stamina) => stamina = stamina with { Value = stamina.Value - 1 };

        [ForEach(Parallel = true, ChunkRows = 64, FusionMode = FusionMode.Off)]
        static void Sum(in Stamina stamina, ref Reduce<StaminaSum> sum) => sum.Value = new StaminaSum { Total = sum.Value.Total + stamina.Value, Rows = sum.Value.Rows + 1 };

        [ForEach(Parallel = true, ChunkRows = 64, FusionMode = FusionMode.Off, SimdMode = SimdMode.Off)]
        static void Report(Entity unit, in Stamina stamina, Emit<Faint> low) { if (stamina.Value < 10) low.Add(new Faint(unit.Id, stamina.Value)); }
    }

    // ---------------- systems whose methods must not fuse ----------------

    /// <summary>The second reads, of other entities, what the first writes.</summary>
    [EcsSystem]
    public ref struct Chase
    {
        [ForEach]
        static void Run(ref Locus p, in Quarry target) => p = p with { X = p.X + target.Target.Index % 2 };

        [ForEach]
        static void Aim(ref Drift v, in Quarry target, Lookup<Locus> spots) { if (spots.Has(target.Target)) v = new Drift(spots.Read(target.Target).X, v.Y); }
    }

    /// <summary>The second reads the singleton the first one's pass sums.</summary>
    [EcsSystem]
    public ref struct Tally
    {
        [ForEach]
        static void Sum(in Stamina stamina, ref Reduce<StaminaSum> sum) => sum.Value = new StaminaSum { Total = sum.Value.Total + stamina.Value, Rows = sum.Value.Rows + 1 };

        [ForEach]
        static void Spread(ref Blows hits, in Stamina stamina, in StaminaSum sum) => hits = new Blows((int)(sum.Total % 1000) + stamina.Value);
    }

    /// <summary>The second filters on changes to what the first writes; then a method with another filter.</summary>
    [EcsSystem]
    public ref struct Echo
    {
        [ForEach]
        static void Touch(ref Stamina stamina) => stamina = stamina with { Value = stamina.Value + 1 };

        [ForEach, Changed<Stamina>]
        static void Seen(in Stamina stamina, ref Blows hits) => hits = new Blows(hits.Count + stamina.Value);

        [ForEach, Changed<Stamina>, Without<Dozing>]
        static void Awake(in Stamina stamina, ref Blows hits) => hits = new Blows(hits.Count - stamina.Value);
    }

    /// <summary>One method changes structure.</summary>
    [EcsSystem]
    public ref struct Nap
    {
        [ForEach]
        static void Tire(ref Stamina stamina) => stamina = stamina with { Value = stamina.Value - 1 };

        [ForEach]
        static void Sleep(Entity unit, in Stamina stamina) { if (stamina.Value < 0) unit.Set(new Dozing()); }
    }

    // ---------------- two systems in one walk ----------------

    /// <summary>Moves; the next system declared, <see cref="Tire"/>, shares its walk (same rows, one after the other).</summary>
    [EcsSystem]
    public ref struct Stride
    {
        [ForEach, With<Stamina>]
        static void Run(ref Locus p, in Drift v)
        {
            if (v.X == 99) throw new InvalidOperationException("stride");
            p = new Locus(p.X + v.X, p.Y + v.Y);
        }
    }

    /// <summary>Reads what <see cref="Stride"/> writes (a later stage) and writes a tracked component.</summary>
    [EcsSystem]
    public ref struct Tire
    {
        [ForEach, With<Drift>]
        static void Run(in Locus p, ref Stamina stamina)
        {
            if (stamina.Max == -1) throw new InvalidOperationException("tire");
            stamina = stamina with { Value = stamina.Value - (p.X > 25 ? 1 : 0) };
        }
    }

    [EcsSystem]
    public ref struct StrideApart
    {
        [ForEach, With<Stamina>]
        static void Run(ref Locus p, in Drift v)
        {
            if (v.X == 99) throw new InvalidOperationException("stride");
            p = new Locus(p.X + v.X, p.Y + v.Y);
        }
    }

    [EcsSystem]
    public ref struct TireApart
    {
        [ForEach(FusionMode = FusionMode.Off), With<Drift>]
        static void Run(in Locus p, ref Stamina stamina)
        {
            if (stamina.Max == -1) throw new InvalidOperationException("tire");
            stamina = stamina with { Value = stamina.Value - (p.X > 25 ? 1 : 0) };
        }
    }

    // ---------------- worlds ----------------

    private static World Populate(int count, bool apart, Action<World> add, WorldOptions options = null)
    {
        var world = new World(options ?? new WorldOptions());
        world.AddSingleton<Fence>() = new Fence(50);
        world.Reserve<Locus, Drift, Stamina, Blows>(count);
        for (int i = 0; i < count; i++)
        {
            var e = world.CreateEntity(new Locus(i % 61, i % 53), new Drift((i % 7) - 3, (i % 5) - 2), new Stamina(i % 40, 30), new Blows(0));
            if (i % 3 != 0) e.Set(new Venom(i % 11));
            if (i % 17 == 0) e.Set(new Dozing());
        }

        add(world);
        return world;
    }

    private static string Schedule(World world) => world.DescribeSchedule().ToString();

    private static void AssertSame(World fused, World apart)
    {
        // Read: a Query of a tracked component would stamp the rows it visits.
        var a = fused.Read<Locus, Drift, Stamina, Blows>().GetEnumerator();
        var b = apart.Read<Locus, Drift, Stamina, Blows>().GetEnumerator();
        int rows = 0;
        while (a.MoveNext())
        {
            Assert.That(b.MoveNext(), Is.True);
            Assert.That(a.Current.Entity.Id, Is.EqualTo(b.Current.Entity.Id));
            Assert.That(a.Current.Component1, Is.EqualTo(b.Current.Component1));
            Assert.That(a.Current.Component2, Is.EqualTo(b.Current.Component2));
            Assert.That(a.Current.Component3, Is.EqualTo(b.Current.Component3));
            Assert.That(a.Current.Component4, Is.EqualTo(b.Current.Component4));
            Assert.That(a.Current.Entity.TryGet<Venom>(out var pa), Is.EqualTo(b.Current.Entity.TryGet<Venom>(out var pb)));
            Assert.That(pa, Is.EqualTo(pb));
            rows++;
        }

        Assert.That(b.MoveNext(), Is.False);
        Assert.That(rows, Is.GreaterThan(0));
        // The same rows changed at the same ticks: every tick since the start.
        for (ulong tick = 0; tick <= fused.ChangeTick + 1; tick++)
        {
            Assert.That(fused.Count<Stamina>(QueryFilter.Create().Changed<Stamina>(tick)), Is.EqualTo(apart.Count<Stamina>(QueryFilter.Create().Changed<Stamina>(tick))), $"Stamina, tick {tick}");
            Assert.That(fused.Count<Blows>(QueryFilter.Create().Changed<Blows>(tick)), Is.EqualTo(apart.Count<Blows>(QueryFilter.Create().Changed<Blows>(tick))), $"Blows, tick {tick}");
        }
        Assert.That(fused.ChangeTick, Is.EqualTo(apart.ChangeTick));
    }

    private static void Compare<TFused, TApart>(int count, int updates)
        where TFused : struct, allows ref struct where TApart : struct, allows ref struct
    {
        using var fused = Populate(count, false, w => w.AddSystem<TFused>());
        using var apart = Populate(count, true, w => w.AddSystem<TApart>());
        for (int i = 0; i < updates; i++)
        {
            fused.Update();
            apart.Update();
        }

        AssertSame(fused, apart);
    }

    [TestCase(1)]
    [TestCase(300)]
    [TestCase(5000)]
    [TestCase(SystemCode.FusedRows + 300)] // above the cache: blocks of rows (below it, each pass on its own)
    public void VectorLoopsFusePerBlock(int count)
    {
        using var world = Populate(count, false, w => w.AddSystem<Motion>());
        Assert.That(Schedule(world), Does.Contain("Integrate: vector"));
        Assert.That(Schedule(world), Does.Contain("one walk with Bounce"));
        Assert.That(Schedule(Populate(1, true, w => w.AddSystem<MotionApart>())), Does.Not.Contain("one walk"));
        Compare<Motion, MotionApart>(count, 5);
    }

    [TestCase(1)]
    [TestCase(1000)]
    public void ScalarMethodsFusePerRow(int count)
    {
        using var world = Populate(count, false, w => w.AddSystem<Vitals>());
        Assert.That(Schedule(world), Does.Contain("Decay: scalar"));
        Assert.That(Schedule(world), Does.Contain("one walk with Regenerate, Count"));
        Compare<Vitals, VitalsApart>(count, 4);
    }

    [TestCase(500)]
    [TestCase(9000)]
    [TestCase(SystemCode.FusedRows + 500)]
    public void ChangedRowsFusePerWord(int count)
    {
        using var world = Populate(count, false, w => w.AddSystem<Watch>());
        Assert.That(Schedule(world), Does.Contain("one walk with Note"));
        // Rows written between updates: some words of each table pass partly.
        static void Write(World w, int round)
        {
            int i = 0;
            foreach (var item in w.Read<Stamina>())
                if (i++ % (3 + round) == 0) item.Entity.Set(new Stamina(item.Component1.Value + round, 30));
        }

        using var fused = Populate(count, false, w => w.AddSystem<Watch>());
        using var apart = Populate(count, true, w => w.AddSystem<WatchApart>());
        for (int round = 0; round < 4; round++)
        {
            fused.Update();
            apart.Update();
            Write(fused, round);
            Write(apart, round);
        }

        AssertSame(fused, apart);
    }

    [TestCase(1, 1)]
    [TestCase(20_000, 1)]
    [TestCase(20_000, 4)]
    public void ParallelWalksMergeEveryMethodsResults(int count, int threads)
    {
        var options = new WorldOptions { MaxDegreeOfParallelism = threads };
        using var fused = new World(options);
        using var apart = new World(options);
        foreach (var w in new[] { fused, apart })
        {
            w.Reserve<Stamina>(count);
            for (int i = 0; i < count; i++)
                w.CreateEntity(new Stamina(i % 50, 50));
        }
        fused.AddSystem<Roster>();
        apart.AddSystem<CensusApart>();
        Assert.That(Schedule(fused), Does.Contain("one walk with Sum, Report"));
        for (int round = 0; round < 3; round++)
        {
            fused.Update();
            apart.Update();
            Assert.That(fused.GetSingleton<StaminaSum>(), Is.EqualTo(apart.GetSingleton<StaminaSum>()));
            ref var a = ref fused.GetSingleton<Emitted<Faint>>().Items;
            ref var b = ref apart.GetSingleton<Emitted<Faint>>().Items;
            Assert.That(a.Count, Is.EqualTo(b.Count));
            for (int i = 0; i < a.Count; i++) Assert.That(a[i], Is.EqualTo(b[i]));
            fused.GetSingleton<Emitted<Faint>>().Items.Clear();
            apart.GetSingleton<Emitted<Faint>>().Items.Clear();
        }
    }

    [Test]
    public void PassesThatCannotBeProvenTheSameRunApart()
    {
        using var world = new World();
        world.AddSystem<Chase>();
        world.AddSystem<Tally>();
        world.AddSystem<Echo>();
        world.AddSystem<Nap>();
        Assert.That(Schedule(world), Does.Not.Contain("one walk"));
    }

    [Test]
    public void FusedPassesAllocateNothing()
    {
        using var world = Populate(4096, false, w =>
        {
            w.AddSystem<Motion>();
            w.AddSystem<Vitals>();
            w.AddSystem<Watch>();
        });
        for (int i = 0; i < 20; i++) world.Update();
        long before = Allocations.Baseline();
        for (int i = 0; i < 100; i++) world.Update();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [TestCase(1)]
    [TestCase(5000)]
    [TestCase(SystemCode.FusedRows + 77)]
    public void SystemsDeclaredOneAfterTheOtherShareOneWalk(int count)
    {
        using var world = Populate(count, false, w => { w.AddSystem<Stride>(); w.AddSystem<Tire>(); });
        Assert.That(Schedule(world), Does.Contain("one walk with the next system, Tire, when both run"));
        using var apart = Populate(1, true, w => { w.AddSystem<StrideApart>(); w.AddSystem<TireApart>(); });
        Assert.That(Schedule(apart), Does.Not.Contain("one walk"));
        // Measured systems and parallel stages run each system apart.
        using var measured = Populate(1, false, w => { w.AddSystem<Stride>(); w.AddSystem<Tire>(); }, new WorldOptions { MeasureSystems = true });
        Assert.That(Schedule(measured), Does.Not.Contain("one walk"));

        using var fused = Populate(count, false, w => { w.AddSystem<Stride>(); w.AddSystem<Tire>(); });
        using var twin = Populate(count, true, w => { w.AddSystem<StrideApart>(); w.AddSystem<TireApart>(); });
        for (int i = 0; i < 6; i++)
        {
            fused.Update();
            twin.Update();
        }

        AssertSame(fused, twin); // the second system's writes carry its own stage's tick
        Assert.That(fused.System<Tire>().LastRunTick, Is.EqualTo(twin.System<TireApart>().LastRunTick));
        Assert.That(fused.System<Stride>().LastRunTick, Is.EqualTo(twin.System<StrideApart>().LastRunTick));
        Assert.That(fused.System<Tire>().RunCount, Is.EqualTo(6));
        Assert.That(fused.System<Stride>().RunCount, Is.EqualTo(6));
    }

    [TestCase(3000)]
    [TestCase(SystemCode.FusedRows + 3000)]
    public void ADisabledSystemLeavesTheOtherItsOwnWalk(int count)
    {
        using var fused = Populate(count, false, w => { w.AddSystem<Stride>(); w.AddSystem<Tire>(); });
        using var twin = Populate(count, true, w => { w.AddSystem<StrideApart>(); w.AddSystem<TireApart>(); });
        for (int i = 0; i < 6; i++)
        {
            bool off = i % 3 == 1, first = i % 2 == 0;
            fused.System<Stride>().Enabled = !(off && first);
            fused.System<Tire>().Enabled = !(off && !first);
            twin.System<StrideApart>().Enabled = !(off && first);
            twin.System<TireApart>().Enabled = !(off && !first);
            fused.Update();
            twin.Update();
        }

        AssertSame(fused, twin);
        Assert.That(fused.System<Tire>().RunCount, Is.EqualTo(twin.System<TireApart>().RunCount));
        Assert.That(fused.System<Stride>().RunCount, Is.EqualTo(twin.System<StrideApart>().RunCount));
    }

    [TestCase(false, 500)]
    [TestCase(true, 500)]
    [TestCase(false, SystemCode.FusedRows + 500)] // one walk: the exception names the system that threw
    [TestCase(true, SystemCode.FusedRows + 500)]
    public void AnErrorDisablesTheSystemThatThrew(bool second, int count)
    {
        var options = new WorldOptions { SystemErrors = SystemErrorPolicy.Disable };
        using var world = Populate(count, false, w => { w.AddSystem<Stride>(); w.AddSystem<Tire>(); }, options);
        world.Update();
        int at = 0;
        foreach (var item in world.Read<Drift, Stamina>())
            if (at++ == 250)
            {
                if (second) item.Entity.Set(new Stamina(1, -1));
                else item.Entity.Set(new Drift(99, 0));
            }

        world.Update();
        var thrower = second ? world.System<Tire>() : world.System<Stride>();
        var other = second ? world.System<Stride>() : world.System<Tire>();
        Assert.That(thrower.Error?.Message, Is.EqualTo(second ? "tire" : "stride"));
        Assert.That(thrower.Enabled, Is.False);
        Assert.That(other.Error, Is.Null);
        Assert.That(other.Enabled, Is.True);
        Assert.That(other.RunCount, Is.EqualTo(2));
        world.Update(); // the other one runs alone
        Assert.That(other.RunCount, Is.EqualTo(3));
        Assert.That(thrower.RunCount, Is.EqualTo(2));
    }

    // ---------------- the generator and the analyzer ----------------

    private const string Parts = """
        public struct A : IComponent { public float V; }
        public struct B : IComponent { public float V; }
        public struct C : IComponent, ITracked { public int V; }
        public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum l, Sum r) => new() { N = l.N + r.N }; }
        """;

    private static string Generated(string source, string system)
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(Parts, source), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        return string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.Contains(system + ".") || t.FilePath.Contains("+")).Select(t => t.ToString()));
    }

    // Each exclusion of the rule leaves the methods apart: one job per method, no fused job.
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach] static void N(ref A a, Entity e) { e.Set(new C()); }", "may change structure")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach] static void N(ref B b, in A a, Lookup<A> l) { }", "Lookup")]
    [TestCase("[ForEach] static void M(in A a, ref Reduce<Sum> s) { } [ForEach] static void N(ref A a, in Sum s) { }", "Sum")]
    [TestCase("[ForEach] static void M(ref C c, in A a) { } [ForEach, Changed<C>] static void N(ref A a, in C c) { }", "")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach, With<B>] static void N(ref A a) { }", "")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(Parallel = true)] static void N(ref A a) { }", "Parallel")]
    [TestCase("[ForEach(ChunkRows = 64, Parallel = true)] static void M(ref A a) { } [ForEach(Parallel = true)] static void N(ref A a) { }", "ChunkRows")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(Slices = 2)] static void N(ref A a) { }", "Slices")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(FusionMode = FusionMode.Off)] static void N(ref A a) { }", "FusionMode.Off")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach, Consume] static void N(in A a) { }", "Consume")]
    public void ExclusionsStayApart(string methods, string reason)
    {
        string code = Generated("[EcsSystem] public ref struct S { " + methods + " }", "S");
        Assert.That(code, Does.Not.Contain("_Fused"), code);
        Assert.That(code, Does.Contain("MJob0").And.Contain("NJob1"), code);
    }

    [Test]
    public void FusedMethodsAreOneJob()
    {
        string code = Generated("[EcsSystem] public ref struct S { [ForEach] static void M(ref A a, in B b) => a.V += b.V; [ForEach] static void N(in A a, ref B b) => b.V -= a.V; [ForEach, With<B>] static void O(ref A a) => a.V *= 2; }", "S");
        Assert.That(code, Does.Contain("M_N_O_Fused0"), code);
        Assert.That(code, Does.Contain("one walk: M, then N, then O"), code);
        Assert.That(code, Does.Contain("one walk with N, O"), code);
        Assert.That(code, Does.Not.Contain("SystemCode.FusedRows"), code); // M and O write A: fused at any size
        // Blocks only above the cache when no component is written twice.
        code = Generated("[EcsSystem] public ref struct S { [ForEach] static void M(ref A a, in B b) => a.V += b.V; [ForEach] static void N(in A a, ref B b) => b.V -= a.V; }", "S");
        Assert.That(code, Does.Contain("< global::EasyEcs.SystemCode.FusedRows)"), code);
    }

    // A view's fields are the method's components: its ref fields writes, so a change filter on them keeps the next apart.
    [Test]
    public void ViewsAreTheirFieldsComponents()
    {
        const string view = "[View] public ref struct AB { public ref A A; public ref readonly B B; } ";
        string code = Generated(view + "[EcsSystem] public ref struct S { [ForEach] static void M(AB v) => v.A.V += v.B.V; [ForEach] static void N(in A a, ref B b) => b.V -= a.V; }", "S");
        Assert.That(code, Does.Contain("M_N_Fused0"), code);
        code = Generated(view + "public struct D : IComponent, ITracked { public int V; } [View] public ref struct AD { public ref readonly A A; public ref D D; } " +
            "[EcsSystem] public ref struct S { [ForEach] static void M(AD v) => v.D.V++; [ForEach, Changed<D>] static void N(ref A a, in D d) => a.V += d.V; }", "S");
        Assert.That(code, Does.Not.Contain("_Fused"), code);
    }

    // Fused at any size: two methods writing one component (its second store stays in L1), and a parallel walk.
    [TestCase("[ForEach] static void M(ref A a, in B b) => a.V += b.V; [ForEach] static void N(ref A a, in B b) => a.V *= b.V;")]
    [TestCase("[ForEach(Parallel = true)] static void M(ref A a, in B b) => a.V += b.V; [ForEach(Parallel = true)] static void N(in A a, ref B b) => b.V -= a.V;")]
    public void SomeWalksFuseAtAnySize(string methods)
    {
        string code = Generated($"[EcsSystem] public ref struct S {{ {methods} }}", "S");
        Assert.That(code, Does.Contain("M_N_Fused0"), code);
        Assert.That(code, Does.Not.Contain("SystemCode.FusedRows"), code);
    }

    [Test]
    public void SystemsInOrderAreOnePairOfTheSet()
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(Parts, """
            [EcsSystem] public ref struct S1 { [ForEach] static void M(ref A a, in B b) => a.V += b.V; }
            [EcsSystem] public ref struct S2 { [ForEach] static void N(in A a, ref B b) => b.V -= a.V; }
            [EcsSystem] public ref struct S3 { [ForEach] static void O(in A a, ref C c) { } }
            [EcsSystem, UpdateAfter<S1>] public ref struct S4 { [ForEach(Parallel = true)] static void P(ref A a, in B b) => a.V += b.V; }
            """), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string set = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("__EasyEcsSystems.g.cs")).ToString();
        Assert.That(set, Does.Contain("systems.Fuse(0, 1);"), set);          // S1 then S2
        Assert.That(set.Split("systems.Fuse(").Length - 1, Is.EqualTo(1), set); // S2 then S3: other rows; S1 then S4: in parallel
        Assert.That(output.SyntaxTrees.Count(t => t.FilePath.EndsWith(".Fusion.g.cs")), Is.EqualTo(1));
    }

    private static async Task<Diagnostic[]> FusionRules(string source) =>
        (await GeneratorTests.Analyze([Parts, source])).Where(d => d.Id is "EECS093" or "EECS094").OrderBy(d => d.Location.SourceSpan.Start).ToArray();

    // EECS093 (Info) names the one thing that keeps two passes over (almost) the same rows apart; EECS094 (Error) a
    // FusionMode.Require method left apart. Each case: the methods of one system, and the ids expected, in order.
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach, With<B>] static void N(ref A a) { }", "EECS093")]                        // one filter apart
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach, Without<B>] static void N(ref A a) { }", "EECS093")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach, WithAny<B, C>] static void N(ref A a) { }", "EECS093")]
    [TestCase("[ForEach(Parallel = true)] static void M(ref A a) => a.V++; [ForEach] static void N(ref A a) => a.V++;", "EECS093")]     // same rows, other settings
    [TestCase("[ForEach(Parallel = true, ChunkRows = 64)] static void M(ref A a) => a.V++; [ForEach(Parallel = true)] static void N(ref A a) => a.V++;", "EECS093")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(Slices = 2)] static void N(ref A a) { }", "EECS093")]
    [TestCase("[ForEach] static void M(ref A a) => a.V++; [ForEach] static void N(ref B b, in A a, Lookup<A> all) => b.V = a.V; [ForEach] static void O(ref A a, in B b) { }", "EECS093")] // a Lookup of A
    [TestCase("[ForEach] static void M(in A a, ref Reduce<Sum> s) => s.Value.N++; [ForEach] static void N(ref A a, in Sum s) => a.V = s.N;", "EECS093")] // a pass's sum, read
    [TestCase("[ForEach] static void M(ref C c) => c.V++; [ForEach, Changed<C>] static void N(in C c, ref A a) => a.V = c.V;", "")]  // other rows: C only, then C and A
    [TestCase("[ForEach] static void M(ref C c, in A a) => c.V++; [ForEach, Changed<C>] static void N(ref A a, in C c) => a.V = c.V;", "")] // a change filter: other rows
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach, With<B>, With<C>] static void N(ref A a) { }", "")]                    // two filters apart: other rows
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(FusionMode = FusionMode.Off), With<B>] static void N(ref A a) { }", "")]  // Off: silent
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach] static void N(ref A a, Entity e) { if (a.V < 0) e.Destroy(); }", "")]  // structure: never one walk
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach] static void N(ref A a) { }", "")]                                       // one walk
    [TestCase("[ForEach] static void M(ref A a) { }", "")]                                                                             // alone
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(FusionMode = FusionMode.Require)] static void N(ref A a) { }", "")]       // required and given
    [TestCase("[ForEach(FusionMode = FusionMode.Require)] static void M(ref A a) { }", "EECS094")]                                    // nothing before it
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(FusionMode = FusionMode.Require), With<B>, With<C>] static void N(ref A a) { }", "EECS094")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach(FusionMode = FusionMode.Require)] static void N(ref A a, Entity e) => e.Destroy();", "EECS094")]
    [TestCase("[ForEach] static void M(ref A a) { } [ForEach] static void N(ref A a, Maybe<B> b) { if (b.HasValue) b.Value.V++; } [ForEach] static void O(ref A a, Lookup<B> l) { }", "EECS093")] // a Maybe writes
    public async Task FusionRulesReportWhatKeepsPassesApart(string methods, string expected)
    {
        var found = await FusionRules("[EcsSystem] public ref struct S { " + methods + " }");
        Assert.That(found.Select(d => d.Id), Is.EqualTo(expected.Length == 0 ? [] : expected.Split(' ')), string.Join("\n", found.Select(d => d.ToString())));
        foreach (var d in found) Assert.That(d.GetMessage(), Does.Not.Contain("{"), d.ToString());
    }

    [Test]
    public async Task NearMissesNameTheirReason()
    {
        var found = await FusionRules("""
            [EcsSystem] public ref struct S
            {
                [ForEach] static void M(ref A a) { }
                [ForEach, With<B>] static void N(ref A a) { }
            }
            [EcsSystem] public ref struct R
            {
                [ForEach] static void M(ref A a) { }
                [ForEach(FusionMode = FusionMode.Require), With<B>, With<C>] static void N(ref A a) { }
            }
            """);
        Assert.That(found.Select(d => d.Id), Is.EqualTo(new[] { "EECS093", "EECS094" }));
        Assert.That(found[0].GetMessage(), Does.Contain("'N' walks the rows of 'M' again").And.Contain("B"));
        Assert.That(found[0].Severity, Is.EqualTo(DiagnosticSeverity.Info));
        Assert.That(found[1].GetMessage(), Does.Contain("'N' has FusionMode.Require but does not share the walk of 'M'"));
        Assert.That(found[1].Severity, Is.EqualTo(DiagnosticSeverity.Error));
    }

    // Systems the analyzer reads like the generator: partial (once), nested, generic, in a group with filters.
    [TestCase("public ref partial struct S { [ForEach] static void M(ref A a) { } } [EcsSystem] public ref partial struct S { [ForEach, With<B>] static void N(ref A a) { } }", "EECS093")] // partial: reported once
    [TestCase("public static class Outer { [EcsSystem] public ref struct S { [ForEach] static void M(ref A a) { } [ForEach, With<B>] static void N(ref A a) { } } }", "EECS093")]
    [TestCase("[EcsSystem] public ref struct S<T> where T : struct, IComponent { [ForEach] static void M(ref T t) { } [ForEach, With<B>] static void N(ref T t) { } }", "EECS093")]
    [TestCase("[With<B>] public struct G : ISystemGroup; [UpdateInGroup<G>, EcsSystem] public ref struct S { [ForEach] static void M(ref A a) { } [ForEach, With<B>] static void N(ref A a) { } }", "")] // the group's B: one filter
    public async Task FusionRulesReadEverySystemShape(string source, string expected)
    {
        var found = await FusionRules(source);
        Assert.That(found.Select(d => d.Id), Is.EqualTo(expected.Length == 0 ? [] : expected.Split(' ')), string.Join("\n", found.Select(d => d.ToString())));
    }
}
