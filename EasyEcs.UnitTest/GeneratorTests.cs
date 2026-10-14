using System.Collections.Immutable;
using System.Linq;
using EasyEcs.Core;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Drives EachGenerator through Roslyn to check its diagnostics and that valid input compiles cleanly.</summary>
public class GeneratorTests
{
    private const string Usings = """
        using EasyEcs.Core;
        using EasyEcs.Core.Components;
        using EasyEcs.Core.Systems;
        public struct P : IComponent { public int X; }
        public struct T : IComponent { }

        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static (ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Compilation) Run(string source)
    {
        var references = System.AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location));

        var compilation = CSharpCompilation.Create("GeneratorTest",
            [CSharpSyntaxTree.ParseText(Usings + source, ParseOptions)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver.Create([new EachGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return (generatorDiagnostics, errors);
    }

    [Test]
    public void ValidSystemCompiles()
    {
        var (generator, compilation) = Run("""
            public partial class S : SystemBase
            {
                [Each, Without<T>] static void A(ref P p) => p.X++;
                [Each, With<T>] void B(int entityId, in P p, EntityRef e, World w) { }
            }
            """);
        Assert.That(generator, Is.Empty);
        Assert.That(compilation, Is.Empty, string.Join("\n", compilation));
    }

    [TestCase("public class S : SystemBase { [Each] static void A(ref P p) { } }", "EECS001")]
    [TestCase("public partial class S : SystemBase { [Each] static void A(ref P p, string s) { } }", "EECS002")]
    [TestCase("public partial class S : SystemBase { [Each] static void A(int entityId) { } }", "EECS003")]
    [TestCase("public partial class S<X> : SystemBase { [Each] static void A(ref P p) { } }", "EECS004")]
    [TestCase("public partial class S { [Each] static void A(ref P p) { } }", "EECS005")]
    public void InvalidInputReportsDiagnostic(string source, string id)
    {
        var (generator, _) = Run(source);
        Assert.That(generator.Select(d => d.Id), Does.Contain(id));
    }
}
