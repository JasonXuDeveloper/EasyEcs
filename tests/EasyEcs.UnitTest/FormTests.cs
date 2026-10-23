using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// The suggestions to declare with attributes what code writes by hand: a one-entity job and its entity.Run calls
/// ([ForEntity], EECS080) and a [ForEach] method that destroys every entity it visits ([Consume], EECS084). Their fixes
/// are in CodeFixTests. And the parameters that do what a [ForEach] method does by hand: a singleton accumulated row by
/// row (Reduce&lt;T&gt;, EECS086), other entities' components read through the World (Lookup&lt;T&gt;, EECS087),
/// records appended to a singleton's list (Emit&lt;T&gt;, EECS089).
/// </summary>
public class FormTests
{
    private const string Data = """
        public struct P : IComponent { public int V; }
        public struct Q : IComponent { public int V; }
        public struct Hit : IComponent { public int Amount; }
        public struct Closed : IComponent;
        public struct Link : IComponent { public EntityId Target; }
        public struct Census : ISingletonComponent { public int Alive; public float Low; }
        public struct Peak : ISingletonComponent { public int High; }
        public record struct Tally(long Total) : ISingletonComponent;
        public struct Seen : ISingletonComponent { public bool Any; public bool All; }
        public partial struct Sum : ISingletonComponent { [Sum] public long N; }
        public struct Hand : ISingletonComponent, IReduction<Hand> { public int N; public static Hand Identity => default; public static Hand Combine(Hand a, Hand b) => new() { N = a.N + b.N }; }
        public struct Log : ISingletonComponent { public System.Collections.Generic.List<Hit> Hits; public int Count; }

        """;

    private static async Task<string[]> Ids(string source) =>
        (await GeneratorTests.Analyze([Data + source])).Where(d => d.Id is "EECS080" or "EECS084").Select(d => d.Id).ToArray();

    private const string Job = "public struct J : IForEachEntity<P> { public int N; public static int WriteMask => 1; public void Execute(Entity e, ref P p) => p.V += N; }\n";

    [TestCase(Job + "public static class U { public static bool F(Entity e) { var j = new J { N = 2 }; return e.Run<P, J>(ref j); } }")]
    [TestCase(Job + "public static class U { static readonly QueryFilter Open = QueryFilter.Create().Without<Closed>().With<Q>(); public static void F(Entity e, int n) { var j = new J { N = n }; e.Run<P, J>(ref j, Open); } }")]
    [TestCase(Job + "public static class U { public static void F(Entity a, Entity b) { var j = new J(); a.Run<P, J>(ref j); var k = new J { N = 1 }; if (b.Run<P, J>(ref k)) { } } }")]
    [TestCase("public struct J : IForEachEntity<P, Q>, INoStructuralChanges { public int N; public void Execute(Entity e, ref P p, ref Q q) => p.V = q.V + N; }\n"
              + "public static class U { public static void F(Entity e) { var j = new J { N = 1 }; e.Run<P, Q, J>(ref j); } }")]   // WriteMask: the default
    public async Task AHandWrittenOneEntityJobIsReported(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS080" }));

    [TestCase(Job + "public static class U { public static int F(Entity e) { var j = new J { N = 2 }; e.Run<P, J>(ref j); return j.N; } }")]           // read after
    [TestCase(Job + "public static class U { public static void F(World w) { var j = new J(); w.ForEachEntity<P, J>(ref j); } }")]                      // a pass
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } static J Make() => default; }")]   // another use
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J { N = Next() }; e.Run<P, J>(ref j); } static int Next() => 1; }")] // a call moved
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j, QueryFilter.Create().Without<Closed>()); } }")] // built per call
    [TestCase(Job + "public static class U { static readonly QueryFilter A = QueryFilter.Create().Without<Closed>(); public static void F(Entity e, Entity f) { var j = new J(); e.Run<P, J>(ref j, A); var k = new J(); f.Run<P, J>(ref k); } }")] // two filters
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J(); System.Console.WriteLine(); e.Run<P, J>(ref j); } }")]   // not next
    [TestCase("public struct J : IForEachEntity<P> { public int N; public void Execute(Entity e, ref P p) => N = p.V; }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // a result
    [TestCase("public struct J : IForEachEntity<P> { public int N; public static int WriteMask => 0; public void Execute(Entity e, ref P p) => p.V = N; }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // EECS037's
    [TestCase("public struct J : IForEachEntity<P> { public int N; public void Execute(Entity e, ref P p) => Help(ref p); void Help(ref P p) { } }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // another member
    [TestCase("public struct J : IForEachEntity<P> { public int N; public void Execute(Entity e, ref P p) => Use(this); static void Use(J j) { } }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // 'this'
    public async Task JobsTheFixCannotConvertAreLeftAlone(string source) =>
        Assert.That(await Ids(source), Is.Empty);

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; e.Destroy(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Hit h, ref P p, World world) { p.V -= h.Amount; world.CreateEntity(new Q()); e.Destroy(); } }")]
    [TestCase("public static class R { [ForEach] static void A(Entity e, in Hit h, ref int total) { total += h.Amount; e.Destroy(); } }")]
    public async Task AFinalDestroyIsReported(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS084" }));

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; if (p.V < 0) e.Destroy(); } }")]   // conditional
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Hit h, ref P p) { if (p.V < 0) return; p.V -= h.Amount; e.Destroy(); } }")] // a return
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<Hit>] static void A(Entity e) { e.Destroy(); } }")]                                     // EECS030's
    [TestCase("public struct Link : IComponent { public EntityId To; } [EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Link l, ref P p) { p.V++; e.World.GetEntity(l.To).Destroy(); } }")] // another entity
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Consume] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; e.Destroy(); } }")]  // already
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 2)] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; e.Destroy(); } }")] // sliced
    public async Task OtherDestroysAreLeftAlone(string source) =>
        Assert.That(await Ids(source), Is.Empty);

    private static async Task<string[]> Hand(string source, string id) =>
        (await GeneratorTests.Analyze([Data + source])).Where(d => d.Id == id).Select(d => d.GetMessage()).ToArray();

    /// <summary>
    /// A system that resets every singleton of <see cref="Data"/> each update, one way each: they hold one pass's
    /// result, which is what EECS086 replaces with a reduction.
    /// </summary>
    private const string Reset = """
        [EcsSystem] public ref struct Reset
        {
            [OnUpdate] static void OnUpdate(World world)
            {
                world.Census = default;
                world.AddSingleton<Peak>();
                world.GetSingleton<Tally>() = new Tally(0);
                world.Seen.Any = false;
                world.RemoveSum();
                world.RemoveSingleton<Hand>();
            }
        }

        """;

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Census c) { c.Alive++; c.Low = System.MathF.Min(c.Low, p.V); } }",
        "Parameter 'c' of [ForEach] method 'A' accumulates into singleton Census row by row (Alive: a sum, Low: a minimum), so the method runs on one thread and without a vector loop; declare Census partial with [Sum] on Alive, [Min] on Low and take it as 'ref Reduce<Census>': each chunk keeps a partial result, and the pass's result replaces Census, so its reset in 'Reset.OnUpdate' goes")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) { if (p.V > k.High) k.High = p.V; } }",
        "Parameter 'k' of [ForEach] method 'A' accumulates into singleton Peak row by row (High: a maximum), so the method runs on one thread and without a vector loop; declare Peak partial with [Max] on High and take it as 'ref Reduce<Peak>': each chunk keeps a partial result, and the pass's result replaces Peak, so its reset in 'Reset.OnUpdate' goes")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) { if (k.High >= p.V) k.High = p.V; } }",
        "Parameter 'k' of [ForEach] method 'A' accumulates into singleton Peak row by row (High: a minimum), so the method runs on one thread and without a vector loop; declare Peak partial with [Min] on High and take it as 'ref Reduce<Peak>': each chunk keeps a partial result, and the pass's result replaces Peak, so its reset in 'Reset.OnUpdate' goes")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Tally t) => t.Total -= p.V; }",              // an auto-property
        "Parameter 't' of [ForEach] method 'A' accumulates into singleton Tally row by row (Total: a sum), so the method runs on one thread and without a vector loop; declare Tally partial with [Sum] on Total and take it as 'ref Reduce<Tally>': each chunk keeps a partial result, and the pass's result replaces Tally, so its reset in 'Reset.OnUpdate' goes")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Seen s) { s.Any |= p.V > 0; s.All &= p.V > 0; } }",
        "Parameter 's' of [ForEach] method 'A' accumulates into singleton Seen row by row (Any: an or, All: an and), so the method runs on one thread and without a vector loop; declare Seen partial with [Any] on Any, [All] on All and take it as 'ref Reduce<Seen>': each chunk keeps a partial result, and the pass's result replaces Seen, so its reset in 'Reset.OnUpdate' goes")]
    [TestCase("public static class R { [ForEach] static void A(in P p, ref Sum s) => s.N += p.V; }",                         // declared by attributes
        "Parameter 's' of [ForEach] method 'A' accumulates into singleton Sum row by row (N: a sum), so the method runs on one thread and without a vector loop; take it as 'ref Reduce<Sum>' and accumulate into 's.Value': each chunk keeps a partial result, and the pass's result replaces Sum, so its reset in 'Reset.OnUpdate' goes")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Hand h) { if (p.V > 0) h.N++; } }",          // a hand-written Combine
        "Parameter 'h' of [ForEach] method 'A' accumulates into singleton Hand row by row (N: a sum), so the method runs on one thread and without a vector loop; take it as 'ref Reduce<Hand>' and accumulate into 'h.Value': each chunk keeps a partial result, and the pass's result replaces Hand, so its reset in 'Reset.OnUpdate' goes")]
    public async Task ASingletonAccumulatedRowByRowIsReported(string source, string message)
    {
        var diagnostics = await GeneratorTests.Analyze([Data + Reset + source]);
        Assert.That(diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "EECS086").Select(d => d.GetMessage()), Is.EqualTo(new[] { message }));
    }

    // Each way to reset the singleton, and the places a reset does not count.
    [TestCase("[EcsSystem] public ref struct R { [OnUpdate] static void OnUpdate(World world) => world.Peak = default; }", true)]
    [TestCase("[EcsSystem] public ref struct R { [OnUpdate] static void OnUpdate(World world) => world.Peak.High = 0; }", true)]
    [TestCase("[EcsSystem] public ref struct R { [OnUpdate] static void OnUpdate(World world) => world.SetPeak(0); }", true)]
    [TestCase("[EcsSystem] public ref struct R { [OnUpdate] static void OnUpdate(World world) => world.AddSingleton<Peak>(); }", true)]
    [TestCase("[EcsSystem] public ref struct R { [ForEach, With<Q>] static void B(ref Peak k) => k = default; }", true)]
    [TestCase("", false)]                                                                                                   // a running total
    [TestCase("[EcsSystem] public ref struct R { [OnCreate] static void OnCreate(World world) => world.Peak = default; }", false)]       // set up once
    [TestCase("[EcsSystem] public ref struct R { [OnUpdate] static void OnUpdate(World world) => world.Peak.High = world.Peak.High / 2; }", false)] // not a constant
    [TestCase("public static class Host { public static void Step(World w) { w.Peak = default; w.Update(); } }", false)]  // not system code
    public async Task OnlyASingletonResetEachPassIsReported(string reset, bool reported)
    {
        var diagnostics = await GeneratorTests.Analyze([Data + reset + "[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) => k.High += p.V; }"]);
        Assert.That(diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error), Is.Empty);
        Assert.That(diagnostics.Count(d => d.Id == "EECS086"), Is.EqualTo(reported ? 1 : 0));
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref Census c) { c.Alive++; c.Low = System.MathF.Min(c.Low, p.V); p.V = c.Alive; } }")] // reads the running value
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Census c) => c.Alive += p.V; }")]                      // Low would be reset
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) { k.High += p.V; k.High = System.Math.Max(k.High, p.V); } }")] // two ways
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) => k.High += k.High; }")]                       // reads itself
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) { if (p.V > k.High) k.High = p.V + 1; } }")]    // not the value compared
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) { if (p.V > k.High) k.High = p.V; else k.High = 0; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k) => k = new Peak { High = p.V }; }")]           // the whole value
    [TestCase("public partial struct Top : ISingletonComponent { [Max] public long N; } [EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Top t) => t.N += p.V; }")] // not its attribute
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 4)] static void A(in P p, ref Peak k) => k.High += p.V; }")]            // a result per slice
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p, ref Peak k) => k.High += p.Length; }")]          // a batch
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Peak k, in Peak last) => k.High += p.V; }")]          // another access
    [TestCase("public static class H { [ForEntity] static void A(in P p, ref Peak k) => k.High += p.V; }")]                          // one entity
    public async Task OtherSingletonWritesAreLeftAlone(string source) =>
        Assert.That(await Hand(source, "EECS086"), Is.Empty);

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t)) p.V = t.Read<Q>().V; } }",
        "[ForEach] method 'A' takes the World only to read Q of other entities ('t.Read<Q>()'), so its system runs alone; take Lookup<Q> instead of 'w' and read them by id (lookup[id], Read(id), Has(id))")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) => p.V = w.GetEntity(l.Target).Q.V; }",
        "[ForEach] method 'A' takes the World only to read Q of other entities ('w.GetEntity(l.Target).Q'), so its system runs alone; take Lookup<Q> instead of 'w' and read them by id (lookup[id], Read(id), Has(id))")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t) && t.HasQ && t.Has<Hit>()) p.V = t.Q.V; } }",
        "[ForEach] method 'A' takes the World only to read Q, Hit of other entities ('t.HasQ'), so its system runs alone; take Lookup<Q>, Lookup<Hit> instead of 'w' and read them by id (lookup[id], Read(id), Has(id))")]
    public async Task OtherEntitiesReadThroughTheWorldAreReported(string source, string message) =>
        Assert.That(await Hand(source, "EECS087"), Is.EqualTo(new[] { message }));

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t)) t.Get<Q>().V = p.V; } }")] // a write
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t)) p.V = t.Read<P>().V; } }")] // P: written here (EECS059)
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t)) { p.V = t.Read<Q>().V; t.Destroy(); } } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t)) p.V = t.Read<Q>().V; w.CreateEntity(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t)) p.V = t.Id.Index; } }")] // the handle itself
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t) && t.TryGet(out Q q)) p.V = q.V; } }")]
    [TestCase("public static class H { [ForEntity] static void A(ref P p, in Link l, World w) => p.V = w.GetEntity(l.Target).Q.V; }")]
    public async Task OtherWorldUsesAreLeftAlone(string source) =>
        Assert.That(await Hand(source, "EECS087"), Is.Empty);

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Log log) { if (p.V > 3) log.Hits.Add(new Hit { Amount = p.V }); } }",
        "Parameter 'log' of [ForEach] method 'A' appends Hit records to 'log.Hits' row by row, so the method runs on one thread; an 'Emit<Hit> hits' parameter appends them per chunk, joined in row order into singleton Emitted<Hit> when the pass ends")]
    public async Task RecordsAppendedToASingletonAreReported(string source, string message) =>
        Assert.That(await Hand(source, "EECS089"), Is.EqualTo(new[] { message }));

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Log log) { log.Hits.Add(new Hit { Amount = p.V }); if (log.Hits.Count > 9) log.Hits.Clear(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Log log) { log.Hits.Add(new Hit { Amount = p.V }); log.Count++; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, ref Log log) => log.Hits.Add(new Hit { Amount = p.V }); }")] // EECS010's
    [TestCase("public struct Names : ISingletonComponent { public System.Collections.Generic.List<string> All; } [EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Names n) => n.All.Add(\"x\"); }")] // not a struct
    [TestCase("public static class H { [ForEntity] static void A(in P p, ref Log log) => log.Hits.Add(new Hit { Amount = p.V }); }")]
    public async Task OtherListWritesAreLeftAlone(string source) =>
        Assert.That(await Hand(source, "EECS089"), Is.Empty);
}
