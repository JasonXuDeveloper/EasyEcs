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
/// analyzer and its code fix: the new parameter list and the new body as text (EECS107's fix: a loop over candidate
/// ids read through Lookup&lt;T&gt;). The rewrite is offered only when it is mechanical: the search is the method's
/// whole body, the candidates' loop does nothing but run the pair's statements, the pair's statements read nothing of
/// the search (no cell, no index), and their first test after the local declarations skips the candidates past a
/// cutoff the generator reads. The pair method then visits every entity holding the neighbor's components within that
/// cutoff (the search's candidates were a superset of them, in another order: a sum may differ in its last bits).
/// </summary>
internal static class PairRewrite
{
    /// <summary>The pair method's parameter list (without parentheses) and its statements, dedented, one line per line.</summary>
    public sealed record Result(string Parameters, string Body);

    /// <summary>
    /// The pair method a [ForEach] method searching its neighbors through Lookup&lt;T&gt; reads becomes, or null when the
    /// rewrite is not mechanical. The shape: local declarations of the search, then nested <c>for</c> / <c>foreach</c>
    /// loops (each body only declarations and the next loop) down to the loop over candidates, whose iteration variable or
    /// first local is the candidate's <c>EntityId</c>; the rest of that body is the pair: an optional
    /// <c>if (id == self.Id) continue;</c>, locals bound to <c>lookup.Read(id)</c> (also after the test), local
    /// declarations, the cutoff test <c>if (d2 &gt;= r2) continue;</c>, then any statements.
    /// </summary>
    public static Result? OfLookupSearch(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
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
        var loop = statements[statements.Count - 1];
        if (!IsLoop(loop)) return null;
        while (Inner(loop) is { } next) loop = next;
        var candidates = Statements(LoopBody(loop));
        ILocalSymbol? id;
        int first = 0;
        if (loop is ForEachStatementSyntax each && model.GetDeclaredSymbol(each, ct) is { } variable && IsEntityId(variable.Type)) id = variable;
        else if (candidates.Count > 0 && candidates[0] is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } idDeclaration
                 && model.GetDeclaredSymbol(idDeclaration.Declaration.Variables[0], ct) is ILocalSymbol local && IsEntityId(local.Type))
        {
            id = local;
            first = 1;
        }
        else return null;

        var pair = candidates.Skip(first).ToList();
        if (pair.Count == 0) return null;
        var spans = pair.Select(s => s.Span).ToList();
        bool InPair(SyntaxNode node) => spans.Any(s => s.Contains(node.Span));

        // The parameters: which lookups the pair reads at the candidate, which parameters only the search reads.
        var lookups = new List<IParameterSymbol>();          // in order of first read
        var neighborOfLocal = new Dictionary<ILocalSymbol, IParameterSymbol>(SymbolEqualityComparer.Default);
        var removed = new List<StatementSyntax>();
        var edits = new List<(TextSpan Span, string Text)>();
        bool entity = false;
        var usedInPair = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        IParameterSymbol? self = null;

        // Top-level statements of the pair: the self skip (before the test) and the locals bound to a lookup's read
        // (anywhere: a local declared after the test reads the neighbor too).
        int at = -1;
        for (int i = 0; i < pair.Count; i++)
        {
            var statement = pair[i];
            if (at < 0 && SelfSkip(statement, id, model, ct) is { } entityParameter)
            {
                if (self != null) return null;
                self = entityParameter;
                removed.Add(statement);
                continue;
            }

            if (statement is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } declared
                && declared.Declaration.Variables[0] is { Initializer.Value: { } value } boundVariable
                && NeighborRead(Unref(value), id, model, ct) is { } read
                && model.GetDeclaredSymbol(boundVariable, ct) is ILocalSymbol bound && !Written(bound, pair, model, ct))
            {
                if (!lookups.Contains(read, SymbolEqualityComparer.Default)) lookups.Add(read);
                neighborOfLocal[bound] = read;
                removed.Add(statement);
                continue;
            }

            if (at < 0 && statement is not LocalDeclarationStatementSyntax) at = i;
        }

        // The cutoff test: the first statement left, skipping the candidate past the cutoff.
        if (at < 0 || pair[at] is not IfStatementSyntax { Else: null } test || !Continues(test.Statement)
            || model.GetOperation(test.Condition, ct) is not { } condition || model.GetOperation(syntax.Body, ct) is not { } bodyOperation
            || !PairForm.HandCutoff(method, bodyOperation, condition))
            return null;

        // Names: the [Neighbor] parameters after their first local (else the lookup's), the neighbor's Entity "other".
        var names = new Dictionary<IParameterSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var lookup in lookups)
            names[lookup] = neighborOfLocal.First(n => SymbolEqualityComparer.Default.Equals(n.Value, lookup)).Key.Name;
        var taken = new HashSet<string>(method.Parameters.Select(p => p.Name));
        foreach (var statement in pair.Except(removed))
            foreach (var declared in statement.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()) taken.Add(declared.Identifier.ValueText);

        // Every name the pair's statements use.
        foreach (var name in pair.SelectMany(s => s.DescendantNodesAndSelf()).OfType<IdentifierNameSyntax>())
        {
            if (removed.Any(r => r.Span.Contains(name.Span))) continue;
            var symbol = model.GetSymbolInfo(name, ct).Symbol;
            if (symbol is ILocalSymbol local)
            {
                if (SymbolEqualityComparer.Default.Equals(local, id))
                {
                    if (name.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call }
                        && NeighborRead(call, id, model, ct) is { } inline)
                    {
                        if (!lookups.Contains(inline, SymbolEqualityComparer.Default)) lookups.Add(inline);
                        continue; // the read itself is replaced below
                    }
                    entity = true;
                    edits.Add((name.Span, "other.Id"));
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
                if (Symbols.Classify(parameter, Symbols.IsRunner(method)) == Symbols.ParamKind.Lookup)
                {
                    // Only as the lookup of a read at the candidate.
                    if (ReadAround(name) is not { } around
                        || NeighborRead(around, id, model, ct) == null)
                        return null;
                    continue;
                }
                usedInPair.Add(parameter);
            }
        }

        // Reads inline at the candidate (positions.Read(id).X): the [Neighbor] parameter.
        foreach (var read in pair.SelectMany(s => s.DescendantNodesAndSelf()).OfType<InvocationExpressionSyntax>())
        {
            if (removed.Any(r => r.Span.Contains(read.Span)) || NeighborRead(read, id, model, ct) is not { } lookup) continue;
            if (!names.ContainsKey(lookup)) names[lookup] = Free(Singular(lookup.Name), taken);
            edits.Add((read.Span, names[lookup]));
        }
        if (lookups.Count == 0) return null;
        foreach (var name in names.Values) if (method.Parameters.Any(p => p.Name == name && !lookups.Any(l => l.Name == name))) return null;

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
        string otherName = Free("other", taken);
        if (entity || self != null) taken.Add(otherName);
        if (otherName != "other") edits = edits.Select(e => e.Text == "other.Id" ? (e.Span, otherName + ".Id") : e).ToList();
        if (self != null)
            edits.Add((new TextSpan(test.Condition.Span.End, 0),
                $" || {otherName}.Id == {self.Name}.Id"));
        if (self != null && test.Condition is ConditionalExpressionSyntax or AssignmentExpressionSyntax) return null;
        edits.Add((test.Statement.Span, "return;"));
        if (self != null) usedInPair.Add(self);

        // The row writes no component it reads of the neighbor (EECS105).
        var neighborTypes = lookups.Select(l => Symbols.LookupElement(l.Type)).ToList();
        foreach (var p in method.Parameters)
            if (p.RefKind == RefKind.Ref && Symbols.Classify(p, Symbols.IsRunner(method)) == Symbols.ParamKind.Component
                && neighborTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, p.Type)))
                return null;

        // The parameter list: the method's, without the lookups the pair reads and the singletons only the search reads,
        // then the [Neighbor] components and the neighbor's Entity.
        var parameters = new List<string>();
        foreach (var p in syntax.ParameterList.Parameters)
        {
            if (model.GetDeclaredSymbol(p, ct) is not { } symbol) return null;
            var kind = Symbols.Classify(symbol, Symbols.IsRunner(method));
            if (lookups.Contains(symbol, SymbolEqualityComparer.Default)) continue;
            if (kind == Symbols.ParamKind.Lookup) return null; // a lookup the search reads (EECS087 is its own rule)
            if (kind == Symbols.ParamKind.Singleton && !usedInPair.Contains(symbol)) continue; // the grid
            parameters.Add(p.ToString());
        }

        string entityType = method.Parameters.FirstOrDefault(p => Symbols.Named(p.Type, Symbols.Entity)) is { } handle
            ? syntax.ParameterList.Parameters[method.Parameters.IndexOf(handle)].Type!.ToString()
            : "Entity";
        foreach (var lookup in lookups)
        {
            var type = syntax.ParameterList.Parameters[method.Parameters.IndexOf(lookup)].Type;
            if (type?.DescendantNodesAndSelf().OfType<GenericNameSyntax>().LastOrDefault(g => g.Identifier.ValueText == "Lookup") is not { TypeArgumentList.Arguments.Count: 1 } generic)
                return null;
            parameters.Add($"[Neighbor] in {generic.TypeArgumentList.Arguments[0]} {names[lookup]}");
        }
        if (entity || self != null) parameters.Add($"[Neighbor] {entityType} {otherName}");

        return new Result(string.Join(", ", parameters), Text(syntax.SyntaxTree, pair, removed, edits, ct));
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
            int from = span.Start - start, to = System.Math.Min(span.End, end) - start;
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
        var target = access.Expression;
        var list = call.ArgumentList.Arguments;
        if (list.Count != 1 || list[0].RefKindKeyword.RawKind != 0
            || model.GetSymbolInfo(list[0].Expression, ct).Symbol is not ILocalSymbol argument || !SymbolEqualityComparer.Default.Equals(argument, id)
            || model.GetSymbolInfo(target, ct).Symbol is not IParameterSymbol lookup || Symbols.LookupElement(lookup.Type) == null)
            return null;
        return lookup;
    }

    /// <summary>The read <paramref name="lookup"/>'s name is the receiver of (<c>lookup.Read(...)</c>), or null.</summary>
    private static ExpressionSyntax? ReadAround(IdentifierNameSyntax lookup) =>
        lookup.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access && access.Expression == lookup ? call : null;

    /// <summary><c>if (id == self.Id) continue;</c> (or <c>self.Id == id</c>, <c>id.Equals(self.Id)</c>): the row's Entity parameter, or null.</summary>
    private static IParameterSymbol? SelfSkip(StatementSyntax statement, ILocalSymbol id, SemanticModel model, System.Threading.CancellationToken ct)
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
        if (IsId(right)) (left, right) = (right, left);
        if (!IsId(left) || right is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Id" } selfId
            || model.GetSymbolInfo(selfId.Expression, ct).Symbol is not IParameterSymbol entity || !Symbols.Named(entity.Type, Symbols.Entity))
            return null;
        return entity;

        bool IsId(ExpressionSyntax e) => model.GetSymbolInfo(e, ct).Symbol is ILocalSymbol l && SymbolEqualityComparer.Default.Equals(l, id);
    }

    private static bool Continues(StatementSyntax s) => s is ContinueStatementSyntax || s is BlockSyntax { Statements.Count: 1 } block && block.Statements[0] is ContinueStatementSyntax;

    /// <summary>Whether a local bound to a neighbor's read is written (assigned, a field of it assigned, passed by ref).</summary>
    private static bool Written(ILocalSymbol local, List<StatementSyntax> pair, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var name in pair.SelectMany(s => s.DescendantNodesAndSelf()).OfType<IdentifierNameSyntax>())
        {
            if (name.Identifier.ValueText != local.Name || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, ct).Symbol, local)) continue;
            ExpressionSyntax outer = name;
            while (outer.Parent is MemberAccessExpressionSyntax m && m.Expression == outer) outer = m;
            if (outer.Parent is AssignmentExpressionSyntax a && a.Left == outer || outer.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                || outer.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or (int)SyntaxKind.OutKeyword }
                || outer.Parent is RefExpressionSyntax)
                return true;
        }
        return false;
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

    private static string Free(string name, HashSet<string> taken)
    {
        string candidate = name;
        for (int i = 2; taken.Contains(candidate); i++) candidate = name + i;
        return candidate;
    }
}
