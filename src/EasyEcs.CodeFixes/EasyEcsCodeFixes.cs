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
/// rules computed (a bulk call, a generated member, a creation given one more component). Each reads the diagnostic's
/// location, its additional locations and its properties, so the analyzer stays the only place that decides.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EasyEcsCodeFixes)), Shared]
public sealed partial class EasyEcsCodeFixes : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create("EECS002", "EECS005", "EECS006", "EECS007", "EECS010", "EECS011", "EECS012", "EECS015", "EECS017",
            "EECS022", "EECS023", "EECS025", "EECS026", "EECS027", "EECS029", "EECS030", "EECS031", "EECS032", "EECS033", "EECS037",
            "EECS038", "EECS039", "EECS043", "EECS046", "EECS052", "EECS053", "EECS054", "EECS061", "EECS062", "EECS065", "EECS066", "EECS067", "EECS069",
            "EECS080", "EECS081", "EECS082", "EECS084");

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

            diagnostic.Properties.TryGetValue("Fix", out var kind);
            switch (diagnostic.Id)
            {
                case "EECS025" or "EECS039" when kind is "MutIn" or "MutRef" && node.FirstAncestorOrSelf<ParameterSyntax>() is { Parent.Parent: MethodDeclarationSyntax method } p
                                                 && FromMut(method, p, kind == "MutIn") is { } plain
                                                 && !await HasCallers(context.Document, p, context.CancellationToken).ConfigureAwait(false):
                    Register(context, diagnostic, kind == "MutIn" ? "Take it by 'in'" : "Take it by 'ref'", root, method, plain);
                    break;
                case "EECS053" when kind == "Mut" && node.FirstAncestorOrSelf<ParameterSyntax>() is { Parent.Parent: MethodDeclarationSyntax method } p
                                    && diagnostic.Properties.TryGetValue("Writes", out var count) && int.TryParse(count, out int writes)
                                    && ToMut(root, method, p, diagnostic.AdditionalLocations, writes) is { } mut
                                    && !await HasCallers(context.Document, p, context.CancellationToken).ConfigureAwait(false):
                    Register(context, diagnostic, $"Take it as a Mut<{p.Type!.WithoutTrivia()}>", root, method, mut);
                    break;
                case "EECS025" or "EECS015" when kind is "In" or "ReadOnlySpan" && node.FirstAncestorOrSelf<ParameterSyntax>() is { } p
                                    && !await HasCallers(context.Document, p, context.CancellationToken).ConfigureAwait(false):
                    if (kind == "ReadOnlySpan")
                        Register(context, diagnostic, "Take it as a ReadOnlySpan", root, p, ReadOnly(p));
                    else
                        Register(context, diagnostic, "Take it by 'in'", root, p, Modify(p, SyntaxKind.InKeyword));
                    break;
                case "EECS015" when kind == "RefReadOnly" && node.FirstAncestorOrSelf<VariableDeclarationSyntax>() is { Type: RefTypeSyntax { ReadOnlyKeyword.RawKind: 0 } type }:
                    Register(context, diagnostic, "Bind it 'ref readonly'", root, type,
                        type.WithReadOnlyKeyword(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space)));
                    break;
                case "EECS007" when kind == "Readonly" && node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is { } field:
                    Register(context, diagnostic, "Make it readonly", root, field,
                        generator.WithModifiers(field, generator.GetModifiers(field).WithIsReadOnly(true)));
                    break;
                case "EECS081" when kind == "Static" && node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method
                                    && !await UsesInstance(context.Document, method, context.CancellationToken).ConfigureAwait(false):
                    MakeStatic(context, diagnostic, generator, root, method);
                    break;
                case "EECS082" when kind == "Schedule" && node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { Parent: ClassDeclarationSyntax type } property:
                    diagnostic.Properties.TryGetValue("Schedule", out var schedule);
                    Register(context, diagnostic, string.IsNullOrEmpty(schedule) ? "Remove it: the system runs every update" : $"Declare it with {schedule}",
                        root, type, Scheduled(type, property, diagnostic.AdditionalLocations, schedule));
                    break;
                case "EECS084" when kind == "Consume" && node.FirstAncestorOrSelf<ExpressionStatementSyntax>() is { Parent: BlockSyntax { Parent: MethodDeclarationSyntax method } } destroy:
                    diagnostic.Properties.TryGetValue("DropEntity", out var entity);
                    if (entity != null && await HasCallers(context.Document, null, context.CancellationToken, method).ConfigureAwait(false)) entity = null;
                    Register(context, diagnostic, "Consume its rows with [Consume]", root, method, Consumed(method, destroy, entity));
                    break;
                case "EECS080" when kind == "ForEntity":
                    context.RegisterCodeFix(CodeAction.Create("Write it as a [ForEntity] method",
                        token => ToForEntity(context.Document, diagnostic, token), diagnostic.Id), diagnostic);
                    break;
                case "EECS037" when kind == "WriteMask" && diagnostic.Properties.TryGetValue("WriteMask", out var writes) && writes != null:
                    if (DeclareWriteMask(root, node, writes) is { } withWrites)
                        context.RegisterCodeFix(CodeAction.Create($"Declare WriteMask => {writes}", _ => Task.FromResult(context.Document.WithSyntaxRoot(withWrites)),
                            diagnostic.Id), diagnostic);
                    break;
                case "EECS043" or "EECS067" when node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { } key:
                    if (diagnostic.Id == "EECS043" && await ExplicitKey(context.Document, key, context.CancellationToken).ConfigureAwait(false) is { } explicitKey)
                        Register(context, diagnostic, "Implement it explicitly", root, key, explicitKey);
                    if (await KeyAttribute(context.Document, root, key, diagnostic.Properties.TryGetValue("Member", out var member) ? member : "this",
                            context.CancellationToken).ConfigureAwait(false) is { } keyed)
                        context.RegisterCodeFix(CodeAction.Create($"Declare it with [{keyed.Attribute}]", _ => Task.FromResult(context.Document.WithSyntaxRoot(keyed.Root)),
                            diagnostic.Id + "attribute"), diagnostic);
                    break;
                case "EECS017" when kind == "EntityId" && node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is { } field:
                    if (await ToEntityId(context.Document, field, context.CancellationToken).ConfigureAwait(false) is { } ids)
                        context.RegisterCodeFix(CodeAction.Create("Store its EntityId", _ => Task.FromResult(ids), diagnostic.Id), diagnostic);
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
                    MakeStatic(context, diagnostic, generator, root, instance);
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
                    MakeStatic(context, diagnostic, generator, root, method);
                    break;
                case "EECS006" when node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } type
                                    && await CanBeStruct(context.Document, type, context.CancellationToken).ConfigureAwait(false)
                                    && ToStruct(type) is { } data:
                    Register(context, diagnostic, "Make it a struct", root, type, data);
                    break;
                case "EECS010" when node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method:
                    // An instance method 'static' alone makes stable (the analyzer checked): the fix EECS029 would offer.
                    if (kind == "Static" && !await UsesInstance(context.Document, method, context.CancellationToken).ConfigureAwait(false)
                        && !await HasCallers(context.Document, null, context.CancellationToken, method).ConfigureAwait(false))
                        MakeStatic(context, diagnostic, generator, root, method);
                    if (Named(method, "Parallel") is { Parent: AttributeArgumentListSyntax arguments } argument)
                        Register(context, diagnostic, "Run it on one thread", root, arguments,
                            arguments.Arguments.Count == 1 ? null : arguments.RemoveNode(argument, SyntaxRemoveOptions.KeepNoTrivia));
                    break;
                case "EECS011" when diagnostic.AdditionalLocations.FirstOrDefault() is { } where:
                    context.RegisterCodeFix(CodeAction.Create("Take it by 'ref'",
                        token => Writable(context.Document, where, token), diagnostic.Id), diagnostic);
                    break;
                case "EECS022" when diagnostic.AdditionalLocations.FirstOrDefault() is { } where:
                    context.RegisterCodeFix(CodeAction.Create("Make it internal",
                        token => Internal(context.Document, where, token), diagnostic.Id), diagnostic);
                    break;
                case "EECS027" when kind == "Parameter" && diagnostic.Properties.TryGetValue("Name", out var name) && name != null
                                    && node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method
                                    && !await HasCallers(context.Document, null, context.CancellationToken, method).ConfigureAwait(false):
                    diagnostic.Properties.TryGetValue("Parameter", out var taken);
                    Register(context, diagnostic, taken == null ? $"Use parameter '{name}'" : $"Take it as a parameter ('{taken}')", root, method,
                        Lookups(root, method, diagnostic.AdditionalLocations, name, taken));
                    break;
                case "EECS054" when kind == "ChunkRows" && node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method
                                    && Named(method, "ChunkRows") is { Parent: AttributeArgumentListSyntax list } chunks:
                    Register(context, diagnostic, "Remove ChunkRows", root, list,
                        list.Arguments.Count == 1 ? null : list.RemoveNode(chunks, SyntaxRemoveOptions.KeepNoTrivia));
                    break;
                case "EECS062" when node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } type
                                    && !type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Any(t => t.Modifiers.Any(SyntaxKind.FileKeyword)):
                    context.RegisterCodeFix(CodeAction.Create("Make it partial", _ => Task.FromResult(context.Document.WithSyntaxRoot(Partial(root, type))),
                        diagnostic.Id), diagnostic);
                    break;
                case "EECS065" or "EECS066" when node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } type:
                    if (diagnostic.Id == "EECS066" && diagnostic.Properties.TryGetValue("Identity", out var identity) && identity != null
                        && node.FirstAncestorOrSelf<ExpressionSyntax>() is { } start)
                        Register(context, diagnostic, "Fix the Identity", root, start, SyntaxFactory.ParseExpression(identity).WithTriviaFrom(start));
                    if (diagnostic.Properties.TryGetValue("Ops", out var ops) && ops != null
                        && await Attributed(context.Document, root, type, ops, context.CancellationToken).ConfigureAwait(false) is { } attributed)
                        context.RegisterCodeFix(CodeAction.Create("Declare it with attributes", _ => Task.FromResult(context.Document.WithSyntaxRoot(attributed)),
                            diagnostic.Id + "attributes"), diagnostic);
                    break;
                case "EECS069" when node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } type:
                    if (!type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Any(t => t.Modifiers.Any(SyntaxKind.FileKeyword))
                        && await Marked(context.Document, root, type, true, context.CancellationToken).ConfigureAwait(false) is { } nino)
                        context.RegisterCodeFix(CodeAction.Create("Mark it [NinoType]", _ => Task.FromResult(context.Document.WithSyntaxRoot(nino)), diagnostic.Id + "nino"), diagnostic);
                    if (await Marked(context.Document, root, type, false, context.CancellationToken).ConfigureAwait(false) is { } notSaved)
                        context.RegisterCodeFix(CodeAction.Create("Mark it [NotSaved]", _ => Task.FromResult(context.Document.WithSyntaxRoot(notSaved)), diagnostic.Id + "notsaved"), diagnostic);
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
            var statement = SyntaxFactory.ParseStatement(insert).WithLeadingTrivia(indent).WithTrailingTrivia(EndOfLine(root));
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

    /// <summary>
    /// EECS025 / EECS039: a <c>Mut&lt;T&gt;</c> parameter becomes <c>in T</c> (<paramref name="read"/>) or <c>ref T</c>, and
    /// its uses <c>p.Value</c> and <c>p.Write()</c> become <c>p</c>; null when the type cannot be read off the syntax.
    /// </summary>
    private static MethodDeclarationSyntax? FromMut(MethodDeclarationSyntax method, ParameterSyntax parameter, bool read)
    {
        var mut = Generic(parameter.Type);
        if (mut is not { Identifier.Text: "Mut", TypeArgumentList.Arguments.Count: 1 }) return null;
        string name = parameter.Identifier.ValueText;
        var uses = method.DescendantNodes().Where(n =>
            n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax w, Name.Identifier.ValueText: "Write" }, ArgumentList.Arguments.Count: 0 }
                && w.Identifier.ValueText == name
            || n is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax v, Name.Identifier.ValueText: "Value" } && v.Identifier.ValueText == name);
        var plain = SyntaxFactory.Parameter(parameter.AttributeLists, SyntaxFactory.TokenList(), mut.TypeArgumentList.Arguments[0].WithTrailingTrivia(SyntaxFactory.Space),
            parameter.Identifier, parameter.Default);
        plain = Modify(plain.WithLeadingTrivia(parameter.GetLeadingTrivia()), read ? SyntaxKind.InKeyword : SyntaxKind.RefKeyword);
        return method.ReplaceNodes(uses.Append(parameter), (original, _) => original == parameter ? plain
            : SyntaxFactory.IdentifierName(name).WithTriviaFrom(original));
    }

    /// <summary>
    /// EECS053: a <c>ref T</c> parameter becomes <c>Mut&lt;T&gt;</c>; the uses the analyzer found (<paramref name="uses"/>:
    /// the first <paramref name="writes"/> write, the others read) become <c>p.Write()</c> and <c>p.Value</c>. Null when
    /// the parameter has another modifier than 'ref' or a use is not the parameter's name.
    /// </summary>
    private static MethodDeclarationSyntax? ToMut(SyntaxNode root, MethodDeclarationSyntax method, ParameterSyntax parameter,
        System.Collections.Generic.IReadOnlyList<Location> uses, int writes)
    {
        if (parameter.Type == null || parameter.Modifiers.Count != 1 || !parameter.Modifiers[0].IsKind(SyntaxKind.RefKeyword)) return null;
        string name = parameter.Identifier.ValueText;
        var write = new System.Collections.Generic.HashSet<SyntaxNode>();
        var nodes = new System.Collections.Generic.List<SyntaxNode> { parameter };
        for (int i = 0; i < uses.Count; i++)
        {
            if (root.FindNode(uses[i].SourceSpan, getInnermostNodeForTie: true) is not IdentifierNameSyntax use
                || use.Identifier.ValueText != name || !method.Span.Contains(use.Span))
                return null;
            nodes.Add(use);
            if (i < writes) write.Add(use);
        }

        var mut = SyntaxFactory.GenericName(SyntaxFactory.Identifier("Mut"),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(parameter.Type.WithoutTrivia())));
        var replaced = SyntaxFactory.Parameter(parameter.AttributeLists, SyntaxFactory.TokenList(), mut.WithTrailingTrivia(SyntaxFactory.Space),
            parameter.Identifier, parameter.Default).WithLeadingTrivia(parameter.Modifiers[0].LeadingTrivia);
        if (parameter.AttributeLists.Count > 0) replaced = replaced.WithLeadingTrivia(parameter.GetLeadingTrivia());
        return method.ReplaceNodes(nodes, (original, _) =>
        {
            if (original == parameter) return replaced;
            var target = SyntaxFactory.IdentifierName(name);
            ExpressionSyntax access = write.Contains(original)
                ? SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target, SyntaxFactory.IdentifierName("Write")))
                : SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target, SyntaxFactory.IdentifierName("Value"));
            return access.WithTriviaFrom(original);
        });
    }

    /// <summary>EECS037: the job's WriteMask declared as <paramref name="value"/>, replacing its declaration or as its first member.</summary>
    private static SyntaxNode? DeclareWriteMask(SyntaxNode root, SyntaxNode node, string value)
    {
        var member = (MemberDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"public static int WriteMask => {value};")!;
        if (node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { Identifier.ValueText: "WriteMask" } declared)
            return root.ReplaceNode(declared, member.WithTriviaFrom(declared));
        if (node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } type) return null;
        var first = type.Members.FirstOrDefault();
        var indent = first?.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).LastOrDefault() ?? default;
        // One line ({ public int N; ... }): the member joins it; one member per line: it gets a line of its own.
        bool lines = type.OpenBraceToken.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia))
                     || first?.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) == true;
        member = lines
            ? member.WithLeadingTrivia(indent).WithTrailingTrivia(EndOfLine(root))
            : member.WithTrailingTrivia(SyntaxFactory.Space);
        return root.ReplaceNode(type, type.WithMembers(type.Members.Insert(0, member)));
    }

    /// <summary>
    /// EECS043: <c>public Self Key => this;</c> becomes <c>Self IIndexed&lt;Self&gt;.Key => this;</c>, unless code reads
    /// <c>.Key</c> through the type (an explicit implementation is reached through the interface only).
    /// </summary>
    private static async Task<PropertyDeclarationSyntax?> ExplicitKey(Document document, PropertyDeclarationSyntax key, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model?.GetDeclaredSymbol(key, token) is not IPropertySymbol property || key.ExplicitInterfaceSpecifier != null) return null;
        var face = property.ContainingType.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == "EasyEcs.IIndexed<TKey>"
                                                                            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], property.ContainingType));
        if (face == null || await ReadThroughType(document, property, token).ConfigureAwait(false)) return null;

        var modifiers = SyntaxFactory.TokenList(key.Modifiers.Where(m => !SyntaxFacts.IsAccessibilityModifier(m.Kind())));
        return key.WithModifiers(modifiers)
            .WithExplicitInterfaceSpecifier(SyntaxFactory.ExplicitInterfaceSpecifier(SyntaxFactory.ParseName(face.ToMinimalDisplayString(model, key.SpanStart))))
            .WithLeadingTrivia(key.GetLeadingTrivia());
    }

    /// <summary>
    /// Whether code of the project reads <paramref name="key"/> (an implicit Key) through its type, not the interface:
    /// such a read stops compiling once the key is an explicit implementation or the generator's.
    /// </summary>
    private static async Task<bool> ReadThroughType(Document document, IPropertySymbol key, CancellationToken token)
    {
        if (key.ExplicitInterfaceImplementations.Length > 0) return false;
        foreach (var other in document.Project.Documents)
        {
            var otherRoot = await other.GetSyntaxRootAsync(token).ConfigureAwait(false);
            if (otherRoot == null) continue;
            SemanticModel? otherModel = null;
            foreach (var name in otherRoot.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (name.Identifier.ValueText != "Key" || name.Parent is PropertyDeclarationSyntax) continue;
                otherModel ??= await other.GetSemanticModelAsync(token).ConfigureAwait(false);
                if (SymbolEqualityComparer.Default.Equals(otherModel?.GetSymbolInfo(name, token).Symbol, key)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// EECS017: an <c>Entity</c> field becomes an <c>EntityId</c> when every use still compiles: <c>x.Field.Id</c> (which
    /// becomes <c>x.Field</c>), a comparison, an assignment to it, or a use as an <c>EntityId</c> (a conversion); null
    /// otherwise (a member of the handle, the handle passed on).
    /// </summary>
    private static async Task<Solution?> ToEntityId(Document document, FieldDeclarationSyntax field, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model == null || field.Declaration.Variables.Count != 1
            || model.GetDeclaredSymbol(field.Declaration.Variables[0], token) is not IFieldSymbol symbol
            || model.Compilation.GetTypeByMetadataName("EasyEcs.EntityId") is not { } entityId) return null;
        var solution = document.Project.Solution;
        var edits = new System.Collections.Generic.Dictionary<DocumentId, System.Collections.Generic.List<(SyntaxNode Old, SyntaxNode New)>>();
        foreach (var found in await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, solution, token).ConfigureAwait(false))
        foreach (var location in found.Locations)
        {
            if (location.IsImplicit || location.IsCandidateLocation) return null;
            var useRoot = await location.Document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            var useModel = await location.Document.GetSemanticModelAsync(token).ConfigureAwait(false);
            if (useRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true) is not SimpleNameSyntax name || useModel == null) return null;
            ExpressionSyntax use = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
            if (!edits.TryGetValue(location.Document.Id, out var list)) edits[location.Document.Id] = list = new();
            switch (use.Parent)
            {
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Id" } id when id.Expression == use:
                    list.Add((id, use.WithTriviaFrom(id)));
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression):
                case AssignmentExpressionSyntax assignment when assignment.Left == use && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                    break;
                default:
                    if (!SymbolEqualityComparer.Default.Equals(useModel.GetTypeInfo(use, token).ConvertedType, entityId)) return null;
                    break;
            }
        }

        var type = SyntaxFactory.ParseTypeName(entityId.ToMinimalDisplayString(model, field.SpanStart)).WithTriviaFrom(field.Declaration.Type);
        if (!edits.TryGetValue(document.Id, out var own)) edits[document.Id] = own = new();
        own.Add((field.Declaration.Type, type));
        foreach (var edit in edits)
        {
            var root = await solution.GetDocument(edit.Key)!.GetSyntaxRootAsync(token).ConfigureAwait(false);
            var map = edit.Value.ToDictionary(e => e.Old, e => e.New);
            solution = solution.WithDocumentSyntaxRoot(edit.Key, root!.ReplaceNodes(map.Keys, (original, _) => map[original]));
        }

        return solution;
    }

    /// <summary>EECS025: <c>Span&lt;T&gt;</c> (however it is spelled) becomes <c>ReadOnlySpan&lt;T&gt;</c>.</summary>
    private static ParameterSyntax ReadOnly(ParameterSyntax parameter)
    {
        var type = parameter.Type!;
        if (Generic(type) is { Identifier.Text: "Span" } generic)
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
                if (IsForEach(attribute))
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

    /// <summary>EECS005, EECS010, EECS029: the method becomes static (the caller checked that it reads no instance state).</summary>
    private static void MakeStatic(CodeFixContext context, Diagnostic diagnostic, SyntaxGenerator generator, SyntaxNode root, MethodDeclarationSyntax method) =>
        Register(context, diagnostic, "Make it static", root, method, generator.WithModifiers(method, generator.GetModifiers(method).WithIsStatic(true)));

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
            writable = Generic(type) is { Identifier.Text: "ReadOnlySpan" } generic
                ? parameter.WithType(type.ReplaceNode(generic, generic.WithIdentifier(SyntaxFactory.Identifier("Span").WithTriviaFrom(generic.Identifier))))
                : parameter.WithType(SyntaxFactory.ParseTypeName($"System.Span<{span.TypeArguments[0].ToMinimalDisplayString(model, type.SpanStart)}>").WithTriviaFrom(type));
        else writable = Modify(parameter, SyntaxKind.RefKeyword);
        return solution.WithDocumentSyntaxRoot(target.Id, root.ReplaceNode(parameter, writable));
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

    /// <summary>
    /// EECS027: the lookups the analyzer found (<paramref name="uses"/>) become <paramref name="name"/>, and
    /// <paramref name="parameter"/> (when the method does not take the type yet) is added after the others.
    /// </summary>
    private static MethodDeclarationSyntax Lookups(SyntaxNode root, MethodDeclarationSyntax method, System.Collections.Generic.IEnumerable<Location> uses,
        string name, string? parameter)
    {
        var nodes = uses.Select(l => root.FindNode(l.SourceSpan, getInnermostNodeForTie: true)).Where(n => method.Span.Contains(n.Span)).ToList();
        var replaced = method.ReplaceNodes(nodes, (original, _) => SyntaxFactory.IdentifierName(name).WithTriviaFrom(original));
        if (parameter == null) return replaced;
        var added = SyntaxFactory.ParseParameterList("(" + parameter + ")").Parameters[0];
        var list = replaced.ParameterList.Parameters;
        return replaced.WithParameterList(replaced.ParameterList.WithParameters(list.Count == 0
            ? SyntaxFactory.SingletonSeparatedList(added)
            : SyntaxFactory.SeparatedList<ParameterSyntax>(list.GetWithSeparators()
                .Add(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space)).Add(added))));
    }

    /// <summary>The named argument <paramref name="name"/> of the method's [ForEach] attribute (EECS010: <c>Parallel = true</c>, EECS054: <c>ChunkRows = 64</c>).</summary>
    private static AttributeArgumentSyntax? Named(MethodDeclarationSyntax method, string name) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Where(IsForEach)
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name);

    /// <summary>Whether <paramref name="attribute"/> is spelled as [ForEach] (the fixes edit its syntax, not its symbol).</summary>
    private static bool IsForEach(AttributeSyntax attribute) =>
        attribute.Name.ToString() is "ForEach" or "ForEachAttribute" or "EasyEcs.ForEach" or "EasyEcs.ForEachAttribute" or "global::EasyEcs.ForEach"
            or "global::EasyEcs.ForEachAttribute";

    /// <summary>The generic name of a type, qualified (<c>System.Span&lt;T&gt;</c>) or not; null for any other type.</summary>
    private static GenericNameSyntax? Generic(TypeSyntax? type) => type as GenericNameSyntax ?? (type as QualifiedNameSyntax)?.Right as GenericNameSyntax;

    /// <summary>The file's own line ending, for a line a fix adds: a CRLF file stays CRLF, an LF file LF.</summary>
    /// <summary>
    /// EECS062 and the fixes that declare a part: <paramref name="type"/> and every type containing it get
    /// <c>partial</c> (last among their modifiers, where C# wants it), in <paramref name="root"/>.
    /// </summary>
    internal static SyntaxNode Partial(SyntaxNode root, TypeDeclarationSyntax type)
    {
        var chain = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Where(t => !t.Modifiers.Any(SyntaxKind.PartialKeyword)).ToList();
        return root.ReplaceNodes(chain, (_, rewritten) =>
        {
            var t = (TypeDeclarationSyntax)rewritten;
            var partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            if (t.Modifiers.Count > 0) return t.WithModifiers(t.Modifiers.Add(partial));
            // No modifier: the keyword's leading trivia (indentation, or the line after the attributes) moves to partial.
            return t.WithModifiers(SyntaxFactory.TokenList(partial.WithLeadingTrivia(t.Keyword.LeadingTrivia)))
                .WithKeyword(t.Keyword.WithLeadingTrivia());
        });
    }

    /// <summary>
    /// EECS065 / EECS066: a hand-written reduction declared with attributes instead. <paramref name="ops"/> names each
    /// field's attribute (<c>Alive=Sum;Lowest=Min</c>): it goes on the field, the auto-property or the record's
    /// parameter; Identity, Combine and the IReduction&lt;T&gt; of the base list go; the type becomes partial along its
    /// chain. Null when a declaration holds two fields of different attributes.
    /// </summary>
    private static async Task<SyntaxNode?> Attributed(Document document, SyntaxNode root, TypeDeclarationSyntax type, string ops, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model == null) return null;
        var byName = ops.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        return Declare(model, root, type, byName, null,
            static m => m is MethodDeclarationSyntax { Identifier.ValueText: "Combine" } or PropertyDeclarationSyntax { Identifier.ValueText: "Identity" }
                        && m.Modifiers.Any(SyntaxKind.StaticKeyword),
            static b => Generic(b.Type) is { Identifier.ValueText: "IReduction" });
    }

    /// <summary>
    /// EECS067 (and EECS043's second fix): a hand-written Key returning <paramref name="member"/> (<c>this</c>: the value)
    /// declared with <c>[Key]</c> (<c>[UniqueKey]</c> for a unique index) on that member or the type; the Key and the
    /// IIndexed / IUniqueIndexed of the base list go (an IDirectIndexed stays: its Slots and SlotOf are the user's), the
    /// type becomes partial along its chain. Null when code reads the Key through the type.
    /// </summary>
    private static async Task<(SyntaxNode Root, string Attribute)?> KeyAttribute(Document document, SyntaxNode root, PropertyDeclarationSyntax key,
        string? member, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (member == null || model?.GetDeclaredSymbol(key, token) is not IPropertySymbol property || key.Parent is not TypeDeclarationSyntax type
            || await ReadThroughType(document, property, token).ConfigureAwait(false))
            return null;
        bool unique = property.ContainingType.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "EasyEcs.IUniqueIndexed<TKey>");
        string attribute = unique ? "UniqueKey" : "Key";
        var byName = new System.Collections.Generic.Dictionary<string, string>();
        if (member != "this") byName[member] = attribute;
        var rewritten = Declare(model, root, type, byName, member == "this" ? attribute : null,
            static m => m is PropertyDeclarationSyntax { Identifier.ValueText: "Key" } p && !p.Modifiers.Any(SyntaxKind.StaticKeyword), // the analyzer saw one Key
            static b => Generic(b.Type) is { Identifier.ValueText: "IIndexed" or "IUniqueIndexed" });
        return rewritten == null ? null : (rewritten, attribute);
    }

    /// <summary>
    /// The declarations of a part written into <paramref name="type"/>: <paramref name="byName"/>'s attribute on each
    /// member it names (a field, an auto-property, a record's parameter), <paramref name="onType"/> on the type itself,
    /// the members <paramref name="remove"/> picks gone, the bases <paramref name="removeBase"/> picks gone when another
    /// base stays (alone, one stays: declared twice is fine), and partial along the chain. Null when one field
    /// declaration holds members of different attributes.
    /// </summary>
    private static SyntaxNode? Declare(SemanticModel model, SyntaxNode root, TypeDeclarationSyntax type, System.Collections.Generic.Dictionary<string, string> byName,
        string? onType, System.Func<MemberDeclarationSyntax, bool> remove, System.Func<BaseTypeSyntax, bool> removeBase)
    {
        bool imported = model.LookupNamespacesAndTypes(type.SpanStart, name: "SumAttribute").Any(s => s.ToDisplayString() == "EasyEcs.SumAttribute");
        AttributeListSyntax List(string attribute, SyntaxTriviaList leading, SyntaxTriviaList trailing) =>
            SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName(imported ? attribute : "EasyEcs." + attribute))))
                .WithLeadingTrivia(leading).WithTrailingTrivia(trailing);
        var annotate = new System.Collections.Generic.Dictionary<SyntaxNode, string>();
        foreach (var member in type.Members)
            switch (member)
            {
                case FieldDeclarationSyntax field when field.Declaration.Variables.Any(v => byName.ContainsKey(v.Identifier.ValueText)):
                    var kinds = field.Declaration.Variables.Select(v => byName.TryGetValue(v.Identifier.ValueText, out var k) ? k : null).Distinct().ToList();
                    if (kinds.Count != 1 || kinds[0] == null) return null;
                    annotate[field] = kinds[0]!;
                    break;
                case PropertyDeclarationSyntax property when byName.TryGetValue(property.Identifier.ValueText, out var op):
                    annotate[property] = op;
                    break;
            }

        if (type.ParameterList != null)
            foreach (var parameter in type.ParameterList.Parameters)
                if (byName.TryGetValue(parameter.Identifier.ValueText, out var op))
                    annotate[parameter] = op;

        var rewritten = type.ReplaceNodes(annotate.Keys, (original, current) =>
        {
            var list = List(annotate[original], current.GetLeadingTrivia(), SyntaxFactory.TriviaList(SyntaxFactory.Space));
            return current switch
            {
                FieldDeclarationSyntax f => f.WithoutLeadingTrivia().WithAttributeLists(f.WithoutLeadingTrivia().AttributeLists.Insert(0, list)),
                PropertyDeclarationSyntax p => p.WithoutLeadingTrivia().WithAttributeLists(p.WithoutLeadingTrivia().AttributeLists.Insert(0, list)),
                ParameterSyntax p => p.WithoutLeadingTrivia().WithAttributeLists(p.WithoutLeadingTrivia().AttributeLists.Insert(0, list)),
                _ => current,
            };
        });
        // The members and the bases go: found again, as the rewrite made new nodes.
        rewritten = rewritten.RemoveNodes(rewritten.Members.Where(remove), SyntaxRemoveOptions.KeepNoTrivia)!;
        if (rewritten.BaseList is { } bases)
        {
            var types = bases.Types;
            foreach (var listed in bases.Types.Where(removeBase))
            {
                if (types.Count == 1) break;
                var current = types.First(t => t.IsEquivalentTo(listed));
                bool last = types.Last() == current;
                types = types.Remove(current);
                if (last) types = types.Replace(types.Last(), types.Last().WithTrailingTrivia(current.GetTrailingTrivia()));
            }

            rewritten = rewritten.WithBaseList(bases.WithTypes(types));
        }

        if (onType != null) rewritten = WithAttribute(rewritten, imported ? onType : "EasyEcs." + onType, root);

        var marked = root.ReplaceNode(type, rewritten.WithAdditionalAnnotations(Marker));
        return Partial(marked, marked.GetAnnotatedNodes(Marker).OfType<TypeDeclarationSyntax>().Single());
    }

    /// <summary>
    /// <paramref name="type"/> with attribute <paramref name="attribute"/> (as written) first: on the type's line, its
    /// indentation kept, the type on the next line indented the same.
    /// </summary>
    private static TypeDeclarationSyntax WithAttribute(TypeDeclarationSyntax type, string attribute, SyntaxNode root)
    {
        var leading = type.GetLeadingTrivia();
        var indent = leading.Count > 0 && leading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? leading.Last() : default;
        var next = indent == default ? SyntaxFactory.TriviaList(EndOfLine(root)) : SyntaxFactory.TriviaList(EndOfLine(root), indent);
        var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName(attribute))))
            .WithLeadingTrivia(leading).WithTrailingTrivia(next);
        var withoutLeading = type.WithoutLeadingTrivia();
        return withoutLeading.WithAttributeLists(withoutLeading.AttributeLists.Insert(0, list));
    }

    /// <summary>
    /// EECS069: <paramref name="type"/> marked <c>[NinoType]</c> and partial along its chain (the Nino generator writes
    /// its formatter into a part), or <c>[NotSaved]</c>; each named as the file's usings allow.
    /// </summary>
    private static async Task<SyntaxNode?> Marked(Document document, SyntaxNode root, TypeDeclarationSyntax type, bool nino, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model == null) return null;
        string name = nino ? "NinoType" : "NotSaved", ns = nino ? "Nino.Core" : "EasyEcs";
        bool imported = model.LookupNamespacesAndTypes(type.SpanStart, name: name + "Attribute").Any(s => s.ToDisplayString() == ns + "." + name + "Attribute");
        var marked = WithAttribute(type, imported ? name : ns + "." + name, root);
        if (!nino) return root.ReplaceNode(type, marked);
        var withMark = root.ReplaceNode(type, marked.WithAdditionalAnnotations(Marker));
        return Partial(withMark, withMark.GetAnnotatedNodes(Marker).OfType<TypeDeclarationSyntax>().Single());
    }

    private static readonly SyntaxAnnotation Marker = new("EasyEcs.Part");

    private static SyntaxTrivia EndOfLine(SyntaxNode root) =>
        root.DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) is { RawKind: (int)SyntaxKind.EndOfLineTrivia } eol ? eol : SyntaxFactory.LineFeed;
}
