using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace EasyEcs.Generator;

/// <summary>
/// A [ForEach] method that takes views (<c>[View]</c> ref structs) is translated as the method a user would write
/// without them: each view field becomes a component parameter (<c>ref Health __fighter_Health</c>, appended after the
/// method's own parameters, so the others keep their positions and job fields), each use of a field that parameter,
/// and each call of a view method or read of a view property is inlined as any other call is (Kernel.Inlining.cs: the
/// view is <c>this</c>, its fields the parameters). The desugared method is a static local function, the one statement
/// of the method's body in a speculative model (the parameters change, which a speculative body alone cannot take; a
/// copy of the compilation per method would rebind its tree), and the vector loop is that function's: a view costs
/// what the refs written by hand cost, and the reason a method stays scalar is the reason its desugared form would.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// The desugared form of <paramref name="method"/> (<see cref="Kernel"/> remarks): the local function, its body's
    /// operation and the speculative model they are bound in; or null with the reason a view keeps it scalar (a view
    /// used as a value, a view member the vector loop cannot follow).
    /// </summary>
    private static (IMethodSymbol Method, IOperation Body, SemanticModel Model)? Desugar(IMethodSymbol method,
        MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct, out string? reason)
    {
        reason = null;
        if (method.PartialDefinitionPart != null || method.PartialImplementationPart != null)
        {
            reason = "it is a partial method that takes a view";
            return null;
        }

        var used = new HashSet<string>(syntax.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText));
        var inliner = new Inliner(used, method, model.Compilation, ct);
        inliner.Callees.Offer(model, syntax);
        var added = new List<ParameterSyntax>();
        foreach (var p in method.Parameters)
        {
            if (Symbols.Classify(p, Symbols.IsRunner(method)) != Symbols.ParamKind.View) continue;
            foreach (var field in Views.Fields((INamedTypeSymbol)p.Type))
            {
                if (field.Mut)
                {
                    // As a Mut<T> parameter: the rows a vector writes are stamped per row, which no vector form does.
                    reason = $"it takes a Mut<{field.Component.Name}> through view '{p.Name}' (field '{field.Symbol.Name}')";
                    return null;
                }

                string name = inliner.Fresh($"__{p.Name}_{field.Symbol.Name}");
                inliner.Fields[field.Symbol] = name;
                added.Add(Parameter(Identifier(name)).WithType(ParseTypeName(field.Component.ToDisplayString(Symbols.Fqn)))
                    .WithModifiers(TokenList(Token(field.Writes ? SyntaxKind.RefKeyword : SyntaxKind.InKeyword))));
            }
        }

        var pass = inliner.Method(model);
        BlockSyntax? body = syntax.Body != null ? pass.Body(syntax.Body)
            : syntax.ExpressionBody != null ? Block(pass.Arrow(syntax.ExpressionBody.Expression, null)) : null;
        if (body == null || inliner.Reason != null)
        {
            reason = inliner.Reason ?? "a view is used in a way the vector loop cannot follow";
            return null;
        }

        body = inliner.WithAccessors(Inliner.WithoutInlinedLocalFunctions(body));

        // The method's parameters as written (their types bind where they did) and the view fields after them.
        var function = LocalFunctionStatement(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier(inliner.Fresh("__Desugared")))
            .WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(syntax.ParameterList.AddParameters(added.ToArray()))
            .WithBody(body);
        var replaced = syntax.WithExpressionBody(null).WithSemicolonToken(default).WithBody(Block(function));
        int position = syntax.Body?.SpanStart ?? syntax.ExpressionBody!.SpanStart;
        if (!model.TryGetSpeculativeSemanticModelForMethodBody(position, replaced, out var speculative) || speculative == null
            || replaced.Body!.Statements[0] is not LocalFunctionStatementSyntax declared
            || speculative.GetDeclaredSymbol(declared, ct) is not IMethodSymbol desugared
            || speculative.GetOperation(declared, ct) is not ILocalFunctionOperation { Body: { } operation })
        {
            reason = "its views could not be desugared";
            return null;
        }

        return (desugared, operation, speculative);
    }
}
