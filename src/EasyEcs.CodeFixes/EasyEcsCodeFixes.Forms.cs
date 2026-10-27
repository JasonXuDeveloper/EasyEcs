using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace EasyEcs.CodeFixes;

/// <summary>
/// The fixes that turn hand-written forms into the declarations the generator reads: a [ForEach] method's final destroy
/// into [Consume] (EECS084), and
/// a one-entity job with its calls into a [ForEntity] method and its entry point's calls (EECS080), and a batch method's
/// hand-written vector loop into the method for one row the generator vectorizes (EECS103), and a total accumulated
/// through a 'ref' singleton into a Reduce&lt;T&gt; of an [Accumulate] singleton (EECS086). The analyzer decided each
/// (its properties); these only rewrite.
/// </summary>
public sealed partial class EasyEcsCodeFixes
{
    /// <summary>
    /// EECS109: a system's OnUpdate that walks Emitted&lt;T&gt; and clears it becomes <c>[ForEachRecord] static void
    /// Handle(in T record, ref S s, ...)</c> holding the loop's body (the singleton locals as 'ref' parameters, the World
    /// when the body uses it: the analyzer's property), its lines moved out by one level.
    /// </summary>
    private static MethodDeclarationSyntax? RecordMethod(SyntaxNode root, MethodDeclarationSyntax hook, ForEachStatementSyntax loop,
        System.Collections.Immutable.ImmutableDictionary<string, string?> props)
    {
        if (!props.TryGetValue("RecordParameters", out var others) || others == null || !props.TryGetValue("Record", out var record) || record == null) return null;
        var list = SyntaxFactory.ParseParameterList($"(in {record} {loop.Identifier.ValueText}{(others.Length > 0 ? ", " + others : "")})");
        if (list.ContainsDiagnostics) return null;
        var eol = EndOfLine(root);
        var leading = hook.GetLeadingTrivia();
        string indent = leading.Count > 0 && leading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? leading.Last().ToFullString() : "";
        if (hook.Modifiers.Count == 0 || hook.AttributeLists.Count > 0) return null;
        // [ForEachRecord] on a line of its own, where the hook's line began (its leading trivia), the modifiers indented below it.
        var attribute = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("ForEachRecord"))))
            .WithLeadingTrivia(leading).WithTrailingTrivia(eol);
        var method = hook.WithoutLeadingTrivia();
        method = method.WithModifiers(method.Modifiers.Replace(method.Modifiers[0], method.Modifiers[0].WithLeadingTrivia(SyntaxFactory.Whitespace(indent))))
            .WithAttributeLists(SyntaxFactory.SingletonList(attribute))
            .WithIdentifier(SyntaxFactory.Identifier("Handle").WithTriviaFrom(hook.Identifier))
            .WithParameterList(list.WithTriviaFrom(hook.ParameterList));
        if (loop.Statement is ExpressionStatementSyntax only)
            return method.WithBody(null).WithParameterList(method.ParameterList.WithTrailingTrivia(SyntaxFactory.Space))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(only.Expression.WithoutTrivia().WithLeadingTrivia(SyntaxFactory.Space)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(hook.GetTrailingTrivia()));
        if (loop.Statement is not BlockSyntax body) return null;

        // The body's lines one level out: the loop's indentation to the method's.
        var loopLeading = loop.GetLeadingTrivia();
        string inner = loopLeading.Count > 0 && loopLeading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? loopLeading.Last().ToFullString() : "";
        int shift = System.Math.Max(0, inner.Length - indent.Length);
        string newline = eol.ToFullString();
        var lines = body.WithoutTrivia().ToFullString().Replace("\r\n", "\n").Split('\n')
            .Select((line, i) => i == 0 ? indent + line.TrimStart() : line.Length >= shift && line.Substring(0, shift).Trim().Length == 0 ? line.Substring(shift) : line.TrimStart());
        var block = (BlockSyntax)SyntaxFactory.ParseStatement(string.Join(newline, lines));
        return method.WithExpressionBody(null).WithSemicolonToken(default)
            .WithParameterList(method.ParameterList.WithTrailingTrivia(eol))
            .WithBody(block.WithTrailingTrivia(hook.Body?.GetTrailingTrivia() ?? hook.GetTrailingTrivia()));
    }

    /// <summary>
    /// EECS086 for a total: parameter <paramref name="parameter"/> (<c>ref T s</c>) becomes <c>ref Reduce&lt;T&gt; s</c> and
    /// each <c>s.F</c> of the method <c>s.Value.F</c>; with the property <c>Declare</c>, T's declaration (the diagnostic's
    /// additional location) gets <c>[Accumulate]</c>, so the pass's result is combined into the total.
    /// </summary>
    private static async Task<Solution> Accumulated(Document document, Diagnostic diagnostic, ParameterSyntax parameter, CancellationToken token)
    {
        var solution = document.Project.Solution;
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        if (model == null || root == null || parameter.Type == null || model.GetDeclaredSymbol(parameter, token) is not { } symbol
            || parameter.Parent?.Parent is not MethodDeclarationSyntax method) return solution;

        // The uses s.F become s.Value.F (each use of the parameter is a field access: the analyzer checked).
        var uses = method.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(n => n.Identifier.ValueText == parameter.Identifier.ValueText && n.Parent is MemberAccessExpressionSyntax { Expression: var e } && e == n
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, token).Symbol, symbol)).ToList();
        var reduce = SyntaxFactory.GenericName(SyntaxFactory.Identifier("Reduce"),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(parameter.Type.WithoutTrivia()))).WithTriviaFrom(parameter.Type);
        var updated = method.ReplaceNodes(uses.Cast<SyntaxNode>().Append(parameter.Type), (original, _) => original == parameter.Type
            ? reduce
            : SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, (IdentifierNameSyntax)original, SyntaxFactory.IdentifierName("Value")));

        var declared = diagnostic.Properties.TryGetValue("Declare", out var declare) && declare == "Accumulate"
            ? diagnostic.AdditionalLocations.FirstOrDefault() : null;
        var tree = declared?.SourceTree;
        if (declared == null || tree == null) return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(method, updated));
        if (tree == root.SyntaxTree)
        {
            var type = root.FindNode(declared.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (type == null) return solution;
            return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNodes(new SyntaxNode[] { method, type },
                (original, rewritten) => original == method ? updated : WithAccumulate((TypeDeclarationSyntax)rewritten)));
        }

        solution = solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(method, updated));
        if (solution.GetDocument(tree) is not { } other || await other.GetSyntaxRootAsync(token).ConfigureAwait(false) is not { } otherRoot
            || otherRoot.FindNode(declared.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } declaration)
            return solution;
        return solution.WithDocumentSyntaxRoot(other.Id, otherRoot.ReplaceNode(declaration, WithAccumulate(declaration)));
    }

    /// <summary><paramref name="type"/> with <c>[Accumulate]</c> first in its first attribute list (a list of its own when it has none).</summary>
    private static TypeDeclarationSyntax WithAccumulate(TypeDeclarationSyntax type)
    {
        var attribute = SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("Accumulate"));
        if (type.AttributeLists.Count > 0)
            return type.WithAttributeLists(type.AttributeLists.Replace(type.AttributeLists[0], type.AttributeLists[0].WithAttributes(type.AttributeLists[0].Attributes.Insert(0, attribute))));
        var leading = type.GetLeadingTrivia();
        var indent = leading.Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).LastOrDefault();
        var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute)).WithLeadingTrivia(leading)
            .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
        return type.WithoutLeadingTrivia().WithLeadingTrivia(indent).WithAttributeLists(SyntaxFactory.SingletonList(list));
    }

    /// <summary>
    /// EECS084: the method without its final destroy, with <c>Consume</c> after <c>ForEach</c> in its attribute list,
    /// and without its Entity parameter <paramref name="entity"/> when that was its only use (null: kept).
    /// </summary>
    private static MethodDeclarationSyntax Consumed(MethodDeclarationSyntax method, ExpressionStatementSyntax destroy, string? entity)
    {
        var forEach = method.AttributeLists.SelectMany(l => l.Attributes).First(IsForEach);
        var list = (AttributeListSyntax)forEach.Parent!;
        var consume = SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("Consume"));
        var updated = method.ReplaceNodes(new SyntaxNode[] { list, destroy }, (original, rewritten) => original == list
            ? ((AttributeListSyntax)rewritten).WithAttributes(((AttributeListSyntax)rewritten).Attributes.Insert(list.Attributes.IndexOf(forEach) + 1, consume))
            : rewritten);
        // The destroy goes with its line: it is the body's last statement.
        updated = updated.RemoveNode(updated.Body!.Statements.Last(), SyntaxRemoveOptions.KeepNoTrivia)!;
        if (entity != null && updated.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.ValueText == entity) is { } parameter)
            updated = updated.WithParameterList(updated.ParameterList.WithParameters(updated.ParameterList.Parameters.Remove(parameter)));
        return updated;
    }

    /// <summary>
    /// EECS080: the job struct as a static class with a [ForEntity] method Run (the job's fields its last parameters,
    /// in their order), each creation and Run call as one call of the entry point, and the filter field gone once
    /// nothing else uses it.
    /// </summary>
    private static async Task<Solution> ToForEntity(Document document, Diagnostic diagnostic, CancellationToken token)
    {
        var solution = document.Project.Solution;
        var props = diagnostic.Properties;
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (root == null || model == null || root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<StructDeclarationSyntax>() is not { } job
            || model.GetDeclaredSymbol(job, token) is not { } type) return solution;

        // The call sites: (statement, call) pairs, then the filter field when there is one.
        var locations = diagnostic.AdditionalLocations;
        int calls = locations.Count / 2;
        var edits = new Dictionary<DocumentId, DocumentEditor>();
        async Task<DocumentEditor?> Editor(SyntaxTree? tree)
        {
            if (tree == null || solution.GetDocument(tree) is not { } at) return null;
            if (!edits.TryGetValue(at.Id, out var editor)) edits[at.Id] = editor = await DocumentEditor.CreateAsync(at, token).ConfigureAwait(false);
            return editor;
        }

        var replaced = new List<Location>();
        for (int k = 0; k < calls; k++)
        {
            var statementAt = locations[2 * k];
            var callAt = locations[2 * k + 1];
            if (!props.TryGetValue("Call" + k, out var text) || text == null || await Editor(callAt.SourceTree).ConfigureAwait(false) is not { } editor) return solution;
            var tree = editor.OriginalRoot;
            var statement = tree.FindNode(statementAt.SourceSpan).FirstAncestorOrSelf<LocalDeclarationStatementSyntax>();
            var call = tree.FindNode(callAt.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (statement == null || call == null) return solution;
            editor.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia);
            editor.ReplaceNode(call, SyntaxFactory.ParseExpression(text).WithTriviaFrom(call));
            replaced.Add(callAt);
        }

        // The filter field, when every use of it is one of the calls replaced.
        if (locations.Count % 2 == 1 && await Editor(locations[locations.Count - 1].SourceTree).ConfigureAwait(false) is { } fieldEditor
            && fieldEditor.OriginalRoot.FindNode(locations[locations.Count - 1].SourceSpan) is var declaratorNode
            && declaratorNode.FirstAncestorOrSelf<VariableDeclaratorSyntax>() is { } declarator
            && declarator.Parent?.Parent is FieldDeclarationSyntax { Declaration.Variables.Count: 1 } fieldDeclaration
            && fieldEditor.SemanticModel.GetDeclaredSymbol(declarator, token) is { } field)
        {
            var references = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(field, solution, token).ConfigureAwait(false);
            if (references.SelectMany(r => r.Locations).All(l => replaced.Any(c => c.SourceTree == l.Location.SourceTree && c.SourceSpan.Contains(l.Location.SourceSpan))))
            {
                fieldEditor.RemoveNode(fieldDeclaration, SyntaxRemoveOptions.KeepNoTrivia);
                // The first member gone: the next one does not start its type with the blank line that set it apart.
                if (fieldDeclaration.Parent is TypeDeclarationSyntax owner && owner.Members.IndexOf(fieldDeclaration) == 0 && owner.Members.Count > 1)
                {
                    var next = owner.Members[1];
                    fieldEditor.ReplaceNode(next, (node, _) => node.WithLeadingTrivia(node.GetLeadingTrivia().SkipWhile(t => t.IsKind(SyntaxKind.EndOfLineTrivia))));
                }
            }
        }

        if (await Editor(job.SyntaxTree).ConfigureAwait(false) is not { } jobEditor || Method(job, type, model, props, token) is not { } converted) return solution;
        jobEditor.ReplaceNode(job, converted);
        foreach (var editor in edits.Values)
            solution = solution.WithDocumentSyntaxRoot(editor.OriginalDocument.Id, editor.GetChangedRoot());
        return solution;
    }

    /// <summary>The static class the job becomes: its modifiers but ref / readonly, and one [ForEntity] method Run.</summary>
    private static MemberDeclarationSyntax? Method(StructDeclarationSyntax job, INamedTypeSymbol type, SemanticModel model,
        System.Collections.Immutable.ImmutableDictionary<string, string?> props, CancellationToken token)
    {
        if (job.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "Execute") is not { } execute
            || !props.TryGetValue("Parameters", out var pairs) || !props.TryGetValue("Modifiers", out var modifiers)) return null;
        var names = new Dictionary<string, string>();
        foreach (var pair in (pairs ?? "").Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=');
            names[parts[0]] = parts[1];
        }

        // The body, each field read now a parameter.
        SyntaxNode body = (SyntaxNode?)execute.Body ?? execute.ExpressionBody!;
        var renamed = body.ReplaceNodes(body.DescendantNodes().OfType<ExpressionSyntax>().Where(e => e is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }),
            (original, rewritten) =>
            {
                var name = original as IdentifierNameSyntax ?? ((MemberAccessExpressionSyntax)original).Name as IdentifierNameSyntax;
                if (name == null || original is IdentifierNameSyntax && original.Parent is MemberAccessExpressionSyntax { Name: var right, Expression: ThisExpressionSyntax } && right == original) return rewritten;
                return model.GetSymbolInfo(name, token).Symbol is IFieldSymbol f && SymbolEqualityComparer.Default.Equals(f.ContainingType, type)
                       && names.TryGetValue(f.Name, out var parameter)
                    ? SyntaxFactory.IdentifierName(parameter).WithTriviaFrom(original)
                    : rewritten;
            });

        var parameters = new List<string>();
        var executeParameters = execute.ParameterList.Parameters;
        if (!props.ContainsKey("DropEntity") || props["DropEntity"] == null)
            parameters.Add($"{executeParameters[0].Type} {executeParameters[0].Identifier.ValueText}");
        var kinds = (modifiers ?? "").Split(',');
        var required = new List<string>(); // components the body never names: [With<T>]
        for (int k = 1; k < executeParameters.Count; k++)
            if (kinds[k - 1] == "with") required.Add($"With<{executeParameters[k].Type}>");
            else parameters.Add($"{kinds[k - 1]} {executeParameters[k].Type} {executeParameters[k].Identifier.ValueText}");
        foreach (var field in job.Members.OfType<FieldDeclarationSyntax>())
            foreach (var variable in field.Declaration.Variables)
                parameters.Add($"{field.Declaration.Type} {names[variable.Identifier.ValueText]}");

        props.TryGetValue("Filter", out var filter);
        if (!string.IsNullOrEmpty(filter)) required.Add(filter!);
        filter = string.Join(", ", required);
        string eol = EndOfLine(job.SyntaxTree.GetRoot()).ToFullString();
        var leading = job.GetLeadingTrivia();
        string indent = leading.Count > 0 && leading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? leading.Last().ToFullString() : "";
        // Execute's comments stay, not the blank lines that set it apart from the fields.
        var methodLeading = SyntaxFactory.TriviaList(execute.GetLeadingTrivia().SkipWhile(t => t.IsKind(SyntaxKind.EndOfLineTrivia)
            || t.IsKind(SyntaxKind.WhitespaceTrivia) && t.Token.LeadingTrivia.IndexOf(t) + 1 < t.Token.LeadingTrivia.Count
            && t.Token.LeadingTrivia[t.Token.LeadingTrivia.IndexOf(t) + 1].IsKind(SyntaxKind.EndOfLineTrivia))).ToFullString();
        string methodIndent = execute.GetLeadingTrivia() is { Count: > 0 } t && t.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? t.Last().ToFullString() : indent + "    ";
        var keep = job.Modifiers.Where(m => !m.IsKind(SyntaxKind.RefKeyword) && !m.IsKind(SyntaxKind.ReadOnlyKeyword)).Select(m => m.ValueText);
        var text = new StringBuilder();
        text.Append(string.Join(" ", keep.Concat(["static", "class"]))).Append(' ').Append(job.Identifier.ValueText).Append(eol);
        text.Append(indent).Append('{').Append(eol);
        text.Append(methodLeading).Append("[ForEntity").Append(string.IsNullOrEmpty(filter) ? "" : ", " + filter).Append(']').Append(eol);
        text.Append(methodIndent).Append("static void Run(").Append(string.Join(", ", parameters)).Append(')');
        text.Append(renamed is ArrowExpressionClauseSyntax arrow ? " " + arrow.ToFullString().Trim() + ";" + eol : eol + renamed.ToFullString().TrimEnd() + eol);
        text.Append(indent).Append('}');
        var converted = SyntaxFactory.ParseMemberDeclaration(text.ToString());
        return converted?.WithLeadingTrivia(job.GetLeadingTrivia()).WithTrailingTrivia(job.GetTrailingTrivia());
    }

    /// <summary>
    /// EECS103: the method with the parameters and statements of the method for one row the analyzer wrote (attributes,
    /// modifiers, name and comments kept): one statement as an expression body, several as a block; null when the
    /// properties do not parse.
    /// </summary>
    private static MethodDeclarationSyntax? PerRow(SyntaxNode root, MethodDeclarationSyntax method, System.Collections.Immutable.ImmutableDictionary<string, string?> props)
    {
        if (!props.TryGetValue("PerRowParameters", out var parameters) || parameters == null
            || !props.TryGetValue("PerRowBody", out var body) || body == null) return null;
        var list = SyntaxFactory.ParseParameterList("(" + parameters + ")");
        var statements = body.Split('\n').Select(s => SyntaxFactory.ParseStatement(s)).ToList();
        if (list.ContainsDiagnostics || statements.Any(s => s.ContainsDiagnostics)) return null;
        var updated = method.WithParameterList(list.WithTriviaFrom(method.ParameterList));
        var eol = EndOfLine(root);
        if (statements.Count == 1 && statements[0] is ExpressionStatementSyntax only)
            return updated.WithBody(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(only.Expression.WithLeadingTrivia(SyntaxFactory.Space))
                    .WithLeadingTrivia(SyntaxFactory.Space))
                .WithParameterList(updated.ParameterList.WithTrailingTrivia())
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(method.GetTrailingTrivia()));

        // The block indented as the method's own lines are.
        var leading = method.GetLeadingTrivia();
        string indent = leading.Count > 0 && leading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? leading.Last().ToFullString() : "";
        var text = new StringBuilder();
        text.Append(indent).Append('{').Append(eol.ToFullString());
        foreach (var statement in statements) text.Append(indent).Append("    ").Append(statement.ToFullString().Trim()).Append(eol.ToFullString());
        text.Append(indent).Append('}');
        var block = (BlockSyntax)SyntaxFactory.ParseStatement(text.ToString());
        return updated.WithExpressionBody(null).WithSemicolonToken(default)
            .WithParameterList(updated.ParameterList.WithTrailingTrivia(eol))
            .WithBody(block.WithLeadingTrivia(SyntaxFactory.Whitespace(indent)).WithTrailingTrivia(method.GetTrailingTrivia()));
    }

    /// <summary>
    /// EECS107: the method as a pair method, with the parameters and statements the analyzer wrote (PairRewrite: the
    /// lookups the search read at the candidate as [Neighbor] parameters, the candidate's statements as the body);
    /// attributes, modifiers, name and comments kept; null when the properties do not parse.
    /// </summary>
    private static MethodDeclarationSyntax? PairMethod(SyntaxNode root, MethodDeclarationSyntax method, System.Collections.Immutable.ImmutableDictionary<string, string?> props)
    {
        if (method.Body == null || !props.TryGetValue("PairParameters", out var parameters) || parameters == null
            || !props.TryGetValue("PairBody", out var body) || body == null) return null;
        var list = SyntaxFactory.ParseParameterList("(" + parameters + ")");
        if (list.ContainsDiagnostics) return null;
        var leading = method.GetLeadingTrivia();
        string indent = leading.Count > 0 && leading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? leading.Last().ToFullString() : "";
        string eol = EndOfLine(root).ToFullString();
        var text = new StringBuilder();
        text.Append(indent).Append('{').Append(eol);
        foreach (var line in body.Split('\n')) text.Append(line.Length == 0 ? "" : indent + "    " + line).Append(eol);
        text.Append(indent).Append('}');
        if (SyntaxFactory.ParseStatement(text.ToString()) is not BlockSyntax block || block.ContainsDiagnostics) return null;
        return method.WithParameterList(list.WithTriviaFrom(method.ParameterList))
            .WithBody(block.WithLeadingTrivia(method.Body.GetLeadingTrivia()).WithTrailingTrivia(method.Body.GetTrailingTrivia()));
    }
}
