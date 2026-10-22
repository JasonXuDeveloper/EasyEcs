using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// PartGenerator and the rules of the declarations it writes parts for: the part's place (nested, generic, every kind
/// of containing type), EECS062-EECS066, and the caching of its stage.
/// </summary>
public class PartTests
{
    /// <summary>The generated part files and the compilation with them.</summary>
    private static (string Parts, Compilation Output) Generate(string source)
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(source), out var output, out _);
        return (string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Part.g.cs")).Select(t => t.ToString())), output);
    }

    private static string[] Errors(Compilation output) =>
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();

    private static async Task<string[]> Ids(string source) =>
        (await GeneratorTests.Analyze([source])).Where(d => d.Id.StartsWith("EECS") || d.Severity == DiagnosticSeverity.Error)
        .Select(d => d.Id).Distinct().OrderBy(x => x).ToArray();

    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; }",
        "public partial struct Census : global::EasyEcs.IReduction<Census>")]
    [TestCase("namespace Game.Rules { public static partial class Outer { internal partial struct Census : ISingletonComponent { [Sum] public int Alive; } } }",
        "namespace Game.Rules;\n\npublic partial class Outer\n{\n    internal partial struct Census : global::EasyEcs.IReduction<Census>")]
    [TestCase("public partial class Outer<T> where T : struct { public partial record struct Inner<U> : ISingletonComponent { [Sum] public int Alive; } }",
        "public partial class Outer<T>\n{\n    public partial record struct Inner<U> : global::EasyEcs.IReduction<Inner<U>>")]
    [TestCase("public partial record Outer { private protected partial class Mid { protected internal readonly partial struct Census { [Sum] public int Alive { get; init; } } } }",
        "public partial record Outer\n{\n    private protected partial class Mid\n    {\n        protected internal readonly partial struct Census : global::EasyEcs.IReduction<Census>")]
    [TestCase("public partial interface IHolder { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } }",
        "public partial interface IHolder\n{\n    public partial struct Census")]
    [TestCase("public partial record struct Census([Sum] int Alive, [Max] float Oldest) : ISingletonComponent;",
        "public static Census Combine(Census left, Census right) => new Census { Alive = left.Alive + right.Alive, Oldest = global::System.Math.Max(left.Oldest, right.Oldest) };")]
    public void PartsGoInsideEveryContainingType(string source, string expected)
    {
        var (parts, output) = Generate(source);
        Assert.That(parts.Replace("\r", ""), Does.Contain(expected));
        Assert.That(Errors(output), Is.Empty, parts);
    }

    [Test]
    public void EveryAttributeHasItsIdentityAndCombine()
    {
        var (parts, output) = Generate("""
            public partial struct Totals : ISingletonComponent
            {
                [Sum] public int Count;
                [Product] public double Odds;
                [Min] public float Lowest;
                [Max] public long Highest;
                [Min] public byte Least;
                [Sum] public short Small;
                [All] public bool Every;
                [Any] public bool Some;
                [Max] public decimal Price;
            }
            """);
        Assert.That(Errors(output), Is.Empty, parts);
        Assert.That(parts, Does.Contain("public static Totals Identity => new Totals { Count = 0, Odds = 1, Lowest = float.PositiveInfinity, Highest = long.MinValue, "
                                        + "Least = byte.MaxValue, Small = 0, Every = true, Some = false, Price = decimal.MinValue };"));
        Assert.That(parts, Does.Contain("Count = left.Count + right.Count, Odds = left.Odds * right.Odds, Lowest = global::System.Math.Min(left.Lowest, right.Lowest), "
                                        + "Highest = global::System.Math.Max(left.Highest, right.Highest), Least = global::System.Math.Min(left.Least, right.Least), "
                                        + "Small = (short)(left.Small + right.Small), Every = left.Every & right.Every, Some = left.Some | right.Some, "
                                        + "Price = global::System.Math.Max(left.Price, right.Price) };"));
        Assert.That(parts, Does.Contain("[global::System.CodeDom.Compiler.GeneratedCode(\"EasyEcs.Generator\""));
    }

    // EECS062 at each level of the chain, and for a file type; EECS063 and EECS064.
    [TestCase("public struct Census : ISingletonComponent { [Sum] public int Alive; }", "EECS062")]
    [TestCase("public class Outer { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } }", "EECS062")]
    [TestCase("public class Outer<T> { public partial class Mid { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } } }", "EECS062")]
    [TestCase("public partial class Outer { public class Mid { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } } }", "EECS062")]
    [TestCase("file partial struct Census : ISingletonComponent { [Sum] public int Alive; }", "EECS062")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; public static Census Identity => default; }", "EECS063")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; public static Census Combine(Census a, Census b) => a; }", "EECS063")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; public int Dead; }", "EECS064")]               // no attribute
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public bool Alive; }", "EECS064")]                            // a bool summed
    [TestCase("public partial struct Census : ISingletonComponent { [Any] public int Alive; }", "EECS064")]                             // an int or-ed
    [TestCase("public partial struct Census : ISingletonComponent { [Sum, Max] public int Alive; }", "EECS064")]                        // two
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public readonly int Alive; }", "EECS064")]                     // not settable
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive { get; } }", "EECS064")]                      // no setter
    [TestCase("public partial struct Census : ISingletonComponent { public int A; [Sum] public int Alive => A; }", "EECS064")]          // computed
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public string Name; }", "EECS064")]
    public async Task DeclarationsThePartCannotHoldAreReported(string source, string id)
    {
        Assert.That(await Ids(source), Is.EqualTo(new[] { id }), "one diagnostic, and no error of the generated code");
        Assert.That(Generate(source).Parts, Is.Empty, "the generator skips what the analyzer reports");
    }

    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; [Min] public float Lowest; }")]
    [TestCase("public partial record struct Census([Sum] int Alive, [property: Max] long Oldest) : ISingletonComponent;")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive { get; set; } public const int Cap = 4; public static int Shared; }")]
    [TestCase("public partial struct Census : ISingletonComponent, IReduction<Census> { [Sum] public int Alive; }")]  // the interface written too
    public async Task ValidDeclarationsCompileWithoutDiagnostics(string source) =>
        Assert.That(await Ids(source), Is.Empty);

    // A hand-written reduction the attributes declare (EECS065), and one whose Identity does not fit (EECS066).
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public float L; public static C Identity => new C { L = float.MaxValue }; public static C Combine(C a, C b) => new C { N = a.N + b.N, L = System.Math.Min(a.L, b.L) }; }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public static C Identity => default; public static C Combine(C a, C b) { a.N += b.N; return a; } }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public float H; public static C Identity { get; } = new() { H = float.NegativeInfinity }; public static C Combine(C a, C b) => new() { H = a.H > b.H ? a.H : b.H }; }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public bool Any; public static C Identity => new C(); public static C Combine(C a, C b) => new C { Any = a.Any || b.Any }; }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public float L; public static C Identity => default; public static C Combine(C a, C b) => new C { L = System.MathF.Min(a.L, b.L) }; }", "EECS066")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int Lo, Hi; public static C Identity => new C { Lo = int.MaxValue }; public static C Combine(C a, C b) => new C { Lo = System.Math.Min(a.Lo, b.Lo), Hi = System.Math.Max(a.Hi, b.Hi) }; }", "EECS066")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public static C Identity => new C { N = 1 }; public static C Combine(C a, C b) => new C { N = a.N + b.N }; }", "EECS066")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N, First; public static C Identity => new C { N = 5, First = -1 }; public static C Combine(C a, C b) => new C { N = a.N * b.N, First = a.First < 0 ? b.First : a.First }; }", "EECS066")] // the other field is the user's
    public async Task HandWrittenReductionsAreRead(string source, string id) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { id }));

    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N, First; public static C Identity => new C { First = -1 }; public static C Combine(C a, C b) => new C { N = a.N + b.N, First = a.First < 0 ? b.First : a.First }; }")] // a first value: not field-wise
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; static readonly C Start = new C { N = 9 }; public static C Identity => Start; public static C Combine(C a, C b) => new C { N = a.N + b.N }; }")]  // not a constant
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public static C Identity => default; public static C Combine(C a, C b) => new C { N = a.N - b.N }; }")]   // no attribute says it
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public float L; public static C Identity => new C { L = float.MaxValue }; public static C Combine(C a, C b) => new C { L = System.Math.Min(a.L, b.L) }; }", "EECS065")] // MaxValue is an identity of a float minimum
    public async Task OtherReductionsAreLeftAlone(string source, string only = null) =>
        Assert.That(await Ids(source), only == null ? Is.Empty : Is.EqualTo(new[] { only }));

    [Test]
    public void AChangedDeclarationWritesItsPartAlone()
    {
        var first = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic partial struct A : ISingletonComponent { [Sum] public int N; }", GeneratorTests.Parse);
        var second = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic partial struct B : ISingletonComponent { [Sum] public int N; }", GeneratorTests.Parse);
        var compilation = GeneratorTests.Compile().AddSyntaxTrees(first, second);
        var driver = GeneratorTests.Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(second,
            CSharpSyntaxTree.ParseText("using EasyEcs;\npublic partial struct B : ISingletonComponent { [Max] public int N; }", GeneratorTests.Parse)));

        var reasons = GeneratorTests.Steps(driver)["Parts"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // A
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New), Is.EqualTo(1));  // B
    }
}
