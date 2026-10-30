using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [ForEachRecord]: a method run on each record of <see cref="Emitted{T}"/> in the order the passes emitted them, then
/// the records cleared, against the walk and Clear written by hand; its generated code, EECS108 and EECS109.
/// </summary>
public class RecordTests
{
    public record struct Power(int Value) : IComponent;
    public record struct Toughness(int Value) : IComponent;

    /// <summary>A hit: who takes it and how much.</summary>
    public record struct Blow(EntityId Target, int Amount);

    /// <summary>A record a record method emits in turn: the deaths its hits caused.</summary>
    public record struct Fallen(EntityId Who);

    /// <summary>What the records added up to; Order depends on the order they came in.</summary>
    public struct Ledger : ISingletonComponent
    {
        public long Total;
        public long Order;
        public int Count;
    }

    /// <summary>The same, written by the hand-written walk.</summary>
    public struct HandLedger : ISingletonComponent
    {
        public long Total;
        public long Order;
        public int Count;
    }

    [EcsSystem]
    public ref struct Strikes
    {
        [ForEach(Parallel = true, ChunkSize = 64, SimdMode = SimdMode.Off)]
        static void Strike(Entity entity, in Power power, Emit<Blow> blows)
        {
            if (power.Value % 3 != 0) blows.Add(new Blow(entity.Id, power.Value));
            if (power.Value % 5 == 0) blows.Add(new Blow(entity.Id, 1));
        }
    }

    [EcsSystem, UpdateAfter<Strikes>]
    public ref struct Book
    {
        [ForEachRecord]
        static void Enter(in Blow blow, ref Ledger ledger)
        {
            ledger.Total += blow.Amount;
            ledger.Order = ledger.Order * 31 + blow.Amount;
            ledger.Count++;
        }
    }

    /// <summary>The walk and Clear by hand: what [ForEachRecord] replaces (EECS109).</summary>
    [EcsSystem, UpdateAfter<Strikes>]
    public ref struct HandBook
    {
        static void OnUpdate(World world)
        {
            ref var ledger = ref world.GetOrAddSingleton<HandLedger>();
            ref var blows = ref world.GetOrAddSingleton<Emitted<Blow>>().Items;
            foreach (ref readonly var blow in blows.AsReadOnlySpan())
            {
                ledger.Total += blow.Amount;
                ledger.Order = ledger.Order * 31 + blow.Amount;
                ledger.Count++;
            }
            blows.Clear();
        }
    }

    /// <summary>Emit and handle in one system: the record method runs after the pass, in declaration order.</summary>
    [EcsSystem]
    public ref struct StrikeAndBook
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Strike(Entity entity, in Power power, Emit<Blow> blows) => blows.Add(new Blow(entity.Id, power.Value));

        [ForEachRecord]
        static void Enter(in Blow blow, ref Ledger ledger, Lookup<Toughness> lives, Emit<Fallen> fallen)
        {
            ledger.Total += blow.Amount;
            ledger.Count++;
            if (lives.Has(blow.Target) && lives.Read(blow.Target).Value <= blow.Amount) fallen.Add(new Fallen(blow.Target));
        }
    }

    public static class Records
    {
        [ForEachRecord]
        public static void Count(in Blow blow, ref int count, ref long total)
        {
            count++;
            total += blow.Amount;
        }

        [ForEachRecord]
        public static void Apply(in Blow blow, World world)
        {
            if (world.TryGetEntity(blow.Target, out var target)) target.Get<Toughness>().Value -= blow.Amount;
            if (blow.Amount == 7) throw new InvalidOperationException("seven");
        }
    }

    private static (World World, Entity[] Entities) Fighters(int count, int threads = 4)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var entities = new Entity[count];
        world.CreateEntities(entities, new Power(0), new Toughness(100));
        world.GetOrAddSingleton<Ledger>(); // a 'ref' singleton must exist, as for a [ForEach] method
        for (int i = 0; i < count; i++) entities[i].Power = new Power(i % 50 == 7 ? 8 : i % 50);
        return (world, entities);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void EachRecordIsHandledOnceInOrderAsByHand(int threads)
    {
        var (world, _) = Fighters(5_000, threads);
        using var _w = world;
        world.AddSystem<Strikes>();
        world.AddSystem<Book>();
        world.Update();
        // The hand-written walk, over the same records: another world, the same rows.
        var (hand, _) = Fighters(5_000, threads);
        using var _h = hand;
        hand.AddSystem<Strikes>();
        hand.AddSystem<HandBook>();
        for (int run = 0; run < 10; run++)
        {
            if (run > 0) world.Update();
            hand.Update();
            var l = world.GetSingleton<Ledger>();
            var h = hand.GetSingleton<HandLedger>();
            Assert.That((l.Total, l.Order, l.Count), Is.EqualTo((h.Total, h.Order, h.Count)), $"run {run}");
            Assert.That(world.GetSingleton<Emitted<Blow>>().Items.Count, Is.Zero, "cleared");
        }
    }

    [Test]
    public void ARecordMethodRunsAfterTheMethodsDeclaredBeforeIt()
    {
        var (world, entities) = Fighters(1_000);
        using var _w = world;
        entities[3].Toughness = new Toughness(2);
        world.AddSystem<StrikeAndBook>();
        world.Update();
        long expected = entities.Sum(e => (long)e.Power.Value);
        Assert.That((world.GetSingleton<Ledger>().Total, world.GetSingleton<Ledger>().Count), Is.EqualTo((expected, 1_000)));
        Assert.That(world.GetSingleton<Emitted<Blow>>().Items.Count, Is.Zero);
        var fallen = world.GetSingleton<Emitted<Fallen>>().Items.AsReadOnlySpan().ToArray();
        Assert.That(fallen.Select(f => f.Who), Is.EqualTo(entities.Where(e => e.Toughness.Value <= e.Power.Value).Select(e => e.Id)));
    }

    [Test]
    public void ARunnerTakesTheWorldAndTheCallersValues()
    {
        using var world = new World();
        int count = 0;
        long total = 0;
        Records.Count(world, ref count, ref total); // no Emitted<Blow>: nothing to do, nothing added
        Assert.That((count, total, world.HasSingleton<Emitted<Blow>>()), Is.EqualTo((0, 0L, false)));

        ref var blows = ref world.GetOrAddSingleton<Emitted<Blow>>().Items;
        for (int i = 1; i <= 4; i++) blows.Add(new Blow(default, i));
        Records.Count(world, ref count, ref total);
        Assert.That((count, total, blows.Count), Is.EqualTo((4, 10L, 0)));
    }

    [Test]
    public void APassThatThrowsKeepsItsRecords()
    {
        var (world, entities) = Fighters(3);
        using var _w = world;
        ref var blows = ref world.GetOrAddSingleton<Emitted<Blow>>().Items;
        blows.Add(new Blow(entities[0].Id, 5));
        blows.Add(new Blow(entities[1].Id, 7));
        Assert.Throws<InvalidOperationException>(() => Records.Apply(world));
        Assert.That(blows.Count, Is.EqualTo(2), "nothing cleared");
        Assert.That((entities[0].Toughness.Value, entities[1].Toughness.Value), Is.EqualTo((95, 93)));
    }

    [Test]
    public void ARecordPassWritesEmittedForTheScheduler()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        world.AddSystem<Strikes>();
        world.AddSystem<Book>();
        Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(2), world.DescribeSchedule().ToString());
    }

    [Test]
    public void ARecordPassDoesNotAllocateAfterItsFirstUpdates()
    {
        var (world, _) = Fighters(World.MaxChunkRows * 2);
        using var _w = world;
        world.AddSystem<Strikes>();
        world.AddSystem<Book>();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 200; i++) world.Update(); }), Is.Zero);
    }

    // ---------------- generated code and rules ----------------

    private const string Data = """
        public struct P : IComponent { public int V; }
        public struct Tally : ISingletonComponent { public long N; }
        public record struct Rec(int V);
        public record struct Other(int V);

        """;

    [Test]
    public void TheGeneratedPassRunsTheJobOverTheRecords()
    {
        string source = "using EasyEcs;\n" + Data + "[EcsSystem] public ref struct S { [ForEachRecord] static void H(in Rec r, ref Tally t, Lookup<P> ps, Emit<Other> o) { t.N += r.V; o.Add(new Other(r.V)); } }" +
                        "public static class R { [ForEachRecord] public static void G(in Rec r, ref int n) => n += r.V; }";
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(source), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string text = string.Join("\n", output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(text, Does.Contain("internal ref struct HJob0 : global::EasyEcs.IForEachRecord<global::Rec>"));
        Assert.That(text, Does.Contain("public void Execute(in global::Rec record) => __ForEach.H_0(default, in record, ref s1, new global::EasyEcs.Lookup<global::P>(World, null), new global::EasyEcs.Emit<global::Other>(ref this.e3));"));
        Assert.That(text, Does.Contain("global::EasyEcs.Emitted<global::Rec>.Consume(world, ref job0);"));
        Assert.That(text, Does.Contain("access.WriteSingleton<global::EasyEcs.Emitted<global::Rec>>();"));
        Assert.That(text, Does.Contain("public static void G(global::EasyEcs.World world, ref int n)"));
        Assert.That(text, Does.Contain("\"H: the records of Emitted<Rec>, in order, then cleared\""));
    }

    [TestCase("public static class R { [ForEachRecord] static void A(in Rec r, in P p) { } }", "takes 'p', which a pass over records has none of")]
    [TestCase("public static class R { [ForEachRecord] static void A(Rec r) { } }", "does not take its record first by 'in'")]
    [TestCase("public static class R { [ForEachRecord] static void A(in P r) { } }", "does not take its record first by 'in'")]          // a component
    [TestCase("public static class R { [ForEachRecord] static void A(in Rec r, Emit<Rec> more) { } }", "an Emit<Rec> of its own records")]
    [TestCase("public static class R { [ForEachRecord, With<P>] static void A(in Rec r) { } }", "has a filter or [Consume]")]
    [TestCase("public class R { [ForEachRecord] void A(in Rec r) { } }", "is an instance method")]
    [TestCase("public static class R { [ForEachRecord] static void A<T>(in Rec r) { } }", "is generic")]
    [TestCase("public static class R { [ForEachRecord, ForEach] static void A(in Rec r) { } }", "also has [ForEach] or [ForEntity]")]
    [TestCase("[EcsSystem] public ref struct S { [ForEachRecord] static void A(in Rec r, int n) { } }", "takes 'n', which a pass over records has none of")] // a caller's value in a system
    public async Task AnInvalidRecordMethodIsReported(string source, string reason)
    {
        var found = (await GeneratorTests.Analyze([Data + source])).Where(d => d.Id == "EECS108").Select(d => d.GetMessage()).ToArray();
        Assert.That(found, Has.Exactly(1).Items);
        Assert.That(found[0], Does.Contain(reason));
    }

    [TestCase("public static class R { [ForEachRecord] static void A(in Rec r, ref Tally t, Lookup<P> p, World w, Emit<Other> o, ref int n) { } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEachRecord] static void A(in Rec r, in Tally t, World w) { } }")]
    public async Task AValidRecordMethodIsNotReported(string source) =>
        Assert.That((await GeneratorTests.Analyze([Data + source])).Where(d => d.Id.StartsWith("EECS1") || d.Severity == DiagnosticSeverity.Error), Is.Empty);

    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World w) { ref var t = ref w.GetOrAddSingleton<Tally>(); ref var rs = ref w.GetSingleton<Emitted<Rec>>().Items; foreach (var r in rs) t.N += r.V; rs.Clear(); } }", true)]
    [TestCase("public static class H { public static int F(World w) { ref var rs = ref w.GetOrAddSingleton<Emitted<Rec>>().Items; int n = 0; foreach (ref readonly var r in rs.AsReadOnlySpan()) n += r.V; rs.Clear(); return n; } }", false)]
    public async Task AHandWrittenWalkAndClearIsReported(string source, bool fixable)
    {
        var found = (await GeneratorTests.Analyze([Data + source])).Where(d => d.Id == "EECS109").ToArray();
        Assert.That(found.Select(d => d.GetMessage()), Has.Exactly(1).Items);
        Assert.That(found[0].GetMessage(), Does.Contain("walks the records of Emitted<Rec> and clears them by hand: a [ForEachRecord] method taking 'in Rec r'"));
        Assert.That(found[0].Severity, Is.EqualTo(DiagnosticSeverity.Info));
        Assert.That(found[0].Properties.ContainsKey("RecordParameters"), Is.EqualTo(fixable));
    }

    [TestCase("public static class H { public static void F(World w) { ref var rs = ref w.GetOrAddSingleton<Emitted<Rec>>().Items; foreach (var r in rs) { } } }")] // not cleared
    [TestCase("public static class H { public static void F(World w) { ref var rs = ref w.GetOrAddSingleton<Emitted<Rec>>().Items; rs.Clear(); foreach (var r in rs) { } } }")] // cleared before
    [TestCase("public static class H { public static void F(World w, DynamicBuffer<Rec> rs) { foreach (var r in rs) { } rs.Clear(); } }")]                                // not Emitted's
    public async Task OtherWalksAreLeftAlone(string source) =>
        Assert.That((await GeneratorTests.Analyze([Data + source])).Where(d => d.Id == "EECS109"), Is.Empty);
}
