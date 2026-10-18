using System.Collections.Generic;
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
        "public sealed class Q : IComponent { public int V; }",
        "public struct Q : IComponent { public int V; }")]
    [TestCase("EECS006", "Make it a struct",
        "public record Q(int V) : IComponent;",
        "public record struct Q(int V) : IComponent;")]
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
        "[With<P>] public struct Q : IComponent { public int V; }",
        "public struct Q : IComponent")]
    [TestCase("EECS023", "Remove the filter",
        "[System.Serializable, With<P>] public struct Q : IComponent { public int V; }",
        "[System.Serializable] public struct Q")]
    public async Task FixesTheDiagnostic(string id, string title, string source, string expected)
    {
        var document = Document(Data + source);
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
    [TestCase("EECS006", "public partial class Q : IComponent { public int V; }\npublic partial class Q { }")]
    [TestCase("EECS006", "public class Base { } public class Q : Base, IComponent { public int V; }")]
    [TestCase("EECS006", "public class Q : IComponent { protected int V; }")]
    [TestCase("EECS011", "public struct Grid : ISingletonComponent { public System.Collections.Generic.List<int> Cells; }\n"
                         + "public class S : EcsSystem { [ForEach(Parallel = true)] static void A(ref P p, in Grid g) => g.Cells.Add(p.V); }")]
    [TestCase("EECS012", "public interface IUse { int Count(DynamicBuffer<int> items); }\n"
                         + "public class Use : IUse { public int Count(DynamicBuffer<int> items) => items.Count; }")]
    [TestCase("EECS012", "public static class Use { public static int Count(DynamicBuffer<int> items) => items.Count;\n"
                         + "  public static int Call(Entity e) => Count(e.Read<DynamicBuffer<int>>()); }", "Take it by 'in' (read)")]
    [TestCase("EECS022", "file struct C : IComponent { public int V; } public class S : EcsSystem { [ForEach] static void A(ref C c) => c.V++; }")]
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

    private static Document Document(string source)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Test", "Test", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            metadataReferences: GeneratorTests.Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location))));
        return workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));
    }

    private static async Task<Diagnostic[]> Analyze(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var all = await compilation.WithAnalyzers([new EasyEcsAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return all.Where(d => d.Id.StartsWith("EECS")).ToArray();
    }
}
