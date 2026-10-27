using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of pass fusion (EECS093, EECS094; the rule itself: <see cref="Fusion"/>): consecutive [ForEach] methods of a
/// system run as one walk when that is provably the same result, and the generator fuses them exactly when
/// <see cref="Fusion.Problem(IReadOnlyList{FusionSide}, FusionSide)"/> finds nothing, so what it leaves apart is what
/// these rules report: a near miss as a message (EECS093), a method that requires the walk as an error (EECS094).
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor NearFusion = new("EECS093", "Two passes over the same rows that do not share one walk",
        "'{0}' walks the rows of '{1}' again: they would share one walk (each row loaded once), but {2}",
        Category, DiagnosticSeverity.Info, true,
        "Consecutive [ForEach] methods of a system that visit the same rows run as one walk: each row is loaded into cache " +
        "once and every method runs on it in turn (pass fusion). The generator fuses them when one walk gives the results of " +
        "separate passes. This message names what keeps two methods apart when they almost qualify: filters that differ by " +
        "one component, different Parallel / ChunkSize / Slices, a Lookup<T> of a component the other writes, a singleton " +
        "or change filter one reads and the other writes. Change it if the difference is not needed; a pass that must see " +
        "the whole result of the other stays apart, and FusionMode.Off silences the message.", helpLinkUri: Help("EECS093"));

    public static readonly DiagnosticDescriptor FusionRequired = new("EECS094", "A method that requires one walk does not get it",
        "'{0}' has FusionMode.Require but does not share the walk of {1}: {2}",
        Category, DiagnosticSeverity.Error, true,
        "[ForEach(FusionMode = FusionMode.Require)] asks for a method to share one walk with the [ForEach] method declared " +
        "before it in its system. The generator fuses them only when one walk gives the results of separate passes; the " +
        "message names what keeps them apart.", helpLinkUri: Help("EECS094"));

    internal static void InitializeFusion(AnalysisContext context) =>
        context.RegisterSyntaxNodeAction(AnalyzeFusion, SyntaxKind.StructDeclaration);

    /// <summary>EECS093 and EECS094 over the [ForEach] methods of a system, in the groups the generator fuses them into.</summary>
    private static void AnalyzeFusion(SyntaxNodeAnalysisContext context)
    {
        var declaration = (StructDeclarationSyntax)context.Node;
        // A system is a ref struct; its [EcsSystem] may sit on another part of a partial type.
        if (!declaration.Modifiers.Any(SyntaxKind.RefKeyword) || context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type
            || !Symbols.GeneratesSystem(type)
            || type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) != declaration) // a partial type: once
            return;

        var group = new List<FusionSide>();
        string? before = null;
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method || Symbols.IsForEntity(method) || !Symbols.IsValidForEach(method)
                || method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not MethodDeclarationSyntax syntax)
                continue;
            var model = Symbols.ModelOf(syntax.SyntaxTree, context.SemanticModel);
            bool stable = BodyFacts.IsStable(method, syntax, model);
            var side = FusionSide.Of(method, stable, BodyFacts.OnlyIdentifies(method, syntax, model),
                Symbols.IsParallel(method) && BodyFacts.ParallelProblem(method, syntax, model) == null);
            var problem = group.Count == 0 ? "it is the first [ForEach] method of its system" : Fusion.Problem(group, side);
            if (problem == null)
            {
                group.Add(side);
                before = side.Name;
                continue;
            }

            var location = syntax.Identifier.GetLocation();
            if (Symbols.Fusion(method) == 1)
                context.ReportDiagnostic(Diagnostic.Create(FusionRequired, location, side.Name, before == null ? "no method" : $"'{before}'", problem));
            else if (group.Count > 0 && NearMiss(group, side))
                context.ReportDiagnostic(Diagnostic.Create(NearFusion, location, side.Name, before, problem));
            group.Clear();
            group.Add(side);
            before = side.Name;
        }
    }

    /// <summary>
    /// Whether <paramref name="side"/> almost joins <paramref name="group"/>: neither can never fuse (a structural change,
    /// [Consume], the World...), and the filters are the same (something else keeps them apart) or differ by one
    /// component and nothing else does.
    /// </summary>
    private static bool NearMiss(List<FusionSide> group, FusionSide side)
    {
        var last = group[group.Count - 1];
        if (side.Blocker != null || group.Any(g => g.Blocker != null)) return false;
        return Fusion.FilterDistance(last, side) switch
        {
            0 => true,
            1 => Fusion.Problem(group, side with { With = last.With, Without = last.Without, WithAny = last.WithAny }) == null,
            _ => false,
        };
    }
}

/// <summary>The rules of pass fusion (EasyEcsAnalyzer.Fusion.cs), as one analyzer: Roslyn skips an analyzer only when every rule it reports is suppressed, so a
/// NoWarn of these rules saves their cost.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FusionAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        EasyEcsAnalyzer.FusionRequired, EasyEcsAnalyzer.NearFusion);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeFusion(context);
}
