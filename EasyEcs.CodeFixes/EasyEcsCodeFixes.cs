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
/// the generated code cannot name. Each reads the diagnostic's location (and EECS011 / EECS022 their additional
/// location: the parameter, the type), so the analyzer stays the only place that decides.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EasyEcsCodeFixes)), Shared]
public sealed class EasyEcsCodeFixes : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create("EECS002", "EECS005", "EECS006", "EECS010", "EECS011", "EECS012", "EECS022", "EECS023");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;
        var generator = SyntaxGenerator.GetGenerator(context.Document); // modifiers with their trivia in place
        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            switch (diagnostic.Id)
            {
                case "EECS002" when diagnostic.Properties.ContainsKey("ByValue") && node is ParameterSyntax parameter:
                case "EECS012" when node is ParameterSyntax { Modifiers.Count: 0 } p && (parameter = p) != null:
                    Register(context, diagnostic, "Take it by 'in' (read)", root, parameter, Modify(parameter, SyntaxKind.InKeyword));
                    Register(context, diagnostic, "Take it by 'ref' (write)", root, parameter, Modify(parameter, SyntaxKind.RefKeyword));
                    break;
                case "EECS005" when node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method:
                    Register(context, diagnostic, "Make it static", root, method,
                        generator.WithModifiers(method, generator.GetModifiers(method).WithIsStatic(true)));
                    break;
                case "EECS006" when node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } type && ToStruct(type) is { } data:
                    Register(context, diagnostic, "Make it a struct", root, type, data);
                    break;
                case "EECS010" when node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method && Parallel(method) is { } argument:
                    Register(context, diagnostic, "Run it on one thread", root, argument.Parent!,
                        ((AttributeArgumentListSyntax)argument.Parent!).Arguments.Count == 1 ? null : ((AttributeArgumentListSyntax)argument.Parent!).RemoveNode(argument, SyntaxRemoveOptions.KeepNoTrivia));
                    break;
                case "EECS011" when diagnostic.AdditionalLocations.FirstOrDefault() is { } where:
                    context.RegisterCodeFix(CodeAction.Create("Take it by 'ref'",
                        token => ChangeAt(context.Document, where, token, static n => n.FirstAncestorOrSelf<ParameterSyntax>() is { } p ? (p, Writable(p)) : null),
                        diagnostic.Id), diagnostic);
                    break;
                case "EECS022" when diagnostic.AdditionalLocations.FirstOrDefault() is { } where:
                    context.RegisterCodeFix(CodeAction.Create("Make it internal",
                        token => ChangeAt(context.Document, where, token, n => n.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() is { } t
                            ? (t, generator.WithAccessibility(t, Accessibility.Internal)) : null),
                        diagnostic.Id), diagnostic);
                    break;
                case "EECS023" when node.FirstAncestorOrSelf<AttributeSyntax>() is { Parent: AttributeListSyntax list } attribute:
                    Register(context, diagnostic, "Remove the filter", root, list.Attributes.Count == 1 ? list : attribute, null);
                    break;
            }
        }
    }

    private static void Register(CodeFixContext context, Diagnostic diagnostic, string title, SyntaxNode root, SyntaxNode node,
        SyntaxNode? replacement) =>
        context.RegisterCodeFix(CodeAction.Create(title, _ => Task.FromResult(context.Document.WithSyntaxRoot(replacement == null
                ? root.RemoveNode(node, SyntaxRemoveOptions.KeepNoTrivia)!
                : root.ReplaceNode(node, replacement))), diagnostic.Id + title), diagnostic);

    // The node an additional location points at may live in another document (EECS022's type).
    private static async Task<Solution> ChangeAt(Document document, Location where, CancellationToken token,
        System.Func<SyntaxNode, (SyntaxNode Old, SyntaxNode New)?> change)
    {
        var solution = document.Project.Solution;
        var target = solution.GetDocument(where.SourceTree) ?? document;
        var root = await target.GetSyntaxRootAsync(token).ConfigureAwait(false);
        if (root == null || change(root.FindNode(where.SourceSpan)) is not { } edit) return solution;
        return solution.WithDocumentSyntaxRoot(target.Id, root.ReplaceNode(edit.Old, edit.New));
    }

    private static ParameterSyntax Modify(ParameterSyntax parameter, SyntaxKind kind)
    {
        var modifiers = SyntaxFactory.TokenList(parameter.Modifiers.Where(m => !m.IsKind(SyntaxKind.InKeyword) && !m.IsKind(SyntaxKind.RefKeyword)
                                                                                && !m.IsKind(SyntaxKind.ReadOnlyKeyword)));
        return parameter.WithModifiers(modifiers.Add(SyntaxFactory.Token(kind).WithTrailingTrivia(SyntaxFactory.Space)))
            .WithLeadingTrivia(parameter.GetLeadingTrivia());
    }

    /// <summary>EECS011: <c>in T</c> becomes <c>ref T</c>, <c>ReadOnlySpan&lt;T&gt;</c> becomes <c>Span&lt;T&gt;</c>.</summary>
    private static ParameterSyntax Writable(ParameterSyntax parameter)
    {
        if (Span(parameter.Type) is { } span)
            return parameter.WithType(parameter.Type!.ReplaceNode(span, span.WithIdentifier(SyntaxFactory.Identifier("Span").WithTriviaFrom(span.Identifier))));
        return Modify(parameter, SyntaxKind.RefKeyword);

        static GenericNameSyntax? Span(TypeSyntax? type) => type switch
        {
            GenericNameSyntax { Identifier.Text: "ReadOnlySpan" } name => name,
            QualifiedNameSyntax { Right: GenericNameSyntax { Identifier.Text: "ReadOnlySpan" } name } => name,
            AliasQualifiedNameSyntax { Name: GenericNameSyntax { Identifier.Text: "ReadOnlySpan" } name } => name,
            _ => null,
        };
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
