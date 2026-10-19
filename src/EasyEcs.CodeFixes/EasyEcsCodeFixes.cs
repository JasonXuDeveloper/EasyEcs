using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace EasyEcs.CodeFixes;

/// <summary>
/// Code fixes for the diagnostics of EasyEcsAnalyzer that have one mechanical answer: a parameter's modifier, a
/// missing <c>static</c>, a class that should be a struct, a filter or <c>Parallel = true</c> that cannot apply, a type
/// the generated code cannot name, a parameter that only reads or is not used, and the replacements the performance
/// rules computed (a bulk call, a generated member). Each reads the diagnostic's location, its additional locations
/// and its properties, so the analyzer stays the only place that decides.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EasyEcsCodeFixes)), Shared]
public sealed class EasyEcsCodeFixes : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create("EECS002", "EECS005", "EECS006", "EECS010", "EECS011", "EECS012", "EECS022", "EECS023",
            "EECS025", "EECS026", "EECS029", "EECS030", "EECS031", "EECS032", "EECS033");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;
        var generator = SyntaxGenerator.GetGenerator(context.Document); // modifiers with their trivia in place
        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (diagnostic.Properties.TryGetValue("Title", out var title) && title != null
                && (diagnostic.Properties.ContainsKey("Replace") || diagnostic.Properties.ContainsKey("Insert")))
            {
                context.RegisterCodeFix(CodeAction.Create(title, _ => Task.FromResult(Rewrite(context.Document, root, node, diagnostic)),
                    diagnostic.Id + title), diagnostic);
                continue;
            }

            switch (diagnostic.Id)
            {
                case "EECS025" when node.FirstAncestorOrSelf<ParameterSyntax>() is { } p
                                    && !await HasCallers(context.Document, p, context.CancellationToken).ConfigureAwait(false):
                    if (diagnostic.Properties.TryGetValue("Fix", out var kind) && kind == "ReadOnlySpan")
                        Register(context, diagnostic, "Take it as a ReadOnlySpan", root, p, ReadOnly(p));
                    else
                        Register(context, diagnostic, "Take it by 'in'", root, p, Modify(p, SyntaxKind.InKeyword));
                    break;
                case "EECS026" when diagnostic.Properties.ContainsKey("Fix") && node.FirstAncestorOrSelf<ParameterSyntax>() is { Parent.Parent: MethodDeclarationSyntax method } p
                                    && method.Modifiers.All(m => !m.IsKind(SyntaxKind.PartialKeyword))
                                    && !await HasCallers(context.Document, p, context.CancellationToken).ConfigureAwait(false):
                    diagnostic.Properties.TryGetValue("With", out var with);
                    Register(context, diagnostic, with == null ? "Remove the parameter" : $"Require it with [With<{with}>]", root, method,
                        Unused(method, p, with));
                    break;
                case "EECS029" when node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } instance
                                    && !await HasCallers(context.Document, instance.ParameterList.Parameters.FirstOrDefault(), context.CancellationToken, instance)
                                        .ConfigureAwait(false):
                    Register(context, diagnostic, "Make it static", root, instance,
                        generator.WithModifiers(instance, generator.GetModifiers(instance).WithIsStatic(true)));
                    break;
                case "EECS002" when diagnostic.Properties.ContainsKey("ByValue") && node is ParameterSyntax parameter:
                    Register(context, diagnostic, "Take it by 'in' (read)", root, parameter, Modify(parameter, SyntaxKind.InKeyword));
                    Register(context, diagnostic, "Take it by 'ref' (write)", root, parameter, Modify(parameter, SyntaxKind.RefKeyword));
                    break;
                // 'in' takes what callers pass now; 'ref' only when each caller passes a variable, which then gets 'ref' too
                case "EECS012" when !diagnostic.Properties.ContainsKey("SharedSignature") && node is ParameterSyntax { Modifiers.Count: 0 } p:
                    Register(context, diagnostic, "Take it by 'in' (read)", root, p, Modify(p, SyntaxKind.InKeyword));
                    if (await Callers(context.Document, p, context.CancellationToken).ConfigureAwait(false) is { } callers)
                        context.RegisterCodeFix(CodeAction.Create("Take it by 'ref' (write)",
                            token => ByRef(context.Document.Project.Solution, p, callers, token), diagnostic.Id + "ref"), diagnostic);
                    break;
                case "EECS005" when node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method
                                    && !await UsesInstance(context.Document, method, context.CancellationToken).ConfigureAwait(false):
                    Register(context, diagnostic, "Make it static", root, method,
                        generator.WithModifiers(method, generator.GetModifiers(method).WithIsStatic(true)));
                    break;
                case "EECS006" when node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } type
                                    && await CanBeStruct(context.Document, type, context.CancellationToken).ConfigureAwait(false)
                                    && ToStruct(type) is { } data:
                    Register(context, diagnostic, "Make it a struct", root, type, data);
                    break;
                case "EECS010" when node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method && Parallel(method) is { } argument:
                    Register(context, diagnostic, "Run it on one thread", root, argument.Parent!,
                        ((AttributeArgumentListSyntax)argument.Parent!).Arguments.Count == 1 ? null : ((AttributeArgumentListSyntax)argument.Parent!).RemoveNode(argument, SyntaxRemoveOptions.KeepNoTrivia));
                    break;
                case "EECS011" when diagnostic.AdditionalLocations.FirstOrDefault() is { } where:
                    context.RegisterCodeFix(CodeAction.Create("Take it by 'ref'",
                        token => Writable(context.Document, where, token), diagnostic.Id), diagnostic);
                    break;
                case "EECS022" when diagnostic.AdditionalLocations.FirstOrDefault() is { } where:
                    context.RegisterCodeFix(CodeAction.Create("Make it internal",
                        token => Internal(context.Document, where, token), diagnostic.Id), diagnostic);
                    break;
                case "EECS023" when node.FirstAncestorOrSelf<AttributeSyntax>() is { Parent: AttributeListSyntax list } attribute:
                    Register(context, diagnostic, "Remove the filter", root, list.Attributes.Count == 1 ? list : attribute, null);
                    break;
            }
        }
    }

    /// <summary>
    /// The analyzer's replacement: <c>Replace</c> takes the place of the node at the diagnostic's location (and with
    /// <c>Remove</c> the statements at its additional locations go), <c>Insert</c> goes before its statement.
    /// </summary>
    private static Document Rewrite(Document document, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic)
    {
        var props = diagnostic.Properties;
        if (props.TryGetValue("Insert", out var insert) && insert != null && node.FirstAncestorOrSelf<StatementSyntax>() is { } before)
        {
            var indent = before.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).LastOrDefault();
            // The file's own line ending: a CRLF file stays CRLF, an LF file LF.
            var eol = root.DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            var statement = SyntaxFactory.ParseStatement(insert).WithLeadingTrivia(indent)
                .WithTrailingTrivia(eol.IsKind(SyntaxKind.EndOfLineTrivia) ? eol : SyntaxFactory.LineFeed);
            return document.WithSyntaxRoot(root.InsertNodesBefore(before, [statement]));
        }

        string text = props["Replace"]!;
        SyntaxNode replacement = node is StatementSyntax ? SyntaxFactory.ParseStatement(text) : SyntaxFactory.ParseExpression(text);
        replacement = replacement.WithTriviaFrom(node);
        if (!props.ContainsKey("Remove") || diagnostic.AdditionalLocations.Count == 0)
            return document.WithSyntaxRoot(root.ReplaceNode(node, replacement));
        var others = diagnostic.AdditionalLocations.Select(l => root.FindNode(l.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<StatementSyntax>()!)
            .Where(n => n != null).ToList();
        var tracked = root.TrackNodes(others.Append(node));
        tracked = tracked.ReplaceNode(tracked.GetCurrentNode(node)!, replacement);
        tracked = tracked.RemoveNodes(others.Select(o => tracked.GetCurrentNode(o)!), SyntaxRemoveOptions.KeepNoTrivia)!;
        return document.WithSyntaxRoot(tracked);
    }

    /// <summary>EECS025: <c>Span&lt;T&gt;</c> (however it is spelled) becomes <c>ReadOnlySpan&lt;T&gt;</c>.</summary>
    private static ParameterSyntax ReadOnly(ParameterSyntax parameter)
    {
        var type = parameter.Type!;
        var generic = type as GenericNameSyntax ?? (type as QualifiedNameSyntax)?.Right as GenericNameSyntax;
        if (generic is { Identifier.Text: "Span" })
            return parameter.WithType(type.ReplaceNode(generic, generic.WithIdentifier(SyntaxFactory.Identifier("ReadOnlySpan").WithTriviaFrom(generic.Identifier))));
        return parameter; // an alias: left as it is (the fix then changes nothing)
    }

    /// <summary>EECS026: the parameter goes; a component is required by a [With&lt;T&gt;] next to [ForEach].</summary>
    private static MethodDeclarationSyntax Unused(MethodDeclarationSyntax method, ParameterSyntax parameter, string? with)
    {
        var updated = method.WithParameterList(method.ParameterList.WithParameters(method.ParameterList.Parameters.Remove(parameter)));
        if (with == null) return updated;
        foreach (var list in updated.AttributeLists)
            foreach (var attribute in list.Attributes)
                if (attribute.Name.ToString() is "ForEach" or "ForEachAttribute" or "EasyEcs.ForEach" or "EasyEcs.ForEachAttribute" or "global::EasyEcs.ForEach")
                {
                    var added = SyntaxFactory.Attribute(SyntaxFactory.ParseName($"With<{with}>"));
                    return updated.ReplaceNode(list, list.WithAttributes(list.Attributes.Insert(list.Attributes.IndexOf(attribute) + 1, added)));
                }

        return updated;
    }

    /// <summary>
    /// Whether code calls the method itself (a runner's method called directly): changing its signature would break the
    /// call. The generated code reaches it by name (UnsafeAccessor), not by reference.
    /// </summary>
    private static async Task<bool> HasCallers(Document document, ParameterSyntax? parameter, CancellationToken token, MethodDeclarationSyntax? declaration = null)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        var syntax = declaration ?? parameter?.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (model == null || syntax == null || model.GetDeclaredSymbol(syntax, token) is not IMethodSymbol method) return true;
        foreach (var found in await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(method, document.Project.Solution, token).ConfigureAwait(false))
            if (found.Locations.Any()) return true;
        return false;
    }

    private static void Register(CodeFixContext context, Diagnostic diagnostic, string title, SyntaxNode root, SyntaxNode node,
        SyntaxNode? replacement) =>
        context.RegisterCodeFix(CodeAction.Create(title, _ => Task.FromResult(context.Document.WithSyntaxRoot(replacement == null
                ? root.RemoveNode(node, SyntaxRemoveOptions.KeepNoTrivia)!
                : root.ReplaceNode(node, replacement))), diagnostic.Id + title), diagnostic);

    private static ParameterSyntax Modify(ParameterSyntax parameter, SyntaxKind kind)
    {
        // the parameter's leading trivia (indentation) moves to its new first token instead of being repeated
        var lead = parameter.GetLeadingTrivia();
        parameter = parameter.WithoutLeadingTrivia();
        var modifiers = SyntaxFactory.TokenList(parameter.Modifiers.Where(m => !m.IsKind(SyntaxKind.InKeyword) && !m.IsKind(SyntaxKind.RefKeyword)
                                                                                && !m.IsKind(SyntaxKind.ReadOnlyKeyword)));
        return parameter.WithModifiers(modifiers.Add(SyntaxFactory.Token(kind).WithTrailingTrivia(SyntaxFactory.Space)))
            .WithoutLeadingTrivia().WithLeadingTrivia(lead);
    }

    /// <summary>
    /// EECS011: <c>in T</c> becomes <c>ref T</c>, a <c>ReadOnlySpan&lt;T&gt;</c> (however it is spelled: an alias too) becomes
    /// <c>Span&lt;T&gt;</c>.
    /// </summary>
    private static async Task<Solution> Writable(Document document, Location where, CancellationToken token)
    {
        var solution = document.Project.Solution;
        var target = solution.GetDocument(where.SourceTree) ?? document;
        var root = await target.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var model = await target.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (root?.FindNode(where.SourceSpan).FirstAncestorOrSelf<ParameterSyntax>() is not { Type: { } type } parameter || model == null)
            return solution;
        ParameterSyntax writable;
        if (model.GetTypeInfo(type, token).Type is INamedTypeSymbol { Name: "ReadOnlySpan", TypeArguments.Length: 1 } span
            && span.ContainingNamespace.ToDisplayString() == "System")
            writable = type is GenericNameSyntax { Identifier.Text: "ReadOnlySpan" } or QualifiedNameSyntax { Right: GenericNameSyntax { Identifier.Text: "ReadOnlySpan" } }
                ? parameter.WithType(type.ReplaceNode(Generic(type), Generic(type).WithIdentifier(SyntaxFactory.Identifier("Span").WithTriviaFrom(Generic(type).Identifier))))
                : parameter.WithType(SyntaxFactory.ParseTypeName($"System.Span<{span.TypeArguments[0].ToMinimalDisplayString(model, type.SpanStart)}>").WithTriviaFrom(type));
        else writable = Modify(parameter, SyntaxKind.RefKeyword);
        return solution.WithDocumentSyntaxRoot(target.Id, root.ReplaceNode(parameter, writable));

        static GenericNameSyntax Generic(TypeSyntax type) => type as GenericNameSyntax ?? (GenericNameSyntax)((QualifiedNameSyntax)type).Right;
    }

    /// <summary>
    /// EECS022: the hidden type and every hidden type that contains it become internal, in each part that states an
    /// accessibility (a partial type's parts must agree) and in the part the diagnostic points at.
    /// </summary>
    private static async Task<Solution> Internal(Document document, Location where, CancellationToken token)
    {
        var solution = document.Project.Solution;
        var target = solution.GetDocument(where.SourceTree) ?? document;
        var root = await target.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var model = await target.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (root?.FindNode(where.SourceSpan).FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() is not { } declaration || model == null
            || model.GetDeclaredSymbol(declaration, token) is not { } type)
            return solution;

        var editor = new SolutionEditor(solution); // nested parts: each edit sees the one inside it
        for (var t = type; t != null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal) continue;
            foreach (var reference in t.DeclaringSyntaxReferences)
            {
                var part = (BaseTypeDeclarationSyntax)await reference.GetSyntaxAsync(token).ConfigureAwait(false);
                if (part != declaration && !part.Modifiers.Any(m => SyntaxFacts.IsAccessibilityModifier(m.Kind()))) continue;
                if (solution.GetDocumentId(part.SyntaxTree) is not { } id) continue;
                (await editor.GetDocumentEditorAsync(id, token).ConfigureAwait(false))
                    .ReplaceNode(part, static (node, generator) => generator.WithAccessibility(node, Accessibility.Internal));
            }
        }

        return editor.GetChangedSolution();
    }

    /// <summary>EECS005: a method that reads <c>this</c> or an instance member of its type cannot become static.</summary>
    private static async Task<bool> UsesInstance(Document document, MethodDeclarationSyntax method, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model == null) return true;
        foreach (var node in method.DescendantNodes())
        {
            if (node is ThisExpressionSyntax or BaseExpressionSyntax) return true;
            if (node is SimpleNameSyntax name && model.GetSymbolInfo(name, token).Symbol is
                    { IsStatic: false, Kind: SymbolKind.Field or SymbolKind.Property or SymbolKind.Method or SymbolKind.Event } member
                && member.ContainingType is { } owner && !IsQualified(name)
                && model.GetEnclosingSymbol(name.SpanStart, token)?.ContainingType is { } enclosing && InheritsFrom(enclosing, owner))
                return true;
        }

        return false;

        // 'p.V' names V through p, not through this
        static bool IsQualified(SimpleNameSyntax name) =>
            name.Parent is MemberAccessExpressionSyntax access && access.Name == name
            || name.Parent is MemberBindingExpressionSyntax
            || name.Parent is QualifiedNameSyntax qualified && qualified.Right == name;

        static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol owner)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, owner.OriginalDefinition)) return true;
            return false;
        }
    }

    /// <summary>
    /// EECS006: a class can become a struct only when it is declared once, derives from nothing but object and has no
    /// member a struct cannot carry (protected, virtual, abstract, a finalizer).
    /// </summary>
    private static async Task<bool> CanBeStruct(Document document, TypeDeclarationSyntax type, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model?.GetDeclaredSymbol(type, token) is not { } symbol) return false;
        if (symbol.DeclaringSyntaxReferences.Length != 1 || symbol.BaseType is { SpecialType: not SpecialType.System_Object }) return false;
        foreach (var member in symbol.GetMembers())
            if (!member.IsImplicitlyDeclared && (member.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal
                                                     or Accessibility.ProtectedAndInternal
                                                 || member.IsVirtual || member.IsAbstract
                                                 || member is IMethodSymbol { MethodKind: MethodKind.Destructor }))
                return false;
        return true;
    }

    /// <summary>
    /// EECS012: the arguments every caller passes for <paramref name="parameter"/>, when each is a variable 'ref' can take
    /// and the method is used only by calls; else null (a method group, an rvalue, a caller out of reach).
    /// </summary>
    private static async Task<System.Collections.Generic.List<(DocumentId Document, ArgumentSyntax Argument)>?> Callers(Document document,
        ParameterSyntax parameter, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model?.GetDeclaredSymbol(parameter, token) is not { ContainingSymbol: IMethodSymbol method } symbol) return null;
        var solution = document.Project.Solution;
        var arguments = new System.Collections.Generic.List<(DocumentId, ArgumentSyntax)>();
        foreach (var found in await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(method, solution, token).ConfigureAwait(false))
        foreach (var location in found.Locations)
        {
            if (location.IsImplicit || location.IsCandidateLocation) return null;
            var root = await location.Document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            var callerModel = await location.Document.GetSemanticModelAsync(token).ConfigureAwait(false);
            var name = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            if (name?.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } call || callerModel == null
                || callerModel.GetOperation(call, token) is not Microsoft.CodeAnalysis.Operations.IInvocationOperation invocation
                || !SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, method.OriginalDefinition))
                return null;
            foreach (var argument in invocation.Arguments)
            {
                if (!SymbolEqualityComparer.Default.Equals(argument.Parameter?.OriginalDefinition, symbol.OriginalDefinition)) continue;
                if (argument.Syntax is not ArgumentSyntax { RefKindKeyword.RawKind: 0 } syntax || !IsVariable(argument.Value)) return null;
                arguments.Add((location.Document.Id, syntax));
            }
        }

        return arguments;

        static bool IsVariable(IOperation value) => value switch
        {
            Microsoft.CodeAnalysis.Operations.ILocalReferenceOperation { Local.IsRef: var isRef, Local.RefKind: var kind } =>
                !isRef || kind == RefKind.Ref,
            Microsoft.CodeAnalysis.Operations.IParameterReferenceOperation { Parameter.RefKind: RefKind.None or RefKind.Ref or RefKind.Out } => true,
            Microsoft.CodeAnalysis.Operations.IFieldReferenceOperation { Field.IsReadOnly: false } => true,
            Microsoft.CodeAnalysis.Operations.IArrayElementReferenceOperation => true,
            Microsoft.CodeAnalysis.Operations.IInvocationOperation { TargetMethod.RefKind: RefKind.Ref } => true,
            _ => false,
        };
    }

    /// <summary>EECS012: the parameter becomes 'ref', and so does each caller's argument.</summary>
    private static async Task<Solution> ByRef(Solution solution, ParameterSyntax parameter,
        System.Collections.Generic.List<(DocumentId Document, ArgumentSyntax Argument)> callers, CancellationToken token)
    {
        var edits = new System.Collections.Generic.Dictionary<DocumentId, System.Collections.Generic.List<SyntaxNode>>();
        var own = solution.GetDocumentId(parameter.SyntaxTree)!;
        edits[own] = new System.Collections.Generic.List<SyntaxNode> { parameter };
        foreach (var (document, argument) in callers)
        {
            if (!edits.TryGetValue(document, out var nodes)) edits[document] = nodes = new System.Collections.Generic.List<SyntaxNode>();
            nodes.Add(argument);
        }

        foreach (var edit in edits)
        {
            var root = await solution.GetDocument(edit.Key)!.GetSyntaxRootAsync(token).ConfigureAwait(false);
            solution = solution.WithDocumentSyntaxRoot(edit.Key, root!.ReplaceNodes(edit.Value, static (_, node) => node switch
            {
                ParameterSyntax p => Modify(p, SyntaxKind.RefKeyword),
                ArgumentSyntax a => a.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space)),
                _ => node,
            }));
        }

        return solution;
    }

    /// <summary>EECS006: <c>class</c> (or <c>record</c>, <c>record class</c>) becomes a struct; modifiers a struct cannot carry go.</summary>
    private static TypeDeclarationSyntax? ToStruct(TypeDeclarationSyntax type)
    {
        var modifiers = SyntaxFactory.TokenList(type.Modifiers.Where(m => !m.IsKind(SyntaxKind.SealedKeyword) && !m.IsKind(SyntaxKind.AbstractKeyword)));
        var keepTrivia = type.GetLeadingTrivia();
        switch (type)
        {
            case ClassDeclarationSyntax cls:
                return SyntaxFactory.StructDeclaration(cls.AttributeLists, modifiers, SyntaxFactory.Token(SyntaxKind.StructKeyword).WithTriviaFrom(cls.Keyword),
                    cls.Identifier, cls.TypeParameterList, cls.ParameterList, cls.BaseList, cls.ConstraintClauses, cls.OpenBraceToken, cls.Members,
                    cls.CloseBraceToken, cls.SemicolonToken).WithLeadingTrivia(keepTrivia);
            case RecordDeclarationSyntax record when !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword):
                var keyword = record.ClassOrStructKeyword.IsKind(SyntaxKind.None)
                    ? SyntaxFactory.Token(SyntaxKind.StructKeyword).WithLeadingTrivia(SyntaxFactory.Space)
                    : SyntaxFactory.Token(SyntaxKind.StructKeyword).WithTriviaFrom(record.ClassOrStructKeyword);
                return SyntaxFactory.RecordDeclaration(SyntaxKind.RecordStructDeclaration, record.AttributeLists, modifiers, record.Keyword,
                    keyword, record.Identifier, record.TypeParameterList, record.ParameterList, record.BaseList, record.ConstraintClauses,
                    record.OpenBraceToken, record.Members, record.CloseBraceToken, record.SemicolonToken).WithLeadingTrivia(keepTrivia);
            default:
                return null;
        }
    }

    /// <summary>EECS010: the <c>Parallel = true</c> argument of the method's [ForEach].</summary>
    private static AttributeArgumentSyntax? Parallel(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => a.Name.ToString() is "ForEach" or "ForEachAttribute" or "EasyEcs.ForEach" or "EasyEcs.ForEachAttribute" or "global::EasyEcs.ForEach")
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Parallel");
}
