using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// EECS115-117: stepping tiles (<c>ref Tile&lt;TCell, TTile&gt;</c>, Jacobi or [GaussSeidel]). A step the generator cannot
/// write (Error), a second buffer and its commit written by hand where the tile could step itself (Warning), and a
/// relaxation stepped one cell per neighbor per pass that [GaussSeidel] would settle in place (Info).
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor BadStep = new("EECS115", "A tile step the generator cannot write",
        "{0} {1}",
        Category, DiagnosticSeverity.Error, true,
        "A [ForEach] method that takes a tile by ref steps it: a Jacobi step writes the rows of the tile's second " +
        "buffer, which the pass swaps with its cells once every tile stepped; a [GaussSeidel] step writes the cells in " +
        "place, color by color, sweep after sweep until the tile settles. So the pass steps one tile per entity over the " +
        "whole grid ([EntityMethod], Slices and [Consume] cannot), [GaussSeidel] needs that tile and a key of integer fields " +
        "to color the tiles by, and Write is for the tile the method steps (a tile taken by value throws).",
        helpLinkUri: Help("EECS115"));

    public static readonly DiagnosticDescriptor HandCommit = new("EECS116", "A tile's step and its commit written by hand",
        "{0} copies {1} into {2}, which {3} steps into {1}: take the tile by ref (ref Tile<{2}, ...>) and write its rows with Write(row); the pass swaps the two buffers, O(1) per tile, without {1} or this copy",
        Category, DiagnosticSeverity.Warning, true,
        "A stencil over tiles reads the cells as they were before the pass, so it writes its result into a second buffer " +
        "(NextHeat beside Heat), and a commit copies it back once every tile stepped: two buffer types, a pass that " +
        "copies every cell, and a stamp of every tile. Taken by ref, the tile steps itself: Write(row) hands out the row " +
        "of a second buffer the pass keeps per tile, the pass swaps it with the cells (O(1) per tile), zeroes the rows " +
        "the step skipped, and stamps only the tiles that changed, which also lets the tiles that cannot change sleep. " +
        "Reported for a [ForEach] method whose body is only the copy, at the end of the compilation.",
        helpLinkUri: Help("EECS116"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor SuggestGaussSeidel = new("EECS117", "A relaxation stepped one cell per pass",
        "{0} relaxes its tile with Min / Max, so a value crosses one cell per pass: with [GaussSeidel] a pass settles each tile in place and carries the field across the grid, if no system reads the field between passes",
        Category, DiagnosticSeverity.Info, true,
        "A Jacobi step reads the cells as the pass before left them, so a distance field, a flood fill or a scent moves " +
        "one cell per pass and needs as many passes as cells it crosses. [GaussSeidel] writes the cells in place: each " +
        "sweep reads the rows it already wrote, the tile sweeps until it settles, the tiles step color by color so " +
        "each color reads the ones before it, and a settled tile sleeps. The result after one pass is not a Jacobi " +
        "step: keep Jacobi when another system reads the field after each pass as a step in time (a wave, heat).",
        helpLinkUri: Help("EECS117"));

    internal static void InitializeTiles(AnalysisContext context) => context.RegisterCompilationStartAction(AnalyzeTiles);

    private static void AnalyzeTiles(CompilationStartAnalysisContext start)
    {
        // EECS116: the steps that write a second buffer beside a tile (its next cells, its cells), and the plain copies.
        var steps = new ConcurrentQueue<(ITypeSymbol Next, ITypeSymbol Cells, IMethodSymbol Method)>();
        var copies = new ConcurrentQueue<(ITypeSymbol Next, ITypeSymbol Cells, IMethodSymbol Method)>();

        start.RegisterSymbolAction(context =>
        {
            var method = (IMethodSymbol)context.Symbol;
            if (!Symbols.IsForEach(method) && !Symbols.IsEntityMethod(method)) return;
            if (Symbols.StepProblem(method) is { } problem)
                context.ReportDiagnostic(Diagnostic.Create(BadStep, method.Locations.FirstOrDefault(), Symbols.Describe(method), problem));
            foreach (var tile in method.Parameters)
            {
                if (tile.RefKind == RefKind.Ref || Symbols.TileElements(tile.Type) is not { } elements) continue;
                foreach (var p in method.Parameters)
                    if (WrittenBuffer(p) is { } next) steps.Enqueue((next, elements.Cell, method));
            }
        }, SymbolKind.Method);

        start.RegisterOperationBlockAction(context =>
        {
            if (context.OwningSymbol is not IMethodSymbol method || !Symbols.IsForEach(method)) return;
            foreach (var block in context.OperationBlocks)
            {
                if (PlainCopy(method, block) is { } copy) copies.Enqueue((copy.Next, copy.Cells, method));
                if (method.Parameters.FirstOrDefault(Symbols.IsSteppingTile) is { } stepped && !Symbols.IsGaussSeidel(method) && Relaxes(block))
                    context.ReportDiagnostic(Diagnostic.Create(SuggestGaussSeidel, method.Locations.FirstOrDefault(),
                        CanColor(method, stepped) ? GaussSeidelFix : null, Symbols.Describe(method)));
            }
        });

        // EECS115: Write on a tile the method takes by value (no step of it runs: it throws).
        start.RegisterOperationAction(context =>
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod is not { Name: "Write" } write || !Symbols.Named(write.ContainingType.OriginalDefinition, "EasyEcs.Tile<TCell, TTile>")) return;
            if (invocation.Instance is not IParameterReferenceOperation { Parameter: { RefKind: not RefKind.Ref } tile }
                || tile.ContainingSymbol is not IMethodSymbol method || !Symbols.IsForEach(method)) return;
            context.ReportDiagnostic(Diagnostic.Create(BadStep, invocation.Syntax.GetLocation(), Symbols.Describe(method),
                $"writes '{tile.Name}', a tile it takes by value: Write is for the tile the method steps (ref Tile<...>), and throws for another"));
        }, OperationKind.Invocation);

        start.RegisterCompilationEndAction(context =>
        {
            foreach (var (next, cells, method) in copies)
            {
                var step = steps.FirstOrDefault(s => SymbolEqualityComparer.Default.Equals(s.Next, next) && SymbolEqualityComparer.Default.Equals(s.Cells, cells));
                if (step.Method == null) continue;
                context.ReportDiagnostic(Diagnostic.Create(HandCommit, method.Locations.FirstOrDefault(), Symbols.Describe(method),
                    next.Name, cells.Name, Symbols.Describe(step.Method)));
            }
        });
    }

    /// <summary>The element of the buffer a [ForEach] parameter writes (<c>Mut&lt;DynamicBuffer&lt;T&gt;&gt;</c>, <c>ref DynamicBuffer&lt;T&gt;</c>), else null.</summary>
    private static ITypeSymbol? WrittenBuffer(IParameterSymbol p) => Symbols.Classify(p) switch
    {
        Symbols.ParamKind.Mutable => Symbols.BufferElement(Symbols.MutElement(p.Type)!),
        Symbols.ParamKind.Component when p.RefKind == RefKind.Ref => Symbols.BufferElement(p.Type),
        _ => null,
    };

    /// <summary>
    /// A body that is only a copy of one buffer parameter (<c>in DynamicBuffer&lt;TNext&gt;</c>) into another it writes
    /// (TCells), the method taking nothing else: one CopyTo call, through spans and casts.
    /// </summary>
    private static (ITypeSymbol Next, ITypeSymbol Cells)? PlainCopy(IMethodSymbol method, IOperation block)
    {
        if (method.Parameters.Length != 2) return null;
        var statements = block.Descendants().OfType<IExpressionStatementOperation>().ToArray();
        if (statements.Length != 1 || statements[0].Operation is not IInvocationOperation { TargetMethod.Name: "CopyTo" } copy) return null;
        if (block.Descendants().Any(o => o is IConditionalOperation or ILoopOperation or IVariableDeclaratorOperation)) return null;
        var from = copy.Instance?.DescendantsAndSelf().OfType<IParameterReferenceOperation>().Select(r => r.Parameter).Distinct(SymbolEqualityComparer.Default).ToArray();
        var into = copy.Arguments.SelectMany(a => a.DescendantsAndSelf()).OfType<IParameterReferenceOperation>().Select(r => r.Parameter).Distinct(SymbolEqualityComparer.Default).ToArray();
        if (from is not { Length: 1 } || into is not { Length: 1 } || from[0] is not IParameterSymbol source || into[0] is not IParameterSymbol target) return null;
        if (source.RefKind != RefKind.In || Symbols.Classify(source) != Symbols.ParamKind.Component || Symbols.BufferElement(source.Type) is not { } next) return null;
        return WrittenBuffer(target) is { } cells ? (next, cells) : null;
    }

    /// <summary>EECS117's fix: [GaussSeidel] after [ForEach] (EasyEcs.CodeFixes).</summary>
    private static readonly ImmutableDictionary<string, string?> GaussSeidelFix = ImmutableDictionary<string, string?>.Empty.Add("Fix", "GaussSeidel");

    /// <summary>
    /// Whether [GaussSeidel] on <paramref name="method"/> makes a step the generator writes: the step has no problem now
    /// and the grid's key has integer fields to color the tiles by (else the fix would trade EECS117 for EECS115).
    /// </summary>
    private static bool CanColor(IMethodSymbol method, IParameterSymbol stepped) =>
        Symbols.StepProblem(method) == null && Symbols.TileElements(stepped.Type) is { } tile
        && Symbols.TileKey(tile.Grid) is { } key && Symbols.KeyParities(key, "key") is { Count: > 0 };

    /// <summary>A body that takes the Min or Max of values (Math, MathF, the vector types): a relaxation's shape.</summary>
    private static bool Relaxes(IOperation block) => block.Descendants().OfType<IInvocationOperation>().Any(i =>
        i.TargetMethod.Name is "Min" or "Max" && i.TargetMethod.ContainingType.ToDisplayString() is "System.Math" or "System.MathF"
            or "System.Numerics.Vector" or "System.Runtime.Intrinsics.Vector128" or "System.Runtime.Intrinsics.Vector256" or "System.Runtime.Intrinsics.Vector512");
}

/// <summary>The rules of stepping tiles (EasyEcsAnalyzer.Tiles.cs), as one analyzer: Roslyn skips an analyzer only when every rule it reports is suppressed, so a
/// NoWarn of these rules saves their cost.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TileAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        EasyEcsAnalyzer.BadStep, EasyEcsAnalyzer.HandCommit, EasyEcsAnalyzer.SuggestGaussSeidel);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeTiles(context);
}
