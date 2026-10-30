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
/// own component, read through a Lookup&lt;T&gt; or walked as a query, and tests their squared distance; a Warning with its fix where
/// <see cref="PairRewrite"/> rewrites it, else a suggestion).
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor BadPairMethod = new("EECS105", "A pair method that cannot run per pair",
        "Pair method '{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "A [ForEach] method with [Neighbor] parameters runs for every pair of its row and a neighbor within the cutoff its " +
        "body tests. The pass runs over every row, on several threads (Parallel) when it writes no neighbor or writes both " +
        "sides of each pair (its chunks colored apart, the same result on any number of threads): no Slices, change filters " +
        "or [Consume]. It takes " +
        "components of its row (by 'in', 'ref', or Mut<T> on one thread: only the rows it writes are stamped), [Neighbor] " +
        "components (they say which entities are neighbors) by 'in', or by 'ref' to write them, each once, the " +
        "neighbor's Entity, singletons, values and Reduce<T> results (each chunk's combined in chunk order); it writes no component it reads of the " +
        "neighbor and no component of the neighbor it reads of its row, so what either side sees does not depend on the " +
        "order of the pairs.",
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
        Category, DiagnosticSeverity.Warning, true,
        "A method that loops over other entities of its own component (a grid of cells, an index of chunks, a list of " +
        "ids, each read through a Lookup<T>, or a walk of world.Read / Query) and tests their squared distance to the " +
        "row's is a neighbor search. A pair " +
        "method takes the neighbor's components as [Neighbor] parameters and tests the distance once: the pass keeps the " +
        "tables in space order and visits clusters of rows against the rows within the cutoff, a vector of rows per " +
        "instruction where the body qualifies, with no grid to build or keep. The code fix rewrites the method when the " +
        "search is its whole body and each candidate's statements read nothing of the search but the candidate: the loop " +
        "over candidates becomes [Neighbor] parameters (a 'continue' a 'return', the self skip a term of the cutoff test), " +
        "and the code that builds the grid can then go. Other searches (a sum kept in locals and written after the loop, " +
        "statements after it) are reported as a suggestion, to rewrite by hand.",
        helpLinkUri: Help("EECS107"));

    internal static void InitializePairs(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzePairDeclaration, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(AnalyzePairBody, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzePairDeclaration(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!Symbols.IsValidForEach(method) && !Symbols.IsEntityMethod(method) || !PairForm.IsPairMethod(method)) return;
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
            if (PairForm.Problem(method) == null
                && PairForm.ReadCutoff(method, syntax, context.SemanticModel, static (_, _) => "", context.CancellationToken, out string? reason) == null)
                context.ReportDiagnostic(Diagnostic.Create(NoPairCutoff, syntax.Identifier.GetLocation(), method.Name, reason));
            return;
        }

        if (PairForm.HandWritten(method, syntax, context.SemanticModel, context.CancellationToken) is not { } how) return;
        // A Warning with its fix when the rewrite is mechanical (PairRewrite), a suggestion otherwise.
        var rewrite = PairRewrite.Of(method, syntax, context.SemanticModel, context.CancellationToken);
        var properties = rewrite == null ? null : ImmutableDictionary<string, string?>.Empty
            .Add("Fix", "Pairs").Add("PairParameters", rewrite.Parameters).Add("PairBody", rewrite.Body);
        context.ReportDiagnostic(Diagnostic.Create(HandWrittenNeighbors, syntax.Identifier.GetLocation(),
            rewrite != null ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, null, properties, method.Name, how));
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
