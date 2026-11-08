using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EasyEcs.Generator;

/// <summary>
/// Suppresses "private member is unused" (IDE0051, IDE0052) on [ForEach], [EntityMethod], [Drain] and hook (a system's
/// OnCreate, OnUpdate, OnDestroy) methods: the generated code calls them through UnsafeAccessor, by name, which the IDE's
/// analyzers do not see as a use. Rider and ReSharper read the [MeansImplicitUse] those attributes carry instead. Also
/// "unused parameter" (IDE0060) on a [ForEach] or [EntityMethod] method's parameters that EECS026 checks (all but a
/// caller's values): one mistake, one diagnostic, and EECS026's carries the fix (a filter in its place, or the parameter gone).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedSuppressor : DiagnosticSuppressor
{
    private const string Called = "The generated code calls [ForEach], [EntityMethod], [Drain] and hook (OnCreate, OnUpdate, OnDestroy) methods through UnsafeAccessor";

    public static readonly SuppressionDescriptor Unused = new("EECSS01", "IDE0051", Called);

    public static readonly SuppressionDescriptor Unread = new("EECSS02", "IDE0052", Called);

    public static readonly SuppressionDescriptor UnusedParameter = new("EECSS03", "IDE0060",
        "EECS026 reports an unused parameter of a [ForEach] or [EntityMethod] method, with its fix");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = [Unused, Unread, UnusedParameter];

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var tree = diagnostic.Location.SourceTree;
            if (tree == null) continue;
            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan);
            var declared = context.GetSemanticModel(tree).GetDeclaredSymbol(node, context.CancellationToken);
            if (diagnostic.Id == UnusedParameter.SuppressedDiagnosticId)
            {
                if (declared is IParameterSymbol { ContainingSymbol: IMethodSymbol owner } parameter && Symbols.IsGeneratedJob(owner)
                    && Symbols.Classify(parameter, Symbols.IsRunner(owner)) is not (Symbols.ParamKind.Context or Symbols.ParamKind.Invalid))
                    context.ReportSuppression(Suppression.Create(UnusedParameter, diagnostic));
                continue;
            }
            if (declared is not IMethodSymbol method
                || !Symbols.IsGeneratedJob(method) && !Symbols.IsDrain(method) && Symbols.Hook(method) == Symbols.HookKind.None)
                continue;
            context.ReportSuppression(Suppression.Create(diagnostic.Id == Unused.SuppressedDiagnosticId ? Unused : Unread, diagnostic));
        }
    }
}
