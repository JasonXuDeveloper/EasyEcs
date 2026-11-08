using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EasyEcs.Benchmark;

/// <summary>
/// What the generators and the analyzer cost the IDE and the build, on <see cref="GeneratorCorpus"/> (a game server's
/// worth of code). <c>Cold</c>: a first run (a build, or the IDE opening the project) on a new compilation.
/// <c>EditMethodBody</c>: a keystroke, one line changed in a [ForEach] method's body, the driver kept from the previous
/// run, every output produced (a build's driver); <c>EditMethodBodyIde</c>: the same as an IDE runs the generators while
/// typing, implementation outputs skipped (the steps only they read never run); <c>EditUnrelatedFile</c>: a keystroke in
/// a file with no EasyEcs code. <c>Compile</c> and <c>CompileAndAnalyze</c>: a build's diagnostics without and with the analyzer.
/// <see cref="OneNamespace"/>: every module in one namespace (a game's <c>Game</c>), as many projects lay out their
/// components, instead of one namespace per module. That the corpus compiles and the analyzer has nothing to say about
/// it is checked once by GeneratorTests.TheBenchmarkCorpusIsClean, not in every process here.
/// </summary>
[MemoryDiagnoser, GeneratedBytesColumn]
public class GeneratorBenchmark
{
    [Params(false, true)] public bool OneNamespace;

    private ImmutableArray<SyntaxTree> _trees;
    private ImmutableArray<MetadataReference> _references;
    private SyntaxTree[] _system, _unrelated; // the edited tree's two versions
    private int _systemIndex, _unrelatedIndex, _edits;
    private CSharpCompilation _compilation, _ideCompilation, _generated;
    private GeneratorDriver _driver, _ideDriver;

    [GlobalSetup]
    public void Setup()
    {
        var parse = GeneratorCorpus.Parse;
        _trees = [.. GeneratorCorpus.Sources(OneNamespace).Select((s, i) => CSharpSyntaxTree.ParseText(s, parse, path: $"File{i}.cs"))];
        _systemIndex = 1 + GeneratorCorpus.Modules / 2;
        _unrelatedIndex = _trees.Length - 1;
        _system = [_trees[_systemIndex], CSharpSyntaxTree.ParseText(GeneratorCorpus.Module(GeneratorCorpus.Modules / 2, OneNamespace)
            .Replace("p.V += 1;", "p.V += 2;"), parse, path: $"File{_systemIndex}.cs")];
        _unrelated = [_trees[_unrelatedIndex], CSharpSyntaxTree.ParseText(GeneratorCorpus.Unrelated(1), parse, path: $"File{_unrelatedIndex}.cs")];
        _references = GeneratorCorpus.References();

        _compilation = _ideCompilation = NewCompilation();
        _ideDriver = GeneratorCorpus.Driver(IncrementalGeneratorOutputKind.Implementation).RunGenerators(_ideCompilation);
        _driver = GeneratorCorpus.Driver().RunGeneratorsAndUpdateCompilation(_compilation, out var generated, out _);
        _generated = (CSharpCompilation)generated;
        if (_driver.GetRunResult().GeneratedTrees.Length < GeneratorCorpus.Modules * 15)
            throw new InvalidOperationException("The generators skipped the benchmark's code.");
    }

    private CSharpCompilation NewCompilation() => GeneratorCorpus.Compilation(_trees, _references);

    /// <summary>A first run: every model built, every file emitted.</summary>
    [Benchmark(Baseline = true)]
    public GeneratorDriver Cold() => GeneratorCorpus.Driver().RunGenerators(NewCompilation());

    /// <summary>A keystroke in a [ForEach] method's body: the IDE's next compilation, the previous run's driver.</summary>
    [Benchmark]
    public GeneratorDriver EditMethodBody() => _driver = Edit(ref _compilation, _driver, _systemIndex, _system);

    /// <summary><see cref="EditMethodBody"/> as an IDE runs it while typing: implementation outputs skipped.</summary>
    [Benchmark]
    public GeneratorDriver EditMethodBodyIde() => _ideDriver = Edit(ref _ideCompilation, _ideDriver, _systemIndex, _system);

    /// <summary>A keystroke in a file with no EasyEcs code.</summary>
    [Benchmark]
    public GeneratorDriver EditUnrelatedFile() => _driver = Edit(ref _compilation, _driver, _unrelatedIndex, _unrelated);

    private GeneratorDriver Edit(ref CSharpCompilation compilation, GeneratorDriver driver, int index, SyntaxTree[] versions)
    {
        var current = compilation.SyntaxTrees[index];
        compilation = compilation.ReplaceSyntaxTree(current, versions[++_edits & 1]);
        return driver.RunGenerators(compilation);
    }

    /// <summary>The compiler's own diagnostics of the generated compilation (a new one: nothing bound yet).</summary>
    [Benchmark]
    public int Compile() => Fresh().GetDiagnostics().Length;

    /// <summary>
    /// <see cref="Compile"/> with the analyzer, as a build runs it: the difference is what the analyzer costs a build.
    /// </summary>
    [Benchmark]
    public async Task<int> CompileAndAnalyze() =>
        (await Fresh().WithAnalyzers(EasyEcsAnalyzer.All).GetAllDiagnosticsAsync()).Length;

    private Compilation Fresh() => _generated.RemoveAllSyntaxTrees().AddSyntaxTrees(_generated.SyntaxTrees);
}

/// <summary>
/// The generated text of <see cref="GeneratorBenchmark"/>'s corpus as a column: the bytes and files every output of one
/// run holds (what a build compiles; GeneratedTextTests counts its dead lines). Computed once per corpus layout in the
/// process that prints the summary.
/// </summary>
public sealed class GeneratedBytesColumn : IColumn
{
    private static readonly Dictionary<bool, string> Values = [];

    public string Id => "GeneratedBytes";
    public string ColumnName => "Output";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => false;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "The generated text of one run: bytes in files";
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => Of(benchmarkCase.Parameters["OneNamespace"] is true);
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => GetValue(summary, benchmarkCase);

    private static string Of(bool oneNamespace)
    {
        if (Values.TryGetValue(oneNamespace, out var value)) return value;
        var trees = GeneratorCorpus.Sources(oneNamespace).Select((s, i) => CSharpSyntaxTree.ParseText(s, GeneratorCorpus.Parse, path: $"File{i}.cs"));
        var generated = GeneratorCorpus.Driver().RunGenerators(GeneratorCorpus.Compilation([.. trees], GeneratorCorpus.References()))
            .GetRunResult().GeneratedTrees;
        long bytes = 0;
        foreach (var tree in generated) bytes += Encoding.UTF8.GetByteCount(tree.GetText().ToString());
        return Values[oneNamespace] = $"{bytes / 1024.0:N0} KiB in {generated.Length}";
    }
}

public sealed class GeneratedBytesColumnAttribute() : ColumnConfigBaseAttribute(new GeneratedBytesColumn());
