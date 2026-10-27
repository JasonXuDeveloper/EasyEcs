using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of pair methods (<c>[Neighbor]</c> parameters, <see cref="PairForm"/>): a declaration that cannot run per
/// pair (EECS105), a body whose cutoff test the generator cannot read, so every pair runs (EECS106), and a neighbor
/// search written by hand that the pair form writes (EECS107: a [ForEach] method that loops over other entities of its
/// own component, read through a Lookup&lt;T&gt;, and tests their squared distance).
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor BadPairMethod = new("EECS105", "A pair method that cannot run per pair",
        "Pair method '{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "A [ForEach] method with [Neighbor] parameters runs for every pair of its row and a neighbor within the cutoff its " +
        "body tests. The pass runs on one thread over every row: no Parallel, Slices, change filters or [Consume]. It takes " +
        "components of its row, [Neighbor] components by 'in' (they say which entities are neighbors) and the neighbor's " +
        "Entity, singletons and values; it writes no component it reads of the neighbor, so what a neighbor sees does not " +
        "depend on the order of the pairs.",
        helpLinkUri: Help("EECS105"));

    public static readonly DiagnosticDescriptor NoPairCutoff = new("EECS106", "A pair method without a cutoff test the generator reads",
        "Pair method '{0}' runs for every pair of rows: {1}",
        Category, DiagnosticSeverity.Warning, true,
        "The pass visits only the neighbors within the cutoff the method tests first: 'if (d2 >= r2) return;' after the " +
        "local declarations (or the rest of the body under 'if (d2 < r2)'), d2 the sum of the squared differences of the " +
        "same float fields of a component of the row and of the neighbor (dx * dx + dy * dy, Vector2/3.DistanceSquared or " +
        "(a - b).LengthSquared() of a vector field), r2 a value of constants, readonly statics, singletons and values, or " +
        "of the row's 'in' components (a cutoff per row). Without such a test, every row meets every neighbor: O(n * m).",
        helpLinkUri: Help("EECS106"));

    public static readonly DiagnosticDescriptor HandWrittenNeighbors = new("EECS107", "A neighbor search written by hand",
        "[ForEach] method '{0}' searches the neighbors of its row by hand ({1}): a pair method ([Neighbor] parameters) does it",
        Category, DiagnosticSeverity.Info, true,
        "A method that loops over other entities of its own component (a grid of cells, an index of chunks, a list of " +
        "ids), reads each through a Lookup<T> and tests its squared distance to the row's is a neighbor search. A pair " +
        "method takes the neighbor's components as [Neighbor] parameters and tests the distance once: the pass keeps the " +
        "tables in space order and visits windows of rows against the rows within the cutoff, a vector of rows per " +
        "instruction where the body qualifies, with no grid to build or keep. The grid's upkeep goes with it, so no code " +
        "fix rewrites the method.",
        helpLinkUri: Help("EECS107"));

    internal static void InitializePairs(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzePairDeclaration, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(AnalyzePairBody, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzePairDeclaration(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!Symbols.IsValidForEach(method) && !Symbols.IsForEntity(method) || !PairForm.IsPairMethod(method)) return;
        if (PairForm.Problem(method) is { } problem)
            context.ReportDiagnostic(Diagnostic.Create(BadPairMethod, method.Locations.FirstOrDefault(), method.Name, problem));
    }

    private static void AnalyzePairBody(SyntaxNodeAnalysisContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (syntax.Body == null && syntax.ExpressionBody == null || syntax.AttributeLists.Count == 0) return; // syntax first: most methods leave here
        if (context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } method || !Symbols.IsValidForEach(method)) return;
        if (PairForm.IsPairMethod(method))
        {
            if (PairForm.Problem(method) == null && PairForm.Read(method, syntax, context.SemanticModel, context.CancellationToken, out string? reason) == null)
                context.ReportDiagnostic(Diagnostic.Create(NoPairCutoff, syntax.Identifier.GetLocation(), method.Name, reason));
            return;
        }

        if (PairForm.HandWritten(method, syntax, context.SemanticModel, context.CancellationToken) is { } how)
            context.ReportDiagnostic(Diagnostic.Create(HandWrittenNeighbors, syntax.Identifier.GetLocation(), method.Name, how));
    }
}

/// <summary>The rules of pair methods (EasyEcsAnalyzer.Pairs.cs), as one analyzer.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PairAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        EasyEcsAnalyzer.BadPairMethod, EasyEcsAnalyzer.NoPairCutoff, EasyEcsAnalyzer.HandWrittenNeighbors);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializePairs(context);
}
