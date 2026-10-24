using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A lambda pass: <c>world.ForEach(static (ref Position p, in Velocity v) =&gt; …)</c> or <c>ForEachParallel</c>, with
/// the caller's values before the lambda (<c>world.ForEach(dt, static (…, float dt) =&gt; …)</c>). The generator
/// (SystemGenerator.Lambdas.cs) intercepts each call and runs the job a [ForEach] method with the lambda's parameters
/// would get, the lambda's body copied into it; the analyzer (EasyEcsAnalyzer.Lambdas.cs) reports what the generator
/// skips. Both read a call through this class, so "skipped" is exactly "reported".
/// </summary>
internal static class LambdaPass
{
    public const string ForEach = "ForEach", ForEachParallel = "ForEachParallel";

    /// <summary>The namespace of the generated interceptors: a project lists it in <c>InterceptorsNamespaces</c> (EasyEcs.props).</summary>
    public const string Namespace = "EasyEcs.Generated";

    /// <summary>A call the generator looks at, by syntax alone: <c>x.ForEach(…, (…) =&gt; …)</c> or <c>ForEachParallel</c>.</summary>
    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier.ValueText: ForEach or ForEachParallel } },
            ArgumentList.Arguments: { Count: > 0 } arguments,
        } && arguments[arguments.Count - 1].Expression is ParenthesizedLambdaExpressionSyntax;

    /// <summary>One of the World methods a lambda pass binds to: <c>ForEach</c> / <c>ForEachParallel</c> whose last parameter is a Delegate.</summary>
    public static bool IsTarget(IMethodSymbol? method) =>
        method is { Name: ForEach or ForEachParallel, IsStatic: false, Parameters.Length: > 0 } && Symbols.Named(method.ContainingType, Symbols.World)
        && method.Parameters[method.Parameters.Length - 1].Type.SpecialType == SpecialType.System_Delegate;

    /// <summary>
    /// A call of a lambda pass: the World method it binds to, its lambda (null when the body is no lambda written at the
    /// call) and the types of the values before the lambda.
    /// </summary>
    public sealed class Call(IInvocationOperation operation, IAnonymousFunctionOperation? function, AnonymousFunctionExpressionSyntax? syntax)
    {
        public IInvocationOperation Operation { get; } = operation;
        public IAnonymousFunctionOperation? Function { get; } = function;
        public AnonymousFunctionExpressionSyntax? Syntax { get; } = syntax;
        public IMethodSymbol? Lambda => Function?.Symbol;
        public bool Parallel => Operation.TargetMethod.Name == ForEachParallel;
        public System.Collections.Immutable.ImmutableArray<ITypeSymbol> Values => Operation.TargetMethod.TypeArguments;
        public InvocationExpressionSyntax Invocation => (InvocationExpressionSyntax)Operation.Syntax;
    }

    /// <summary>The lambda pass <paramref name="operation"/> calls, or null when it calls something else.</summary>
    public static Call? Read(IInvocationOperation operation)
    {
        if (!IsTarget(operation.TargetMethod) || operation.Syntax is not InvocationExpressionSyntax) return null;
        var body = operation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == operation.TargetMethod.Parameters.Length - 1)?.Value;
        while (body is IConversionOperation { IsImplicit: true } conversion) body = conversion.Operand;
        var function = (body as IDelegateCreationOperation)?.Target as IAnonymousFunctionOperation;
        return new Call(operation, function, function?.Syntax as AnonymousFunctionExpressionSyntax);
    }

    // ---------------- what the generator cannot compile (EECS095) ----------------

    /// <summary>
    /// Why the generator writes no job for <paramref name="call"/>, or null: the lambda is written at the call, captures
    /// nothing, takes what a [ForEach] method takes and the values before it, in order and of their types.
    /// <paramref name="captured"/> lists what a capturing lambda captures (its code fix passes them as values).
    /// </summary>
    public static string? Problem(Call call, SemanticModel model, System.Threading.CancellationToken ct, out List<ISymbol> captured)
    {
        captured = [];
        if (call.Function is not { } function || call.Lambda is not { } lambda || call.Syntax is not { } syntax)
            return "its last argument is no lambda written at the call: the generator compiles the lambda's body into a job, and a delegate made elsewhere has none";
        if (call.Invocation.Parent is ConditionalAccessExpressionSyntax || call.Invocation.Expression is not MemberAccessExpressionSyntax)
            return "it is not called as world.ForEach(…): write the call on the World itself";
        captured = Captured(function);
        if (captured.Count > 0)
            return $"the lambda captures {Names(captured)}: a closure and a delegate per call, and a body that runs through the delegate; make it static and pass "
                   + (captured.Any(c => c is IParameterSymbol { IsThis: true } or INamedTypeSymbol) ? "the values it reads" : Names(captured))
                   + $" before it: world.{call.Operation.TargetMethod.Name}({string.Join(", ", captured.Select(c => c.Name))}, static (…, {string.Join(", ", captured.Select(c => $"{TypeOf(c)?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {c.Name}"))}) => …)";
        if (lambda.IsAsync) return "the lambda is async: a pass runs its rows to the end before it returns";
        // An expression body that is a statement (h.Value += f.Value: a float) gives the lambda a value it drops; a block
        // that returns one, or an expression that is no statement, computes what nothing reads.
        if (!lambda.ReturnsVoid && !Discards(syntax)) return "the lambda returns a value: a pass has no use for one";
        if (syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))) return "the lambda is async";

        // A change filter looks back from a tick: the first value before the lambda, which the filter takes, not the lambda.
        bool since = Symbols.TracksSince(lambda);
        if (since && (call.Values.Length == 0 || call.Values[0].SpecialType != SpecialType.System_UInt64))
            return $"its change filter needs the tick to look back from: pass it first, world.{call.Operation.TargetMethod.Name}(since, [Changed<T>] static (…) => …), a ulong such as a system's LastRunTick + 1";
        int components = 0, values = since ? 1 : 0;
        foreach (var p in lambda.Parameters)
        {
            var kind = Symbols.Classify(p, true);
            switch (kind)
            {
                case Symbols.ParamKind.Invalid:
                    return $"parameter '{p.Name}' is no component (by ref or in), singleton, value or parameter of EasyEcs";
                case Symbols.ParamKind.Optional or Symbols.ParamKind.Tile or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Entities:
                    return $"parameter '{p.Name}' is a {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}, which a lambda pass does not take: write a [ForEach] method";
                case Symbols.ParamKind.Context when p.RefKind is RefKind.Ref or RefKind.Out:
                    return $"it takes '{p.Name}' by ref, but a lambda pass copies its values into the job: pass it by value, and collect results in a Reduce<T> or an Emit<T>";
                case Symbols.ParamKind.Context:
                    if (values >= call.Values.Length)
                        return $"parameter '{p.Name}' is a value the call does not pass: pass it before the lambda (world.{call.Operation.TargetMethod.Name}({p.Name}, static (…) => …))";
                    if (!SymbolEqualityComparer.Default.Equals(p.Type, call.Values[values]))
                        return $"value {values + 1} before the lambda is a {call.Values[values].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}, but parameter '{p.Name}' is a {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}";
                    values++;
                    break;
                case Symbols.ParamKind.Component or Symbols.ParamKind.Mutable:
                    components++;
                    break;
            }

            if (Symbols.ContainsTypeParameter(p.Type)) return $"parameter '{p.Name}' is of a type parameter: the generated job names concrete types";
            if (Symbols.Hidden(p.Type) is { } hidden) return $"parameter '{p.Name}' is of {hidden.Name}, which code outside its type cannot name";
        }

        if (values < call.Values.Length)
            return $"the call passes {call.Values.Length} value{(call.Values.Length == 1 ? "" : "s")} before the lambda, which takes {values - (since ? 1 : 0)}"
                   + (since ? " after the tick" : "") + ": each value is a parameter of the lambda, in order";
        foreach (var attribute in lambda.GetAttributes())
            if (attribute.AttributeClass is { } type && !IsFilter(type))
                return $"[{type.Name.Replace("Attribute", "")}] does not apply to a lambda pass: its attributes are the filters of a [ForEach] method";
        if (!Symbols.ComponentCountValid(lambda, components))
            return $"the lambda takes {components} components: a pass takes 1 to {Symbols.MaxComponents}, or none with a [With<T>] filter";
        if (call.Parallel)
        {
            if (!IsStable(function)) return "it runs in parallel but can change structure (it takes the World or changes an entity): make it a ForEach";
            if (lambda.Parameters.FirstOrDefault(p => Symbols.Classify(p) == Symbols.ParamKind.Singleton && p.RefKind == RefKind.Ref) is { } singleton)
                return $"'ref {singleton.Type.Name} {singleton.Name}' writes a singleton, which every chunk would race on: take it as 'in', and collect results in a Reduce<T> or an Emit<T>";
        }

        return model.GetInterceptableLocation(call.Invocation, ct) == null ? "the compiler cannot intercept this call" : null;
    }

    private static bool IsFilter(INamedTypeSymbol type)
    {
        var definition = type.IsGenericType ? type.ConstructedFrom : type;
        return Symbols.Named(definition, Symbols.WithAttribute) || Symbols.Named(definition, Symbols.WithoutAttribute)
            || Symbols.Named(definition, Symbols.ChangedAttribute) || Symbols.Named(definition, Symbols.RemovedAttribute)
            || Symbols.AnyFilter(type) != null; // WithAny / ChangedAny / RemovedAny of 2..9 components
    }

    private static string Names(List<ISymbol> symbols) =>
        string.Join(", ", symbols.Select(s => s is IParameterSymbol { IsThis: true } || s is INamedTypeSymbol ? "'this'" : $"'{s.Name}'"));

    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        _ => null,
    };

    /// <summary>
    /// The locals and parameters of the code around the lambda that its body uses, and <c>this</c> (as the containing
    /// type) when it reads an instance member: what the compiler would capture into a closure, in order of first use.
    /// </summary>
    public static List<ISymbol> Captured(IAnonymousFunctionOperation function)
    {
        var captured = new List<ISymbol>();
        var lambda = function.Symbol;
        foreach (var op in function.Body.Descendants())
        {
            ISymbol? symbol = op switch
            {
                ILocalReferenceOperation local when !Inside(local.Local.ContainingSymbol, lambda) => local.Local,
                IParameterReferenceOperation parameter when !Inside(parameter.Parameter.ContainingSymbol, lambda) => parameter.Parameter,
                IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } self => self.Type,
                _ => null,
            };
            if (symbol != null && !captured.Contains(symbol, SymbolEqualityComparer.Default)) captured.Add(symbol);
        }

        return captured;
    }

    /// <summary>Whether <paramref name="symbol"/> is the lambda or a function declared inside it.</summary>
    private static bool Inside(ISymbol? symbol, IMethodSymbol lambda)
    {
        for (; symbol != null; symbol = symbol.ContainingSymbol)
            if (SymbolEqualityComparer.Default.Equals(symbol, lambda)) return true;
        return false;
    }

    /// <summary>
    /// The lambda cannot change the world's structure: it takes no World, and calls no structural member of an
    /// Entity (or a generated writer) in its body. Its job gets INoStructuralChanges and may run in parallel.
    /// </summary>
    public static bool IsStable(IAnonymousFunctionOperation function) =>
        !function.Symbol.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.World)
        && !function.Body.Descendants().Any(op => op is IInvocationOperation { TargetMethod: var m } && Symbols.IsStructural(m));

    // ---------------- the body as generated code ----------------

    /// <summary>
    /// The first name in the lambda's body that code in another file cannot reach (a private member of the type around
    /// the call, a file-local type), or null: the generator then runs the lambda through its delegate (EECS096).
    /// Names the compilation cannot resolve (another generator's members, while the generator runs) are left alone.
    /// </summary>
    public static string? Unreachable(Call call, SemanticModel model, System.Threading.CancellationToken ct) => Unreachable(call, model, ct, out _);

    /// <inheritdoc cref="Unreachable(Call, SemanticModel, System.Threading.CancellationToken)"/>
    /// <param name="blocking">The symbol code outside its type cannot reach (EECS096's fix makes a private member internal).</param>
    public static string? Unreachable(Call call, SemanticModel model, System.Threading.CancellationToken ct, out ISymbol? blocking)
    {
        blocking = null;
        var lambda = call.Lambda!;
        foreach (var name in call.Syntax!.Body.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name, ct).Symbol;
            if (symbol is null or ILocalSymbol or IParameterSymbol or ILabelSymbol or IRangeVariableSymbol or IDiscardSymbol or INamespaceSymbol
                || symbol is IFieldSymbol { HasConstantValue: true } || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }
                || Inside(symbol.ContainingSymbol, lambda))
                continue;
            if (symbol is ITypeParameterSymbol) return $"it names type parameter {symbol.Name}";
            if ((blocking = Blocking(symbol)) != null) return $"it names {Describe(blocking)}";
        }

        return null;
    }

    /// <summary>The symbol of <paramref name="symbol"/>'s chain (itself or a containing type) that another file cannot reach, or null.</summary>
    private static ISymbol? Blocking(ISymbol symbol)
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

    private static string Describe(ISymbol symbol) =>
        $"{(symbol.DeclaredAccessibility == Accessibility.Private ? "private " : symbol is INamedTypeSymbol { IsFileLocal: true } ? "file-local " : "protected ")}"
        + $"{symbol.Kind.ToString().ToLowerInvariant().Replace("namedtype", "type")} '{symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'"
        + (symbol.ContainingType is { } type ? $" of {type.Name}" : "")
        + ", which code outside its type (or file) cannot reach";

    /// <summary>
    /// Whether the lambda's body is an expression that may stand as a statement (an assignment, an increment, a call),
    /// whose value the pass drops like a statement does.
    /// </summary>
    public static bool Discards(AnonymousFunctionExpressionSyntax syntax) => syntax.Body is
        AssignmentExpressionSyntax or InvocationExpressionSyntax or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
        or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression };

    /// <summary>
    /// The lambda's body as the body of a static method in a generated file: every name of a type, a namespace or a static
    /// member qualified (<c>global::Game.Rules.Limit</c>), constants written as their values (a private one too), the
    /// rest as written. Call only when <see cref="Unreachable"/> found nothing. A block keeps its braces; an expression
    /// becomes <c>=&gt; expression;</c>.
    /// </summary>
    public static string Copy(Call call, SemanticModel model, System.Threading.CancellationToken ct)
    {
        var body = (CSharpSyntaxNode)new Qualifier(model, call.Lambda!, ct).Visit(call.Syntax!.Body)!;
        return body is BlockSyntax block ? block.ToFullString().Trim() : $"=> {body.ToFullString().Trim()};";
    }

    /// <summary>The using directives of the file of the call that bring extension members into scope (aliases are qualified away).</summary>
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

    /// <summary>Rewrites the names <see cref="Copy"/> qualifies.</summary>
    private sealed class Qualifier(SemanticModel model, IMethodSymbol lambda, System.Threading.CancellationToken ct) : CSharpSyntaxRewriter
    {
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
                    when symbol.IsStatic && !Inside(symbol.ContainingSymbol, lambda) && symbol.ContainingType is { } owner:
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

    /// <summary>A constant as C# writes it, or null when it has no literal form here.</summary>
    private static string? Constant(object? value, ITypeSymbol type)
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

    /// <summary>
    /// The lambda's body scans: it loops or calls (a Scratch method that does runs out of line, as a [ForEach] method's,
    /// SystemGenerator.ForEachModel.OutOfLine).
    /// </summary>
    public static bool LoopsOrCalls(AnonymousFunctionExpressionSyntax syntax) =>
        syntax.Body.DescendantNodesAndSelf().Any(static n => n is ForStatementSyntax or ForEachStatementSyntax
            or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax or InvocationExpressionSyntax);

    /// <summary>A parameter as the generated code declares it: its modifier and its type (<c>ref global::Position</c>).</summary>
    public static string Declared(IParameterSymbol p) =>
        (p.RefKind switch { RefKind.Ref => "ref ", RefKind.In => "in ", RefKind.RefReadOnlyParameter => "ref readonly ", _ => "" })
        + p.Type.ToDisplayString(Symbols.Fqn);

    /// <summary>"file.cs(12,9)" of a location, for a comment in the generated code.</summary>
    public static string Where(Location location)
    {
        var span = location.GetLineSpan();
        return $"{System.IO.Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }
}
