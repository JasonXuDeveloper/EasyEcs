using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// EECS115-117, EECS120-121: stepping tiles (<c>ref Tile&lt;TCell, TTile&gt;</c>, Jacobi or [GaussSeidel]). A step the
/// generator cannot write (Error), a second buffer and its commit written by hand where the tile could step itself
/// (Warning), a relaxation stepped one cell per neighbor per pass that [GaussSeidel] would settle in place (Info), a
/// cursor's load the generator cannot prove (Info), and a buffer copied over a tile's cells that no method writes (Warning).
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
        "to color the tiles by, Write is for the tile the method steps (a tile taken by value throws), and Restart for " +
        "the tile a [GaussSeidel] method sweeps.",
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

    public static readonly DiagnosticDescriptor CheckedTileLoad = new("EECS120", "A tile load checked on every call",
        "{0} at a column the generator cannot follow is checked on every call: {1}; write the loop as 'for (int x = 0; x < TTile.Width; x += step)' with nothing else writing x, or walk the row's columns with c.Columns(step)",
        Category, DiagnosticSeverity.Info, true,
        "A TileCursor load (Load128, Load256, LoadVector) checks that its cells lie in the padded row: a compare and a " +
        "sign extension per column. In a column loop the generator can follow (the loop declares an int from a constant, " +
        "tests it against a constant or the shape's Width, Depth, Height or Halo, adds a positive constant, nothing else " +
        "writes it, and the call is in its body, not in a lambda or local function there), the load's column is the " +
        "loop's variable plus a constant, and the generator checks the loop's first and last column once, in constants " +
        "the JIT folds. Elsewhere the load stays correct and checked. c.Columns(step) walks the row's columns with " +
        "loads at constant offsets of the column, checked by the JIT alone.",
        helpLinkUri: Help("EECS120"));

    public static readonly DiagnosticDescriptor StaleCommit = new("EECS121", "A tile's cells overwritten by a buffer no method writes",
        "{0} copies {1} over the cells of {2}, but no method writes {1}: every pass puts back what {1} held when it was made; delete the copy (a tile stepped by ref swaps its own buffers), or write {1} where the step runs",
        Category, DiagnosticSeverity.Warning, true,
        "A commit copies a second buffer (NextHeat) over a tile's cells (Heat) after the step wrote it. When no method " +
        "writes that buffer (no Mut<DynamicBuffer<T>> or ref DynamicBuffer<T> parameter, no AsSpan, Add, Insert or " +
        "RemoveAt on it, no SetCount whose span is used), the copy puts the same values back every pass: what is left of " +
        "a commit once its step took the tile by ref and writes its rows with Write(row), whose pass swaps the buffers " +
        "itself. Sizing the buffer (SetCount, Clear, EnsureCapacity) when entities are made does not count as a write. " +
        "Deleting the copy changes what the pass does (the cells stop being reset), so there is no code fix: delete the " +
        "copy, its method and the buffer, or write the buffer in the step.",
        helpLinkUri: Help("EECS121"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static void InitializeTiles(AnalysisContext context) => context.RegisterCompilationStartAction(AnalyzeTiles);

    private static void AnalyzeTiles(CompilationStartAnalysisContext start)
    {
        // EECS116: the steps that write a second buffer beside a tile (its next cells, its cells), and the plain copies.
        var steps = new ConcurrentQueue<(ITypeSymbol Next, ITypeSymbol Cells, IMethodSymbol Method)>();
        var copies = new ConcurrentQueue<(ITypeSymbol Next, ITypeSymbol Cells, IMethodSymbol Method)>();

        // EECS121: the cells of tiles, the buffers some method writes, and the copies of a buffer over a buffer it writes.
        var cellTypes = new ConcurrentDictionary<ITypeSymbol, byte>(SymbolEqualityComparer.Default);
        var written = new ConcurrentDictionary<ITypeSymbol, byte>(SymbolEqualityComparer.Default);
        var overwrites = new ConcurrentQueue<(ITypeSymbol Next, ITypeSymbol Cells, IMethodSymbol Method, Location At)>();

        start.RegisterSymbolAction(context =>
        {
            var method = (IMethodSymbol)context.Symbol;
            foreach (var p in method.Parameters)
                if (p.Type is INamedTypeSymbol { Name: "Mut" or "DynamicBuffer" } && WrittenBuffer(p) is { } buffer) written.TryAdd(buffer, 0);
            if (!Symbols.IsForEach(method) && !Symbols.IsEntityMethod(method)) return;
            foreach (var p in method.Parameters)
                if (Symbols.TileElements(p.Type) is { } cells) cellTypes.TryAdd(cells.Cell, 0);
            if (Symbols.StepProblem(method) is { } problem)
                context.ReportDiagnostic(Diagnostic.Create(BadStep, method.Locations.FirstOrDefault(), Symbols.Describe(method), problem));
            foreach (var tile in method.Parameters)
            {
                if (tile.RefKind == RefKind.Ref || Symbols.TileElements(tile.Type) is not { } elements) continue;
                foreach (var p in method.Parameters)
                    if (WrittenBuffer(p) is { } next) steps.Enqueue((next, elements.Cell, method));
            }
        }, SymbolKind.Method);

        // EECS116's fix: per step that writes a second buffer, the rewrite of the step by ref when it is mechanical.
        var rewrites = new ConcurrentDictionary<(IMethodSymbol Method, ITypeSymbol Next), ImmutableDictionary<string, string?>>(StepKeys.Instance);

        start.RegisterOperationBlockAction(context =>
        {
            if (context.OwningSymbol is not IMethodSymbol method || !Symbols.IsForEach(method)) return;
            if (context.OperationBlocks.FirstOrDefault()?.SemanticModel is { } model)
                foreach (var tile in method.Parameters)
                {
                    if (tile.RefKind == RefKind.Ref || Symbols.TileElements(tile.Type) is not { } elements) continue;
                    foreach (var p in method.Parameters)
                        if (WrittenBuffer(p) is { } next && StepRewrite(method, tile, p, elements.Cell, next, model, context.CancellationToken) is { } rewrite)
                            rewrites.TryAdd((method, next), rewrite);
                }
            foreach (var block in context.OperationBlocks)
            {
                if (PlainCopy(method, block) is { } copy) copies.Enqueue((copy.Next, copy.Cells, method));
                foreach (var over in Overwrites(method, block)) overwrites.Enqueue((over.Next, over.Cells, method, over.At));
                if (method.Parameters.FirstOrDefault(Symbols.IsSteppingTile) is { } stepped && !Symbols.IsGaussSeidel(method) && Relaxes(block))
                    context.ReportDiagnostic(Diagnostic.Create(SuggestGaussSeidel, method.Locations.FirstOrDefault(),
                        CanColor(method, stepped) ? GaussSeidelFix : null, Symbols.Describe(method)));
            }
        });

        // EECS115: Write on a tile the method takes by value (no step of it runs: it throws), Restart on a tile no
        // [GaussSeidel] step sweeps (a Jacobi step writes every row anyway: it throws too).
        // EECS121: a write of a buffer's elements through the buffer's extensions (sizing it alone is no write).
        start.RegisterOperationAction(context =>
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod is { IsExtensionMethod: true, ContainingType.Name: "DynamicBufferExtensions", TypeArguments.Length: 1 } extension
                && extension.ContainingNamespace?.Name == "EasyEcs" && WritesElements(invocation))
                written.TryAdd(extension.TypeArguments[0], 0);
        }, OperationKind.Invocation);

        start.RegisterOperationAction(context =>
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod is not { Name: "Write" or "Restart" } call || !Symbols.Named(call.ContainingType.OriginalDefinition, "EasyEcs.Tile<TCell, TTile>")) return;
            if (invocation.Instance is not IParameterReferenceOperation { Parameter: var tile }
                || tile.ContainingSymbol is not IMethodSymbol method || !Symbols.IsForEach(method)) return;
            if (call.Name == "Write" && tile.RefKind != RefKind.Ref)
                context.ReportDiagnostic(Diagnostic.Create(BadStep, invocation.Syntax.GetLocation(), Symbols.Describe(method),
                    $"writes '{tile.Name}', a tile it takes by value: Write is for the tile the method steps (ref Tile<...>), and throws for another"));
            else if (call.Name == "Restart" && (tile.RefKind != RefKind.Ref || !Symbols.IsGaussSeidel(method)))
                context.ReportDiagnostic(Diagnostic.Create(BadStep, invocation.Syntax.GetLocation(), Symbols.Describe(method),
                    $"restarts '{tile.Name}', which no [GaussSeidel] sweep steps: Restart is for the tile a [GaussSeidel] method takes by ref, and throws for another"));
        }, OperationKind.Invocation);

        // EECS115: Sweep of a tile the method takes by value (it has no sweeps: it throws).
        start.RegisterOperationAction(context =>
        {
            var read = (IPropertyReferenceOperation)context.Operation;
            if (read.Property.Name != "Sweep" || !Symbols.Named(read.Property.ContainingType.OriginalDefinition, "EasyEcs.Tile<TCell, TTile>")) return;
            if (read.Instance is not IParameterReferenceOperation { Parameter: { RefKind: not RefKind.Ref } tile }
                || tile.ContainingSymbol is not IMethodSymbol method || !Symbols.IsForEach(method)) return;
            context.ReportDiagnostic(Diagnostic.Create(BadStep, read.Syntax.GetLocation(), Symbols.Describe(method),
                $"reads the sweep of '{tile.Name}', a tile it takes by value: Sweep is for the tile the method steps (ref Tile<...>), and throws for another"));
        }, OperationKind.PropertyReference);

        // EECS120: a cursor's load at a column the tile-load interceptor cannot follow (the reasons TileLoad.Read gives).
        start.RegisterSyntaxNodeAction(context =>
        {
            var invocation = (Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax)context.Node;
            if (!TileLoad.IsCandidate(invocation)) return;
            if (TileLoad.Read(invocation, context.SemanticModel, context.CancellationToken, out var reason) == null && reason != null)
                context.ReportDiagnostic(Diagnostic.Create(CheckedTileLoad, invocation.GetLocation(),
                    ((Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax)invocation.Expression).Name.Identifier.ValueText, reason));
        }, Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);

        start.RegisterCompilationEndAction(context =>
        {
            foreach (var (next, cells, method, at) in overwrites)
                if (cellTypes.ContainsKey(cells) && !written.ContainsKey(next))
                    context.ReportDiagnostic(Diagnostic.Create(StaleCommit, at, Symbols.Describe(method), next.Name, cells.Name));
            foreach (var (next, cells, method) in copies)
            {
                var step = steps.FirstOrDefault(s => SymbolEqualityComparer.Default.Equals(s.Next, next) && SymbolEqualityComparer.Default.Equals(s.Cells, cells));
                if (step.Method == null) continue;
                // The fix rewrites the step, so its location goes along; the commit and the buffer are the user's to delete.
                bool fix = rewrites.TryGetValue((step.Method, next), out var rewrite);
                context.ReportDiagnostic(Diagnostic.Create(HandCommit, method.Locations.FirstOrDefault(),
                    fix ? step.Method.Locations.Take(1) : null, fix ? rewrite : null, Symbols.Describe(method),
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

    /// <summary>
    /// The CopyTo calls of a [ForEach] body that copy one buffer parameter it reads (<c>in DynamicBuffer&lt;TNext&gt;</c>) into
    /// one it writes (TCells), through spans and casts, whatever else the body does.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<(ITypeSymbol Next, ITypeSymbol Cells, Location At)> Overwrites(IMethodSymbol method, IOperation block)
    {
        bool reads = false, writes = false;
        foreach (var p in method.Parameters)
        {
            reads |= p.RefKind is RefKind.In or RefKind.None && Symbols.Classify(p) == Symbols.ParamKind.Component && Symbols.BufferElement(p.Type) != null;
            writes |= WrittenBuffer(p) != null;
        }
        if (!reads || !writes) yield break;
        foreach (var copy in block.Descendants().OfType<IInvocationOperation>())
        {
            if (copy.TargetMethod.Name != "CopyTo" || copy.Instance == null) continue;
            var from = copy.Instance.DescendantsAndSelf().OfType<IParameterReferenceOperation>().Select(r => r.Parameter).Distinct(SymbolEqualityComparer.Default).ToArray();
            var into = copy.Arguments.SelectMany(a => a.DescendantsAndSelf()).OfType<IParameterReferenceOperation>().Select(r => r.Parameter).Distinct(SymbolEqualityComparer.Default).ToArray();
            if (from is not { Length: 1 } || into is not { Length: 1 } || from[0] is not IParameterSymbol source || into[0] is not IParameterSymbol target) continue;
            if (source.RefKind is not (RefKind.In or RefKind.None) || Symbols.Classify(source) != Symbols.ParamKind.Component || Symbols.BufferElement(source.Type) is not { } next) continue;
            if (WrittenBuffer(target) is { } cells) yield return (next, cells, copy.Syntax.GetLocation());
        }
    }

    /// <summary>
    /// Whether a call of a DynamicBuffer extension writes the buffer's elements: AsSpan, Add, AddRange, Insert,
    /// RemoveAt, RemoveAtSwapBack, and SetCount when the span it hands out is used; Clear, EnsureCapacity, TrimExcess
    /// and a SetCount as a statement only size it (zeroes, made when the entity is).
    /// </summary>
    private static bool WritesElements(IInvocationOperation call) => call.TargetMethod.Name switch
    {
        "Clear" or "EnsureCapacity" or "TrimExcess" => false,
        "SetCount" => call.Parent is not IExpressionStatementOperation,
        _ => true,
    };

    /// <summary>Equality of (step, buffer) keys by symbol.</summary>
    private sealed class StepKeys : System.Collections.Generic.IEqualityComparer<(IMethodSymbol Method, ITypeSymbol Next)>
    {
        public static readonly StepKeys Instance = new();

        public bool Equals((IMethodSymbol Method, ITypeSymbol Next) a, (IMethodSymbol Method, ITypeSymbol Next) b) =>
            SymbolEqualityComparer.Default.Equals(a.Method, b.Method) && SymbolEqualityComparer.Default.Equals(a.Next, b.Next);

        public int GetHashCode((IMethodSymbol Method, ITypeSymbol Next) key) =>
            SymbolEqualityComparer.Default.GetHashCode(key.Method) * 31 + SymbolEqualityComparer.Default.GetHashCode(key.Next);
    }

    /// <summary>
    /// EECS116's fix of the step (EasyEcs.CodeFixes), as properties, or null when it is not mechanical. The step takes
    /// <paramref name="tile"/> by value and writes <paramref name="next"/>, a buffer of <paramref name="nextCell"/>, in one
    /// statement of its body, <c>var into = next.Write().AsSpan();</c> (<c>next.AsSpan()</c> for a <c>ref</c> buffer),
    /// alone or in <c>MemoryMarshal.Cast&lt;TNext, TLane&gt;(...)</c>; <c>into</c> is cleared by a statement of the body
    /// (the pass zeroes what the step skips) or sliced or indexed at <c>row.Index</c> (plus a term) in one
    /// <c>foreach (var row in tile.Rows(...))</c>. Then the step takes the tile by ref, drops <paramref name="next"/>,
    /// and declares <c>into</c> in the row loop over <c>tile.Write(row)</c>, each <c>row.Index</c> dropped. Not when the
    /// method would be a step the generator cannot write (EECS115), or a lambda or local function names the tile (a ref
    /// parameter cannot be captured).
    /// </summary>
    private static ImmutableDictionary<string, string?>? StepRewrite(IMethodSymbol method, IParameterSymbol tile, IParameterSymbol next,
        ITypeSymbol cell, ITypeSymbol nextCell, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (method.Parameters.Any(Symbols.IsSteppingTile) || Symbols.IsEntityMethod(method) || Symbols.Slices(method) > 1 || Symbols.IsConsume(method)
            || method.DeclaringSyntaxReferences.Length != 1
            || method.DeclaringSyntaxReferences[0].GetSyntax(ct) is not MethodDeclarationSyntax { Body: { } body } declaration
            || declaration.SyntaxTree != model.SyntaxTree)
            return null;
        bool Is(IdentifierNameSyntax name, ISymbol symbol) =>
            name.Identifier.ValueText == symbol.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, ct).Symbol, symbol);

        // next.Write().AsSpan() (or next.AsSpan()), maybe in MemoryMarshal.Cast<TNext, TLane>(...), declaring `into` in the body.
        var uses = body.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => Is(n, next)).ToArray();
        if (uses.Length != 1) return null;
        SyntaxNode source = uses[0];
        if (Symbols.Classify(next) == Symbols.ParamKind.Mutable)
        {
            if (source.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Write" } write
                || write.Parent is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } written)
                return null;
            source = written;
        }
        if (source.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AsSpan" } asSpan
            || asSpan.Parent is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } span)
            return null;
        SyntaxNode init = span;
        string? lane = null;
        if (span.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: InvocationExpressionSyntax cast } }
            && model.GetSymbolInfo(cast, ct).Symbol is IMethodSymbol { Name: "Cast", TypeArguments.Length: 2 } castMethod
            && castMethod.ContainingType.ToDisplayString() == "System.Runtime.InteropServices.MemoryMarshal"
            && SymbolEqualityComparer.Default.Equals(castMethod.TypeArguments[0], nextCell))
        {
            init = cast;
            lane = castMethod.TypeArguments[1].ToMinimalDisplayString(model, cast.SpanStart);
        }
        if (init.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
            || declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1 } || declarator.Parent.Parent?.Parent != body
            || model.GetDeclaredSymbol(declarator, ct) is not ILocalSymbol into)
            return null;

        // Each use of `into`: a Clear statement of the body, or a slice or index at row.Index (+ a term) in the one row loop.
        ForEachStatementSyntax? rows = null;
        foreach (var name in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!Is(name, into)) continue;
            if (name.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Clear" } clear
                && clear.Parent is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0, Parent: ExpressionStatementSyntax statement } && statement.Parent == body)
                continue;
            ExpressionSyntax? at = name.Parent switch
            {
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Slice", Parent: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } slice } => slice.ArgumentList.Arguments[0].Expression,
                ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } index when index.Expression == name => index.ArgumentList.Arguments[0].Expression,
                _ => null,
            };
            var loop = name.Ancestors().OfType<ForEachStatementSyntax>().FirstOrDefault(f =>
                f.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Rows", Expression: IdentifierNameSyntax t } } && Is(t, tile));
            if (at == null || loop == null || rows != null && rows != loop || !AtRow(at, loop.Identifier.ValueText)) return null;
            rows = loop;
        }
        if (rows == null || body.DescendantNodes().Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                && n.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => Is(i, tile))))
            return null;
        return ImmutableDictionary<string, string?>.Empty.Add("Fix", "Step").Add("Tile", tile.Name).Add("Next", next.Name)
            .Add("Into", into.Name).Add("Cell", cell.ToMinimalDisplayString(model, declaration.SpanStart)).Add("Lane", lane)
            .Add("NextCell", nextCell.ToMinimalDisplayString(model, declaration.SpanStart));

        // row.Index, or row.Index + a term.
        static bool AtRow(ExpressionSyntax at, string row) =>
            at is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Index", Expression: IdentifierNameSyntax r } && r.Identifier.ValueText == row
            || at is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum && AtRow(sum.Left, row) && sum.Left is MemberAccessExpressionSyntax;
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
        EasyEcsAnalyzer.BadStep, EasyEcsAnalyzer.HandCommit, EasyEcsAnalyzer.SuggestGaussSeidel, EasyEcsAnalyzer.CheckedTileLoad, EasyEcsAnalyzer.StaleCommit);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeTiles(context);
}
