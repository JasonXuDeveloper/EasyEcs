using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EasyEcs.Benchmark;

/// <summary>
/// What the generators and the analyzer cost the IDE and the build, on a game server's worth of code: the battle server
/// (BattleServer.cs) and <see cref="Modules"/> more files, each with 10 components (plain, tracked, indexed, a tag), a
/// buffer element, 5 systems of 2 [ForEach] methods and a query loop. <c>Cold</c>: a first run (a build, or the IDE
/// opening the project) on a new compilation. <c>EditMethodBody</c>: an IDE keystroke, one line changed in a [ForEach]
/// method's body, the driver kept from the previous run; <c>EditUnrelatedFile</c>: the same in a file with no EasyEcs
/// code. <c>Compile</c> and <c>CompileAndAnalyze</c>: a build's diagnostics without and with the analyzer.
/// </summary>
[MemoryDiagnoser]
public class GeneratorBenchmark
{
    private const int Modules = 40;
    private static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    private ImmutableArray<SyntaxTree> _trees;
    private ImmutableArray<MetadataReference> _references;
    private SyntaxTree[] _system, _unrelated; // the edited tree's two versions
    private int _systemIndex, _unrelatedIndex, _edits;
    private CSharpCompilation _compilation, _generated;
    private GeneratorDriver _driver;

    private GeneratorDriver NewDriver() => CSharpGeneratorDriver.Create(
        [new ArityGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new SystemGenerator().AsSourceGenerator()], parseOptions: Parse,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None));

    [GlobalSetup]
    public void Setup()
    {
        using var stream = typeof(GeneratorBenchmark).Assembly.GetManifestResourceStream("BattleServer.cs")!;
        string battle = new StreamReader(stream).ReadToEnd();
        var sources = Enumerable.Range(0, Modules).Select(Module).Prepend(battle).Append(Unrelated(0)).ToArray();
        _trees = [.. sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, Parse, path: $"File{i}.cs"))];
        _systemIndex = 1 + Modules / 2;
        _unrelatedIndex = _trees.Length - 1;
        _system = [_trees[_systemIndex], CSharpSyntaxTree.ParseText(Module(Modules / 2).Replace("p.V += 1;", "p.V += 2;"), Parse, path: $"File{_systemIndex}.cs")];
        _unrelated = [_trees[_unrelatedIndex], CSharpSyntaxTree.ParseText(Unrelated(1), Parse, path: $"File{_unrelatedIndex}.cs")];
        _references = [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Append(typeof(World).Assembly.Location)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];

        _compilation = NewCompilation();
        _driver = NewDriver().RunGeneratorsAndUpdateCompilation(_compilation, out var generated, out _);
        _generated = (CSharpCompilation)generated;
        var errors = generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException("The benchmark's code does not compile: " + errors[0]);
        if (_driver.GetRunResult().GeneratedTrees.Length < Modules * 15)
            throw new InvalidOperationException("The generators skipped the benchmark's code.");
        // The code is written as users should: the analyzer has nothing to say about it.
        var warnings = Analyzed();
        if (warnings.Length > 0) throw new InvalidOperationException("The analyzer reports the benchmark's code: " + warnings[0]);
    }

    private CSharpCompilation NewCompilation() => CSharpCompilation.Create("Game", _trees, _references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>A first run: every model built, every file emitted.</summary>
    [Benchmark(Baseline = true)]
    public GeneratorDriver Cold() => NewDriver().RunGenerators(NewCompilation());

    /// <summary>A keystroke in a [ForEach] method's body: the IDE's next compilation, the previous run's driver.</summary>
    [Benchmark]
    public GeneratorDriver EditMethodBody() => Edit(_systemIndex, _system);

    /// <summary>A keystroke in a file with no EasyEcs code.</summary>
    [Benchmark]
    public GeneratorDriver EditUnrelatedFile() => Edit(_unrelatedIndex, _unrelated);

    private GeneratorDriver Edit(int index, SyntaxTree[] versions)
    {
        var current = _compilation.SyntaxTrees[index];
        _compilation = _compilation.ReplaceSyntaxTree(current, versions[++_edits & 1]);
        return _driver = _driver.RunGenerators(_compilation);
    }

    /// <summary>The compiler's own diagnostics of the generated compilation (a new one: nothing bound yet).</summary>
    [Benchmark]
    public int Compile() => Fresh().GetDiagnostics().Length;

    /// <summary>
    /// <see cref="Compile"/> with the analyzer, as a build runs it: the difference is what the analyzer costs a build.
    /// </summary>
    [Benchmark]
    public async Task<int> CompileAndAnalyze() =>
        (await Fresh().WithAnalyzers([new EasyEcsAnalyzer()]).GetAllDiagnosticsAsync()).Length;

    private Compilation Fresh() => _generated.RemoveAllSyntaxTrees().AddSyntaxTrees(_generated.SyntaxTrees);

    /// <summary>The analyzer's diagnostics of the benchmark's code (none: it is written as users should write it).</summary>
    private Diagnostic[] Analyzed() => Fresh().WithAnalyzers([new EasyEcsAnalyzer()]).GetAnalyzerDiagnosticsAsync().Result
        .Where(d => d.Severity >= DiagnosticSeverity.Info).ToArray();

    /// <summary>One module of a game: components, a buffer element, systems with [ForEach] methods and a query loop.</summary>
    private static string Module(int m)
    {
        var sb = new StringBuilder("using EasyEcs;\n\nnamespace Game.Module").Append(m).Append(";\n\n");
        for (int c = 0; c < 10; c++)
            sb.Append(c switch
            {
                0 => $"public struct Tag{m}_{c} : IComponent;\n",
                1 => $"public record struct Score{m}_{c}(int Value) : IComponent, IIndexed<int> {{ public int Key => Value; }}\n",
                2 or 3 => $"public record struct Tracked{m}_{c}(int V, float W) : IComponent, ITrackChanges;\n",
                _ => $"public struct Data{m}_{c} : IComponent {{ public int V; public float X, Y; }}\n",
            });
        sb.Append($"public record struct Item{m}(int Id);\n");
        sb.Append($"public record struct Clock{m}(long Tick) : ISingletonComponent;\n\n");
        for (int s = 0; s < 5; s++)
        {
            string a = $"Data{m}_{4 + s}", b = $"Data{m}_{5 + s % 5}", t = $"Tracked{m}_{2 + s % 2}";
            sb.Append($$"""
                public class System{{m}}_{{s}} : EcsSystem
                {
                    [ForEach, Without<Tag{{m}}_0>]
                    static void Move(ref {{a}} p, in {{b}} q)
                    {
                        p.V += 1;
                        p.X += q.X;
                    }

                    [ForEach]
                    static void Watch(Mut<{{t}}> t, in Clock{{m}} clock)
                    {
                        if (clock.Tick % 2 == 0) t.SetIfChanged(new {{t}}(t.Value.V + 1, t.Value.W));
                    }

                    [ForEach]
                    static void Pick(ref DynamicBuffer<Item{{m}}> items, in {{b}} q)
                    {
                        if (q.V > 100 && items.Count < 8) items.Add(new Item{{m}}(q.V));
                    }
                }


                """);
        }

        sb.Append($$"""
            public static class Report{{m}}
            {
                public static int Sum(World world)
                {
                    int sum = 0;
                    foreach (var item in world.Read<Data{{m}}_4, Tracked{{m}}_2>()) sum += item.Data{{m}}_4.V + item.Tracked{{m}}_2.V;
                    return sum;
                }

                public static void Reset(World world)
                {
                    foreach (var item in world.Query<Data{{m}}_5, Data{{m}}_6>()) item.Data{{m}}_5.V = item.Data{{m}}_6.V;
                }
            }

            """);
        return sb.ToString();
    }

    private static string Unrelated(int version) => $$"""
        namespace Game.Util;

        public static class Text
        {
            public static string Greeting(string name) => "Hello " + name + {{version}};
        }

        """;
}
