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
    // Stands in for the IDE's analyzer (not referenced by the tests): IDE0051 on every private method. Run by the test
    // only, so the rules for shipped analyzers do not apply.
#pragma warning disable RS1001, RS1036, RS2008
    private sealed class PrivateMethods : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("IDE0051", "Remove unused private members", "'{0}' is unused", "Style", DiagnosticSeverity.Warning, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(static c =>
            {
                var method = (MethodDeclarationSyntax)c.Node;
                if (c.SemanticModel.GetDeclaredSymbol(method) is { DeclaredAccessibility: Accessibility.Private } symbol)
                    c.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), symbol.Name));
            }, Microsoft.CodeAnalysis.CSharp.SyntaxKind.MethodDeclaration);
        }
    }
#pragma warning restore RS1001, RS1036, RS2008

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
