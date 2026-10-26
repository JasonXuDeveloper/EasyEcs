using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace EasyEcs.Generator;

/// <summary>
/// A [ForEach] method that takes views (<c>[View]</c> ref structs) is translated as the method a user would write
/// without them: each view field becomes a component parameter (<c>ref Health __fighter_Health</c>, appended after the
/// method's own parameters, so the others keep their positions and job fields), each use of a field that parameter,
/// and each call of a view method or read of a view property is inlined (its parameters bound to locals of their
/// declared types, its names qualified, its locals renamed). The desugared method replaces the original in a copy of
/// its syntax tree, bound in a copy of the compilation, and the vector loop is that method's: a view costs what the
/// refs written by hand cost, and the reason a method stays scalar is the reason its desugared form would.
/// </summary>
internal sealed partial class Kernel
{
    private const int MaxInlining = 8;

    /// <summary>
    /// The desugared form of <paramref name="method"/> (<see cref="Kernel"/> remarks), or null with the reason a view
    /// keeps it scalar (a view used as a value, a view method that returns early or takes a ref).
    /// </summary>
    private static (IMethodSymbol Method, MethodDeclarationSyntax Syntax, SemanticModel Model)? Desugar(IMethodSymbol method,
        MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct, out string? reason)
    {
        reason = null;
        if (method.PartialDefinitionPart != null || method.PartialImplementationPart != null)
        {
            reason = "it is a partial method that takes a view";
            return null;
        }

        var used = new HashSet<string>(syntax.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText));
        var inliner = new ViewInliner(used, ct);
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
        BlockSyntax? body = syntax.Body != null ? pass.Visit(syntax.Body) as BlockSyntax
            : syntax.ExpressionBody != null && pass.Statement(syntax.ExpressionBody.Expression) is { } statement ? Block(statement) : null;
        if (body == null || inliner.Reason != null)
        {
            reason = inliner.Reason ?? "a view is used in a way the vector loop cannot follow";
            return null;
        }

        var annotation = new SyntaxAnnotation("EasyEcs.Desugared");
        var desugared = syntax.WithParameterList(syntax.ParameterList.AddParameters(added.ToArray()))
            .WithExpressionBody(null).WithSemicolonToken(default).WithBody(body).WithAdditionalAnnotations(annotation);
        var tree = syntax.SyntaxTree;
        var copy = tree.WithRootAndOptions(tree.GetRoot(ct).ReplaceNode(syntax, desugared), tree.Options);
        var copyModel = model.Compilation.ReplaceSyntaxTree(tree, copy).GetSemanticModel(copy);
        var copySyntax = copy.GetRoot(ct).GetAnnotatedNodes(annotation).OfType<MethodDeclarationSyntax>().First();
        if (copyModel.GetDeclaredSymbol(copySyntax, ct) is not { } copyMethod)
        {
            reason = "its views could not be desugared";
            return null;
        }

        return (copyMethod, copySyntax, copyModel);
    }

    /// <summary>
    /// Rewrites a body that uses views into one that uses the views' fields as parameters: the [ForEach] method's own
    /// body (where a view is a parameter) and, inlined, the bodies of view members (where the view is <c>this</c>).
    /// </summary>
    private sealed class ViewInliner(HashSet<string> used, System.Threading.CancellationToken ct)
    {
        /// <summary>Each view field, as the name of its parameter.</summary>
        public Dictionary<IFieldSymbol, string> Fields { get; } = new(SymbolEqualityComparer.Default);

        /// <summary>Why the views cannot be desugared (the first construct met), or null.</summary>
        public string? Reason { get; private set; }

        private int _depth;

        /// <summary>A name no identifier of the method uses yet.</summary>
        public string Fresh(string stem)
        {
            string name = stem;
            while (!used.Add(name)) name = "_" + name;
            return name;
        }

        /// <summary>The pass over the [ForEach] method's own body.</summary>
        public Rewriter Method(SemanticModel model) => new(this, new Scope(model, null, new(SymbolEqualityComparer.Default)));

        private void Fail(string reason) => Reason ??= reason;

        /// <summary>
        /// Where a body comes from: its tree's model, the view member it belongs to (null for the [ForEach] method), and
        /// what its parameters and locals become.
        /// </summary>
        public sealed record Scope(SemanticModel Model, ISymbol? Member, Dictionary<ISymbol, ExpressionSyntax> Names);

        /// <summary>One body's rewrite. Every node it binds is an original node of <see cref="Scope.Model"/>'s tree.</summary>
        public sealed class Rewriter(ViewInliner owner, Scope scope) : CSharpSyntaxRewriter
        {
            private bool Inlined => scope.Member != null;

            private ISymbol? Bound(SyntaxNode node) => scope.Model.GetSymbolInfo(node, owner.Token).Symbol;

            /// <summary>The receiver is the view: <c>this</c> (or none) inside a view member, a view parameter in the method.</summary>
            private bool IsView(ExpressionSyntax? receiver)
            {
                if (receiver == null || receiver is ThisExpressionSyntax) return Inlined;
                return !Inlined && Bound(receiver) is IParameterSymbol p && Views.IsView(p.Type);
            }

            /// <summary>A statement of one expression: a void view method's call inlined as a block, anything else rewritten.</summary>
            public StatementSyntax? Statement(ExpressionSyntax expression)
            {
                if (expression is InvocationExpressionSyntax call && ViewMethod(call) is { ReturnsVoid: true } method) return InlineStatements(method, call);
                return Visit(expression) is ExpressionSyntax rewritten ? ExpressionStatement(rewritten) : null;
            }

            public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node) =>
                Statement(node.Expression)?.WithTriviaFrom(node);

            public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                if (IsView(node.Expression) && Bound(node) is { } member && Member(member) is { } replaced) return replaced.WithTriviaFrom(node);
                return base.VisitMemberAccessExpression(node);
            }

            /// <summary>A view member reached from the view: a field's parameter, a property's inlined getter (null: not one).</summary>
            private ExpressionSyntax? Member(ISymbol member) => member switch
            {
                IFieldSymbol field when owner.Fields.TryGetValue(field, out var name) => IdentifierName(name),
                IPropertySymbol { IsStatic: false } property when Views.IsView(property.ContainingType) =>
                    InlineExpression(property, property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(owner.Token), default),
                _ => null,
            };

            public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (ViewMethod(node) is not { } method) return base.VisitInvocationExpression(node);
                if (method.ReturnsVoid)
                {
                    owner.Fail($"it calls view method {method.ContainingType.Name}.{method.Name} inside an expression");
                    return node;
                }

                return InlineExpression(method, method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(owner.Token), node.ArgumentList.Arguments)?.WithTriviaFrom(node) ?? node;
            }

            /// <summary>An instance method of a view called on the view, or null.</summary>
            private IMethodSymbol? ViewMethod(InvocationExpressionSyntax call)
            {
                ExpressionSyntax? receiver;
                if (call.Expression is MemberAccessExpressionSyntax access) receiver = access.Expression;
                else if (call.Expression is SimpleNameSyntax) receiver = null;
                else return null;
                return Bound(call) is IMethodSymbol { IsStatic: false } method && Views.IsView(method.ContainingType) && IsView(receiver) ? method : null;
            }

            public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
            {
                if (Inlined) owner.Fail($"view member {scope.Member!.Name} uses the view as a value ('this')");
                return node;
            }

            public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) => Name(node) ?? base.VisitIdentifierName(node);

            public override SyntaxNode? VisitGenericName(GenericNameSyntax node) => Name(node) ?? base.VisitGenericName(node);

            /// <summary>
            /// A simple name. In the method, a view parameter used other than for a field, property or method fails. In
            /// an inlined body: a view field or property read through <c>this</c>, a parameter or local of the member, a
            /// type or static member (qualified: the member's file may import other namespaces). Null: left as it is.
            /// </summary>
            private SyntaxNode? Name(SimpleNameSyntax node)
            {
                if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node || node.Parent is QualifiedNameSyntax qualified && qualified.Right == node)
                    return null; // the member of something: its receiver decides
                var symbol = Bound(node);
                if (symbol == null) return null;
                if (!Inlined)
                {
                    if (symbol is IParameterSymbol p && Views.IsView(p.Type))
                        owner.Fail(node.Parent is MemberAccessExpressionSyntax
                            ? $"it reaches a member of view '{p.Name}' the vector loop cannot follow"
                            : $"it uses view '{p.Name}' as a value");
                    return null;
                }

                if (scope.Names.TryGetValue(symbol, out var replaced)) return replaced.WithTriviaFrom(node);
                switch (symbol)
                {
                    case ITypeSymbol type:
                        return ParseTypeName(type.ToDisplayString(Symbols.Fqn)).WithTriviaFrom(node);
                    case INamespaceSymbol ns:
                        return ParseName(ns.ToDisplayString(Symbols.Fqn)).WithTriviaFrom(node);
                    case IMethodSymbol { MethodKind: MethodKind.LocalFunction }:
                        break;
                    case { IsStatic: true } or IFieldSymbol { IsConst: true }:
                        var name = node is GenericNameSyntax generic ? (SimpleNameSyntax)base.VisitGenericName(generic)! : node.WithoutTrivia();
                        return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            ParseExpression(symbol.ContainingType.ToDisplayString(Symbols.Fqn)), name).WithTriviaFrom(node);
                    default:
                        if (Member(symbol) is { } member) return member.WithTriviaFrom(node);
                        break;
                }

                owner.Fail($"view member {scope.Member!.Name} uses '{node.Identifier.ValueText}', which the vector loop cannot follow");
                return null;
            }

            public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
            {
                if (!Inlined || scope.Model.GetDeclaredSymbol(node, owner.Token) is not ILocalSymbol local) return base.VisitVariableDeclarator(node);
                string name = owner.Fresh($"__{local.Name}");
                scope.Names[local] = IdentifierName(name);
                return ((VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!).WithIdentifier(Identifier(name));
            }

            public override SyntaxNode? VisitSingleVariableDesignation(SingleVariableDesignationSyntax node)
            {
                if (!Inlined || scope.Model.GetDeclaredSymbol(node, owner.Token) is not ILocalSymbol local) return base.VisitSingleVariableDesignation(node);
                string name = owner.Fresh($"__{local.Name}");
                scope.Names[local] = IdentifierName(name);
                return node.WithIdentifier(Identifier(name));
            }

            public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
            {
                if (Inlined) owner.Fail($"view member {scope.Member!.Name} returns before its end");
                return base.VisitReturnStatement(node);
            }

            public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => Refuse(node, "declares a local function");

            public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => Refuse(node, "declares a lambda");

            public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => Refuse(node, "declares a lambda");

            private SyntaxNode Refuse(SyntaxNode node, string what)
            {
                if (Inlined) owner.Fail($"view member {scope.Member!.Name} {what}");
                return node;
            }

            /// <summary>
            /// A void view method called as a statement: a block that declares its parameters as locals of their types,
            /// bound to the arguments (rewritten here, in order, as the call evaluates them), then runs its statements (a
            /// final <c>return;</c> dropped).
            /// </summary>
            private StatementSyntax? InlineStatements(IMethodSymbol method, InvocationExpressionSyntax call)
            {
                if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(owner.Token) is not MethodDeclarationSyntax declaration
                    || declaration.Body == null && declaration.ExpressionBody == null)
                {
                    owner.Fail($"view method {method.ContainingType.Name}.{method.Name} has no body");
                    return null;
                }

                if (!Enter(method)) return null;
                try
                {
                    var inner = new Scope(Symbols.ModelOf(declaration.SyntaxTree, scope.Model), method, new(SymbolEqualityComparer.Default));
                    var statements = new List<StatementSyntax>();
                    if (!Bind(method, call.ArgumentList.Arguments, inner, statements)) return null;
                    var pass = new Rewriter(owner, inner);
                    if (declaration.ExpressionBody is { } arrow)
                    {
                        if (pass.Statement(arrow.Expression) is not { } statement) return null;
                        statements.Add(statement);
                    }
                    else
                    {
                        var body = declaration.Body!.Statements;
                        int count = body.Count > 0 && body[body.Count - 1] is ReturnStatementSyntax { Expression: null } ? body.Count - 1 : body.Count;
                        for (int i = 0; i < count; i++)
                            if (pass.Visit(body[i]) is StatementSyntax rewritten) statements.Add(rewritten);
                    }

                    return Block(statements);
                }
                finally
                {
                    owner._depth--;
                }
            }

            /// <summary>
            /// A value read from the view: a method or getter whose body is one expression (<c>=&gt; expr</c> or
            /// <c>{ return expr; }</c>), its parameters replaced by the arguments converted to their types (each argument
            /// free of calls and writes, since it may be evaluated more than once), in parentheses.
            /// </summary>
            private ExpressionSyntax? InlineExpression(ISymbol member, SyntaxNode? declaration, SeparatedSyntaxList<ArgumentSyntax> arguments)
            {
                ExpressionSyntax? expression = declaration switch
                {
                    MethodDeclarationSyntax m => m.ExpressionBody?.Expression ?? Single(m.Body),
                    PropertyDeclarationSyntax p => p.ExpressionBody?.Expression
                        ?? (p.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) is { } get
                            ? get.ExpressionBody?.Expression ?? Single(get.Body) : null),
                    _ => null,
                };
                if (expression == null)
                {
                    owner.Fail($"view member {member.ContainingType.Name}.{member.Name} is not a single expression");
                    return null;
                }

                if (!Enter(member)) return null;
                try
                {
                    var inner = new Scope(Symbols.ModelOf(expression.SyntaxTree, scope.Model), member, new(SymbolEqualityComparer.Default));
                    if (member is IMethodSymbol method && !Bind(method, arguments, inner, null)) return null;
                    return new Rewriter(owner, inner).Visit(expression) is ExpressionSyntax rewritten ? ParenthesizedExpression(rewritten) : null;
                }
                finally
                {
                    owner._depth--;
                }
            }

            private static ExpressionSyntax? Single(BlockSyntax? body) =>
                body is { Statements.Count: 1 } && body.Statements[0] is ReturnStatementSyntax { Expression: { } value } ? value : null;

            private bool Enter(ISymbol member)
            {
                if (++owner._depth <= MaxInlining) return true;
                owner._depth--;
                owner.Fail($"view members call each other more than {MaxInlining} deep ({member.Name})");
                return false;
            }

            /// <summary>
            /// The method's parameters bound to the call's arguments (rewritten in this scope): locals of the parameters'
            /// types (<paramref name="statements"/> given), or the arguments themselves where they are free of calls and
            /// writes. Ref, out, params and optional parameters and named arguments keep the method scalar.
            /// </summary>
            private bool Bind(IMethodSymbol method, SeparatedSyntaxList<ArgumentSyntax> arguments, Scope inner, List<StatementSyntax>? statements)
            {
                if (arguments.Count != method.Parameters.Length || arguments.Any(a => a.NameColon != null || !a.RefKindKeyword.IsKind(SyntaxKind.None)))
                {
                    owner.Fail($"it calls view member {method.ContainingType.Name}.{method.Name} with named, omitted or ref arguments");
                    return false;
                }

                for (int i = 0; i < arguments.Count; i++)
                {
                    var parameter = method.Parameters[i];
                    if (parameter.RefKind != RefKind.None || parameter.IsParams)
                    {
                        owner.Fail($"view member {method.ContainingType.Name}.{method.Name} takes '{parameter.Name}' by {(parameter.IsParams ? "params" : parameter.RefKind.ToString().ToLowerInvariant())}");
                        return false;
                    }

                    if (Visit(arguments[i].Expression) is not ExpressionSyntax value) return false;
                    var type = ParseTypeName(parameter.Type.ToDisplayString(Symbols.Fqn));
                    if (statements != null && Plain(arguments[i].Expression, value, parameter, inner))
                    {
                        // The argument itself, as the method written by hand would read it: a local would hide from the
                        // vector loop that each field of a component gets the same operation (flat lanes).
                        inner.Names[parameter] = ParenthesizedExpression(value);
                        continue;
                    }

                    if (statements != null)
                    {
                        string name = owner.Fresh($"__{parameter.Name}");
                        statements.Add(LocalDeclarationStatement(VariableDeclaration(type,
                            SingletonSeparatedList(VariableDeclarator(Identifier(name)).WithInitializer(EqualsValueClause(value))))));
                        inner.Names[parameter] = IdentifierName(name);
                        continue;
                    }

                    if (value.DescendantNodesAndSelf().Any(n => n is AssignmentExpressionSyntax or InvocationExpressionSyntax or PostfixUnaryExpressionSyntax
                            || n.IsKind(SyntaxKind.PreIncrementExpression) || n.IsKind(SyntaxKind.PreDecrementExpression)))
                    {
                        owner.Fail($"it passes a call or a write to view member {method.ContainingType.Name}.{method.Name}: assign it to a local first");
                        return false;
                    }

                    inner.Names[parameter] = ParenthesizedExpression(CastExpression(type, ParenthesizedExpression(value)));
                }

                return true;
            }

            /// <summary>
            /// An argument a void method's body may read in place of its parameter: a name, a literal or a chain of member
            /// reads of one, of exactly the parameter's type, for a parameter the body never writes.
            /// </summary>
            private bool Plain(ExpressionSyntax argument, ExpressionSyntax value, IParameterSymbol parameter, Scope inner)
            {
                static bool Reads(ExpressionSyntax e) => e switch
                {
                    IdentifierNameSyntax or LiteralExpressionSyntax => true,
                    MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } access => Reads(access.Expression),
                    ParenthesizedExpressionSyntax parenthesized => Reads(parenthesized.Expression),
                    _ => false,
                };

                if (!Reads(value) || !SymbolEqualityComparer.Default.Equals(scope.Model.GetTypeInfo(argument, owner.Token).Type, parameter.Type)) return false;
                if (parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(owner.Token).Parent?.Parent is not MethodDeclarationSyntax declaration) return false;
                foreach (var name in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (name.Identifier.ValueText != parameter.Name) continue;
                    // The value read or written: the name, or a field of it (a struct parameter's field is the parameter).
                    SyntaxNode node = name;
                    while (node.Parent is ParenthesizedExpressionSyntax || node.Parent is MemberAccessExpressionSyntax access && access.Expression == node)
                        node = node.Parent;
                    bool written = node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node
                        || node.Parent is PostfixUnaryExpressionSyntax
                        || node.Parent.IsKind(SyntaxKind.PreIncrementExpression) || node.Parent.IsKind(SyntaxKind.PreDecrementExpression)
                        || node.Parent is ArgumentSyntax argumentOf && !argumentOf.RefKindKeyword.IsKind(SyntaxKind.None)
                        || node != name && node.Parent is InvocationExpressionSyntax && parameter.Type.IsValueType; // a method of it may write it
                    if (written && SymbolEqualityComparer.Default.Equals(inner.Model.GetSymbolInfo(name, owner.Token).Symbol, parameter)) return false;
                }

                return true;
            }
        }

        public System.Threading.CancellationToken Token => ct;
    }
}
