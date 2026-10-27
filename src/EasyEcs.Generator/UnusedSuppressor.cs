using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EasyEcs.Generator;

/// <summary>
/// Suppresses "private member is unused" (IDE0051, IDE0052) on [ForEach], [ForEntity], [ForEachRecord] and hook (a system's
/// OnCreate, OnUpdate, OnDestroy) methods: the generated code calls them through UnsafeAccessor, by name, which the IDE's
/// analyzers do not see as a use. Rider and ReSharper read the [MeansImplicitUse] those attributes carry instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedSuppressor : DiagnosticSuppressor
{
    public static readonly SuppressionDescriptor Unused = new("EECSS01", "IDE0051",
        "The generated code calls [ForEach], [ForEntity] and hook methods through UnsafeAccessor");

    public static readonly SuppressionDescriptor Unread = new("EECSS02", "IDE0052",
        "The generated code calls [ForEach], [ForEntity] and hook methods through UnsafeAccessor");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = [Unused, Unread];

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var tree = diagnostic.Location.SourceTree;
            if (tree == null) continue;
            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan);
            if (context.GetSemanticModel(tree).GetDeclaredSymbol(node, context.CancellationToken) is not IMethodSymbol method
                || !Symbols.IsGeneratedJob(method) && !Symbols.IsForEachRecord(method) && Symbols.Hook(method) == Symbols.HookKind.None)
                continue;
            context.ReportSuppression(Suppression.Create(diagnostic.Id == Unused.SuppressedDiagnosticId ? Unused : Unread, diagnostic));
        }
    }
}
