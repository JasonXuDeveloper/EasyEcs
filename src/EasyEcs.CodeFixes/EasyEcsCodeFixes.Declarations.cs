using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.CodeFixes;

/// <summary>
/// The migrations from the forms of released versions, each from the compiler's error on the old form (the old form no
/// longer binds, so these are syntax only, offered in a project that references EasyEcs): single-type
/// <c>[WithAny&lt;T&gt;]</c> attributes and <c>.WithAny&lt;T&gt;()</c> calls (CS0305) merged into one
/// <c>WithAny&lt;T1, T2, ...&gt;</c> (a lone one is <c>With&lt;T&gt;</c>), and a renamed member of an EasyEcs type
/// (CS0117: <c>World.ParallelChunkRows</c> is <c>World.MaxChunkSize</c>). A form that never shipped has no fix (D141).
/// EECS090's rename of a method named almost like a hook lives here too.
/// </summary>
public sealed partial class EasyEcsCodeFixes
{
    /// <summary>A <c>WithAny&lt;T&gt;</c> of one type, the form the 2..9-type family replaced.</summary>
    private static bool IsSingleWithAny(SimpleNameSyntax? name) =>
        name is GenericNameSyntax { Identifier.ValueText: "WithAny", TypeArgumentList.Arguments.Count: 1 };

    /// <summary>The generic name an attribute is spelled with (<c>WithAny&lt;T&gt;</c>, <c>EasyEcs.WithAny&lt;T&gt;</c>).</summary>
    private static SimpleNameSyntax? LastName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => q.Right,
        AliasQualifiedNameSyntax a => a.Name,
        SimpleNameSyntax s => s,
        _ => null,
    };

    /// <summary>
    /// CS0305 on a single-type <c>WithAny&lt;T&gt;</c>: every such attribute of the same declaration, or every such call
    /// of the same filter chain, merged into one <c>WithAny&lt;T1, T2, ...&gt;</c> in their order (the first keeps its
    /// place, the others go); a lone one becomes <c>With&lt;T&gt;</c>, which is what it meant. Returns the node to
    /// replace and its replacement, or null when the name is in no attribute or call chain.
    /// </summary>
    private static (string Title, SyntaxNode Old, SyntaxNode New)? MergedWithAny(GenericNameSyntax any)
    {
        SyntaxNode? at = any.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax ? any.Parent : any;
        if (at?.Parent is AttributeSyntax { Parent: AttributeListSyntax { Parent: { } owner } })
        {
            var attributes = owner.ChildNodes().OfType<AttributeListSyntax>().SelectMany(l => l.Attributes)
                .Where(a => IsSingleWithAny(LastName(a.Name))).ToList();
            var types = Merged(attributes.Select(a => (GenericNameSyntax)LastName(a.Name)!));
            var first = attributes[0];
            var named = (GenericNameSyntax)LastName(first.Name)!;
            var replacement = Renamed(named, types);
            // The first takes the merged list; the others go, with the lists they leave empty.
            var updated = owner.TrackNodes(attributes);
            updated = updated.ReplaceNode(updated.GetCurrentNode(first)!, first.ReplaceNode(named, replacement));
            var rest = attributes.Skip(1).ToList();
            var emptied = rest.Where(a => ((AttributeListSyntax)a.Parent!).Attributes.All(rest.Contains)).ToList();
            updated = updated.RemoveNodes(rest.Except(emptied).Select(a => updated.GetCurrentNode(a)!), SyntaxRemoveOptions.KeepNoTrivia)!;
            updated = updated.RemoveNodes(emptied.Select(a => updated.GetCurrentNode(a)?.Parent).Where(l => l != null).Distinct()!, SyntaxRemoveOptions.KeepNoTrivia)!;
            return (Title(replacement), owner, updated);
        }

        if (any.Parent is not MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access || access.Name != any) return null;
        // The whole chain: up to the outermost call, then each WithAny call of one type from the inside out.
        ExpressionSyntax top = call;
        while (top.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } link && link.Expression == top) top = outer;
        var calls = new List<InvocationExpressionSyntax>();
        for (var current = top as InvocationExpressionSyntax; current is { Expression: MemberAccessExpressionSyntax m };
             current = m.Expression as InvocationExpressionSyntax)
            if (IsSingleWithAny(m.Name) && current.ArgumentList.Arguments.Count == 0) calls.Add(current);
        calls.Reverse(); // source order: the innermost call comes first
        var all = Merged(calls.Select(c => (GenericNameSyntax)((MemberAccessExpressionSyntax)c.Expression).Name));
        var name = Renamed((GenericNameSyntax)((MemberAccessExpressionSyntax)calls[0].Expression).Name, all);
        var chain = top.ReplaceNodes(calls, (original, current) => original == calls[0]
            ? current.WithExpression(((MemberAccessExpressionSyntax)current.Expression).WithName(name))
            : ((MemberAccessExpressionSyntax)current.Expression).Expression.WithTrailingTrivia(current.GetTrailingTrivia()));
        return (Title(name), top, chain);

        static string Title(GenericNameSyntax name) => $"Write it as {name.Identifier.ValueText}<{name.TypeArgumentList.Arguments}>";
    }

    /// <summary>The members of EasyEcs types that shipped under another name, (type, old name, new name): the old name no longer binds.</summary>
    private static readonly (string Type, string Old, string New)[] RenamedMembers = [("World", "ParallelChunkRows", "MaxChunkSize")];

    /// <summary>
    /// CS0117 on <c>Type.Member</c> of a renamed member (<see cref="RenamedMembers"/>) of an EasyEcs type: the name takes
    /// the new one, the qualification kept. Null for any other member, or a type that is not EasyEcs's.
    /// </summary>
    private static async Task<(string Title, SyntaxNode Old, SyntaxNode New)?> RenamedMember(Document document, MemberAccessExpressionSyntax access, CancellationToken token)
    {
        string spelled = access.Name.Identifier.ValueText;
        foreach (var (type, old, renamed) in RenamedMembers)
        {
            if (spelled != old) continue;
            var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
            if (model?.GetSymbolInfo(access.Expression, token).Symbol is not INamedTypeSymbol { Name: var name, ContainingNamespace: { Name: "EasyEcs", ContainingNamespace.IsGlobalNamespace: true } }
                || name != type)
                return null;
            return ($"Rename it to {renamed}", access.Name, SyntaxFactory.IdentifierName(renamed).WithTriviaFrom(access.Name));
        }

        return null;
    }

    /// <summary>EECS090: a method named almost like a hook takes the hook's name (the analyzer checked it has no caller).</summary>
    private static MethodDeclarationSyntax RenamedHook(MethodDeclarationSyntax method, string hook) =>
        method.WithIdentifier(SyntaxFactory.Identifier(hook).WithTriviaFrom(method.Identifier));

    /// <summary>The type arguments of the merged names in order, each once.</summary>
    private static List<TypeSyntax> Merged(IEnumerable<GenericNameSyntax> names)
    {
        var types = new List<TypeSyntax>();
        foreach (var name in names)
        {
            var type = name.TypeArgumentList.Arguments[0];
            if (types.All(t => !t.IsEquivalentTo(type))) types.Add(type.WithoutTrivia());
        }

        return types;
    }

    /// <summary><c>WithAny&lt;T1, T2, ...&gt;</c> of <paramref name="types"/>, or <c>With&lt;T&gt;</c> for one.</summary>
    private static GenericNameSyntax Renamed(GenericNameSyntax name, List<TypeSyntax> types) =>
        types.Count == 1
            ? name.WithIdentifier(SyntaxFactory.Identifier("With").WithTriviaFrom(name.Identifier))
            : name.WithTypeArgumentList(name.TypeArgumentList.WithArguments(SyntaxFactory.SeparatedList(
                types.Select((t, i) => i == 0 ? t : t.WithLeadingTrivia(SyntaxFactory.Space)))));
}
