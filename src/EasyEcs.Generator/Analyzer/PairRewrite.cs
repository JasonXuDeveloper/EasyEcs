using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EasyEcs.Generator;

/// <summary>
/// What a neighbor search written by hand becomes as a pair method (<c>[Neighbor]</c> parameters), read once for the
/// analyzer and its code fix (EECS107): the new parameter list and the new body as text. Two searches: a loop over
/// candidate ids read through Lookup&lt;T&gt; (a grid of cells, a list of ids), and a walk of a query of other
/// entities (<c>foreach (var o in world.Read&lt;Position, Velocity&gt;())</c>). The rewrite is offered only when it is
/// mechanical: the search is the method's whole body, the candidates' loop does nothing but run the pair's statements,
/// the pair's statements read nothing of the search (no cell, no index), and their first test after the local
/// declarations skips the candidates past a cutoff the generator reads. The pair method then visits every entity
/// holding the neighbor's components within that cutoff (the search's candidates were a superset of them, or the same
/// set for a query walk, in the order of the tables' rows: a sum over a grid may differ in its last bits). A query walk
/// of the candidates after the row (<c>if (o.Entity.Id.Index &lt;= self.Id.Index) continue;</c>) that writes the
/// candidate's components the row takes by <c>ref</c> meets each unordered pair once and writes both sides: it becomes
/// the pair method writing both sides (<c>[Neighbor] ref</c>, PairForm.IsSymmetric), its pairs in the tables' order.
/// </summary>
internal static class PairRewrite
{
    /// <summary>The pair method's parameter list (without parentheses) and its statements, dedented, one line per line.</summary>
    public sealed record Result(string Parameters, string Body);

    /// <summary>The neighbor's Entity in a candidate's use, before its free name is known.</summary>
    private const string Other = "\u0001other";

    /// <summary>
    /// What the pair reads of the candidate, by search: a read of a neighbor's component (its type and the name its
    /// parameter gets when no local names it), a use of the candidate's variable outside a read (its replacement, or
    /// null: no rewrite), the candidate's id in a self skip, and the components every neighbor holds.
    /// </summary>
    private sealed class Candidate
    {
        public ILocalSymbol Variable = null!;
        public Func<ExpressionSyntax, (ITypeSymbol Type, string Name)?> Read = null!;
        public Func<IdentifierNameSyntax, (TextSpan Span, string Text)?> Use = null!;
        public Func<ExpressionSyntax, bool> IsId = null!;
        public ITypeSymbol[] Holds = [];
    }

    /// <summary>
    /// The pair method a [ForEach] method searching its neighbors by hand becomes, or null when the rewrite is not
    /// mechanical. The shape: local declarations of the search, then either nested <c>for</c> / <c>foreach</c> loops
    /// (each body only declarations and the next loop) down to the loop over candidates, whose iteration variable or
    /// first local is the candidate's <c>EntityId</c>, read through <c>lookup.Read(id)</c>; or one <c>foreach</c> over
    /// <c>world.Read&lt;...&gt;()</c> / <c>world.Query&lt;...&gt;()</c> without a filter, read through the item's
    /// components (<c>o.Component1</c>, <c>o.Position</c>). The rest of that body is the pair: an optional self skip
    /// (<c>if (id == self.Id) continue;</c>, <c>o.Entity.Id</c>), locals bound to a read (also after the test), local
    /// declarations, the cutoff test <c>if (d2 &gt;= r2) continue;</c>, then any statements.
    /// </summary>
    public static Result? Of(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (!method.IsStatic || method.IsGenericMethod || Symbols.IsParallel(method) || Symbols.Slices(method) > 1 || Symbols.IsConsume(method)
            || Symbols.AttributeFilters(method).Any(f => f.Item1 is Symbols.FilterKind.Changed or Symbols.FilterKind.Removed
                or Symbols.FilterKind.ChangedAny or Symbols.FilterKind.RemovedAny))
            return null; // the pair method would be EECS105's
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        if (syntax.Body is not { Statements.Count: > 0 } body) return null;

        // The search: declarations, then the loops down to the one over candidates.
        var statements = body.Statements;
        for (int i = 0; i < statements.Count - 1; i++)
            if (statements[i] is not LocalDeclarationStatementSyntax) return null;
        var outer = statements[statements.Count - 1];
        if (!IsLoop(outer)) return null;
        var loop = outer;
        while (Inner(loop) is { } next) loop = next;
        var candidates = Statements(LoopBody(loop));
        int first = 0;
        Candidate? candidate;
        if (QueryWalk(loop, method, model, ct) is { } walk)
            candidate = ReferenceEquals(loop, outer) ? walk : null; // a walk inside another loop would meet each pair again
        else if (loop is ForEachStatementSyntax each && model.GetDeclaredSymbol(each, ct) is { } variable && IsEntityId(variable.Type))
            candidate = IdSearch(variable, model, ct);
        else if (candidates.Count > 0 && candidates[0] is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } idDeclaration
                 && model.GetDeclaredSymbol(idDeclaration.Declaration.Variables[0], ct) is ILocalSymbol local && IsEntityId(local.Type))
        {
            candidate = IdSearch(local, model, ct);
            first = 1;
        }
        else return null;
        if (candidate == null) return null;

        var pair = candidates.Skip(first).ToList();
        return pair.Count == 0 ? null : Rewrite(method, syntax, model, candidate, loop, pair, ct);
    }

    /// <summary>The rewrite of the pair statements <paramref name="pair"/> of <paramref name="loop"/> over <paramref name="candidate"/>.</summary>
    private static Result? Rewrite(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, Candidate candidate, StatementSyntax loop,
        List<StatementSyntax> pair, System.Threading.CancellationToken ct)
    {
        var spans = pair.Select(s => s.Span).ToList();
        bool InPair(SyntaxNode node) => spans.Any(s => s.Contains(node.Span));
        bool outside = Symbols.IsRunner(method);

        var neighbors = new List<ITypeSymbol>(); // in order of first read
        var names = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
        var neighborOfLocal = new Dictionary<ILocalSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        var removed = new List<StatementSyntax>();
        var edits = new List<(TextSpan Span, string Text)>();
        bool entity = false, half = false;
        IParameterSymbol? halfSelf = null; // the row's Entity the half walk compares, dropped when nothing else reads it
        var usedInPair = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        var written = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default); // the neighbor's components a half walk writes
        IParameterSymbol? self = null;
        void Neighbor(ITypeSymbol type)
        {
            if (!neighbors.Contains(type, SymbolEqualityComparer.Default)) neighbors.Add(type);
        }

        // Top-level statements of the pair: the self skip (before the test) and the locals bound to a read (anywhere:
        // a local declared after the test reads the neighbor too).
        int at = -1;
        for (int i = 0; i < pair.Count; i++)
        {
            var statement = pair[i];
            if (at < 0 && SelfSkip(statement, candidate, model, ct) is { } entityParameter)
            {
                if (self != null) return null;
                self = entityParameter;
                removed.Add(statement);
                continue;
            }

            // if (o.Entity.Id.Index <= self.Id.Index) continue;: each unordered pair once, the pair method writing both sides.
            if (at < 0 && !half && self == null && HalfSkip(statement, candidate, model, ct) is { } compared)
            {
                half = true;
                halfSelf = compared;
                removed.Add(statement);
                continue;
            }

            if (statement is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } declared
                && declared.Declaration.Variables[0] is { Initializer.Value: { } value } boundVariable
                && candidate.Read(Unref(value)) is { } boundRead
                && model.GetDeclaredSymbol(boundVariable, ct) is ILocalSymbol bound && !Written(bound, pair, model, ct))
            {
                var type = boundRead.Type;
                Neighbor(type);
                neighborOfLocal[bound] = type;
                if (!names.ContainsKey(type)) names[type] = bound.Name;
                removed.Add(statement);
                continue;
            }

            if (at < 0 && statement is not LocalDeclarationStatementSyntax) at = i;
        }

        // The cutoff test: the first statement left, skipping the candidate past the cutoff.
        if (at < 0 || pair[at] is not IfStatementSyntax { Else: null } test || !Continues(test.Statement)
            || model.GetOperation(test.Condition, ct) is not { } condition || model.GetOperation(syntax.Body!, ct) is not { } bodyOperation
            || !PairForm.HandCutoff(method, bodyOperation, condition))
            return null;

        var taken = new HashSet<string>(method.Parameters.Select(p => p.Name));
        foreach (var statement in pair.Except(removed))
            foreach (var declared in statement.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()) taken.Add(declared.Identifier.ValueText);
        string otherName = Free("other", taken); // the neighbor's Entity (Candidate.Use writes Other for it)
        taken.Add(otherName);

        // Reads inline at the candidate (positions.Read(id).X, o.Position.X): the [Neighbor] parameter.
        var reads = new List<(ExpressionSyntax Read, ITypeSymbol Type, string Name)>();
        foreach (var node in pair.SelectMany(s => s.DescendantNodesAndSelf()).OfType<ExpressionSyntax>())
            if (!removed.Any(r => r.Span.Contains(node.Span)) && !reads.Any(r => r.Read.Span.Contains(node.Span))
                && candidate.Read(node) is { } read)
            {
                if (WrittenThrough(node))
                {
                    // A write of the neighbor: only both sides of a half walk ([Neighbor] ref of the row's ref component).
                    if (!half) return null;
                    written.Add(read.Type);
                }
                reads.Add((node, read.Type, read.Name));
                Neighbor(read.Type);
            }
        foreach (var (read, type, name) in reads)
        {
            if (!names.ContainsKey(type))
            {
                names[type] = Free(name, taken);
                taken.Add(names[type]);
            }
            edits.Add((read.Span, names[type]));
        }

        // Every name the pair's statements use.
        foreach (var name in pair.SelectMany(s => s.DescendantNodesAndSelf()).OfType<IdentifierNameSyntax>())
        {
            if (removed.Any(r => r.Span.Contains(name.Span)) || reads.Any(r => r.Read.Span.Contains(name.Span))) continue;
            var symbol = model.GetSymbolInfo(name, ct).Symbol;
            if (symbol is ILocalSymbol local)
            {
                if (SymbolEqualityComparer.Default.Equals(local, candidate.Variable))
                {
                    if (candidate.Use(name) is not { } use) return null;
                    entity = true;
                    edits.Add((use.Span, use.Text.Replace(Other, otherName)));
                }
                else if (neighborOfLocal.TryGetValue(local, out var neighbor))
                {
                    if (local.Name != names[neighbor]) edits.Add((name.Span, names[neighbor]));
                }
                else if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is { } declaration && !InPair(declaration))
                    return null; // reads a value of the search (a cell, an index)
            }
            else if (symbol is IParameterSymbol parameter)
            {
                // The world the pass reads through: a pair method takes none (EECS105).
                if (Symbols.Classify(parameter, outside) == Symbols.ParamKind.World) return null;
                usedInPair.Add(parameter);
            }
        }
        if (neighbors.Count == 0) return null;
        foreach (var type in candidate.Holds) Neighbor(type); // the query's components: every neighbor holds them
        foreach (var type in neighbors)
            if (!names.ContainsKey(type))
            {
                names[type] = Free(Camel(type.Name), taken);
                taken.Add(names[type]);
            }
        if (names.Values.Distinct().Count() != names.Count) return null; // two locals of one name in the pair's blocks

        // continue of the candidates' loop -> return; break, return and goto leave the search: no rewrite.
        foreach (var node in pair.SelectMany(s => s.DescendantNodesAndSelf()))
        {
            if (removed.Any(r => r.Span.Contains(node.Span))) continue;
            if (node is ReturnStatementSyntax or GotoStatementSyntax or YieldStatementSyntax && !InFunction(node, pair)) return null;
            if (node is BreakStatementSyntax && ReferenceEquals(Target(node), loop)) return null;
            if (node is BreakStatementSyntax && Target(node) == null) return null;
            if (node is ContinueStatementSyntax && ReferenceEquals(Target(node), loop) && !test.Statement.Span.Contains(node.Span))
                edits.Add((node.Span, "return;"));
        }

        // The test: the self skip joins it, its continue returns.
        if (self != null && test.Condition is ConditionalExpressionSyntax or AssignmentExpressionSyntax) return null;
        if (self != null) edits.Add((new TextSpan(test.Condition.Span.End, 0), $" || {otherName}.Id == {self.Name}.Id"));
        edits.Add((test.Statement.Span, "return;"));
        if (self != null) usedInPair.Add(self);

        // The row writes no component it reads of the neighbor (EECS105); a half walk writes both sides of each of its
        // row's ref components and of no other, and its neighbors hold only components of its rows (PairForm.Problem).
        var rowTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var p in method.Parameters)
        {
            if (Symbols.Classify(p, outside) != Symbols.ParamKind.Component) continue;
            rowTypes.Add(p.Type);
            bool both = written.Contains(p.Type);
            if (p.RefKind == RefKind.Ref ? half != both || !half && neighbors.Any(t => SymbolEqualityComparer.Default.Equals(t, p.Type)) : both)
                return null;
        }
        if (half && (written.Count == 0 || neighbors.Any(t => !rowTypes.Contains(t)))) return null;

        // The parameter list: the method's, without what only the search reads (the world, lookups, singletons),
        // then the [Neighbor] components and the neighbor's Entity.
        var parameters = new List<string>();
        foreach (var p in syntax.ParameterList.Parameters)
        {
            if (model.GetDeclaredSymbol(p, ct) is not { } symbol) return null;
            var kind = Symbols.Classify(symbol, outside);
            if (kind is Symbols.ParamKind.World or Symbols.ParamKind.Lookup or Symbols.ParamKind.Singleton && !usedInPair.Contains(symbol)) continue;
            if (SymbolEqualityComparer.Default.Equals(symbol, halfSelf) && !usedInPair.Contains(symbol)) continue;
            parameters.Add(p.ToString());
        }

        string entityType = method.Parameters.FirstOrDefault(p => Symbols.Named(p.Type, Symbols.Entity)) is { } handle
            ? syntax.ParameterList.Parameters[method.Parameters.IndexOf(handle)].Type!.ToString()
            : "Entity";
        foreach (var type in neighbors)
            parameters.Add($"[Neighbor] {(written.Contains(type) ? "ref" : "in")} {type.ToMinimalDisplayString(model, syntax.SpanStart)} {names[type]}");
        if (entity || self != null) parameters.Add($"[Neighbor] {entityType} {otherName}");

        return new Result(string.Join(", ", parameters), Text(syntax.SyntaxTree, pair, removed, edits, ct));
    }

    /// <summary>A loop over candidate ids read through Lookup&lt;T&gt; parameters (<c>positions.Read(id)</c>).</summary>
    private static Candidate IdSearch(ILocalSymbol id, SemanticModel model, System.Threading.CancellationToken ct) => new()
    {
        Variable = id,
        Read = e => NeighborRead(e, id, model, ct) is { } lookup ? (Symbols.LookupElement(lookup.Type)!, Singular(lookup.Name)) : null,
        Use = name => (name.Span, Other + ".Id"),
        IsId = e => model.GetSymbolInfo(e, ct).Symbol is ILocalSymbol l && SymbolEqualityComparer.Default.Equals(l, id),
    };

    /// <summary>
    /// A <c>foreach</c> over <c>world.Read&lt;...&gt;()</c> or <c>world.Query&lt;...&gt;()</c> of a World parameter
    /// without a filter: its item's components (<c>o.Component1</c>, the generated <c>o.Position</c>) are the neighbor's,
    /// <c>o.Entity</c> its Entity; or null.
    /// </summary>
    private static Candidate? QueryWalk(StatementSyntax loop, IMethodSymbol method, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (loop is not ForEachStatementSyntax each
            || each.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0, Expression: MemberAccessExpressionSyntax access } call
            || model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol { Name: "Read" or "Query", TypeArguments.Length: > 0 } walk
            || !Symbols.Named(walk.ContainingType, Symbols.World)
            || model.GetSymbolInfo(access.Expression, ct).Symbol is not IParameterSymbol world
            || Symbols.Classify(world, Symbols.IsRunner(method)) != Symbols.ParamKind.World
            || model.GetDeclaredSymbol(each, ct) is not { } item)
            return null;
        var holds = walk.TypeArguments.ToArray();
        bool IsItem(ExpressionSyntax e) => model.GetSymbolInfo(e, ct).Symbol is ILocalSymbol l && SymbolEqualityComparer.Default.Equals(l, item);
        return new Candidate
        {
            Variable = item,
            Holds = holds,
            Read = e => e is MemberAccessExpressionSyntax m && IsItem(m.Expression) && m.Name.Identifier.ValueText != "Entity"
                        && model.GetSymbolInfo(m, ct).Symbol is IPropertySymbol property
                        && holds.FirstOrDefault(h => SymbolEqualityComparer.Default.Equals(h, property.Type)) is { } type
                ? (type, Camel(type.Name))
                : null,
            Use = name => name.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Entity" } entity && entity.Expression == name
                ? (entity.Span, Other)
                : null,
            IsId = e => e is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Id", Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Entity" } entity }
                        && IsItem(entity.Expression),
        };
    }

    /// <summary>The pair's statements with the edits applied, the removed statements' lines dropped, dedented.</summary>
    private static string Text(SyntaxTree tree, List<StatementSyntax> pair, List<StatementSyntax> removed, List<(TextSpan Span, string Text)> edits,
        System.Threading.CancellationToken ct)
    {
        var source = tree.GetText(ct);
        int start = pair[0].FullSpan.Start, end = pair[pair.Count - 1].Span.End;
        var all = edits.Concat(removed.Select(r => (r.FullSpan, ""))).Where(e => e.Item1.Start >= start && e.Item1.End <= end || e.Item1.End > end && e.Item1.Start < end)
            .OrderByDescending(e => e.Item1.Start).ToList();
        var text = new StringBuilder(source.ToString(TextSpan.FromBounds(start, end)));
        foreach (var (span, replacement) in all)
        {
            int from = span.Start - start, to = Math.Min(span.End, end) - start;
            text.Remove(from, to - from).Insert(from, replacement);
        }

        var lines = text.ToString().Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        int indent = lines.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart()));
    }

    private static bool IsLoop(StatementSyntax s) => s is ForStatementSyntax or ForEachStatementSyntax;

    private static StatementSyntax LoopBody(StatementSyntax loop) => loop switch
    {
        ForStatementSyntax f => f.Statement,
        ForEachStatementSyntax f => f.Statement,
        _ => loop,
    };

    private static List<StatementSyntax> Statements(StatementSyntax s) => s is BlockSyntax block ? block.Statements.ToList() : [s];

    /// <summary>The loop the body of <paramref name="loop"/> holds after declarations only, or null.</summary>
    private static StatementSyntax? Inner(StatementSyntax loop)
    {
        var body = Statements(LoopBody(loop));
        if (body.Count == 0 || !IsLoop(body[body.Count - 1])) return null;
        for (int i = 0; i < body.Count - 1; i++)
            if (body[i] is not LocalDeclarationStatementSyntax) return null;
        return body[body.Count - 1];
    }

    private static bool IsEntityId(ITypeSymbol type) => Symbols.Named(type, Symbols.EntityId);

    private static ExpressionSyntax Unref(ExpressionSyntax e) => e is RefExpressionSyntax r ? r.Expression : e;

    /// <summary>
    /// The lookup parameter <paramref name="e"/> reads the candidate's component through (<c>lookup.Read(id)</c>), or
    /// null. The indexer is no such read: it gives a ReadOnlyMaybe&lt;T&gt;, whose absent case a [Neighbor] has not.
    /// </summary>
    private static IParameterSymbol? NeighborRead(ExpressionSyntax e, ILocalSymbol id, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (e is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Read" } access } call) return null;
        var list = call.ArgumentList.Arguments;
        if (list.Count != 1 || list[0].RefKindKeyword.RawKind != 0
            || model.GetSymbolInfo(list[0].Expression, ct).Symbol is not ILocalSymbol argument || !SymbolEqualityComparer.Default.Equals(argument, id)
            || model.GetSymbolInfo(access.Expression, ct).Symbol is not IParameterSymbol lookup || Symbols.LookupElement(lookup.Type) == null)
            return null;
        return lookup;
    }

    /// <summary>
    /// <c>if (id == self.Id) continue;</c> (or <c>self.Id == id</c>, <c>id.Equals(self.Id)</c>; the candidate's id as
    /// <paramref name="candidate"/> names it): the row's Entity parameter, or null.
    /// </summary>
    private static IParameterSymbol? SelfSkip(StatementSyntax statement, Candidate candidate, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (statement is not IfStatementSyntax { Else: null } skip || !Continues(skip.Statement)) return null;
        (ExpressionSyntax? left, ExpressionSyntax? right) = skip.Condition switch
        {
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } equal => (equal.Left, equal.Right),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Equals" } access, ArgumentList.Arguments.Count: 1 } call =>
                (access.Expression, call.ArgumentList.Arguments[0].Expression),
            _ => (null, null),
        };
        if (left == null || right == null) return null;
        if (candidate.IsId(right)) (left, right) = (right, left);
        if (!candidate.IsId(left) || right is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Id" } selfId
            || model.GetSymbolInfo(selfId.Expression, ct).Symbol is not IParameterSymbol entity || !Symbols.Named(entity.Type, Symbols.Entity))
            return null;
        return entity;
    }

    /// <summary>
    /// <c>if (o.Entity.Id.Index &lt;= self.Id.Index) continue;</c> (or <c>self.Id.Index &gt;= ...</c>; the candidate's id
    /// as <paramref name="candidate"/> names it): the walk keeps only the candidates after the row, each unordered pair
    /// once, which a pair method writing both sides does: the row's Entity parameter, or null.
    /// </summary>
    private static IParameterSymbol? HalfSkip(StatementSyntax statement, Candidate candidate, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (statement is not IfStatementSyntax { Else: null, Condition: BinaryExpressionSyntax compare } skip || !Continues(skip.Statement)) return null;
        static ExpressionSyntax? Index(ExpressionSyntax e) => e is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Index" } m ? m.Expression : null;
        (var them, var us) = compare.Kind() switch
        {
            SyntaxKind.LessThanOrEqualExpression => (Index(compare.Left), Index(compare.Right)),
            SyntaxKind.GreaterThanOrEqualExpression => (Index(compare.Right), Index(compare.Left)),
            _ => (null, null),
        };
        return them != null && us is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Id" } selfId && candidate.IsId(them)
            && model.GetSymbolInfo(selfId.Expression, ct).Symbol is IParameterSymbol entity && Symbols.Named(entity.Type, Symbols.Entity)
            ? entity
            : null;
    }

    private static bool Continues(StatementSyntax s) => s is ContinueStatementSyntax || s is BlockSyntax { Statements.Count: 1 } block && block.Statements[0] is ContinueStatementSyntax;

    /// <summary>Whether a local bound to a neighbor's read is written (assigned, a field of it assigned, passed by ref).</summary>
    private static bool Written(ILocalSymbol local, List<StatementSyntax> pair, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var name in pair.SelectMany(s => s.DescendantNodesAndSelf()).OfType<IdentifierNameSyntax>())
            if (name.Identifier.ValueText == local.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, ct).Symbol, local)
                && WrittenThrough(name))
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="e"/> (or a field of it) is assigned, incremented or passed by ref.</summary>
    private static bool WrittenThrough(ExpressionSyntax e)
    {
        var outer = e;
        while (outer.Parent is MemberAccessExpressionSyntax m && m.Expression == outer) outer = m;
        return outer.Parent is AssignmentExpressionSyntax a && a.Left == outer || outer.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
            || outer.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or (int)SyntaxKind.OutKeyword }
            || outer.Parent is RefExpressionSyntax;
    }

    /// <summary>The loop a break or continue leaves (a switch for a break inside one), or null outside any.</summary>
    private static SyntaxNode? Target(SyntaxNode node)
    {
        for (var at = node.Parent; at != null; at = at.Parent)
        {
            if (at is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax) return at;
            if (at is SwitchSectionSyntax && node is BreakStatementSyntax) return at;
            if (at is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) return null;
        }
        return null;
    }

    /// <summary>Whether <paramref name="node"/> sits in a local function or lambda inside the pair (its own return).</summary>
    private static bool InFunction(SyntaxNode node, List<StatementSyntax> pair)
    {
        for (var at = node.Parent; at != null && !pair.Contains(at); at = at.Parent)
            if (at is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) return true;
        return false;
    }

    /// <summary>A name for a neighbor's component read inline: the lookup's name made singular ("positions" -> "position").</summary>
    private static string Singular(string name) => name.Length > 1 && name.EndsWith("s") ? name.Substring(0, name.Length - 1) : name + "Of";

    /// <summary>A name for a neighbor's component from its type ("Position" -> "position").</summary>
    private static string Camel(string type) => type.Length == 0 ? "neighbor" : char.ToLowerInvariant(type[0]) + type.Substring(1);

    private static string Free(string name, HashSet<string> taken)
    {
        string candidate = name;
        for (int i = 2; taken.Contains(candidate); i++) candidate = name + i;
        return candidate;
    }
}
