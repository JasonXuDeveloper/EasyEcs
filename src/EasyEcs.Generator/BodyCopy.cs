using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// User code copied into a generated file: a lambda pass's body (SystemGenerator.Lambdas.cs) or a [ForEach] method's
/// body whose element loops the generator rewrites (Kernel.Loops.cs). The copy binds in another type and file, so every
/// name of a type, a namespace or a static member is qualified, constants are written as their values (a private one
/// too) and the rest is left as written; the file gets the using directives the body's extension calls need. A body
/// that names what code outside its type cannot reach is not copied (<see cref="Unreachable"/>).
/// </summary>
internal static class BodyCopy
{
    /// <summary>
    /// <paramref name="body"/> (of <paramref name="scope"/>, a method or a lambda) as text for a generated file, names
    /// qualified (<see cref="BodyCopy"/>). <paramref name="replace"/> may give the text of any statement instead
    /// (called with the statement as written and as qualified; null keeps the qualified one).
    /// </summary>
    public static string Copy(SyntaxNode body, IMethodSymbol scope, SemanticModel model,
        System.Func<SyntaxNode, SyntaxNode, string?>? replace, System.Threading.CancellationToken ct) =>
        new Qualifier(model, scope, replace, ct).Visit(body)!.ToFullString();

    /// <summary>The using directives of the file of <paramref name="node"/> that bring extension members into scope (aliases are qualified away).</summary>
    public static IEnumerable<string> Usings(SyntaxNode node)
    {
        var usings = new List<string>();
        foreach (var unit in node.Ancestors())
        {
            var directives = unit switch
            {
                CompilationUnitSyntax compilation => compilation.Usings,
                BaseNamespaceDeclarationSyntax ns => ns.Usings,
                _ => default,
            };
            foreach (var u in directives)
                if (u.Alias == null && u.GlobalKeyword.IsKind(SyntaxKind.None) && u.NamespaceOrType is { } name)
                    usings.Add($"using {(u.StaticKeyword.IsKind(SyntaxKind.None) ? "" : "static ")}{name.ToString()};");
            if (unit is BaseNamespaceDeclarationSyntax enclosing) // the namespace and those around it, in scope at the call
            {
                var parts = enclosing.Name.ToString().Split('.');
                for (int i = 1; i <= parts.Length; i++) usings.Add($"using {string.Join(".", parts.Take(i))};");
            }
        }

        return usings;
    }

    /// <summary>
    /// The first name in <paramref name="body"/> (of <paramref name="scope"/>) that code in another file cannot reach (a
    /// private member of the type around it, a file-local type, a type parameter), or null. Names the compilation cannot
    /// resolve (another generator's members, while the generator runs) are left alone.
    /// </summary>
    /// <param name="blocking">The symbol code outside its type cannot reach (EECS096's fix makes a private member internal).</param>
    public static string? Unreachable(SyntaxNode body, IMethodSymbol scope, SemanticModel model, System.Threading.CancellationToken ct, out ISymbol? blocking)
    {
        blocking = null;
        foreach (var name in body.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name, ct).Symbol;
            if (symbol is null or ILocalSymbol or IParameterSymbol or ILabelSymbol or IRangeVariableSymbol or IDiscardSymbol or INamespaceSymbol
                || symbol is IFieldSymbol { HasConstantValue: true } || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }
                || Inside(symbol.ContainingSymbol, scope))
                continue;
            if (symbol is ITypeParameterSymbol) return $"it names type parameter {symbol.Name}";
            if ((blocking = Blocking(symbol)) != null) return $"it names {Describe(blocking)}";
        }

        return null;
    }

    /// <summary>The symbol of <paramref name="symbol"/>'s chain (itself or a containing type) that another file cannot reach, or null.</summary>
    internal static ISymbol? Blocking(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } extension }) symbol = extension;
        if (symbol is IMethodSymbol { IsGenericMethod: true } generic) symbol = generic.ConstructedFrom;
        for (var s = symbol; s != null && s is not INamespaceSymbol; s = s.ContainingSymbol)
        {
            if (s is INamedTypeSymbol { IsFileLocal: true }) return s;
            if (s.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal) return s;
            if (s is INamedTypeSymbol { IsGenericType: true } named)
                foreach (var argument in named.TypeArguments)
                    if (argument is INamedTypeSymbol && Blocking(argument) is { } inner) return inner;
        }

        return null;
    }

    internal static string Describe(ISymbol symbol) =>
        $"{(symbol.DeclaredAccessibility == Accessibility.Private ? "private " : symbol is INamedTypeSymbol { IsFileLocal: true } ? "file-local " : "protected ")}"
        + $"{symbol.Kind.ToString().ToLowerInvariant().Replace("namedtype", "type")} '{symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'"
        + (symbol.ContainingType is { } type ? $" of {type.Name}" : "")
        + ", which code outside its type (or file) cannot reach";

    /// <summary>Whether <paramref name="symbol"/> is <paramref name="scope"/> or declared inside it (a local function, a parameter).</summary>
    internal static bool Inside(ISymbol? symbol, IMethodSymbol scope)
    {
        for (; symbol != null; symbol = symbol.ContainingSymbol)
            if (SymbolEqualityComparer.Default.Equals(symbol, scope)) return true;
        return false;
    }

    /// <summary>A constant as C# writes it, or null when it has no literal form here.</summary>
    internal static string? Constant(object? value, ITypeSymbol type)
    {
        string? text = value switch
        {
            null => type.IsReferenceType ? $"default({type.ToDisplayString(Symbols.Fqn)})" : null,
            string s => SymbolDisplay.FormatLiteral(s, true),
            char c => SymbolDisplay.FormatLiteral(c, true),
            bool b => b ? "true" : "false",
            float f when float.IsNaN(f) => "float.NaN",
            float f when float.IsInfinity(f) => f > 0 ? "float.PositiveInfinity" : "float.NegativeInfinity",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d when double.IsNaN(d) => "double.NaN",
            double d when double.IsInfinity(d) => d > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
            System.IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => null,
        };
        return text == null ? null : $"(({type.ToDisplayString(Symbols.Fqn)})({text}))";
    }

    /// <summary>Rewrites the names <see cref="Copy"/> qualifies, and the statements it replaces.</summary>
    private sealed class Qualifier(SemanticModel model, IMethodSymbol scope, System.Func<SyntaxNode, SyntaxNode, string?>? replace,
        System.Threading.CancellationToken ct) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            var rewritten = base.Visit(node);
            if (replace == null || node is not StatementSyntax || rewritten == null || replace(node, rewritten) is not { } text) return rewritten;
            return SyntaxFactory.ParseStatement(text).WithLeadingTrivia(rewritten.GetLeadingTrivia()).WithTrailingTrivia(rewritten.GetTrailingTrivia());
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            Qualified(node, null) is { } qualified ? qualified.WithTriviaFrom(node) : base.VisitIdentifierName(node);

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            var rewritten = (GenericNameSyntax)base.VisitGenericName(node)!;
            return Qualified(node, rewritten.TypeArgumentList.ToString()) is { } qualified ? qualified.WithTriviaFrom(node) : rewritten;
        }

        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node) => node; // global::… as written

        /// <summary>The qualified form of a leftmost name, or null to keep it.</summary>
        private ExpressionSyntax? Qualified(SimpleNameSyntax node, string? typeArguments)
        {
            if (!Leftmost(node) || node is IdentifierNameSyntax { IsVar: true } && model.GetSymbolInfo(node, ct).Symbol is ITypeSymbol) return null;
            var symbol = model.GetSymbolInfo(node, ct).Symbol;
            switch (symbol)
            {
                case INamespaceSymbol ns when !ns.IsGlobalNamespace:
                    return SyntaxFactory.ParseName(ns.ToDisplayString(Symbols.Fqn));
                case INamedTypeSymbol type when typeArguments == null:
                    return SyntaxFactory.ParseName(type.ToDisplayString(Symbols.Fqn));
                case INamedTypeSymbol type:
                    return SyntaxFactory.ParseName(Outer(type.OriginalDefinition) + type.Name + typeArguments);
                case IFieldSymbol { HasConstantValue: true } constant when constant.Type is { } t && Constant(constant.ConstantValue, t) is { } value:
                    return SyntaxFactory.ParseExpression(value);
                case IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }
                    when symbol.IsStatic && !Inside(symbol.ContainingSymbol, scope) && symbol.ContainingType is { } owner:
                    return SyntaxFactory.ParseExpression($"{owner.ToDisplayString(Symbols.Fqn)}.{Code.Escape(symbol.Name)}{typeArguments}");
                default:
                    return null;
            }
        }

        /// <summary>The containing types and namespace of a type as a prefix (<c>global::Ns.Outer.</c>).</summary>
        private static string Outer(INamedTypeSymbol type) => type.ContainingType is { } outer
            ? outer.ToDisplayString(Symbols.Fqn) + "."
            : type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString(Symbols.Fqn) + "." : "global::";

        /// <summary>A name that starts an expression or a type: not a member after a dot, a named argument or an initialized member.</summary>
        private static bool Leftmost(SimpleNameSyntax node) => node.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Expression == node,
            QualifiedNameSyntax qualified => qualified.Left == node,
            MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax or AliasQualifiedNameSyntax or ExplicitInterfaceSpecifierSyntax => false,
            AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment => assignment.Left != node,
            _ => true,
        };
    }
}
