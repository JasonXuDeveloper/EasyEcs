using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// What a pair method is (<c>[Neighbor]</c> parameters), read once for the generator (SystemGenerator.Pairs.cs) and the
/// analyzer (EECS105, EECS106): whether its declaration can run per pair (<see cref="Problem"/>), and the cutoff its body
/// tests (<see cref="Read"/>): a test of the squared distance between the same float fields of a component of the row
/// and of the neighbor against a value the pass can compute once (or once per row), which every write of the method
/// follows. Pairs farther apart than that value never run, so the pass visits only the rows within it.
/// </summary>
internal static class PairForm
{
    /// <summary>The method takes a [Neighbor] parameter: it runs per pair.</summary>
    public static bool IsPairMethod(IMethodSymbol method) => method.Parameters.Any(Symbols.IsNeighbor);

    /// <summary>
    /// What the generator writes from a pair method's body: the component compared (<paramref name="Position"/>, the
    /// row's parameter <paramref name="Row"/>), the paths of its float fields that are the axes (1 to 3), the cutoff as
    /// symbols (an operation of uniform values, or of the row's components when <paramref name="PerRow"/>).
    /// </summary>
    public sealed record Shape(IParameterSymbol Row, ITypeSymbol Position, string[] Axes, IOperation Cutoff, bool PerRow);

    /// <summary>
    /// Why the declaration of pair method <paramref name="method"/> cannot run per pair (EECS105), or null. A pair
    /// pass runs on one thread over every row (no Parallel, Slices, change filters or [Consume]), takes components of
    /// the row and of the neighbor, singletons and values, and writes no component the neighbor reads (a row's write
    /// would reach the neighbors after it in one order and not in another).
    /// </summary>
    public static string? Problem(IMethodSymbol method)
    {
        if (!IsPairMethod(method)) return null;
        bool outside = Symbols.IsRunner(method);
        if (Symbols.IsForEntity(method)) return "is a [ForEntity] method: a pair runs over the world";
        if (!method.IsStatic) return "is an instance method: make it static";
        if (method.IsGenericMethod || method.ContainingType.IsGenericType) return "is generic: a pair method's order names its component";
        if (Symbols.IsParallel(method)) return "runs in parallel: a pair pass runs on one thread for now";
        if (Symbols.Slices(method) > 1) return "visits slices of its rows: a pair pass visits every row";
        if (Symbols.IsConsume(method)) return "has [Consume]: a pair pass destroys nothing";
        if (Symbols.AttributeFilters(method).Any(f => f.Item1 is Symbols.FilterKind.Changed or Symbols.FilterKind.Removed
                or Symbols.FilterKind.ChangedAny or Symbols.FilterKind.RemovedAny))
            return "filters by change: a pair pass visits every row";
        var written = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        bool row = false, neighbor = false;
        foreach (var p in method.Parameters)
            switch (Symbols.Classify(p, outside))
            {
                case Symbols.ParamKind.Component:
                    row = true;
                    if (p.RefKind == RefKind.Ref) written.Add(p.Type);
                    break;
                case Symbols.ParamKind.Neighbor:
                    neighbor |= !Symbols.Named(p.Type, Symbols.Entity);
                    break;
                case Symbols.ParamKind.Entity or Symbols.ParamKind.Singleton:
                    break;
                case Symbols.ParamKind.Context when p.RefKind != RefKind.Ref:
                    break;
                case Symbols.ParamKind.Invalid: // EECS002 reports it (a [Neighbor] one too)
                    break;
                default:
                    return $"takes '{p.Name}': a pair method takes components of its row, [Neighbor] components (by 'in') and the neighbor's Entity, singletons and values";
            }
        if (!row) return "takes no component of its row";
        if (!neighbor) return "takes no [Neighbor] component: the neighbors are the entities holding them";
        foreach (var p in method.Parameters)
            if (Symbols.Classify(p, outside) == Symbols.ParamKind.Neighbor && written.Contains(p.Type))
                return $"writes {p.Type.Name}, which it reads of the neighbor ('{p.Name}'): what a neighbor sees would depend on the order of the pairs";
        return null;
    }

    /// <summary>
    /// The cutoff test of a pair method's body, or null with the reason none was found (EECS106: the pass then runs
    /// every pair). The test is the first statement after the method's local declarations, <c>if (d2 &gt;= r2 || ...)
    /// return;</c>, or its last and only other, <c>if (d2 &lt; r2 &amp;&amp; ...) { ... }</c>: d2 a sum of squared
    /// differences (<c>dx * dx + dy * dy</c>, locals followed to their one value) of float fields of the same path of a
    /// row's component and of the neighbor's (or <c>Vector2/3.DistanceSquared</c> of a vector field), r2 a value of
    /// constants, readonly statics, singletons and values (the cutoff of the pass), or of the row's components by
    /// <c>in</c> too (a cutoff per row).
    /// </summary>
    public static Shape? Read(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct, out string? reason)
    {
        reason = null;
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        if (syntax.Body == null || model.GetOperation(syntax.Body, ct) is not IBlockOperation block)
        {
            reason = "its body is not a block";
            return null;
        }

        var statements = block.Operations;
        int at = 0;
        while (at < statements.Length && statements[at] is IVariableDeclarationGroupOperation) at++;
        if (at == statements.Length || statements[at] is not IConditionalOperation { WhenFalse: null } test)
        {
            reason = "its first statement after the local declarations is not the cutoff test ('if (d2 >= r2) return;')";
            return null;
        }

        bool returns = test.WhenTrue is IReturnOperation { ReturnedValue: null }
            || test.WhenTrue is IBlockOperation { Operations.Length: 1 } only && only.Operations[0] is IReturnOperation { ReturnedValue: null };
        bool last = at == statements.Length - 1 || at == statements.Length - 2 && statements[at + 1] is IReturnOperation { IsImplicit: true };
        if (!returns && !last)
        {
            reason = "the test after the local declarations neither returns nor holds the rest of the body";
            return null;
        }

        var reader = new Reader(method, block);
        foreach (var term in returns ? Terms(test.Condition, BinaryOperatorKind.ConditionalOr) : Terms(test.Condition, BinaryOperatorKind.ConditionalAnd))
            if (reader.Cutoff(term, far: returns) is { } shape) return shape;
        reason = returns
            ? "no term of the test is the squared distance at or past a cutoff ('d2 >= r2' of a sum of squared differences of a row's field and the neighbor's)"
            : "no term of the test is the squared distance within a cutoff ('d2 < r2' of a sum of squared differences of a row's field and the neighbor's)";
        return null;
    }

    /// <summary>
    /// How a [ForEach] method that is no pair method searches its row's neighbors by hand (EECS107), or null: in a loop,
    /// it tests the squared distance between a component of its row and the same component of another entity read
    /// through a Lookup&lt;T&gt;.
    /// </summary>
    public static string? HandWritten(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (IsPairMethod(method) || !method.Parameters.Any(p => Symbols.Classify(p, Symbols.IsRunner(method)) == Symbols.ParamKind.Lookup)) return null;
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        if (((SyntaxNode?)syntax.Body ?? syntax.ExpressionBody) is not { } node ||!node.DescendantNodes().Any(n => n is ForStatementSyntax
                or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax) || model.GetOperation(node, ct) is not { } body)
            return null;
        return new Reader(method, body, hand: true).TestsInLoop()
            ? "a loop over other entities, read through a Lookup<T>, tests their squared distance to the row's"
            : null;
    }

    /// <summary>The operands of a chain of <paramref name="kind"/> (|| or &amp;&amp;), left to right.</summary>
    private static IEnumerable<IOperation> Terms(IOperation condition, BinaryOperatorKind kind)
    {
        if (condition is IBinaryOperation b && b.OperatorKind == kind)
        {
            foreach (var t in Terms(b.LeftOperand, kind)) yield return t;
            foreach (var t in Terms(b.RightOperand, kind)) yield return t;
        }
        else yield return condition;
    }

    /// <summary>The body's distance and cutoff, locals followed to their one value.</summary>
    /// <param name="hand">
    /// A neighbor search by hand (EECS107): the neighbor's side is a value read through a Lookup&lt;T&gt; of the row's
    /// component (<c>positions.Read(id)</c>) instead of a [Neighbor] parameter.
    /// </param>
    private sealed class Reader(IMethodSymbol method, IOperation body, bool hand = false)
    {
        private readonly bool _outside = Symbols.IsRunner(method);

        /// <summary>The first squared distance a relational test inside a loop compares (EECS107), or null.</summary>
        public bool TestsInLoop()
        {
            foreach (var op in body.Descendants())
                if (op is IBinaryOperation { OperatorKind: BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.GreaterThan
                        or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual } test
                    && InLoop(test) && (Distance(test.LeftOperand) != null || Distance(test.RightOperand) != null))
                    return true;
            return false;
        }

        private bool InLoop(IOperation op)
        {
            for (var at = op.Parent; at != null && !ReferenceEquals(at, body); at = at.Parent)
                if (at is ILoopOperation) return true;
            return false;
        }

        /// <summary>
        /// <paramref name="term"/> as a cutoff test: <c>d2 &gt;= r2</c> (or <c>&gt;</c>, or <c>r2 &lt;= d2</c>) when
        /// <paramref name="far"/>, <c>d2 &lt; r2</c> (or <c>&lt;=</c>, or <c>r2 &gt; d2</c>) otherwise.
        /// </summary>
        public Shape? Cutoff(IOperation term, bool far)
        {
            if (term is not IBinaryOperation { OperatorMethod: null } b) return null;
            bool? left = b.OperatorKind switch
            {
                BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.GreaterThan => far,
                BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual => !far,
                _ => null,
            };
            if (left is not bool distanceLeft) return null;
            var (distance, cutoff) = distanceLeft ? (b.LeftOperand, b.RightOperand) : (b.RightOperand, b.LeftOperand);
            if (Distance(distance) is not var (row, axes)) return null;
            cutoff = Resolve(cutoff);
            bool perRow = false;
            foreach (var op in cutoff.DescendantsAndSelf())
            {
                if (op is ILocalReferenceOperation) return null; // a local the cutoff could not follow to one value
                if (op is not IParameterReferenceOperation { Parameter: var p }) continue;
                var kind = Symbols.Classify(p, _outside);
                if (kind == Symbols.ParamKind.Component && p.RefKind == RefKind.In) perRow = true;
                else if (kind is not (Symbols.ParamKind.Singleton or Symbols.ParamKind.Context)) return null;
            }
            return new Shape(row, row.Type, axes, cutoff, perRow);
        }

        /// <summary>
        /// A sum of 1 to 3 squared differences of distinct float fields of one path of a row's component and the
        /// neighbor's of the same type, or <c>Vector2/3.DistanceSquared</c> of such a field.
        /// </summary>
        private (IParameterSymbol Row, string[] Axes)? Distance(IOperation op)
        {
            op = Resolve(op);
            if (op is IInvocationOperation { Instance: null, TargetMethod: { Name: "DistanceSquared" } m, Arguments.Length: 2 } call
                && m.ContainingType.ToDisplayString() is "System.Numerics.Vector2" or "System.Numerics.Vector3"
                && Difference(call.Arguments[0].Value, call.Arguments[1].Value) is var (vectorRow, vectorPath))
            {
                var fields = m.ContainingType.Name == "Vector2" ? new[] { "X", "Y" } : new[] { "X", "Y", "Z" };
                return (vectorRow, fields.Select(f => vectorPath + "." + f).ToArray());
            }

            // (p.V - q.V).LengthSquared() of a Vector2 / Vector3 field.
            if (op is IInvocationOperation { TargetMethod: { Name: "LengthSquared", Parameters.Length: 0 } length, Instance: { } instance }
                && length.ContainingType.ToDisplayString() is "System.Numerics.Vector2" or "System.Numerics.Vector3"
                && Resolve(instance) is IBinaryOperation { OperatorKind: BinaryOperatorKind.Subtract } difference
                && Difference(difference.LeftOperand, difference.RightOperand) is var (lengthRow, lengthPath))
            {
                var fields = length.ContainingType.Name == "Vector2" ? new[] { "X", "Y" } : new[] { "X", "Y", "Z" };
                return (lengthRow, fields.Select(f => lengthPath + "." + f).ToArray());
            }

            var terms = new List<IOperation>();
            Sum(op, terms);
            IParameterSymbol? row = null;
            var axes = new List<string>();
            foreach (var term in terms)
            {
                if (Resolve(term) is not IBinaryOperation { OperatorKind: BinaryOperatorKind.Multiply, OperatorMethod: null } square
                    || Squared(square.LeftOperand, square.RightOperand) is not var (r, path)
                    || row != null && !SymbolEqualityComparer.Default.Equals(row, r) || axes.Contains(path))
                    return null;
                row = r;
                axes.Add(path);
            }
            return row != null && axes.Count is >= 1 and <= 3 ? (row, axes.ToArray()) : null;
        }

        private void Sum(IOperation op, List<IOperation> terms)
        {
            op = Resolve(op);
            if (op is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, OperatorMethod: null } add)
            {
                Sum(add.LeftOperand, terms);
                Sum(add.RightOperand, terms);
            }
            else terms.Add(op);
        }

        /// <summary><c>dx * dx</c>: both operands the same difference (one local, or the same fields).</summary>
        private (IParameterSymbol Row, string Path)? Squared(IOperation left, IOperation right)
        {
            var l = Resolve(left);
            var r = Resolve(right);
            if (l is not IBinaryOperation { OperatorKind: BinaryOperatorKind.Subtract, OperatorMethod: null } dl
                || r is not IBinaryOperation { OperatorKind: BinaryOperatorKind.Subtract, OperatorMethod: null } dr
                || dl.Type?.SpecialType != SpecialType.System_Single)
                return null;
            var a = Difference(dl.LeftOperand, dl.RightOperand);
            var b = Difference(dr.LeftOperand, dr.RightOperand);
            return a is { } x && b is { } y && SymbolEqualityComparer.Default.Equals(x.Row, y.Row) && x.Path == y.Path
                && Same(dl.LeftOperand, dr.LeftOperand) ? a : null;
        }

        private bool Same(IOperation a, IOperation b) => Path(a) is var (p, f) && Path(b) is var (q, g)
            && f == g && (p is IParameterReferenceOperation x && q is IParameterReferenceOperation y
                ? SymbolEqualityComparer.Default.Equals(x.Parameter, y.Parameter)
                : p.Syntax.IsEquivalentTo(q.Syntax));

        /// <summary>A row's field minus the neighbor's (or the reverse), of one path of components of one type.</summary>
        private (IParameterSymbol Row, string Path)? Difference(IOperation left, IOperation right)
        {
            if (Path(left) is not var (a, pa) || Path(right) is not var (b, pb) || pa != pb) return null;
            if (!SymbolEqualityComparer.Default.Equals(a.Type, b.Type)) return null;
            if (Row(a) is { } row && Other(b, row)) return (row, pa);
            if (Row(b) is { } swapped && Other(a, swapped)) return (swapped, pb);
            return null;
        }

        /// <summary>The row's component by 'in', or null.</summary>
        private IParameterSymbol? Row(IOperation root) => root is IParameterReferenceOperation { Parameter: var p }
            && Symbols.Classify(p, _outside) == Symbols.ParamKind.Component && p.RefKind == RefKind.In ? p : null;

        /// <summary>The neighbor's side: a [Neighbor] parameter, or (by hand) a read of another entity's component of the row's type.</summary>
        private bool Other(IOperation root, IParameterSymbol row) => hand
            ? root is IInvocationOperation { TargetMethod.Name: "Read", Instance: IParameterReferenceOperation { Parameter: var lookup } }
              && Symbols.Classify(lookup, _outside) == Symbols.ParamKind.Lookup
              && SymbolEqualityComparer.Default.Equals(Symbols.LookupElement(lookup.Type), row.Type)
            : root is IParameterReferenceOperation { Parameter: var p } && Symbols.Classify(p, _outside) == Symbols.ParamKind.Neighbor;

        /// <summary>A chain of field reads (<c>p.At.X</c>): what it starts from (a parameter, a call) and the path ("At.X").</summary>
        private (IOperation Root, string Path)? Path(IOperation op)
        {
            op = Resolve(op);
            var names = new List<string>();
            while (true)
            {
                if (op is IFieldReferenceOperation { Field.IsStatic: false, Instance: { } instance } field)
                {
                    names.Insert(0, field.Field.Name);
                    op = instance;
                }
                // An auto-property (a positional record's): a field read, no code of its own.
                else if (op is IPropertyReferenceOperation { Property: { IsStatic: false, Parameters.Length: 0 } property, Instance: { } owner }
                         && Symbols.BackingField(property) != null)
                {
                    names.Insert(0, property.Name);
                    op = owner;
                }
                // A local holding what the chain starts from (ref readonly var q = ref lookup.Read(id)).
                else if (Resolve(op) is var value && !ReferenceEquals(value, op)) op = value;
                else break;
            }
            return names.Count > 0 && op.Type != null ? (op, string.Join(".", names)) : null;
        }

        /// <summary>A local read as its one value (its initializer, when nothing else assigns it), conversions of identity dropped.</summary>
        public IOperation Resolve(IOperation op)
        {
            for (int depth = 0; depth < 8; depth++)
            {
                if (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
                else if (op is ILocalReferenceOperation { Local: var local } && Value(local) is { } value) op = value;
                else return op;
            }
            return op;
        }

        private IOperation? Value(ILocalSymbol local)
        {
            IOperation? value = null;
            foreach (var op in body.Descendants())
            {
                if (op is IVariableDeclaratorOperation d && SymbolEqualityComparer.Default.Equals(d.Symbol, local))
                    value = d.Initializer?.Value;
                // Any other write (an assignment, ++, a ref or out argument, a ref local over it) and it has no one value.
                else if (op is ILocalReferenceOperation { Local: var l } use && SymbolEqualityComparer.Default.Equals(l, local) && Writes(use))
                    return null;
            }
            return value;
        }

        private static bool Writes(IOperation use) => use.Parent switch
        {
            IAssignmentOperation a => ReferenceEquals(a.Target, use),
            IIncrementOrDecrementOperation => true,
            IArgumentOperation { Parameter.RefKind: not (RefKind.None or RefKind.In or RefKind.RefReadOnlyParameter) } => true,
            IAddressOfOperation => true,
            _ => use.Parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.IsRef: true } },
        };
    }
}
