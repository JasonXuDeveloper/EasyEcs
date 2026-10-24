using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.CodeFixes;

/// <summary>
/// The fixes of lambda passes: a capturing lambda becomes static and takes what it captured as values passed before
/// it (EECS095), and a private member its body names becomes internal, so the generator copies the body (EECS096).
/// </summary>
public sealed partial class EasyEcsCodeFixes
{
    /// <summary>
    /// EECS095: <c>world.ForEach((ref P p) =&gt; p.V += n)</c> becomes <c>world.ForEach(n, static (ref P p, int n) =&gt; p.V += n)</c>:
    /// each captured value (the analyzer's CapturedValues, in the order the body reads them) an argument right before the
    /// lambda and a parameter at its end, typed as the variable is.
    /// </summary>
    private static async Task<Document> PassCaptures(Document document, Diagnostic diagnostic, string names, CancellationToken token)
    {
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<ParenthesizedLambdaExpressionSyntax>() is not { } lambda
            || lambda.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax arguments } argument || model == null)
            return document;

        var parameters = lambda.ParameterList;
        var values = arguments.Arguments;
        int at = values.IndexOf(argument);
        foreach (var name in names.Split(','))
        {
            var type = model.LookupSymbols(lambda.SpanStart, name: name).FirstOrDefault() switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol parameter => parameter.Type,
                _ => null,
            };
            if (type == null) return document;
            parameters = parameters.AddParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
                .WithType(SyntaxFactory.ParseTypeName(type.ToMinimalDisplayString(model, lambda.SpanStart)).WithTrailingTrivia(SyntaxFactory.Space)));
            values = values.Insert(at++, SyntaxFactory.Argument(SyntaxFactory.IdentifierName(name)));
        }

        var fixedLambda = lambda.WithParameterList(parameters);
        if (!lambda.Modifiers.Any(SyntaxKind.StaticKeyword))
            fixedLambda = fixedLambda.WithModifiers(lambda.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                .WithLeadingTrivia(lambda.GetLeadingTrivia());
        // The lambda's own argument is the one at its new index; the arguments before it are the inserted names.
        values = values.Replace(values[at], argument.WithExpression(fixedLambda));
        return document.WithSyntaxRoot(root.ReplaceNode(arguments, arguments.WithArguments(values)));
    }

    /// <summary>EECS096: the private member the body names becomes internal (its declaration, in this file, is the additional location).</summary>
    private static MemberDeclarationSyntax? MakeInternal(SyntaxNode root, Location where)
    {
        if (root.FindNode(where.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<MemberDeclarationSyntax>() is not { } member
            || member is BaseTypeDeclarationSyntax and not { Parent: TypeDeclarationSyntax }) return null;
        var modifiers = member.Modifiers;
        var internalToken = SyntaxFactory.Token(SyntaxKind.InternalKeyword);
        if (modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.PrivateKeyword)) is { RawKind: (int)SyntaxKind.PrivateKeyword } existing)
            return member.WithModifiers(modifiers.Replace(existing, internalToken.WithTriviaFrom(existing)));
        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword)))
            return null;
        // No modifier: private by default; internal goes first, with the member's indentation before it.
        var lead = member.GetLeadingTrivia();
        return member.WithoutLeadingTrivia().WithModifiers(modifiers.Insert(0, internalToken.WithTrailingTrivia(SyntaxFactory.Space)))
            .WithLeadingTrivia(lead);
    }
}
