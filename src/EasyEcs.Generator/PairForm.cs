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
    /// The pair method writes both sides of its pairs: it takes one component by <c>ref</c> of its row and of its
    /// neighbor (<c>ref Force f, [Neighbor] ref Force g</c>). It then runs once for each unordered pair of its rows (the
    /// row the earlier one in the tables' order), never for a row with itself, and its neighbors are its rows
    /// (<see cref="Problem"/> holds it to that: <see cref="SymmetricProblem"/>).
    /// </summary>
    public static bool IsSymmetric(IMethodSymbol method)
    {
        bool outside = Symbols.IsRunner(method);
        foreach (var p in method.Parameters)
            if (p.RefKind == RefKind.Ref && Symbols.Classify(p, outside) == Symbols.ParamKind.Neighbor && !Symbols.Named(p.Type, Symbols.Entity))
                foreach (var q in method.Parameters)
                    if (q.RefKind == RefKind.Ref && Symbols.Classify(q, outside) == Symbols.ParamKind.Component && SymbolEqualityComparer.Default.Equals(p.Type, q.Type))
                        return true;
        return false;
    }

    /// <summary>
    /// The components a symmetric pair method writes of both sides (<see cref="IsSymmetric"/>): each type its row and its
    /// neighbor take by <c>ref</c>.
    /// </summary>
    public static IEnumerable<ITypeSymbol> BothSides(IMethodSymbol method)
    {
        bool outside = Symbols.IsRunner(method);
        foreach (var p in method.Parameters)
            if (p.RefKind == RefKind.Ref && Symbols.Classify(p, outside) == Symbols.ParamKind.Neighbor
                && method.Parameters.Any(q => q.RefKind == RefKind.Ref && Symbols.Classify(q, outside) == Symbols.ParamKind.Component && SymbolEqualityComparer.Default.Equals(p.Type, q.Type)))
                yield return p.Type;
    }

    /// <summary>
    /// What the generator writes from a pair method's body: the component compared (<paramref name="Position"/>, the
    /// row's parameter <paramref name="Row"/>), the paths of its float fields that are the axes (1 to 3), the cutoff as
    /// symbols (an operation of uniform values, or of the row's components when <paramref name="PerRow"/>).
    /// </summary>
    public sealed record Shape(IParameterSymbol Row, ITypeSymbol Position, string[] Axes, IOperation Cutoff, bool PerRow);

    /// <summary>
    /// Why the declaration of pair method <paramref name="method"/> cannot run per pair (EECS105), or null. A pair
    /// pass runs over every row, on several threads only when it writes no neighbor or writes both sides (<see cref="IsSymmetric"/>; no Slices, change filters or [Consume]), takes components of
    /// the row (by <c>in</c>, <c>ref</c> or <c>Mut&lt;T&gt;</c>, a Mut&lt;T&gt; on one thread only: its stamps per write
    /// are the row's) and of the neighbor, singletons, values and <c>Reduce&lt;T&gt;</c> results (each chunk's combined
    /// in chunk order), and writes no component the other side of a pair reads: a
    /// row's write would reach the neighbors after it in one order and not in another, and a write of the neighbor's
    /// (<c>[Neighbor] ref T</c>) would reach the rows after it, or the row itself when it is its own neighbor. Each
    /// component of the neighbor is taken once.
    /// </summary>
    public static string? Problem(IMethodSymbol method)
    {
        if (!IsPairMethod(method)) return null;
        bool outside = Symbols.IsRunner(method);
        if (Symbols.IsForEntity(method)) return "is a [ForEntity] method: a pair runs over the world";
        if (!method.IsStatic) return "is an instance method: make it static";
        if (method.IsGenericMethod || method.ContainingType.IsGenericType) return "is generic: a pair method's order names its component";
        if (Symbols.IsParallel(method) && !IsSymmetric(method) && method.Parameters.FirstOrDefault(p => p.RefKind == RefKind.Ref && Symbols.IsNeighbor(p)) is { } shared)
            return $"runs in parallel and writes the neighbor's {shared.Type.Name} ('{shared.Name}'): rows of two threads would write one neighbor; drop Parallel";
        if (Symbols.IsParallel(method) && method.Parameters.FirstOrDefault(p => Symbols.Classify(p, outside) == Symbols.ParamKind.Mutable) is { } mutable)
            return $"runs in parallel and takes '{mutable.Name}' as a Mut<T>: its stamps per write would share the change ticks of the chunks' rows; take it by 'ref' (every row the pass runs for is stamped) or drop Parallel";
        if (Symbols.Slices(method) > 1) return "visits slices of its rows: a pair pass visits every row";
        if (Symbols.IsConsume(method)) return "has [Consume]: a pair pass destroys nothing";
        if (Symbols.AttributeFilters(method).Any(f => f.Item1 is Symbols.FilterKind.Changed or Symbols.FilterKind.Removed
                or Symbols.FilterKind.ChangedAny or Symbols.FilterKind.RemovedAny))
            return "filters by change: a pair pass visits every row";
        var written = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var rows = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var neighbors = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        bool row = false, neighbor = false;
        foreach (var p in method.Parameters)
            switch (Symbols.Classify(p, outside))
            {
                case Symbols.ParamKind.Component:
                    row = true;
                    rows.Add(p.Type);
                    if (p.RefKind == RefKind.Ref) written.Add(p.Type);
                    break;
                case Symbols.ParamKind.Mutable: // a component of the row written where the method writes it
                    row = true;
                    rows.Add(Symbols.MutElement(p.Type)!);
                    written.Add(Symbols.MutElement(p.Type)!);
                    break;
                case Symbols.ParamKind.Reduction: // a partial result of the pass (of each chunk in parallel), combined when it ends
                    break;
                case Symbols.ParamKind.Neighbor:
                    if (Symbols.Named(p.Type, Symbols.Entity)) break;
                    neighbor = true;
                    if (!neighbors.Add(p.Type)) return $"takes {p.Type.Name} of the neighbor twice ('{p.Name}'): take it once, by 'ref' to write it";
                    break;
                case Symbols.ParamKind.Entity or Symbols.ParamKind.Singleton:
                    break;
                case Symbols.ParamKind.Context when p.RefKind != RefKind.Ref:
                    break;
                case Symbols.ParamKind.Invalid: // EECS002 reports it (a [Neighbor] one too)
                    break;
                default:
                    return $"takes '{p.Name}': a pair method takes components of its row (by 'in', 'ref' or Mut<T>), [Neighbor] components (by 'in', or 'ref' to write them) and the neighbor's Entity, singletons, values and Reduce<T>";
            }
        if (!row) return "takes no component of its row";
        if (!neighbor) return "takes no [Neighbor] component: the neighbors are the entities holding them";
        if (IsSymmetric(method)) return SymmetricProblem(method, outside, rows);
        foreach (var p in method.Parameters)
        {
            if (Symbols.Classify(p, outside) != Symbols.ParamKind.Neighbor) continue;
            if (written.Contains(p.Type))
                return $"writes {p.Type.Name}, which it reads of the neighbor ('{p.Name}'): what a neighbor sees would depend on the order of the pairs; "
                    + "to write both sides of each pair once, take it by 'ref' of the row and of the neighbor";
            if (p.RefKind == RefKind.Ref && rows.Contains(p.Type))
                return $"writes the neighbor's {p.Type.Name} ('{p.Name}'), which it reads of its row: what a row sees would depend on the order of the pairs; "
                    + "to write both sides of each pair once, take it by 'ref' of the row too";
        }
        return null;
    }

    /// <summary>
    /// Why a pair method that writes both sides (<see cref="IsSymmetric"/>) cannot run each unordered pair once, or
    /// null: its neighbors are its rows, so each [Neighbor] component is one its row takes too; and each component it
    /// writes (by <c>ref</c>) it writes of both sides, since a write of one side only would see half of its pairs.
    /// A Mut&lt;T&gt; is refused (both sides are stamped as written).
    /// </summary>
    private static string? SymmetricProblem(IMethodSymbol method, bool outside, HashSet<ITypeSymbol> rows)
    {
        var both = new HashSet<ITypeSymbol>(BothSides(method), SymbolEqualityComparer.Default);
        string sides = string.Join(", ", both.Select(t => t.Name).OrderBy(n => n, System.StringComparer.Ordinal));
        foreach (var p in method.Parameters)
            switch (Symbols.Classify(p, outside))
            {
                case Symbols.ParamKind.Neighbor when !Symbols.Named(p.Type, Symbols.Entity) && !rows.Contains(p.Type):
                    return $"writes both sides of its pairs ({sides}), so its neighbors are its rows: take {p.Type.Name} of the row too, or drop '{p.Name}'";
                case Symbols.ParamKind.Neighbor when p.RefKind == RefKind.Ref && !both.Contains(p.Type):
                    return $"writes the neighbor's {p.Type.Name} ('{p.Name}'), which it reads of its row: take it by 'ref' of the row too";
                case Symbols.ParamKind.Component when p.RefKind == RefKind.Ref && !both.Contains(p.Type):
                    return $"writes both sides of its pairs ({sides}) and {p.Type.Name} of its row only ('{p.Name}'): each pair runs once, so the row would see half of its pairs; "
                        + $"write it of both sides ([Neighbor] ref {p.Type.Name}) or read it ('in')";
                case Symbols.ParamKind.Mutable:
                    return $"writes both sides of its pairs ({sides}) and takes '{p.Name}' as a Mut<T>: each pair runs once and both sides are stamped as written; take it by 'ref' of both sides or 'in'";
            }
        return null;
    }

    /// <summary>The method writes a component of the neighbor (<c>[Neighbor] ref T</c>): its pairs run one at a time.</summary>
    public static bool WritesNeighbor(IMethodSymbol method) =>
        method.Parameters.Any(p => p.RefKind == RefKind.Ref && Symbols.Classify(p, Symbols.IsRunner(method)) == Symbols.ParamKind.Neighbor);

    /// <summary>
    /// The cutoff test of a pair method's body, or null with the reason none was found (EECS106: the pass then runs
    /// every pair). The test is the first statement after the method's local declarations, <c>if (d2 &gt;= r2 || ...)
    /// return;</c>, or its last and only other, <c>if (d2 &lt; r2 &amp;&amp; ...) { ... }</c>: d2 a sum of squared
    /// differences (<c>dx * dx + dy * dy</c>, locals followed to their one value) of float fields of the same path of a
    /// row's component and of the neighbor's (or <c>Vector2/3.DistanceSquared</c> of a vector field), r2 a value of
    /// constants, readonly statics, singletons and values (the cutoff of the pass), or of the row's components by
    /// <c>in</c> too (a cutoff per row; of a symmetric method, the row's: the earlier of the pair). A symmetric method
    /// that writes the compared component has none.
    /// </summary>
    private static Shape? Read(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct, out string? reason)
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
            if (reader.Cutoff(term, far: returns) is { } shape)
            {
                // Both sides' points written during the pass: the clusters' boxes would not hold for the pairs after.
                if (IsSymmetric(method) && BothSides(method).Contains(shape.Position, SymbolEqualityComparer.Default))
                {
                    reason = $"it writes {shape.Position.Name} of both sides, which its test compares: the points move during the pass, so no box holds";
                    return null;
                }
                return shape;
            }
        reason = returns
            ? "no term of the test is the squared distance at or past a cutoff ('d2 >= r2' of a sum of squared differences of a row's field and the neighbor's)"
            : "no term of the test is the squared distance within a cutoff ('d2 < r2' of a sum of squared differences of a row's field and the neighbor's)";
        return null;
    }

    /// <summary>
    /// The cutoff test the pass reads (<see cref="Read"/>) with the squared cutoff as C# of the job
    /// (<paramref name="nameOf"/>: a parameter's name there, by index), or null with the reason the pass runs every pair
    /// (EECS106, through this call): no test found, a cutoff the pass cannot compute once (<c>Kernel.ScalarOf</c>), or a
    /// failure of the generator reading it, so one method's failure never drops the generated files of the others.
    /// </summary>
    public static (Shape Shape, string Cutoff)? ReadCutoff(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> nameOf, System.Threading.CancellationToken ct, out string? reason)
    {
        try
        {
            if (Read(method, syntax, model, ct, out reason) is not { } shape) return null;
            bool outside = Symbols.IsRunner(method);
            var names = new Dictionary<IParameterSymbol, string>(SymbolEqualityComparer.Default);
            for (int i = 0; i < method.Parameters.Length; i++) names[method.Parameters[i]] = nameOf(i, Symbols.Classify(method.Parameters[i], outside));
            // A partial method's test names the parameters of its implementing part.
            if (method.PartialImplementationPart is { } body)
                for (int i = 0; i < body.Parameters.Length; i++) names[body.Parameters[i]] = names[method.Parameters[i]];
            if (Kernel.ScalarOf(shape.Cutoff, names, out string? why) is { Length: > 0 } cutoff) return (shape, cutoff);
            reason = $"its cutoff is not a value the pass can read once: {why}";
            return null;
        }
        catch (System.Exception e) when (e is not System.OperationCanceledException)
        {
            reason = Kernel.FailedTo("read its cutoff test", e);
            return null;
        }
    }

    /// <summary>
    /// How a [ForEach] method that is no pair method searches its row's neighbors by hand (EECS107), or null: in a loop,
    /// it tests the squared distance between a component of its row and the same component of another entity read
    /// through a Lookup&lt;T&gt;, or of an item of a query it walks (<c>foreach (var o in world.Read&lt;Position&gt;())</c>).
    /// </summary>
    public static string? HandWritten(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        bool outside = Symbols.IsRunner(method);
        if (IsPairMethod(method) || !method.Parameters.Any(p => Symbols.Classify(p, outside) is Symbols.ParamKind.Lookup or Symbols.ParamKind.World)) return null;
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        if (((SyntaxNode?)syntax.Body ?? syntax.ExpressionBody) is not { } node ||!node.DescendantNodes().Any(n => n is ForStatementSyntax
                or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax) || model.GetOperation(node, ct) is not { } body)
            return null;
        return new Reader(method, body, hand: true).TestsInLoop() is { } walk
            ? walk ? "a walk of a query of other entities tests their squared distance to the row's"
                : "a loop over other entities, read through a Lookup<T>, tests their squared distance to the row's"
            : null;
    }

    /// <summary>
    /// Whether <paramref name="condition"/>, a test of a neighbor search by hand that skips the candidate when true
    /// (<c>if (d2 &gt;= r2) continue;</c>), holds a cutoff the generator reads, the candidate's side read through a
    /// Lookup&lt;T&gt; of the row's component (EECS107's fix: the test becomes the pair method's).
    /// </summary>
    public static bool HandCutoff(IMethodSymbol method, IOperation body, IOperation condition)
    {
        var reader = new Reader(method, body, hand: true);
        foreach (var term in Terms(condition, BinaryOperatorKind.ConditionalOr))
            if (reader.Cutoff(term, far: true) != null) return true;
        return false;
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

        /// <summary>
        /// Whether a relational test inside a loop compares a squared distance (EECS107): null if none, else whether the
        /// neighbor's side is an item of a query walk (else a read through a Lookup&lt;T&gt;).
        /// </summary>
        public bool? TestsInLoop()
        {
            foreach (var op in body.Descendants())
                if (op is IBinaryOperation { OperatorKind: BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.GreaterThan
                        or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual } test
                    && InLoop(test) && (Distance(test.LeftOperand) ?? Distance(test.RightOperand)) != null)
                    return _walked;
            return null;
        }

        // The last Other check of the hand mode took an item of a query walk.
        private bool _walked;

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
        private bool Other(IOperation root, IParameterSymbol row)
        {
            if (!hand) return root is IParameterReferenceOperation { Parameter: var p } && Symbols.Classify(p, _outside) == Symbols.ParamKind.Neighbor;
            if (LookupRead(root) is { } lookup)
            {
                _walked = false;
                return Symbols.Classify(lookup, _outside) == Symbols.ParamKind.Lookup
                       && SymbolEqualityComparer.Default.Equals(Symbols.LookupElement(lookup.Type), row.Type);
            }

            // A query walk's item (o.Component1, the generated o.Position) of the row's component.
            _walked = true;
            return root is IPropertyReferenceOperation { Instance: ILocalReferenceOperation { Local: var item } } property
                   && SymbolEqualityComparer.Default.Equals(property.Type, row.Type) && IsQueryItem(item.Type);
        }

        /// <summary>A QueryItem or ReadItem of EasyEcs (the iteration variable of a query walk).</summary>
        private static bool IsQueryItem(ITypeSymbol type) =>
            type is INamedTypeSymbol { Name: "QueryItem" or "ReadItem", ContainingNamespace: { Name: "EasyEcs", ContainingNamespace.IsGlobalNamespace: true } };

        /// <summary>The parameter <paramref name="op"/> reads another entity's component through (<c>lookup.Read(id)</c>), or null.</summary>
        private static IParameterSymbol? LookupRead(IOperation op) =>
            op is IInvocationOperation { TargetMethod.Name: "Read", Instance: IParameterReferenceOperation { Parameter: var p } } ? p : null;

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
