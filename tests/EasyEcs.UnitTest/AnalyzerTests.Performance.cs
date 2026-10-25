using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;
using static EasyEcs.UnitTest.GeneratorTests;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Performance.cs and .Views.cs: EECS025 and EECS053 for the parameters of a lambda pass (its job declares
// and stamps what a [ForEach] method with its parameters would), and EECS025 for a view's 'ref' field that no code of the
// compilation writes through (reported at the end of the compilation, on the field).
public partial class AnalyzerTests
{
    // A view of an untracked P and a tracked T, T written by a member. Internal: a public view of a library (these
    // compilations are libraries) is left to the assemblies using it.
    private const string Pt = "[View] internal ref struct PT { public ref P P; public ref T T; public void Bump() => T.V++; }\n";

    /// <summary>The messages of <paramref name="id"/> that contain <paramref name="about"/>.</summary>
    private static async Task<string[]> Messages(string id, string about, params string[] sources) =>
        (await GeneratorTests.Analyze(sources)).Where(d => d.Id == id && d.GetMessage().Contains(about)).Select(d => d.GetMessage()).ToArray();

    private static string Run(string body) => $"public static class Run {{ public static void F(World world, int n) {{ {body} }} }}";

    // A lambda pass is seen as a [ForEach] method: a tracked component (or a view's tracked 'ref' field, written directly
    // or through a member) written on some rows only, and a 'ref' component or singleton the body only reads.
    [TestCase("world.ForEach(static (PT v, in Q q) => { if (q.V > 0) v.T.V = q.V; v.P.V++; });", "EECS053", "The lambda of world.ForEach(…) writes T by 'ref' through field 'T' of view PT")]
    [TestCase("world.ForEach(static (PT v, in Q q) => { if (q.V > 0) v.Bump(); v.P.V++; });", "EECS053", "through field 'T' of view PT")]                 // through a member
    [TestCase("world.ForEachParallel(static (PT v, in Q q) => { if (q.V > 0) v.T.V = 1; v.P.V++; });", "EECS053", "The lambda of world.ForEachParallel(…)")] // parallel
    [TestCase("world.ForEach(static (ref T t, in Q q) => { if (q.V > 0) t.V = q.V; });", "EECS053", "Parameter 't' of the lambda of world.ForEach(…) takes T by 'ref'")]
    [TestCase("world.ForEach(n, static (ref T t, int n) => { if (n > 0) return; t.V = n; });", "EECS053", "Parameter 't' of the lambda")]  // a return
    [TestCase("static void G(World w) => w.ForEach(static (ref T t, in Q q) => { if (q.V > 0) t.V = 1; }); G(world);", "EECS053", "Parameter 't' of the lambda")] // in a local function
    [TestCase("world.ForEach(static (ref P p, ref Q q) => q.V = p.V);", "EECS025", "Parameter 'p' of the lambda of world.ForEach(…) (ref) is a write access")]
    [TestCase("world.ForEach(static (ref T t, ref Q q) => q.V = t.V);", "EECS025", "every row it visits is stamped as changed")]                         // tracked
    [TestCase("world.ForEach(static (ref Q q, ref Clock c) => q.V = c.Ticks);", "EECS025", "Parameter 'c' of the lambda")]                                // a singleton
    public async Task ALambdaPassParameterIsSeenAsAMethods(string body, string id, string message) =>
        Assert.That(await Messages(id, "lambda", GeneratorTests.Components, Pt, Run(body)), Has.One.Contains(message));

    [TestCase("world.ForEach(static (PT v) => { v.T.V++; v.P.V++; });")]                                                   // every row
    [TestCase("world.ForEach(static (PT v, in Q q) => { if (q.V > 0) v.Bump(); else v.T.V = 0; v.P.V++; });")]              // both branches
    [TestCase("world.ForEach(static (ref T t, in Q q) => { if (q.V < 0) throw new System.Exception(); t.V = q.V; });")]   // a throw is no row passed through
    [TestCase("world.ForEach(static (Mut<T> t, in Q q) => { if (q.V > 0) t.Write().V = q.V; });")]                       // a Mut stamps its writes
    [TestCase("world.ForEach(static (in P p, ref Q q) => q.V = p.V);")]                                                    // 'in' reads
    [TestCase("world.ForEach(static (ref P p, in Q q) => p.V += q.V);")]                                                   // written
    [TestCase("world.ForEach((ref P p, ref Q q) => q.V = p.V + n);")]                                                     // no job (EECS095): nothing to declare
    [TestCase("System.Action<int> a = static (int x) => { }; a(n);")]                                                     // no lambda pass
    public async Task ALambdaPassThatDeclaresWhatItDoesIsLeftAlone(string body)
    {
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, Pt, Run(body)]))
            .Where(d => d.Id is "EECS025" or "EECS053" && d.GetMessage().Contains("lambda")).Select(d => d.GetMessage());
        Assert.That(found, Is.Empty);
    }

    // A view's 'ref' field that nothing writes through: read in a method, in a lambda pass, in a member of the view.
    [TestCase("[View] internal ref struct V { public ref P P; public ref readonly Q Q; } [EcsSystem] public ref struct S { [ForEach] static void A(V v, ref T t) => t.V = v.P.V + v.Q.V; }",
        "Field 'P' of view V (ref P) is a write access, but the code only reads it: every pass taking the view is scheduled as the component's writer")]
    [TestCase("[View] internal ref struct V { public ref T T; } public static class Run { public static void F(World world) => world.ForEach(static (V v, ref P p) => p.V = v.T.V); }",
        "Field 'T' of view V (ref T) is a write access, but the code only reads it: every row a pass builds the view over is stamped as changed")]
    [TestCase("[View] internal ref struct V { public ref P P; public readonly int Twice => P.V * 2; } [EcsSystem] public ref struct S { [ForEach] static void A(V v, ref Q q) => q.V = v.Twice; }",
        "Field 'P' of view V (ref P)")]
    [TestCase("[View] internal ref struct V { public ref P P; } public static class U { public static int F(Entity e) => e.AsV().P.V; }",
        "Field 'P' of view V (ref P)")]                                                                                   // built by AsV
    public async Task AViewFieldNothingWritesIsAWriteAccessThatOnlyReads(string source, string message)
    {
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, source])).Where(d => d.Id == "EECS025").ToArray();
        Assert.That(found, Has.Length.EqualTo(1));                                     // once, on the field
        Assert.That(found[0].GetMessage(), Does.StartWith(message));
        Assert.That(found[0].Properties["Fix"], Is.EqualTo("RefReadOnly"));
    }

    // A host reports a document's diagnostics live (its semantic analysis) and compilation-end ones on a full analysis:
    // the view field is known only at the end (its descriptor says so), every other EECS025 stays live.
    [Test]
    public async Task AViewFieldIsReportedAtTheEndAndTheOtherCasesLive()
    {
        const string source = "[View] internal ref struct V { public ref P P; } [EcsSystem] public ref struct S { [ForEach] static void A(V v, ref Q q, ref T t) => q.V = v.P.V + t.V; }";
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, source), out var output, out _);
        var analyzers = output.WithAnalyzers([new EasyEcs.Generator.EasyEcsAnalyzer()]);
        var tree = output.SyntaxTrees.Single(t => t.ToString().Contains("ref struct V"));
        var live = (await analyzers.GetAnalyzerSemanticDiagnosticsAsync(output.GetSemanticModel(tree), null, default)).Where(d => d.Id == "EECS025").ToArray();
        var all = (await analyzers.GetAnalyzerDiagnosticsAsync()).Where(d => d.Id == "EECS025").ToArray();

        Assert.That(live.Select(d => d.GetMessage()), Has.One.StartsWith("Parameter 't'").And.None.StartsWith("Field"));
        Assert.That(all.Select(d => d.GetMessage()), Has.One.StartsWith("Parameter 't'").And.One.StartsWith("Field 'P' of view V"));
        foreach (var d in all)
            Assert.That(d.Descriptor.CustomTags, d.GetMessage().StartsWith("Field") ? Has.Member(Microsoft.CodeAnalysis.WellKnownDiagnosticTags.CompilationEnd)
                : Has.No.Member(Microsoft.CodeAnalysis.WellKnownDiagnosticTags.CompilationEnd));
    }

    [TestCase("[View] internal ref struct V { public ref P P; } [EcsSystem] public ref struct S { [ForEach] static void A(V v) => v.P.V++; }")]                            // written
    [TestCase("[View] internal ref struct V { public ref P P; public void Add(int n) => P.V += n; } [EcsSystem] public ref struct S { [ForEach] static void A(V v, in Q q) => v.Add(q.V); }")] // by a member
    [TestCase("[View] internal ref struct V { public ref P P; } public static class U { public static void F(Entity e) => e.AsV().P.V = 1; }")]                            // through AsV
    [TestCase("[View] internal ref struct V { public ref P P; } public static class U { static void Inc(ref P p) => p.V++; public static void F(Entity e) { var v = e.AsV(); Inc(ref v.P); } }")] // passed by ref
    [TestCase("[View] internal ref struct V { public ref P P; } public static class Run { public static void F(World world) => world.ForEach(static (V v) => v.P = new P { V = 1 }); }")] // in a lambda pass
    [TestCase("[View] public ref struct V { public ref P P; } [EcsSystem] public ref struct S { [ForEach] static void A(V v, ref Q q) => q.V = v.P.V; }")]                // public in a library
    [TestCase("[View] internal ref struct V { public ref readonly P P; public Mut<T> T; } [EcsSystem] public ref struct S { [ForEach] static void A(V v, ref Q q) => q.V = v.P.V + v.T.Value.V; }")] // no 'ref' field
    public async Task AViewFieldSomethingWritesIsLeftAlone(string source) =>
        Assert.That((await GeneratorTests.Analyze([GeneratorTests.Components, source])).Where(d => d.Id == "EECS025" && d.GetMessage().StartsWith("Field")), Is.Empty);

    // EECS025: a write access that only reads.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, ref P p) => p.V = t.V; }", "EECS025")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref Q q) => p.V = q.V; }", "EECS025")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref Clock c) => p.V = c.Ticks; }", "EECS025")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) p.V = q.Value.V; } }", "EECS025")]  // a Maybe declares a write
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Maybe<T> t) => p.V = t.HasValue ? t.Value.V : 0; }", "EECS025")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p, ref Clock c) { for (int i = 0; i < p.Length; i++) c.Ticks += p[i].V; } }", "EECS025")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Counter c, ref P p) => p.V = c.Peek(); }", "EECS025")]          // a readonly method
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref Q q) => p.V = Helpers.Look(q.V == 0 ? p : default); }", "EECS025")]
    [TestCase("public static class U { public static int F(Entity e) => e.Get<T>().V; }", "EECS025")]
    [TestCase("public static class U { public static int F(Entity e) => e.WriteT().V; }", "EECS025")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, ref Clock c) { ref readonly var x = ref t; c.Ticks += x.V; } }", "EECS025")] // a 'ref readonly' local reads
    // EECS026: a parameter the method does not use.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Clock c) => c.Ticks++; }", "EECS026")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) => p.V++; }", "EECS026")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, World w) => p.V++; }", "EECS026")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref Clock c) => p.V++; }", "EECS026")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref T t) => p.V++; }", "EECS026")]                       // stamps: no fix
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Changed<T>] static void A(in T t, ref P p) => p.V++; }", "EECS026")]
    // EECS027: a lookup per row.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) => p.V = e.Q.V; }", "EECS027")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) => p.V = e.Owned.Owner.Index; }", "EECS027")] // indexed: an 'in' parameter
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { if (e.HasQ) p.V++; } }", "EECS027")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, World world) => p.V = world.Clock.Ticks; }", "EECS027")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, World w) => p.V = w.HasClock ? 1 : 0; }", "EECS027")]
    // EECS028: allocations in code that runs every update.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { var l = new System.Collections.Generic.List<int>(); p.V = l.Count; } }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) => p.V = new[] { q.V }.Length; }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) { int v = q.V; System.Func<int> f = () => v; p.V = f(); } }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) => p.V = $\"{q.V}\".Length; }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) => p.V = q.V.ToString().Length; }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) { object o = q.V; p.V = o.GetHashCode(); } }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) => p.V = System.Linq.Enumerable.Count(System.Linq.Enumerable.Range(0, q.V)); }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) => world.Clock.Ticks += new System.Text.StringBuilder().Length; }", "EECS028")]
    // EECS030: a structural change entity by entity over a query.
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Destroy(); } }", "EECS030")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Set(new Q { V = 1 }); } }", "EECS030")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P, Q>()) item.Entity.RemoveQ(); } }", "EECS030")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { item.Entity.DisableP(); } } }", "EECS030")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<Boss>] static void A(Entity e) => e.Destroy(); }", "EECS030")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<P>, Without<Boss>] static void A(Entity e) => e.RemoveP(); }", "EECS030")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<P>] static void A(Entity e) { e.Set(new Q { V = 1 }); } }", "EECS030")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<P>] static void A(Entity e, in Clock c) => e.SetBoss(); }", "EECS030")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in P p) { if (p.V == 0) e.Destroy(); } }")]          // some rows only
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in P p) => e.Set(new Q { V = p.V }); }")]             // a value per row
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Changed<T>] static void A(Entity e) => e.SetBoss(); }")]                       // rows since the last run
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, Maybe<P> p) => e.SetBoss(); }", "EECS026")]           // no filter, only an unused parameter
    // EECS031: entities created one by one in a counted loop.
    [TestCase("public static class U { public static void F(World w, int n) { for (int i = 0; i < n; i++) w.CreateEntity(new P { V = i }); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P()); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w) { for (int i = 0; i < 10_000; i++) w.CreateEntity(new P()); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w) { var all = new Entity[10_000]; for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P { V = i }); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Entity[] more) { var all = new Entity[64]; all = more; for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P { V = i }); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Prefab p, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.Instantiate(p, new P()); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Prefab p, int n) { for (int i = 0; i < n; i++) w.Instantiate(p, new P { V = i }); } }")] // no table to reserve
    [TestCase("public static class U { public static void F(World w, Prefab p, Entity[] all, int n) { for (int i = 0; i < n; i++) w.Instantiate(p, all, new P()); } }")] // batches
    // EECS032: query work the world does in one call.
    [TestCase("public static class U { public static bool F(World w) => w.Count<P>() > 0; }", "EECS032")]
    [TestCase("public static class U { public static bool F(World w) => 0 == w.Count<P>(); }", "EECS032")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n++; return n; } }", "EECS032")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) { if (!item.Entity.Has<Q>()) continue; n += item.Component1.V; } return n; } }", "EECS032", "EECS033")]
    // EECS033: the generated member.
    [TestCase("public static class U { public static void F(Entity e) => e.Get<P>().V = 1; }", "EECS033")]
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<P>(); }", "EECS033")]
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<Boss>(); }", "EECS033")]
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new Boss()); }", "EECS033")]
    [TestCase("public static class U { public static void F(Entity e) => e.Remove<P>(); }", "EECS033")]
    [TestCase("public static class U { public static int F(World w) => w.GetSingleton<Clock>().Ticks; }", "EECS033")]
    [TestCase("public static class U { public static int F(World w, Entity e) => w.Index<Owned, EntityId>().Count(e); }", "EECS033")]
    [TestCase("public static class U { public static EntityId F(Entity e) => e.Read<ChildOf>().Parent; }", "EECS033")]          // EasyEcs's ChildOf has its members
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<Retired>(); }", "EECS033")]
    [TestCase("public static class U { public static void F(Entity e, int v) { if (e.T.V != v) e.SetT(v); } }", "EECS033")]           // a guarded write: SetTIfChanged
    [TestCase("public static class U { public static void F(Entity e, int v) { if (v != e.Read<T>().V) { e.SetT(v); } } }", "EECS033")]
    [TestCase("public record struct Hp(int V) : IComponent, ITracked; public static class U { public static void F(Entity e, int v) { if (e.Hp.V != v) e.Set(new Hp(v)); } }", "EECS033")]
    [TestCase("public record struct Hp(int V) : IComponent; public static class U { public static int F(Entity e) => e.GetOrAdd(new Hp(3)).V; }", "EECS033")] // GetOrAddHp(3)
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n += item.Component1.V; return n; } }", "EECS033")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<DynamicBuffer<Loot>>().Add(new Loot()); static DynamicBuffer<Loot> named; }", "EECS033")]
    // EECS034: a world update from a system.
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) => world.Update(); }", "EECS034")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in P p) { if (p.V == 0) e.World.Update(); } }", "EECS034")]
    public async Task CostsAreReported(string source, params string[] ids)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id).Distinct().OrderBy(x => x), Is.EqualTo(ids),
            string.Join("\n", costs.Select(d => d.GetMessage())));
        Assert.That((await Diagnose(Components, Extra, source)).Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
    }

    // A warning costs, a suggestion (Info) is the same call written shorter, an error is code that cannot work (D29).
    [TestCase("EECS025", DiagnosticSeverity.Warning)]
    [TestCase("EECS026", DiagnosticSeverity.Warning)]
    [TestCase("EECS027", DiagnosticSeverity.Warning)]
    [TestCase("EECS028", DiagnosticSeverity.Warning)]
    [TestCase("EECS030", DiagnosticSeverity.Warning)]
    [TestCase("EECS031", DiagnosticSeverity.Warning)]
    [TestCase("EECS032", DiagnosticSeverity.Warning)]
    [TestCase("EECS033", DiagnosticSeverity.Warning)]
    [TestCase("EECS034", DiagnosticSeverity.Error)]
    public void SeveritySaysWhatARuleCosts(string id, DiagnosticSeverity severity) =>
        Assert.That(new EasyEcsAnalyzer().SupportedDiagnostics.Where(d => d.Id == id).Select(d => d.DefaultSeverity), Is.Not.Empty.And.All.EqualTo(severity)); // every descriptor of the id (EECS025 has a compilation-end one)

    // The tricky "written" cases: every form of a write keeps the write access.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p.V = 1; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p.V++; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p.V += 2; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p = default; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => Helpers.Inc(ref p.V); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Counter c) => c.Bump(); }")]                                    // a mutating method
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { ref var x = ref p; x.V = 2; } }")]                         // a ref local
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { ref int v = ref p.V; v = 3; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref DynamicBuffer<int> b) => b.Add(1); }")]                         // a 'this ref' extension
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p) => p.Fill(default); }")]                            // a Span method
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p) { foreach (ref var x in p) x.V++; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p) => p[0].V = 1; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => (p.V, _) = (1, 2); }")]                                // a deconstruction
    [TestCase("public static class U { public static void F(Entity e) { ref var t = ref e.Get<T>(); t.V++; } }")]
    [TestCase("public static class U { public static void F(Entity e) => e.Get<T>().V = 1; }")]
    [TestCase("public static class U { public static void F(Entity e) => e.WriteT().V = 1; }")]
    public async Task WritesKeepTheWriteAccess(string source)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id), Has.None.EqualTo("EECS025").And.None.EqualTo("EECS026"), string.Join("\n", costs.Select(d => d.GetMessage())));
    }

    // Not reported: used, filters, branches, small or reserved creation, and the code each rule leaves alone.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Clock c) { } }")]                                           // being written
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<P>] static void A(ref Clock c) => c.Ticks++; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { if (p.V > 0) p.V = e.Q.V; } }")]       // some rows only
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { if (p.V > 0) return; p.V = e.Q.V; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { if (e.HasQ) e.RemoveQ(); p.V++; } }")]    // changed here
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) { if (q.V < 0) throw new System.InvalidOperationException($\"{q.V}\"); p.V = q.V; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) { System.Diagnostics.Debug.Assert(q.V >= 0, $\"{q.V}\"); p.V = q.V; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p, in Q q) => System.Console.WriteLine($\"{p.V} {q.V}\"); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) { System.Func<int, int> f = static x => x + 1; p.V = f(q.V); } }")]
    [TestCase("[UpdateInterval(20)] [EcsSystem] public ref struct S { static void OnUpdate(World world) => world.Clock.Ticks += new int[4].Length; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, World world) => p.V = world.DefaultUpdateCount > 0 ? 1 : 0; }")]               // uses its system
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Set(new P { V = 1 }); } }")]   // overwrites
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Set(new Q { V = item.Entity.Id.Index }); } }")]
    [TestCase("public static class U { public static void F(World w) { for (int i = 0; i < 100; i++) w.CreateEntity(new P()); } }")]
    [TestCase("public static class U { public static void F(World w) { var all = new Entity[100]; for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P { V = i }); } }")]
    [TestCase("public static class U { public static void F(World w, int n) { for (int i = 0; i < n; i++) if (w.Count<P>() < i) w.CreateEntity(new P { V = i }); } }")]   // now and then
    [TestCase("public static class U { public static void F(World w, int n) { for (int r = 0; r < n; r++) for (int i = 0; i < 8; i++) w.CreateEntity(new P { V = i }); } }")]   // the inner loop's
    [TestCase("public static class U { public static void F(World w, int n) { w.Reserve<P>(n); for (int i = 0; i < n; i++) w.CreateEntity(new P { V = i }); } }")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) { for (int i = 0; i < world.Clock.Ticks; i++) world.CreateEntity(new P()); } }")]
    [TestCase("public static class U { public static bool F(World w) => w.Count<P>() > 2; }")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<DynamicBuffer<Loot>>().Add(new Loot()); }")]          // the only name of the buffer
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { } }")]
    [TestCase("public static class U { public static EntityId F(World w, Entity e) { foreach (var item in w.Query<P, ChildOf>()) _ = item.ChildOf; return e.ChildOf.Parent; } }")]   // EasyEcs's own component, by its members
    [TestCase("public static class U { public static void F(Entity e, int v) { if (e.T.V != v) e.SetT(v); else e.SetT(0); } }")]     // an else
    [TestCase("public static class U { public static void F(Entity e, Entity o, int v) { if (o.T.V != v) e.SetT(v); } }")]             // another entity
    [TestCase("public static class U { public static void F(Entity e, int v) { if (e.T.V != v) e.SetT(v + 1); } }")]                // another value
    [TestCase("public record struct Two(int A, int B) : IComponent, ITracked; public static class U { public static void F(Entity e, int v) { if (e.Two.A != v) e.SetTwo(v, 0); } }")] // two fields
    public async Task CheapCodeIsNotReported(string source)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id), Is.Empty, string.Join("\n", costs.Select(d => d.GetMessage())));
    }

    // One mistake, one diagnostic: where two rules see the same code, one defers to the other (the one whose fix
    // removes what the other would report).
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(DynamicBuffer<Loot> items, ref P p) => p.V = items.Count; }", "EECS002")] // not EECS012
    [TestCase("public static class U { public static void F(Entity e) { var t = e.Get<T>(); t.V = 1; } }", "EECS038")]                       // not EECS025
    [TestCase("public static class U { public static void F(Entity e) { var t = e.WriteT(); t.V = 1; } }", "EECS038")]                       // not EECS025
    [TestCase("public static class U { public static void F(Entity e) { var o = e.Get<Owned>(); o.Owner = default; } }", "EECS038")]         // not EECS015
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, ref Clock c) => p.V += c.Ticks; }", "EECS010")] // not EECS025
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<T>()) n++; return n; } }", "EECS032")] // not EECS046
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<T>()) item.Entity.Destroy(); } }", "EECS030")]  // not EECS046
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Without<P>] static void A(ref P p, ref Q q) => q.V++; }", "EECS049")]                  // not EECS026
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Without<P>] static void A(ref P p, ref Q q) => q.V = p.V; }", "EECS049")]              // not EECS025
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Removed<T>] static void A(ref T t, ref Q q) => q.V = t.V; }", "EECS016")]              // not EECS025
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(World w, ref P p) { w.TrimExcess(); p.V++; } }", "EECS041")]             // not EECS028
    [TestCase("public static class G { public static World W = new(); } [EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { G.W.TrimExcess(); p.V++; } }", "EECS013")] // not EECS028
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Remove<Retired>(); } }", "EECS045")] // not EECS030
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) => p.V = e.Get<Q>().V; }", "EECS027")]               // not EECS033
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); e.Set(new Boss()); } }", "EECS052")] // not EECS033
    [TestCase("public struct Pair { public Entity A; } public struct Pairs : IComponent { public int N; public DynamicBuffer<Pair> All; }", "EECS012", "EECS017")] // two mistakes, once each
    [TestCase("[EcsSystem] public ref struct S { static int _hits; [ForEach(Parallel = true)] static void A(in P p) => _hits += p.V; }", "EECS007")] // not EECS060
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, ref Clock c) { c.Ticks += p.V; G.N++; } } public static class G { public static int N; }", "EECS010")] // not EECS060
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p.V = new System.Random(p.V).Next(); }", "EECS071")]         // not EECS028
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Link l, World w) { if (w.TryGetEntity(l.Target, out var t)) p.V = t.Get<Q>().V; } }", "EECS087")] // not EECS033
    [TestCase("public struct Ship : IComponent { public float X, VX, A, B, C, D, E, F; } [EcsSystem] public ref struct S { [ForEach] static void A(ref Ship s) => s.X += s.VX; }")] // EECS055's split (left out of All), not EECS091
    [TestCase("public static class U { static int Step = 2; public static void F(World w, int n) => w.ForEach((ref P p) => p.V += Step * n); }", "EECS095")] // not EECS096: the fix makes it static first
    [TestCase("public static class U { static int Step = 2; public static void F(World w) => w.ForEach(static (ref P p) => p.V = System.Math.Clamp(p.V, 0, Step) % 7); }", "EECS096")] // not EECS097: no loop to translate
    public async Task OneMistakeIsReportedOnce(string source, params string[] ids)
    {
        var all = await All(source);
        Assert.That(all.Select(d => d.Id).OrderBy(x => x), Is.EqualTo(ids), string.Join("\n", all.Select(d => d.Id + " " + d.GetMessage())));
    }
}
