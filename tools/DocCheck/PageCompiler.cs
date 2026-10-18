using System.Collections.Immutable;
using System.Text;
using EasyEcs;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DocCheck;

/// <summary>A problem of a fence: where it is on the page and what the compiler or the analyzer said.</summary>
public sealed record Failure(string Page, int Line, string Message);

/// <summary>
/// Compiles the C# fences of one page as one compilation, in order (README.md has the conventions):
/// type declarations as they are, members (fields, methods) into a class every fence sees, statements of each fence
/// into a method of that class. A later fence that declares a type again replaces the earlier declaration. A local an
/// earlier fence declared, or a name of <see cref="Ambient"/>, is a field when a later fence uses it undeclared.
/// Fences with <c>doccheck=expect-ID</c> or <c>doccheck=alone</c> compile on their own (with the page's declarations,
/// or with none); the <c>doccheck=example</c> fences compile together, as one program apart from the others.
/// </summary>
public sealed class PageCompiler
{
    private const string Holder = "DocPage";

    /// <summary>Variables a snippet may use without declaring them: the world and entities the prose talks about.</summary>
    public static readonly IReadOnlyDictionary<string, string> Ambient = new Dictionary<string, string>
    {
        ["world"] = "global::EasyEcs.World",
        ["entity"] = "global::EasyEcs.Entity",
        ["unit"] = "global::EasyEcs.Entity",
        ["hero"] = "global::EasyEcs.Entity",
        ["player"] = "global::EasyEcs.Entity",
        ["running"] = "bool",
    };

    private static readonly string[] ImplicitUsings =
        ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Threading", "System.Threading.Tasks", "EasyEcs"];

    private static readonly CSharpParseOptions Regular = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is var name && !name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                        && name is not ("EasyEcs.Generator.dll" or "DocCheck.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
    ];

    // The prelude: declared before every page's fences, so a page's own declaration of a name replaces it.
    private static Snippet Shared => new(new Fence(Prelude.Path, 1, "csharp", "", Prelude.Source));

    private static readonly CSharpCompilationOptions Options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithAllowUnsafe(true).WithNullableContextOptions(NullableContextOptions.Disable);

    static PageCompiler() => GC.KeepAlive(typeof(World)); // EasyEcs.Core is loaded, so it is a trusted assembly

    /// <summary>Every problem of the C# fences of <paramref name="page"/> (<paramref name="fences"/>, in page order).</summary>
    /// <summary>Where to write the generated sources of every compilation (for snippet authors), or null.</summary>
    public string? DumpTo { get; init; }

    public async Task<List<Failure>> CheckAsync(string page, IReadOnlyList<Fence> fences)
    {
        var snippets = fences.Where(f => Markdown.CSharp.Contains(f.Language) && !f.Skip).Select(f => new Snippet(f)).ToList();
        var failures = new List<Failure>();
        var main = snippets.Where(s => !s.Fence.Alone && !s.Fence.Example && !s.Fence.Expected.Any()).ToList();
        if (main.Count != 0) failures.AddRange(await CompileAsync(page, [Shared, .. main], main));
        // The full example: its files compile as one program of their own, so its types never replace the page's.
        var example = snippets.Where(s => s.Fence.Example).ToList();
        if (example.Count != 0)
        {
            var names = example.SelectMany(s => s.TypeNames).ToHashSet();
            failures.AddRange(await CompileAsync(page, [Shared.Without(names), .. example], example));
        }

        foreach (var snippet in snippets.Except(main).Except(example))
        {
            var context = snippet.Fence.Alone ? [] : main.Select(m => m.Without(snippet.TypeNames)).ToList();
            failures.AddRange(await CompileAsync(page, [Shared.Without(snippet.TypeNames), .. context, snippet], [snippet]));
        }

        return failures;
    }

    private async Task<List<Failure>> CompileAsync(string page, List<Snippet> snippets, List<Snippet> focus)
    {
        var fields = new Dictionary<string, string>();
        while (true)
        {
            var (compilation, diagnostics) = await BuildAsync(page, snippets, fields);
            if (Hoist(compilation, diagnostics, snippets, focus, fields)) continue;
            if (DumpTo != null) Dump(compilation, page, focus[0].Fence.Line);
            return Report(page, diagnostics, focus);
        }
    }

    private static async Task<(Compilation, ImmutableArray<Diagnostic>)> BuildAsync(string page, List<Snippet> snippets,
        Dictionary<string, string> fields)
    {
        var trees = new List<SyntaxTree> { Tree("usings", Usings()) };
        // Replacement: of the non-partial types declared under one name, only the last fence's declaration stays.
        var last = new Dictionary<string, Snippet>();
        foreach (var snippet in snippets)
        foreach (var name in snippet.TypeNames)
            last[name] = snippet;
        var holder = new StringBuilder();
        holder.Append("public partial class ").Append(Holder).Append("\n{\n");
        foreach (var (name, type) in fields) holder.Append("    public ").Append(type).Append(' ').Append(name).Append(";\n");
        holder.Append("}\n");
        trees.Add(Tree("fields", holder.ToString()));
        foreach (var snippet in snippets) trees.AddRange(snippet.Emit(page, name => last[name] == snippet));

        var compilation = CSharpCompilation.Create("DocCheck." + Path.GetFileNameWithoutExtension(page), trees, References, Options);
        CSharpGeneratorDriver.Create([new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator()],
                parseOptions: Regular)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var diagnostics = await output.WithAnalyzers([new EasyEcsAnalyzer()]).GetAllDiagnosticsAsync();
        return (output, diagnostics);
    }

    private void Dump(Compilation compilation, string page, int line)
    {
        var dir = Path.Combine(DumpTo!, page.Replace('/', '_') + "#" + line);
        Directory.CreateDirectory(dir);
        int i = 0;
        foreach (var tree in compilation.SyntaxTrees)
            File.WriteAllText(Path.Combine(dir, $"{i++:D2}_{Path.GetFileName(tree.FilePath).Replace('#', '_')}"), tree.ToString());
    }

    private static string Usings()
    {
        var usings = new List<string>();
        foreach (var name in ImplicitUsings) usings.Add($"global using {name};");
        usings.Add($"global using static {Holder};");
        return string.Join('\n', usings) + "\n";
    }

    private static SyntaxTree Tree(string name, string text) => CSharpSyntaxTree.ParseText(text, Regular, path: name + ".g.cs");

    /// <summary>
    /// Undeclared names (CS0103) in statements of the focus: a local of an earlier fence, or an ambient variable,
    /// becomes a field of the holder. Returns whether one was added (compile again).
    /// </summary>
    private static bool Hoist(Compilation compilation, ImmutableArray<Diagnostic> diagnostics, List<Snippet> snippets,
        List<Snippet> focus, Dictionary<string, string> fields)
    {
        bool added = false;
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Id != "CS0103" || diagnostic.Location.SourceTree is not { } tree) continue;
            var snippet = focus.FirstOrDefault(s => s.Owns(tree));
            if (snippet == null) continue;
            string name = tree.GetRoot().FindToken(diagnostic.Location.SourceSpan.Start).ValueText;
            if (fields.ContainsKey(name)) continue;
            string? type = null;
            for (int i = snippets.IndexOf(snippet) - 1; i >= 0 && type == null; i--)
                type = snippets[i].LocalType(compilation, name);
            type ??= Ambient.GetValueOrDefault(name);
            if (type == null) continue;
            fields[name] = type;
            added = true;
        }

        return added;
    }

    private static List<Failure> Report(string page, ImmutableArray<Diagnostic> diagnostics, List<Snippet> focus)
    {
        var failures = new List<Failure>();
        foreach (var snippet in focus)
        {
            var found = new HashSet<string>();
            var expected = snippet.Fence.Expected.ToHashSet();
            foreach (var diagnostic in diagnostics)
            {
                if (!Counts(diagnostic)) continue;
                int line = LineOf(diagnostic, snippet);
                if (line < 0) continue;
                found.Add(diagnostic.Id);
                if (expected.Contains(diagnostic.Id)) continue;
                failures.Add(new Failure(page, line, $"{(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning")} {diagnostic.Id}: {diagnostic.GetMessage()}"));
            }

            foreach (var id in expected.Except(found))
                failures.Add(new Failure(page, snippet.Fence.Line, $"expected {id}, which the fence does not report"));
        }

        // Generated code that does not compile names no fence: report it once, at the first fence of the focus.
        foreach (var diagnostic in diagnostics)
            if (Counts(diagnostic) && diagnostic.Location.SourceTree is { } tree && !snippetsOwn(tree) && tree.FilePath.Contains("Generator"))
                failures.Add(new Failure(page, focus[0].Fence.Line, $"generated code: {diagnostic.Id}: {diagnostic.GetMessage()}"));

        return failures.Distinct().ToList();

        bool snippetsOwn(SyntaxTree tree) => focus.Any(s => s.Owns(tree));
    }

    // Errors, and every analyzer finding (its warnings mark mistakes too); compiler warnings are the snippets' business.
    private static bool Counts(Diagnostic d) =>
        d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning && d.Id.StartsWith("EECS", StringComparison.Ordinal);

    /// <summary>The page line of a diagnostic in <paramref name="snippet"/>'s code (its wrapper: the fence's first line), or -1.</summary>
    private static int LineOf(Diagnostic diagnostic, Snippet snippet)
    {
        var location = diagnostic.Location;
        if (location.SourceTree is not { } tree || !snippet.Owns(tree)) return -1;
        var mapped = location.GetMappedLineSpan();
        return mapped.HasMappedPath ? mapped.StartLinePosition.Line + 1 : snippet.Fence.Line;
    }
}
