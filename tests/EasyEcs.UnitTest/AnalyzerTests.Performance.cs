using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

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
}
