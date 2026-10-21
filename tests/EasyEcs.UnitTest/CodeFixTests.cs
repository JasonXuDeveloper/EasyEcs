using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyEcs.CodeFixes;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Applies EasyEcsCodeFixes to the diagnostics of EasyEcsAnalyzer in a workspace: the fixed code compiles clean.</summary>
public class CodeFixTests
{
    private const string Data = """
        using EasyEcs;
        using RO = System.ReadOnlySpan<Holder>;
        public struct P : IComponent { public int V; }
        public struct Clock : ISingletonComponent { public int Ticks; }
        public class Box { public int V; }
        public struct Holder : IComponent { public System.Collections.Generic.List<int> Items; }
        public struct Q : IComponent { public int V; }
        public struct T : IComponent, ITrackChanges { public int V; }
        public struct Boss : IComponent;

        """;

    [TestCase("EECS002", "Take it by 'in' (read)",
        "public class S : EcsSystem { [ForEach] static void A(P p, ref Clock c) => c.Ticks += p.V; }",
        "static void A(in P p, ref Clock c)")]
    [TestCase("EECS002", "Take it by 'ref' (write)",
        "public class S : EcsSystem { [ForEach] static void A(P p) => p.V++; }",
        "static void A(ref P p)")]
    [TestCase("EECS005", "Make it static",
        "public class Runner { [ForEach] void A(ref P p) => p.V++; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS005", "Make it static",
        "public class Runner { [ForEach] public void A(ref P p) => p.V++; }",
        "[ForEach] public static void A(ref P p)")]
    [TestCase("EECS006", "Make it a struct",
        "public sealed class R : IComponent { public int V; }",
        "public struct R : IComponent { public int V; }")]
    [TestCase("EECS006", "Make it a struct",
        "public record R(int V) : IComponent;",
        "public record struct R(int V) : IComponent;")]
    [TestCase("EECS010", "Run it on one thread",
        "public class S : EcsSystem { [ForEach(Parallel = true)] static void A(ref P p, ref Clock c) => c.Ticks += p.V; }",
        "[ForEach] static void A")]
    [TestCase("EECS011", "Take it by 'ref'",
        "public class S : EcsSystem { [ForEach] static void A(in Holder h) => h.Items.Add(1); }",
        "static void A(ref Holder h)")]
    [TestCase("EECS011", "Take it by 'ref'",
        "public class S : EcsSystem { [ForEach] static void A(System.ReadOnlySpan<Holder> h) => h[0].Items.Add(1); }",
        "static void A(System.Span<Holder> h)")]
    [TestCase("EECS012", "Take it by 'in' (read)",
        "public static class Use { public static int Count(DynamicBuffer<int> items) => items.Count; }",
        "Count(in DynamicBuffer<int> items)")]
    [TestCase("EECS022", "Make it internal",
        "public class S : EcsSystem { private struct Hidden : IComponent { public int V; } [ForEach] static void A(ref Hidden h) => h.V++; }",
        "internal struct Hidden")]
    [TestCase("EECS022", "Make it internal",
        "public class S : EcsSystem {\n    struct Hidden : IComponent { public int V; }\n    [ForEach] static void A(ref Hidden h) => h.V++; }",
        "\n    internal struct Hidden")]
    [TestCase("EECS002", "Take it by 'in' (read)",
        "public class S : EcsSystem { [ForEach] static void A(\n        P p, ref Clock c) => c.Ticks += p.V; }",
        "(\n        in P p, ref Clock c)")]
    [TestCase("EECS005", "Make it static",
        "public class Runner { static int x = 1; [ForEach] void A(ref P p) => p.V += x; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS011", "Take it by 'ref'",
        "public class S : EcsSystem { [ForEach] static void A(RO h) => h[0].Items.Add(1); }",
        "static void A(System.Span<Holder> h)")]
    [TestCase("EECS011", "Take it by 'ref'",
        "public struct Grid : ISingletonComponent { public System.Collections.Generic.List<int> Cells; }\n"
        + "public class S : EcsSystem { [ForEach(Parallel = true)] static void A(ref P p, in Holder h) => h.Items.Add(p.V); }",
        "ref Holder h) => h.Items.Add(p.V); }")] // a component's ref is fine in parallel: each row is one thread's
    [TestCase("EECS012", "Take it by 'ref' (write)",
        "public static class Use { public static int Count(DynamicBuffer<int> items) => items.Count;\n"
        + "  public static int Call(Entity e) { ref var b = ref e.Get<DynamicBuffer<int>>(); return Count(b); } }",
        "return Count(ref b);")]
    [TestCase("EECS022", "Make it internal",
        "public class S : EcsSystem { private class Outer { public struct C : IComponent { public int V; } } [ForEach] static void A(ref Outer.C c) => c.V++; }",
        "internal class Outer")]
    [TestCase("EECS022", "Make it internal",
        "public partial class S : EcsSystem { private partial class Outer { internal struct C : IComponent { public int V; } } [ForEach] static void A(ref Outer.C c) => c.V++; }\n"
        + "public partial class S { private partial class Outer { } }",
        "internal partial class Outer { } }")]
    [TestCase("EECS023", "Remove the filter",
        "[With<P>] public struct R : IComponent { public int V; }",
        "public struct R : IComponent")]
    [TestCase("EECS023", "Remove the filter",
        "[System.Serializable, With<P>] public struct R : IComponent { public int V; }",
        "[System.Serializable] public struct R")]
    [TestCase("EECS025", "Take it by 'in'",
        "public class S : EcsSystem { [ForEach] static void A(ref P p, ref Clock c) => c.Ticks += p.V; }",
        "static void A(in P p, ref Clock c)")]
    [TestCase("EECS025", "Take it as a ReadOnlySpan",
        "public class S : EcsSystem { [ForEach] static void A(System.Span<P> p, ref Clock c) { for (int i = 0; i < p.Length; i++) c.Ticks += p[i].V; } }",
        "static void A(System.ReadOnlySpan<P> p, ref Clock c)")]
    [TestCase("EECS025", "Read it with Read<T>()",
        "public static class U { public static int F(Entity e) => e.Get<T>().V; }",
        "=> e.Read<T>().V;")]
    [TestCase("EECS025", "Read it with .T",
        "public static class U { public static int F(Entity e) => e.WriteT().V; }",
        "=> e.T.V;")]
    [TestCase("EECS026", "Require it with [With<P>]",
        "public class S : EcsSystem { [ForEach] static void A(in P p, ref Clock c) => c.Ticks++; }",
        "[ForEach, With<P>] static void A(ref Clock c)")]
    [TestCase("EECS026", "Remove the parameter",
        "public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) => p.V++; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS026", "Remove the parameter",
        "public class S : EcsSystem { [ForEach, Changed<T>] static void A(in T t, ref P p) => p.V++; }",
        "[ForEach, Changed<T>] static void A(ref P p)")]
    [TestCase("EECS029", "Make it static",
        "public class S : EcsSystem { [ForEach] void A(ref P p) => p.V++; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS030", "Make it one bulk call",
        "public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Destroy(); } }",
        "{ w.DestroyEntities<P>(); }")]
    [TestCase("EECS030", "Make it one bulk call",
        "public static class U { public static void F(World w, QueryFilter f) { foreach (var item in w.Query<P>(f)) item.Entity.Set(new Q { V = 1 }); } }",
        "{ w.SetAll(new Q { V = 1 }, (f).With<P>()); }")]
    [TestCase("EECS030", "Make it one bulk call",
        "public static class U { public static void F(World w) { foreach (var item in w.Query<P, Q>()) item.Entity.RemoveQ(); } }",
        "{ w.RemoveAll<Q>(QueryFilter.Create().With<P>().With<Q>()); }")]
    [TestCase("EECS031", "Create them in one call",
        "public static class U { public static void F(World w, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P()); } }",
        "{ w.CreateEntities(all, new P()); }")]
    [TestCase("EECS031", "Create them in one call",
        "public static class U { public static void F(World w, Prefab p, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.Instantiate(p, new P(), new Q { V = 1 }); } }",
        "{ w.Instantiate(p, all, new P(), new Q { V = 1 }); }")]
    [TestCase("EECS031", "Reserve room first",
        "public static class U { public static void F(World w, int n)\n    {\n        for (int i = 0; i < n; i++) w.CreateEntity(new P { V = i });\n    }\n}",
        "    {\n        w.EnsureCapacity<P>(n);\n        for (int i = 0; i < n; i++)")]
    [TestCase("EECS032", "Use Any",
        "public static class U { public static bool F(World w) => w.Count<P>() > 0; }",
        "=> w.Any<P>();")]
    [TestCase("EECS032", "Use Any",
        "public static class U { public static bool F(World w) => w.Count<P>() == 0; }",
        "=> !w.Any<P>();")]
    [TestCase("EECS032", "Count them with Count",
        "public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n++; return n; } }",
        "{ int n = 0; n += w.Count<P>(); return n; }")]
    [TestCase("EECS033", "Use .P",
        "public static class U { public static void F(Entity e) => e.Get<P>().V = 1; }",
        "=> e.P.V = 1;")]
    [TestCase("EECS033", "Use .WriteT",
        "public static class U { public static void F(Entity e) => e.Get<T>().V = 1; }",
        "=> e.WriteT().V = 1;")]
    [TestCase("EECS033", "Use .T",
        "public static class U { public static int F(Entity e) => e.Read<T>().V; }",
        "=> e.T.V;")]
    [TestCase("EECS033", "Use .HasP",
        "public static class U { public static bool F(Entity e) => e.Has<P>(); }",
        "=> e.HasP;")]
    [TestCase("EECS033", "Use .SetBoss",
        "public static class U { public static void F(Entity e) => e.Set(new Boss()); }",
        "=> e.SetBoss();")]
    [TestCase("EECS033", "Use .SetHpIfChanged",
        "public record struct Hp(int V) : IComponent, ITrackChanges; public static class U { public static bool F(Entity e) => e.SetIfChanged(new Hp(3)); }",
        "=> e.SetHpIfChanged(3);")]
    [TestCase("EECS033", "Use .Clock",
        "public static class U { public static int F(World w) => w.GetSingleton<Clock>().Ticks; }",
        "=> w.Clock.Ticks;")]
    [TestCase("EECS033", "Use .P",
        "public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n += item.Component1.V; return n; } }",
        "n += item.P.V;")]
    [TestCase("EECS037", "Declare Writes => 0",
        "public struct J : IForEach<T> { public int N; public void Execute(ref T t) => N += t.V; }",
        "public struct J : IForEach<T> { public static int Writes => 0; public int N;")]
    [TestCase("EECS037", "Declare Writes => 0b1",
        "public struct J : IForEach<T>\n{\n    public void Execute(ref T t) => t.V++;\n    public static int Writes => 0;\n}",
        "    public static int Writes => 0b1;\n}")]
    [TestCase("EECS038", "Keep the ref",
        "public static class U { public static void F(Entity e) { var p = e.Get<P>(); p.V = 1; } }",
        "{ ref var p = ref e.Get<P>(); p.V = 1; }")]
    [TestCase("EECS038", "Keep the ref",
        "public static class U { public static void F(Entity e) { T t = e.Read<T>(); t.V = 1; } }",
        "{ ref T t = ref e.Get<T>(); t.V = 1; }")]
    [TestCase("EECS038", "Keep the ref",
        "public static class U { public static void F(Entity e) { var t = e.T; t.V++; } }",
        "{ ref var t = ref e.WriteT(); t.V++; }")]
    [TestCase("EECS039", "Take it by 'ref'",
        "public class S : EcsSystem { [ForEach] static void A(Mut<P> p, in Q q) { if (q.V > p.Value.V) p.Write().V = q.V; } }",
        "static void A(ref P p, in Q q) { if (q.V > p.V) p.V = q.V; }")]
    [TestCase("EECS025", "Take it by 'in'",
        "public class S : EcsSystem { [ForEach] static void A(Mut<T> t, ref P p) => p.V = t.Value.V; }",
        "static void A(in T t, ref P p) => p.V = t.V;")]
    [TestCase("EECS015", "Read it with Read<T>()",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static int F(Entity e) => e.Get<Owned>().K; }",
        "=> e.Read<Owned>().K;")]
    [TestCase("EECS015", "Bind it 'ref readonly'",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<Owned>()) { ref var o = ref item.Component1; n += o.K; } return n; } }",
        "ref readonly var o = ref item.Component1;")]
    [TestCase("EECS015", "Take it by 'in'",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public class S : EcsSystem { [ForEach] static void A(ref Owned o, ref Clock c) => c.Ticks += o.K; }",
        "static void A(in Owned o, ref Clock c)")]
    [TestCase("EECS015", "Take it as a ReadOnlySpan",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public class S : EcsSystem { [ForEach] static void A(System.Span<Owned> o, ref Clock c) => c.Ticks += o[0].K; }",
        "static void A(System.ReadOnlySpan<Owned> o, ref Clock c)")]
    [TestCase("EECS007", "Make it readonly",
        "public class S : EcsSystem { private System.Collections.Generic.List<int> _scratch = new(); public int Count => _scratch.Count; }",
        "private readonly System.Collections.Generic.List<int> _scratch = new();")]
    [TestCase("EECS007", "Make it readonly",
        "public class S : EcsSystem { private int _limit; public S(int limit) { _limit = limit; } public int Limit => _limit; }",
        "private readonly int _limit;")]
    [TestCase("EECS018", "Return the constant",
        "public class S : EcsSystem { public override int UpdateInterval { get; } = 5; }",
        "public override int UpdateInterval => 5;")]
    [TestCase("EECS017", "Store its EntityId",
        "public struct Target : IComponent { public Entity Of; }\npublic static class U { public static bool F(Entity e, Entity other) { e.Set(new Target { Of = other }); return e.Read<Target>().Of.Id == other.Id; } }",
        "public struct Target : IComponent { public EntityId Of; }")]
    [TestCase("EECS043", "Implement it explicitly",
        "public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; }",
        "{ Shop IIndexed<Shop>.Key => this; }")]
    public async Task FixesTheDiagnostic(string id, string title, string source, string expected)
    {
        var document = Document(Data + source, generate: string.CompareOrdinal(id, "EECS025") >= 0);
        var diagnostic = (await Analyze(document)).First(d => d.Id == id);

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new EasyEcsCodeFixes().RegisterCodeFixesAsync(context);
        var action = actions.SingleOrDefault(a => a.Title == title);
        Assert.That(action, Is.Not.Null, string.Join(", ", actions.Select(a => a.Title)));

        var operation = (await action!.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var fixedDocument = operation.ChangedSolution.GetDocument(document.Id)!;
        var text = (await fixedDocument.GetTextAsync()).ToString();
        Assert.That(text, Does.Contain(expected));
        Assert.That((await Analyze(fixedDocument)).Where(d => d.Id == id), Is.Empty, text);
        var errors = (await fixedDocument.Project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.That(errors, Is.Empty, text);
    }

    [TestCase("EECS005", "public class Runner { int x = 1; [ForEach] void A(ref P p) => p.V += x; }")]
    [TestCase("EECS005", "public class Runner { int X() => 1; [ForEach] void A(ref P p) => p.V += this.X(); }")]
    [TestCase("EECS006", "public partial class R : IComponent { public int V; }\npublic partial class R { }")]
    [TestCase("EECS006", "public class Base { } public class R : Base, IComponent { public int V; }")]
    [TestCase("EECS006", "public class R : IComponent { protected int V; }")]
    [TestCase("EECS011", "public struct Grid : ISingletonComponent { public System.Collections.Generic.List<int> Cells; }\n"
                         + "public class S : EcsSystem { [ForEach(Parallel = true)] static void A(ref P p, in Grid g) => g.Cells.Add(p.V); }")]
    [TestCase("EECS012", "public interface IUse { int Count(DynamicBuffer<int> items); }\n"
                         + "public class Use : IUse { public int Count(DynamicBuffer<int> items) => items.Count; }")]
    [TestCase("EECS012", "public static class Use { public static int Count(DynamicBuffer<int> items) => items.Count;\n"
                         + "  public static int Call(Entity e) => Count(e.Read<DynamicBuffer<int>>()); }", "Take it by 'in' (read)")]
    [TestCase("EECS022", "file struct C : IComponent { public int V; } public class S : EcsSystem { [ForEach] static void A(ref C c) => c.V++; }")]
    [TestCase("EECS026", "public class S : EcsSystem { [ForEach] static void A(ref P p, ref T t) => p.V++; }")]  // the stamp: [With<T>] would change it
    [TestCase("EECS025", "public static class R { [ForEach] static void A(ref P p, ref int n) => n += p.V;\n"
                         + "  public static int Call() { P p = default; int n = 0; A(ref p, ref n); return n; } }")]       // a caller passes 'ref'
    [TestCase("EECS032", "public static class U { public static bool F(World w) => w.Count<P>(QueryFilter.Create().With<Q>()) > 0; }", "Use Any")]
    [TestCase("EECS017", "public struct Target : IComponent { public Entity Of; } public static class U { public static void F(Entity e) => e.Read<Target>().Of.Destroy(); }")]
    [TestCase("EECS007", "public class S : EcsSystem { int _kills; void K() => _kills++; }")]
    [TestCase("EECS007", "public class S : EcsSystem { private int _limit; public S(int limit) { System.Action a = () => _limit = limit; a(); } }")]
    [TestCase("EECS043", "public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; } public static class U { public static long F(Shop s) => s.Key.Id; }")]
    [TestCase("EECS015", "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static void F(Entity e) => e.Get<Owned>().K = 1; }")]
    [TestCase("EECS015", "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public class S : EcsSystem { [ForEach] static void A(ref Owned o) => o.K++; }")]
    [TestCase("EECS038", "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static void F(Entity e) { var o = e.Read<Owned>(); o.K = 1; } }")]
    [TestCase("EECS039", "public static class R { [ForEach] public static void B(Mut<P> p, in Q q) { if (q.V > 0) p.Write().V = q.V; }\n"
                         + "  public static void Call() { P p = default; B(new Mut<P>(ref p, default), default); } }")]                 // a caller passes a Mut
    public async Task OffersNoFixThatBreaksTheCode(string id, string source, string only = null)
    {
        var document = Document(Data + source);
        var actions = new List<CodeAction>();
        foreach (var diagnostic in (await Analyze(document)).Where(d => d.Id == id))
            await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions.Select(a => a.Title), only == null ? Is.Empty : Is.EquivalentTo(new[] { only }));
        Assert.That(await Analyze(document), Has.Some.Matches<Diagnostic>(d => d.Id == id), "the case reports " + id);
    }

    [Test]
    public async Task ByValueParameterOfOtherTypesHasNoFix()
    {
        var document = Document(Data + "public class S : EcsSystem { [ForEach] static void A(ref P p, Box b) { } }");
        var diagnostic = (await Analyze(document)).First(d => d.Id == "EECS002");
        var actions = new List<CodeAction>();
        await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions, Is.Empty, "a class is no component: nothing mechanical to do");
    }

    /// <summary>A one-file project; with <paramref name="generate"/> the generators run as in a build (the generated members exist).</summary>
    private static Document Document(string source, bool generate = false)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Test", "Test", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            metadataReferences: GeneratorTests.Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)),
            analyzerReferences: generate ? [new Generators()] : []));
        return workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));
    }

    /// <summary>The package's generators, run by the workspace as a build runs them: the generated members exist.</summary>
    private sealed class Generators : AnalyzerReference
    {
        public override string FullPath => "EasyEcs.Generator";
        public override object Id => typeof(Generators);
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        // The same instances every call: the workspace maps each generator back to its reference.
        private readonly ImmutableArray<ISourceGenerator> _generators =
            [new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator()];

        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => _generators;
    }

    private static async Task<Diagnostic[]> Analyze(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var all = await compilation.WithAnalyzers([new EasyEcsAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return all.Where(d => d.Id.StartsWith("EECS")).ToArray();
    }
}
