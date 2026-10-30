using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A tile load the generator checks once instead of per call: <c>c.Load128&lt;T&gt;(x + k, dy, dz)</c> (or
/// <c>Load256</c>, <c>LoadVector</c>) of a <c>TileCursor</c>, its column the variable of the loop around it,
/// <c>for (int x = a; x &lt; e; x += s)</c>, plus a constant. The cursor checks each load's column (a compare and a
/// sign extension per column of the stencil); the loop already says which columns it loads, so the interceptor the
/// generator writes checks the loop's first and last column once, in constants the JIT folds, and loads each column
/// at one zero-extended index off the row's first halo cell.
/// </summary>
/// <param name="Load">The cursor's method: Load128, Load256 or LoadVector.</param>
/// <param name="First">The loop variable's first value (a).</param>
/// <param name="End">The bound the variable stays under (e), as the interceptor writes it: a number, or
/// <c>TTile.Width</c> (or Depth, Height, Halo) of the cursor's shape, plus one for <c>&lt;=</c>.</param>
/// <param name="Step">What the loop adds each time (s &gt; 0).</param>
/// <param name="Offset">The constant added to the variable (k).</param>
internal sealed record TileLoad(string Load, int First, string End, int Step, int Offset)
{
    private static readonly string[] Loads = ["Load128", "Load256", "LoadVector"];
    private static readonly string[] Extents = ["Width", "Depth", "Height", "Halo"];

    /// <summary>A call that may be a cursor's load: decided on syntax alone.</summary>
    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Load128" or "Load256" or "LoadVector" } };

    /// <summary>
    /// The load's loop, or null when the call is not a cursor's load or its column is not proven to step through the
    /// loop: the column is the loop variable plus a constant; the loop declares the variable (an <c>int</c> from a
    /// constant), tests it against a constant or the cursor's shape and adds a positive constant to it; nothing else
    /// writes it, and the call runs in the loop's body itself (not in a lambda or local function there, which could run
    /// after the loop). Those make every column the call reads one the loop reaches; the interceptor's check of the
    /// loop's first and last does the rest.
    /// </summary>
    public static TileLoad? Read(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken ct)
    {
        if (model.GetOperation(invocation, ct) is not IInvocationOperation call || !Loads.Contains(call.TargetMethod.Name)
            || call.TargetMethod.ContainingType is not { Name: "TileCursor", Arity: 2, ContainingNamespace: { Name: "EasyEcs", ContainingNamespace.IsGlobalNamespace: true } } cursor)
            return null;
        var dx = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "dx");
        if (dx == null || dx.ArgumentKind != ArgumentKind.Explicit || Column(dx.Value) is not ({ } x, var offset)) return null;

        // The loop that declares the variable, with no function between it and the call.
        ForStatementSyntax? loop = null;
        for (var node = invocation.Parent; node != null && loop == null; node = node.Parent)
        {
            if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax) return null;
            if (node is ForStatementSyntax { Declaration.Variables: { Count: 1 } only } f && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(only[0], ct), x))
                loop = f;
        }
        if (loop == null || !loop.Statement.Span.Contains(invocation.Span)) return null;
        var declarator = loop.Declaration!.Variables[0];
        if (x.Type.SpecialType != SpecialType.System_Int32 || declarator.Initializer == null
            || model.GetConstantValue(declarator.Initializer.Value, ct) is not { HasValue: true, Value: int first })
            return null;
        if (Bound(loop.Condition, x, cursor.TypeArguments[1], model, ct) is not { } end) return null;
        if (loop.Incrementors.Count != 1 || Increment(loop.Incrementors[0], x, model, ct) is not (> 0 and var step)) return null;
        if (!Unwritten(loop.Statement, x, model)) return null;
        return new TileLoad(call.TargetMethod.Name, first, end, step, offset);
    }

    /// <summary>The local and constant of a column <c>x</c>, <c>x + k</c>, <c>k + x</c> or <c>x - k</c>.</summary>
    private static (ILocalSymbol? Local, int Offset) Column(IOperation value)
    {
        while (value is IConversionOperation { IsImplicit: true } conversion) value = conversion.Operand;
        if (value is ILocalReferenceOperation { Local.RefKind: RefKind.None } alone) return (alone.Local, 0);
        if (value is not IBinaryOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract } binary) return (null, 0);
        bool add = binary.OperatorKind == BinaryOperatorKind.Add;
        if (binary.LeftOperand is ILocalReferenceOperation { Local.RefKind: RefKind.None } left && binary.RightOperand.ConstantValue is { HasValue: true, Value: int right })
            return add || right != int.MinValue ? (left.Local, add ? right : -right) : (null, 0);
        if (add && binary.RightOperand is ILocalReferenceOperation { Local.RefKind: RefKind.None } local && binary.LeftOperand.ConstantValue is { HasValue: true, Value: int constant })
            return (local.Local, constant);
        return (null, 0);
    }

    /// <summary>The bound of <c>x &lt; e</c> / <c>x &lt;= e</c> as the interceptor writes it, or null.</summary>
    private static string? Bound(ExpressionSyntax? condition, ILocalSymbol x, ITypeSymbol shape, SemanticModel model, CancellationToken ct)
    {
        if (condition is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LessThanExpression or (int)SyntaxKind.LessThanOrEqualExpression } test
            || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(test.Left, ct).Symbol, x))
            return null;
        string plus = test.IsKind(SyntaxKind.LessThanOrEqualExpression) ? " + 1L" : "";
        if (model.GetConstantValue(test.Right, ct) is { HasValue: true, Value: int bound }) return bound + "L" + plus;
        // The cursor's shape: its extent, a constant once the JIT compiles the interceptor for the shape.
        if (model.GetOperation(test.Right, ct) is IPropertyReferenceOperation { Instance: null, Property: { IsStatic: true } property } reference
            && Extents.Contains(property.Name) && property.ContainingType is { Name: "ITileShape", ContainingNamespace.Name: "EasyEcs" }
            && SymbolEqualityComparer.Default.Equals(reference.ConstrainedToType, shape))
            return "TTile." + property.Name + plus;
        return null;
    }

    /// <summary>What <c>x++</c>, <c>++x</c>, <c>x += s</c> or <c>x = x + s</c> adds to the variable, or 0.</summary>
    private static int Increment(ExpressionSyntax incrementor, ILocalSymbol x, SemanticModel model, CancellationToken ct)
    {
        bool Is(ExpressionSyntax e) => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(e, ct).Symbol, x);
        int Constant(ExpressionSyntax e) => model.GetConstantValue(e, ct) is { HasValue: true, Value: int s } ? s : 0;
        return incrementor switch
        {
            PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression } post when Is(post.Operand) => 1,
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression } pre when Is(pre.Operand) => 1,
            AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } add when Is(add.Left) => Constant(add.Right),
            AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Right: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum } set
                when Is(set.Left) && Is(sum.Left) => Constant(sum.Right),
            _ => 0,
        };
    }

    /// <summary>
    /// Whether the loop's body leaves the variable alone: no write (assignments, increments, out arguments) and no
    /// reference through which one could happen (<c>ref x</c>, a ref argument, <c>&amp;x</c>).
    /// </summary>
    private static bool Unwritten(StatementSyntax body, ILocalSymbol x, SemanticModel model)
    {
        var flow = model.AnalyzeDataFlow(body);
        if (flow == null || !flow.Succeeded || flow.WrittenInside.Contains(x, SymbolEqualityComparer.Default)) return false;
        foreach (var name in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (name.Identifier.ValueText != x.Name) continue;
            if (name.Parent is RefExpressionSyntax or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression }
                || name.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or (int)SyntaxKind.OutKeyword })
                return false;
        }
        return true;
    }
}
