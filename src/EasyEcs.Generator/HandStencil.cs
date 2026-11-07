using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// A Tile [ForEach] method whose body is a stencil written by hand with the cursor's vector loads (EECS103 v2), read as
/// the cell method that computes the same cells: <c>Cell&lt;TCell, TTile&gt;</c> and <c>out TOut</c>, each load a
/// neighbor <c>t[dx, dy, dz]</c>, each vector local a scalar one. Reported only when the cell method gets the
/// generator's row loop in a copy of the compilation (Kernel.CellFormOf), the rule HandSimd keeps.
/// </summary>
/// <remarks>
/// What qualifies (anything else leaves the method alone): a <c>Tile&lt;TCell, TTile&gt;</c> parameter (by value) and a
/// <c>Mut&lt;DynamicBuffer&lt;TOut&gt;&gt;</c>, nothing else; TCell and TOut of one field of the lane type; a body of an
/// optional <c>t.ExchangeHalo()</c>, <c>var into = MemoryMarshal.Cast&lt;TOut, TLane&gt;(next.Write().AsSpan())</c>
/// and <c>foreach (var row in t.Rows(...)) { var c = t.At(row); for (int x = 0; x &lt; width; x += lanes) ... }</c>, the
/// loop's statements vector locals and one <c>v.CopyTo(into.Slice(row.Index + x))</c>, its expressions loads at
/// <c>x + dx</c> and constant dy, dz, + - * /, negation, constants and broadcasts. A summary per row, a range of rows
/// (<c>ExchangeHalo(from, to)</c>) or a comparison keeps the hand-written form.
/// </remarks>
internal static class HandStencil
{
    /// <summary>The syntactic prefilter: an attributed method whose body calls a cursor load and CopyTo.</summary>
    public static bool MayBe(MethodDeclarationSyntax syntax)
    {
        if (syntax.Body is not { } body || syntax.AttributeLists.Count == 0 || syntax.ParameterList.Parameters.Count != 2) return false;
        bool load = false, copy = false;
        foreach (var token in body.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                load |= token.ValueText is "Load128" or "Load256" or "Load512" or "LoadVector";
                copy |= token.ValueText == "CopyTo";
            }
        return load && copy;
    }

    /// <summary>The cell method <paramref name="method"/>'s hand-written stencil computes, when the generator's row loop takes it; else null.</summary>
    public static HandSimd.PerRow? Of(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (!MayBe(syntax) || !Symbols.IsForEach(method) || Symbols.IsEntityMethod(method) || Symbols.Simd(method) == Symbols.SimdOff
            || Symbols.IsGenerated(method) || !method.IsStatic || !method.ReturnsVoid || method.IsGenericMethod
            || method.PartialDefinitionPart != null || method.PartialImplementationPart != null) return null;
        HandSimd.PerRow? form;
        try
        {
            form = new Reader(method, syntax, model, ct).Read();
        }
        catch (NotStencil)
        {
            return null;
        }

        return form != null && HandSimd.Rewritten(syntax, model, form, ct,
            static (copy, copySyntax, copyModel, ct) => Symbols.IsCellMethod(copy) && Kernel.CellFormOf(copy, copySyntax, copyModel, ct).Loop != null) ? form : null;
    }

    /// <summary>A number a cursor loads as lanes.</summary>
    private static bool IsLane(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_SByte;

    private static int SizeOf(ITypeSymbol lane) => lane.SpecialType switch
    {
        SpecialType.System_Double or SpecialType.System_Int64 or SpecialType.System_UInt64 => 8,
        SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
        SpecialType.System_Byte or SpecialType.System_SByte => 1,
        _ => 4,
    };

    /// <summary>The body is not a stencil this rule rewrites.</summary>
    private sealed class NotStencil : System.Exception;

    private sealed class Reader(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        private IParameterSymbol _tile = null!;
        private ILocalSymbol _cursor = null!, _index = null!;
        private ITypeSymbol _lane = null!;
        private string _cellField = "", _load = "";
        private readonly HashSet<ILocalSymbol> _locals = new(SymbolEqualityComparer.Default);

        private static NotStencil No() => new();

        public HandSimd.PerRow? Read()
        {
            // The parameters: the tile and the buffer it writes.
            IParameterSymbol? tile = null, next = null;
            foreach (var p in method.Parameters)
                if (p.RefKind == RefKind.None && p.Type is INamedTypeSymbol { IsGenericType: true } t && Symbols.Named(t.ConstructedFrom, "EasyEcs.Tile<TCell, TTile>")) tile = p;
                else if (p.RefKind == RefKind.None && Symbols.MutElement(p.Type) is { } written && Symbols.BufferElement(written) != null) next = p;
            if (tile == null || next == null) throw No();
            _tile = tile;
            var tileType = (INamedTypeSymbol)tile.Type;
            var cell = tileType.TypeArguments[0];
            var outType = Symbols.BufferElement(Symbols.MutElement(next.Type)!)!;

            var statements = syntax.Body!.Statements;
            int at = 0;
            // The halo, refreshed for the whole tile: the cell pass does it (ExchangeHalo(from, to), a range of rows, keeps the Tile form).
            if (statements.Count > 0 && statements[0] is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } refresh }
                && model.GetSymbolInfo(refresh, ct).Symbol is IMethodSymbol { Name: "ExchangeHalo" } && Is(Receiver(refresh), tile)) at++;
            if (statements.Count != at + 2) throw No();

            // var into = MemoryMarshal.Cast<TOut, TLane>(next.Write().AsSpan());
            if (statements[at] is not LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer.Value: InvocationExpressionSyntax cast } intoDeclarator] }
                || model.GetSymbolInfo(cast, ct).Symbol is not IMethodSymbol { Name: "Cast", ContainingType.Name: "MemoryMarshal", TypeArguments: [var from, var lane] }
                || !SymbolEqualityComparer.Default.Equals(from, outType) || !IsLane(lane)
                || cast.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax asSpan }]
                || model.GetSymbolInfo(asSpan, ct).Symbol is not IMethodSymbol { Name: "AsSpan" } || Receiver(asSpan) is not InvocationExpressionSyntax write
                || model.GetSymbolInfo(write, ct).Symbol is not IMethodSymbol { Name: "Write" } || !Is(Receiver(write), next)
                || model.GetDeclaredSymbol(intoDeclarator, ct) is not ILocalSymbol into) throw No();
            _lane = lane;

            // foreach (var row in t.Rows(...)) { var c = t.At(row); for (int x = 0; x < width; x += lanes) ... }
            if (statements[at + 1] is not ForEachStatementSyntax { Expression: InvocationExpressionSyntax rows, Statement: BlockSyntax { Statements: [LocalDeclarationStatementSyntax cursor, ForStatementSyntax loop] } } each
                || model.GetSymbolInfo(rows, ct).Symbol is not IMethodSymbol { Name: "Rows" } || !Is(Receiver(rows), tile)
                || model.GetDeclaredSymbol(each, ct) is not { } row
                || cursor.Declaration.Variables is not [{ Initializer.Value: InvocationExpressionSyntax atRow } cursorDeclarator]
                || model.GetSymbolInfo(atRow, ct).Symbol is not IMethodSymbol { Name: "At" } || !Is(Receiver(atRow), tile)
                || atRow.ArgumentList.Arguments is not [{ Expression: var rowArgument }] || !Is(rowArgument, row)
                || model.GetDeclaredSymbol(cursorDeclarator, ct) is not ILocalSymbol cursorLocal) throw No();
            _cursor = cursorLocal;

            if (loop.Declaration is not { Variables: [{ Initializer.Value: var zero } indexDeclarator] } || loop.Initializers.Count != 0
                || model.GetConstantValue(zero, ct) is not { HasValue: true, Value: 0 } || model.GetDeclaredSymbol(indexDeclarator, ct) is not ILocalSymbol index
                || loop.Incrementors is not [AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression, Right: var step } stepped] || !Is(stepped.Left, index)
                || loop.Condition is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LessThanExpression, Left: var counted, Right: var bound } || !Is(counted, index)
                || !Whole(bound, (INamedTypeSymbol)tileType.TypeArguments[1])) throw No();
            _index = index;

            var body = loop.Statement is BlockSyntax block ? (IEnumerable<StatementSyntax>)block.Statements : [loop.Statement];
            _cellField = OneField(cell, lane) ?? throw No();
            var result = new List<string>();
            string? value = null;
            foreach (var statement in body)
            {
                if (value != null) throw No(); // the store comes last
                switch (statement)
                {
                    case LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer.Value: var init } declarator] }
                        when model.GetDeclaredSymbol(declarator, ct) is ILocalSymbol local:
                        result.Add($"{LaneName()} {declarator.Identifier.ValueText} = {Expr(init)};");
                        _locals.Add(local);
                        break;
                    // v.CopyTo(into.Slice(row.Index + x))
                    case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CopyTo", Expression: var stored }, ArgumentList.Arguments: [{ Expression: InvocationExpressionSyntax slice }] } }
                        when model.GetSymbolInfo(slice, ct).Symbol is IMethodSymbol { Name: "Slice", Parameters.Length: 1 } && Is(Receiver(slice), into)
                             && slice.ArgumentList.Arguments is [{ Expression: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } offset }]
                             && (RowIndex(offset.Left, row) && Is(offset.Right, index) || Is(offset.Left, index) && RowIndex(offset.Right, row)):
                        value = Expr(stored is ParenthesizedExpressionSyntax { Expression: var inner } ? inner : stored); // new TOut(...) holds it
                        break;
                    default:
                        throw No();
                }
            }
            if (value == null || !Steps(step)) throw No();
            result.Add($"{next.Name} = {Make(outType, value) ?? throw No()};");

            var cellType = model.Compilation.GetTypeByMetadataName("EasyEcs.Cell`2")?.Construct(cell, tileType.TypeArguments[1]) ?? throw No();
            int position = syntax.SpanStart;
            return new HandSimd.PerRow($"{cellType.ToMinimalDisplayString(model, position)} {tile.Name}, out {outType.ToMinimalDisplayString(model, position)} {next.Name}",
                result.ToArray());
        }

        // ---------------- the loop's shape ----------------

        /// <summary>The bound covers the whole row: <c>TTile.Width</c> (a type parameter's or the shape's) or the shape's constant width.</summary>
        private bool Whole(ExpressionSyntax bound, INamedTypeSymbol shape)
        {
            if (model.GetSymbolInfo(bound, ct).Symbol is IPropertySymbol { Name: "Width", IsStatic: true } width
                && (width.ContainingType.TypeKind == TypeKind.TypeParameter || SymbolEqualityComparer.Default.Equals(width.ContainingType, shape)
                    || Symbols.Named(width.ContainingType, "EasyEcs.ITileShape"))) return true;
            return model.GetConstantValue(bound, ct) is { HasValue: true, Value: int n } && ShapeWidth(shape) == n;
        }

        /// <summary>The width a tile shape declares as a constant (<c>static int ITileShape.Width =&gt; 16;</c>), else null.</summary>
        private static int? ShapeWidth(INamedTypeSymbol shape)
        {
            foreach (var member in shape.GetMembers().OfType<IPropertySymbol>())
                if (member.Name.EndsWith("Width", System.StringComparison.Ordinal) && member.IsStatic)
                    foreach (var reference in member.DeclaringSyntaxReferences)
                        if (reference.GetSyntax() is PropertyDeclarationSyntax { ExpressionBody.Expression: LiteralExpressionSyntax literal } && literal.Token.Value is int width)
                            return width;
            return null;
        }

        /// <summary>The step is the lanes of the loads: a constant, or <c>VectorN&lt;TLane&gt;.Count</c> of the load's width.</summary>
        private bool Steps(ExpressionSyntax step)
        {
            int size = SizeOf(_lane);
            if (model.GetConstantValue(step, ct) is { HasValue: true, Value: int n })
                return _load switch { "Load128" => n * size == 16, "Load256" => n * size == 32, "Load512" => n * size == 64, _ => false };
            return model.GetSymbolInfo(step, ct).Symbol is IPropertySymbol { Name: "Count", ContainingType: INamedTypeSymbol { IsGenericType: true } vector }
                && SymbolEqualityComparer.Default.Equals(vector.TypeArguments[0], _lane)
                && (vector.Name, _load) is ("Vector128", "Load128") or ("Vector256", "Load256") or ("Vector512", "Load512") or ("Vector", "LoadVector");
        }

        private bool RowIndex(ExpressionSyntax e, ISymbol row) =>
            e is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Index", Expression: var r } && Is(r, row);

        private static ExpressionSyntax? Receiver(InvocationExpressionSyntax call) => (call.Expression as MemberAccessExpressionSyntax)?.Expression;

        private bool Is(ExpressionSyntax? e, ISymbol symbol) =>
            e is IdentifierNameSyntax && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(e, ct).Symbol, symbol);

        // ---------------- the cell's expressions ----------------

        private string LaneName() => _lane.ToMinimalDisplayString(model, syntax.SpanStart);

        /// <summary>A vector expression of the loop as the cell's scalar expression.</summary>
        private string Expr(ExpressionSyntax e)
        {
            switch (e)
            {
                case ParenthesizedExpressionSyntax p:
                    return "(" + Expr(p.Expression) + ")";
                case BinaryExpressionSyntax b when b.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression:
                    return $"{Expr(b.Left)} {b.OperatorToken.ValueText} {Expr(b.Right)}";
                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u:
                    return "-" + Expr(u.Operand);
                case InvocationExpressionSyntax call when Receiver(call) is { } receiver && Is(receiver, _cursor):
                    return Load(call);
                case InvocationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var broadcast }] } call
                    when model.GetSymbolInfo(call, ct).Symbol is IMethodSymbol { Name: "Create", IsStatic: true, Parameters: [{ Type: var single }] } create
                         && Kernel.IsVectorType(create.ReturnType) && SymbolEqualityComparer.Default.Equals(single, _lane):
                    return "(" + Scalar(broadcast) + ")";
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: ("One" or "Zero") and var constant } access
                    when model.GetSymbolInfo(access, ct).Symbol is IPropertySymbol { IsStatic: true } property && Kernel.IsVectorType(property.Type):
                    return $"(({LaneName()}){(constant == "One" ? 1 : 0)})";
                case IdentifierNameSyntax name when model.GetSymbolInfo(name, ct).Symbol is ILocalSymbol local && _locals.Contains(local):
                    return name.Identifier.ValueText;
                default:
                    return Scalar(e);
            }
        }

        /// <summary>A scalar in the vector expression (a constant, a static value): kept as written.</summary>
        private string Scalar(ExpressionSyntax e)
        {
            if (model.GetTypeInfo(e, ct).Type is not { } type || Kernel.IsVectorType(type) || !IsLane(type)) throw No();
            if (model.GetConstantValue(e, ct).HasValue) return e.ToString();
            foreach (var name in e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                if (model.GetSymbolInfo(name, ct).Symbol is ILocalSymbol or IParameterSymbol or IMethodSymbol { IsStatic: false } or IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false })
                    throw No(); // the row's own values are loads
            return e.ToString();
        }

        /// <summary><c>c.Load128&lt;TLane&gt;(x + dx, dy, dz)</c> as the neighbor's field: <c>t[dx, dy, dz].F</c> (<c>t.Value.F</c> at the cell).</summary>
        private string Load(InvocationExpressionSyntax call)
        {
            if (model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol { Name: ("Load128" or "Load256" or "Load512" or "LoadVector") and var name, TypeArguments: [var lane] }
                || !SymbolEqualityComparer.Default.Equals(lane, _lane) || _load.Length > 0 && _load != name
                || call.ArgumentList.Arguments is not { Count: >= 1 and <= 3 } args || args.Any(a => a.NameColon != null)) throw No();
            _load = name;
            int dx = args[0].Expression switch
            {
                var only when Is(only, _index) => 0,
                BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression or (int)SyntaxKind.SubtractExpression } shifted when Is(shifted.Left, _index)
                    && model.GetConstantValue(shifted.Right, ct) is { HasValue: true, Value: int k } => shifted.IsKind(SyntaxKind.AddExpression) ? k : -k,
                _ => throw No(),
            };
            int Offset(int i) => args.Count <= i ? 0 : model.GetConstantValue(args[i].Expression, ct) is { HasValue: true, Value: int d } ? d : throw No();
            int dy = Offset(1), dz = Offset(2);
            return dx == 0 && dy == 0 && dz == 0 ? $"{_tile.Name}.Value.{_cellField}" : $"{_tile.Name}[{dx}, {dy}, {dz}].{_cellField}";
        }

        // ---------------- the cell and the out value ----------------

        /// <summary>The member that names a struct's one field of the lane type, else null.</summary>
        private static string? OneField(ITypeSymbol type, ITypeSymbol lane) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Struct } named && named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList() is [var field]
            && SymbolEqualityComparer.Default.Equals(field.Type, lane) ? Symbols.MemberName(field) : null;

        /// <summary>The out value built from <paramref name="value"/>: its constructor of one lane, else an initializer of its field.</summary>
        private string? Make(ITypeSymbol type, string value)
        {
            if (OneField(type, _lane) is not { } field) return null;
            string name = type.ToMinimalDisplayString(model, syntax.SpanStart);
            var named = (INamedTypeSymbol)type;
            if (named.InstanceConstructors.Any(c => c.Parameters is [{ Type: var p }] && SymbolEqualityComparer.Default.Equals(p, _lane)
                    && model.IsAccessible(syntax.SpanStart, c)))
                return $"new {name}({value})";
            return $"new {name} {{ {field} = {value} }}";
        }
    }
}
