using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace EasyEcs.Generator;

/// <summary>
/// Cell methods (the shader form): a [ForEach] method taking a <c>Cell&lt;TCell, TTile&gt;</c> and assigning an
/// <c>out TOut</c> computes one cell from its neighbors at constant offsets. Its body is read as an element loop over a
/// row's cells (Kernel.Loops.cs): each offset a stream (the padded row from that offset), the out parameter a written
/// stream, <c>X</c> the index's lanes, <c>Y</c> / <c>Z</c> and the method's other parameters values the same for the row.
/// SystemGenerator.Cells.cs writes the pass around it.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// What a cell method reads and how its rows run: the offsets its body reads (in the order of the streams; Cell, the
    /// ordinal among its cell parameters, 0 the one that drives the walk), whether it reads its place (X, Y or Z: no row
    /// may be skipped), the other parameters it reads, and the row's vector form (null with <paramref name="Reason"/>:
    /// every cell runs the method as written). The streams are the offsets', then the out cells' in parameter order.
    /// </summary>
    /// <param name="Values">Per value of the vector form, after the streams: "Y", "Z" (the row's) or a parameter's name.</param>
    /// <param name="Fixable">Whether the method can be rewritten for the vector form (EECS055 then warns).</param>
    /// <param name="ConstantOnBlank">
    /// Whether the out cells are proved the same on every row whose driving cells are blank, whatever the rest holds
    /// (<see cref="ConstantOnBlank"/>): the pass then skips those rows even when the method reads its place or other cells.
    /// </param>
    internal sealed record CellForm(EquatableArray<(int Cell, int Dx, int Dy, int Dz)> Offsets, bool ReadsPlace, ElementLoop? Loop, EquatableArray<string> Values, string? Reason,
        bool Fixable = false, bool ConstantOnBlank = false);

    /// <summary>The shape of cell method <paramref name="method"/>'s body, and its row's vector form when Kernel takes it.</summary>
    public static CellForm CellFormOf(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        var cells = Symbols.Cells(method);
        var outputs = method.Parameters.Where(p => Symbols.CellOut(p) != null).ToList();
        var results = method.Parameters.Where(p => Symbols.TileResult(p) != null).ToList();
        SyntaxNode? body = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody?.Expression;
        var offsets = new List<(int, int, int, int)>();
        bool x = false, y = false, z = false;
        string? reason = null;
        var replace = new Dictionary<SyntaxNode, SyntaxNode>();
        if (body == null) return new CellForm(new([]), true, null, new([]), "it has no body");
        foreach (var name in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(name, ct).Symbol is not IParameterSymbol cell) continue;
            int input = cells.FindIndex(c => SymbolEqualityComparer.Default.Equals(c, cell));
            if (input < 0) continue;
            (int, int, int, int)? at = null;
            SyntaxNode? use = null;
            if (name.Parent is ElementAccessExpressionSyntax { ArgumentList.Arguments: { Count: 3 } args } access && access.Expression == name)
            {
                var d = args.Select(a => model.GetConstantValue(a.Expression, ct)).ToArray();
                if (d.All(c => c is { HasValue: true, Value: int }))
                {
                    at = (input, (int)d[0].Value!, (int)d[1].Value!, (int)d[2].Value!);
                    use = access;
                }
            }
            else if (name.Parent is MemberAccessExpressionSyntax member && member.Expression == name)
            {
                switch (member.Name.Identifier.ValueText)
                {
                    case "Value": at = (input, 0, 0, 0); use = member; break;
                    case "X": x = true; replace[member] = IdentifierName("__X").WithTriviaFrom(member); continue;
                    case "Y": y = true; replace[member] = IdentifierName("__Y").WithTriviaFrom(member); continue;
                    case "Z": z = true; replace[member] = IdentifierName("__Z").WithTriviaFrom(member); continue;
                }
            }
            if (at is not { } offset)
            {
                reason ??= $"it uses cell '{cell.Name}' other than at constant offsets ('{name.Parent}'): {cell.Name}[dx, dy, dz] with constants, {cell.Name}.Value, X, Y, Z";
                continue;
            }
            if (!offsets.Contains(offset)) offsets.Add(offset);
            replace[use!] = IdentifierName(Stream(offsets.IndexOf(offset))).WithTriviaFrom(use!);
        }
        bool place = x || y || z;
        if (reason != null) return new CellForm(new(offsets.ToArray()), place, null, new([]), reason, true);
        if (Symbols.Simd(method) == Symbols.SimdOff) return new CellForm(new(offsets.ToArray()), place, null, new([]), "SimdMode.Off");

        // The values: the method's other parameters the body names, then the row's Y and Z where it reads them.
        var others = method.Parameters.Where(p => !cells.Contains(p, SymbolEqualityComparer.Default) && !outputs.Contains(p, SymbolEqualityComparer.Default)
            && !results.Contains(p, SymbolEqualityComparer.Default)
            && body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, ct).Symbol, p))).ToList();
        if (others.FirstOrDefault(p => p.RefKind is RefKind.Ref or RefKind.Out || p.Type.IsRefLikeType) is { } written)
            return new CellForm(new(offsets.ToArray()), place, null, new([]), $"it takes '{written.Name}' {(written.Type.IsRefLikeType ? $"(a {written.Type.Name})" : "by ref")}: a cell's vector form reads values the same for the row and writes only its out cells", true);

        var rewritten = body.ReplaceNodes(replace.Keys, (original, _) => replace[original]);
        var statements = rewritten is BlockSyntax block ? block : Block(ExpressionStatement((ExpressionSyntax)rewritten));
        string CellName(int input) => ((INamedTypeSymbol)cells[input].Type).TypeArguments[0].ToDisplayString(Symbols.Fqn);
        var parameters = new List<ParameterSyntax>();
        for (int s = 0; s < offsets.Count; s++)
            parameters.Add(Parameter(Identifier(Stream(s))).WithType(ParseTypeName(CellName(offsets[s].Item1))).WithModifiers(TokenList(Token(SyntaxKind.InKeyword))));
        foreach (var output in outputs.Concat(results))
            parameters.Add(Parameter(Identifier(global::EasyEcs.Generator.Code.Escape(output.Name))).WithType(ParseTypeName(output.Type.ToDisplayString(Symbols.Fqn)))
                .WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))));
        var values = new List<string>();
        foreach (var p in others)
        {
            parameters.Add(Parameter(Identifier(global::EasyEcs.Generator.Code.Escape(p.Name))).WithType(ParseTypeName(p.Type.ToDisplayString(Symbols.Fqn))));
            values.Add(global::EasyEcs.Generator.Code.Escape(p.Name));
        }
        if (y) { parameters.Add(Parameter(Identifier("__Y")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword)))); values.Add("Y"); }
        if (z) { parameters.Add(Parameter(Identifier("__Z")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword)))); values.Add("Z"); }
        if (x) parameters.Add(Parameter(Identifier("__X")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword))));
        var function = LocalFunctionStatement(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier("__Cell"))
            .WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(ParameterList(SeparatedList(parameters)))
            .WithBody(statements);
        int position = syntax.Body?.SpanStart ?? syntax.ExpressionBody!.Expression.SpanStart;
        var driver = Enumerable.Range(0, offsets.Count).Where(s => offsets[s].Item1 == 0).ToArray();
        var loop = Translated(method, syntax, model, function, position, offsets.Count + outputs.Count, values.Count, x, ct, out reason, out bool fixable,
            driver, outputs.Count, results.Count, out bool constant);
        if (loop == null) return new CellForm(new(offsets.ToArray()), place, null, new([]), reason, fixable, constant);
        var streams = offsets.Select(o => ($"{o}", CellName(o.Item1), false, 0))
            .Concat(outputs.Select(o => ($"{o.Name}", o.Type.ToDisplayString(Symbols.Fqn), true, 0))).ToArray();
        var valueTypes = others.Select(p => (p.Type.ToDisplayString(Symbols.Fqn), global::EasyEcs.Generator.Code.Escape(p.Name), false)).ToList();
        if (y) valueTypes.Add(("int", "__Y", false));
        if (z) valueTypes.Add(("int", "__Z", false));
        return new CellForm(new(offsets.ToArray()), place, new ElementLoop(loop, streams, valueTypes.ToArray(), false), new(values.ToArray()), null, false, constant);

        static string Stream(int s) => "__s" + s;
    }

    /// <summary>
    /// <paramref name="function"/>, a body desugared into a static local function (its streams first, then its values, then
    /// the index when it reads it), bound at <paramref name="position"/>, its calls inlined, translated as an element
    /// loop's; null with the reason. For a cell method (<paramref name="blank"/>: the streams of its driving input,
    /// then <paramref name="outputs"/> out cells after the streams read), <paramref name="constant"/> says whether the
    /// bound body proves its out cells constant on blank driving cells (<see cref="ConstantOnBlank"/>).
    /// </summary>
    private static Code? Translated(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, LocalFunctionStatementSyntax function,
        int position, int streams, int values, bool index, System.Threading.CancellationToken ct, out string? reason, out bool fixable,
        int[] blank, int outputs, int results, out bool constant)
    {
        reason = null;
        fixable = false;
        constant = false;
        if (!model.TryGetSpeculativeSemanticModel(position, function, out var speculative) || speculative == null
            || speculative.GetDeclaredSymbol(function, ct) is not IMethodSymbol elements
            || speculative.GetOperation(function, ct) is not ILocalFunctionOperation { Body: { } operation }
            || operation.Descendants().Any(o => o.Kind == OperationKind.Invalid))
        {
            reason = "its body could not be bound as a loop over a row's cells";
            return null;
        }
        var callees = Callees.Of(model.Compilation);
        callees.Offer(model, syntax);
        if (operation.DescendantsAndSelf().Any(o => callees.Candidate(o) != null))
        {
            var inliner = new Inliner(new HashSet<string>(function.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText)),
                elements, model.Compilation, ct);
            var block = inliner.Method(speculative).Body(function.Body!);
            if (block != null && inliner.Count + inliner.Left > 0)
            {
                var inlined = function.WithBody(inliner.WithAccessors(Inliner.WithoutInlinedLocalFunctions(block)));
                if (model.TryGetSpeculativeSemanticModel(position, inlined, out var rebound) && rebound != null
                    && rebound.GetDeclaredSymbol(inlined, ct) is IMethodSymbol inlinedElements
                    && rebound.GetOperation(inlined, ct) is ILocalFunctionOperation { Body: { } inlinedBody }
                    && !inlinedBody.Descendants().Any(o => o.Kind == OperationKind.Invalid))
                    (speculative, elements, operation) = (rebound, inlinedElements, inlinedBody);
            }
        }
        var read = streams - outputs;
        constant = ConstantOnBlank(operation,
            new HashSet<IParameterSymbol>(blank.Select(b => elements.Parameters[b]), SymbolEqualityComparer.Default),
            new HashSet<IParameterSymbol>(elements.Parameters.Skip(read).Take(outputs), SymbolEqualityComparer.Default),
            new HashSet<IParameterSymbol>(elements.Parameters.Skip(streams).Take(results), SymbolEqualityComparer.Default));
        var lane = new Lane(elements, true, false, Symbols.FastFloats(method), speculative, true, method)
        {
            Streams = new HashSet<IParameterSymbol>(elements.Parameters.Take(streams), SymbolEqualityComparer.Default),
            Sums = new Dictionary<IParameterSymbol, string>(SymbolEqualityComparer.Default),
            Results = new HashSet<IParameterSymbol>(elements.Parameters.Skip(streams).Take(results), SymbolEqualityComparer.Default),
            Index = index ? elements.Parameters[streams + results + values] : null,
            IndexFits = true, // the pass enters the vector form only while X fits (Code.IndexTop)
        };
        var code = Vectorize(lane, operation, static (i, _) => $"x{i}", ct, out var why);
        if (code == null)
        {
            reason = why?.Text ?? "its body has no vector form";
            fixable = why?.IsFixable == true;
            return null;
        }
        if (code.Wide != null || code.Statics.Length > 0 || code.Gathers || code.VectorTail || code.StructuralChanges.Length > 0)
        {
            reason = "it needs a form of the vector loop a cell's row does not take yet (a wide cell, gathers, records)";
            return null;
        }
        return code;
    }
}
