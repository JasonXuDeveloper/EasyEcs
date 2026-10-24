using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.CodeFixes;

public sealed partial class EasyEcsCodeFixes
{
    /// <summary>
    /// EECS101: a view built from a method's component parameters becomes the method's parameter. The parameters it
    /// replaces (<paramref name="fields"/>: <c>parameter=Field;...</c>) go, the view parameter takes the first one's place,
    /// the creation becomes the view, and every other use of a replaced parameter reads the view's field.
    /// </summary>
    private static MethodDeclarationSyntax? ToViewParameter(MethodDeclarationSyntax method, ObjectCreationExpressionSyntax creation,
        string name, string type, string fields)
    {
        var map = new Dictionary<string, string>();
        foreach (var pair in fields.Split(';'))
        {
            int at = pair.IndexOf('=');
            if (at <= 0) return null;
            map[pair.Substring(0, at)] = pair.Substring(at + 1);
        }

        var uses = method.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => map.ContainsKey(n.Identifier.ValueText)
            && !creation.Span.Contains(n.Span) && n.FirstAncestorOrSelf<ParameterSyntax>() == null
            && !(n.Parent is MemberAccessExpressionSyntax access && access.Name == n));
        var rewritten = method.ReplaceNodes(uses.Cast<SyntaxNode>().Append(creation), (original, _) => original == creation
            ? SyntaxFactory.IdentifierName(name).WithTriviaFrom(creation)
            : SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(name),
                SyntaxFactory.IdentifierName(map[((IdentifierNameSyntax)original).Identifier.ValueText])).WithTriviaFrom(original));

        var parameters = rewritten.ParameterList.Parameters;
        int first = parameters.IndexOf(p => map.ContainsKey(p.Identifier.ValueText));
        if (first < 0) return null;
        var view = SyntaxFactory.Parameter(SyntaxFactory.Identifier(name)).WithType(SyntaxFactory.ParseTypeName(type).WithTrailingTrivia(SyntaxFactory.Space))
            .WithLeadingTrivia(parameters[first].GetLeadingTrivia());
        var kept = new List<ParameterSyntax>();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i == first) kept.Add(view);
            if (!map.ContainsKey(parameters[i].Identifier.ValueText)) kept.Add(parameters[i]);
        }

        var commas = Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space), kept.Count - 1);
        return rewritten.WithParameterList(rewritten.ParameterList.WithParameters(
            SyntaxFactory.SeparatedList(kept.Select((p, i) => i == 0 ? p : p.WithLeadingTrivia()), commas)));
    }
}
