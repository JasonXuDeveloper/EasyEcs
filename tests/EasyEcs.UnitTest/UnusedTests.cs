using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [ForEach] and [EntityMethod] methods are called by generated code through UnsafeAccessor, which IDEs do not see as a
/// use: the analyzer suppresses Roslyn's "unused private member", Rider reads [MeansImplicitUse].
/// </summary>
public class UnusedTests
{
    // Stands in for the IDE's analyzers (not referenced by the tests): IDE0051 / IDE0052 on every private method, IDE0060
    // on every parameter. Run by the test only, so the rules for shipped analyzers do not apply.
#pragma warning disable RS1001, RS1017, RS1036, RS2008
    private sealed class PrivateMethods(string id = "IDE0051") : DiagnosticAnalyzer
    {
        private readonly DiagnosticDescriptor _rule = new(id, "Remove unused private members", "'{0}' is unused", "Style", DiagnosticSeverity.Warning, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(c =>
            {
                var method = (MethodDeclarationSyntax)c.Node;
                if (c.SemanticModel.GetDeclaredSymbol(method) is { DeclaredAccessibility: Accessibility.Private } symbol)
                    c.ReportDiagnostic(Diagnostic.Create(_rule, method.Identifier.GetLocation(), symbol.Name));
            }, Microsoft.CodeAnalysis.CSharp.SyntaxKind.MethodDeclaration);
        }
    }

    private sealed class Parameters : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("IDE0060", "Remove unused parameter", "'{0}' is unused", "Style", DiagnosticSeverity.Warning, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(static c =>
                c.ReportDiagnostic(Diagnostic.Create(Rule, ((ParameterSyntax)c.Node).Identifier.GetLocation(), ((ParameterSyntax)c.Node).Identifier.ValueText)),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.Parameter);
        }
    }
#pragma warning restore RS1001, RS1017, RS1036, RS2008

    private static async Task<System.Collections.Generic.Dictionary<string, bool>> Suppressed(string source, DiagnosticAnalyzer ide)
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(source), out var output, out _);
        var options = new CompilationWithAnalyzersOptions(null, null, concurrentAnalysis: false, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
        var diagnostics = await output.WithAnalyzers([ide, new UnusedSuppressor()], options).GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(d => d.Id.StartsWith("IDE")).GroupBy(d => d.GetMessage()).ToDictionary(g => g.Key, g => g.All(d => d.IsSuppressed));
    }

    // [Drain] methods and hooks are called through UnsafeAccessor too; IDE0052 (assigned, never read) is suppressed alike.
    [TestCase("IDE0051")]
    [TestCase("IDE0052")]
    public async Task RecordMethodsAndHooksAreNotUnused(string id)
    {
        var suppressed = await Suppressed("""
            public record struct Hit(int V);
            [EcsSystem]
            public ref struct Life
            {
                static void OnCreate(World world) { }
                static void OnUpdate(World world) { }
                static void Plain(World world) { }
            }
            public static class Log { [Drain] static void Write(in Hit hit) { } static void Other(in Hit hit) { } }
            """, new PrivateMethods(id));
        Assert.That(suppressed, Is.EquivalentTo(new System.Collections.Generic.Dictionary<string, bool>
        {
            ["'OnCreate' is unused"] = true, ["'OnUpdate' is unused"] = true, ["'Write' is unused"] = true,
            ["'Plain' is unused"] = false, ["'Other' is unused"] = false, // plain methods stay reported
        }));
    }

    // IDE0060 on a [ForEach] parameter is EECS026's (one mistake, one diagnostic); a caller's value, and a plain method's
    // parameter, stay Roslyn's.
    [Test]
    public async Task UnusedParametersOfAPassAreEECS026s()
    {
        var suppressed = await Suppressed("""
            public struct Hp : IComponent { public int V; }
            public struct Armor : IComponent { public int V; }
            public static class Regen
            {
                [ForEach] public static void Heal(ref Hp hp, in Armor armor, int by) => hp.V++;
                [EntityMethod] public static void Drink(ref Hp hp, in Armor worn) => hp.V++;
                public static void Plain(int unused) { }
            }
            """, new Parameters());
        Assert.That(suppressed, Is.EquivalentTo(new System.Collections.Generic.Dictionary<string, bool>
        {
            ["'hp' is unused"] = true, ["'armor' is unused"] = true, ["'worn' is unused"] = true,
            ["'by' is unused"] = false, ["'unused' is unused"] = false,
        }));
    }

    [Test]
    public void TheJustificationsNameEveryMethodTheyCover()
    {
        foreach (var suppression in new UnusedSuppressor().SupportedSuppressions.Take(2))
            Assert.That(suppression.Justification.ToString(), Does.Contain("[ForEach]").And.Contain("[EntityMethod]").And.Contain("[Drain]").And.Contain("OnUpdate"));
    }

    [Test]
    public async Task MethodsTheGeneratedCodeCallsAreNotUnused()
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile("""
            public struct Hp : IComponent { public int V; }
            [EcsSystem]
            public ref struct Regen
            {
                [ForEach] static void Heal(ref Hp hp) => hp.V++;
                static void Helper() { }
            }
            public static class Potions
            {
                [EntityMethod] static void Drink(ref Hp hp, int amount) => hp.V += amount;
            }
            """), out var output, out _);

        var options = new CompilationWithAnalyzersOptions(null, null, concurrentAnalysis: false, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
        var diagnostics = await output.WithAnalyzers([new PrivateMethods(), new UnusedSuppressor()], options)
            .GetAnalyzerDiagnosticsAsync();

        var suppressed = diagnostics.Where(d => d.Id == "IDE0051").ToDictionary(d => d.GetMessage(), d => d.IsSuppressed);
        Assert.That(suppressed, Is.EquivalentTo(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, bool>("'Heal' is unused", true),
            new System.Collections.Generic.KeyValuePair<string, bool>("'Helper' is unused", false), // a plain method stays reported
            new System.Collections.Generic.KeyValuePair<string, bool>("'Drink' is unused", true),
        }));
    }

    [TestCase(typeof(ForEachAttribute))]
    [TestCase(typeof(EntityMethodAttribute))]
    public void RiderSeesTheMethodsAsUsed(System.Type attribute) =>
        Assert.That(attribute.GetCustomAttributes(false).Select(a => a.GetType().FullName),
            Does.Contain("JetBrains.Annotations.MeansImplicitUseAttribute"));
}
