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
/// the caller's values before the lambda (<c>world.ForEach(dt, static (…, float dt) =&gt; …)</c>), or <c>ParallelFor</c>
/// over a buffer's elements (<c>world.ParallelFor(ref hits.Items, keep, static (ref Hit hit, float keep) =&gt; …)</c>,
/// an <c>IForSpan&lt;T&gt;</c> job whose element is the loop's one column). The generator
/// (SystemGenerator.Lambdas.cs) intercepts each call and runs the job a [ForEach] method with the lambda's parameters
/// would get, the lambda's body copied into it; the analyzer (EasyEcsAnalyzer.Lambdas.cs) reports what the generator
/// skips. Both read a call through this class, so "skipped" is exactly "reported".
/// </summary>
internal static class LambdaPass
{
    public const string ForEach = "ForEach", ForEachParallel = "ForEachParallel", ParallelFor = "ParallelFor";

    /// <summary>The namespace of the generated interceptors: a project lists it in <c>InterceptorsNamespaces</c> (EasyEcs.props).</summary>
    public const string Namespace = "EasyEcs.Generated";

    /// <summary>A call the generator looks at, by syntax alone: <c>x.ForEach(…, (…) =&gt; …)</c>, <c>ForEachParallel</c> or <c>ParallelFor</c>.</summary>
    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier.ValueText: ForEach or ForEachParallel or ParallelFor } },
            ArgumentList.Arguments: { Count: > 0 } arguments,
        } && arguments[arguments.Count - 1].Expression is ParenthesizedLambdaExpressionSyntax;

    /// <summary>
    /// One of the World methods a lambda pass binds to: <c>ForEach</c> / <c>ForEachParallel</c> / <c>ParallelFor</c> whose
    /// last parameter is a Delegate (ParallelFor's first is the buffer, by ref).
    /// </summary>
    public static bool IsTarget(IMethodSymbol? method) =>
        method is { Name: ForEach or ForEachParallel or ParallelFor, IsStatic: false, Parameters.Length: > 0 } && Symbols.Named(method.ContainingType, Symbols.World)
        && method.Parameters[method.Parameters.Length - 1].Type.SpecialType == SpecialType.System_Delegate
        && (method.Name != ParallelFor || method.Parameters[0].RefKind == RefKind.Ref);

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
        public bool Parallel => Operation.TargetMethod.Name is ForEachParallel or ParallelFor;

        /// <summary>A ParallelFor over a buffer's elements: the element type (its first type argument), else null.</summary>
        public ITypeSymbol? Element => Operation.TargetMethod.Name == ParallelFor ? Operation.TargetMethod.TypeArguments[0] : null;

        /// <summary>The types of the values before the lambda (a ParallelFor's after its element type).</summary>
        public System.Collections.Immutable.ImmutableArray<ITypeSymbol> Values => Element != null
            ? Operation.TargetMethod.TypeArguments.RemoveAt(0) : Operation.TargetMethod.TypeArguments;
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
        if (call.Element is { } element) return BufferProblem(call, lambda, element) ?? Intercepted(call, model, ct);

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
                case Symbols.ParamKind.View when !Views.IsValid(p.Type):
                    return $"parameter '{p.Name}' is a view the generators cannot use (EECS098)";
                case Symbols.ParamKind.View:
                    components += Views.Fields((INamedTypeSymbol)p.Type).Count;
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
        if (Views.Overlap(lambda) is var (_, overlap)) return overlap;
        if (!Symbols.ComponentCountValid(lambda, components))
            return $"the lambda takes {components} components: a pass takes 1 to {Symbols.MaxComponents}, or none with a [With<T>] filter";
        if (call.Parallel)
        {
            if (!IsStable(function)) return "it runs in parallel but can change structure (it takes the World or changes an entity): make it a ForEach";
            if (lambda.Parameters.FirstOrDefault(p => Symbols.Classify(p) == Symbols.ParamKind.Singleton && p.RefKind == RefKind.Ref) is { } singleton)
                return $"'ref {singleton.Type.Name} {singleton.Name}' writes a singleton, which every chunk would race on: take it as 'in', and collect results in a Reduce<T> or an Emit<T>";
        }

        return Intercepted(call, model, ct);
    }

    private static string? Intercepted(Call call, SemanticModel model, System.Threading.CancellationToken ct) =>
        model.GetInterceptableLocation(call.Invocation, ct) == null ? "the compiler cannot intercept this call" : null;

    /// <summary>
    /// Why a ParallelFor's lambda is not one the generator compiles, or null: it takes the element by ref or in, then the
    /// values before it by value, in order and of their types; it has no attributes and changes no structure (its
    /// chunks run on several threads).
    /// </summary>
    private static string? BufferProblem(Call call, IMethodSymbol lambda, ITypeSymbol element)
    {
        string type = element.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        if (lambda.Parameters.Length == 0 || lambda.Parameters[0] is not { RefKind: RefKind.Ref or RefKind.In } first
            || !SymbolEqualityComparer.Default.Equals(first.Type, element))
            return $"its lambda takes no element first: write world.ParallelFor(ref items, static (ref {type} item) => …), the element by ref or in";
        if (Symbols.ContainsTypeParameter(element)) return $"the element is of a type parameter: the generated job names concrete types";
        if (Symbols.Hidden(element) is { } hidden) return $"the element is of {hidden.Name}, which code outside its type cannot name";
        var values = call.Values;
        for (int i = 1; i < lambda.Parameters.Length; i++)
        {
            var p = lambda.Parameters[i];
            if (i - 1 >= values.Length)
                return $"parameter '{p.Name}' is a value the call does not pass: pass it before the lambda (world.ParallelFor(ref items, {p.Name}, static (…) => …))";
            if (p.RefKind is not (RefKind.None or RefKind.In))
                return $"it takes '{p.Name}' by ref, but the job copies its values: pass it by value";
            if (!SymbolEqualityComparer.Default.Equals(p.Type, values[i - 1]))
                return $"value {i} before the lambda is a {values[i - 1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}, but parameter '{p.Name}' is a {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}";
            if (Symbols.ContainsTypeParameter(p.Type)) return $"parameter '{p.Name}' is of a type parameter: the generated job names concrete types";
            if (Symbols.Hidden(p.Type) is { } hiddenValue) return $"parameter '{p.Name}' is of {hiddenValue.Name}, which code outside its type cannot name";
        }
        if (lambda.Parameters.Length - 1 < values.Length)
            return $"the call passes {values.Length} value{(values.Length == 1 ? "" : "s")} before the lambda, which takes {lambda.Parameters.Length - 1}: each value is a parameter of the lambda after the element, in order";
        if (lambda.GetAttributes().FirstOrDefault()?.AttributeClass is { } attribute)
            return $"[{attribute.Name.Replace("Attribute", "")}] does not apply to a ParallelFor lambda: it runs every element";
        if (!IsStable(call.Function!)) return "its chunks run on several threads but it can change structure (it takes the World or changes an entity)";
        return null;
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
    /// The first name in the lambda's body that code in another file cannot reach (<see cref="BodyCopy.Unreachable"/>), or
    /// null: the generator then runs the lambda through its delegate (EECS096).
    /// </summary>
    public static string? Unreachable(Call call, SemanticModel model, System.Threading.CancellationToken ct) => Unreachable(call, model, ct, out _);

    /// <inheritdoc cref="Unreachable(Call, SemanticModel, System.Threading.CancellationToken)"/>
    /// <param name="blocking">The symbol code outside its type cannot reach (EECS096's fix makes a private member internal).</param>
    public static string? Unreachable(Call call, SemanticModel model, System.Threading.CancellationToken ct, out ISymbol? blocking) =>
        BodyCopy.Unreachable(call.Syntax!.Body, call.Lambda!, model, ct, out blocking);

    /// <summary>
    /// Whether the lambda's body is an expression that may stand as a statement (an assignment, an increment, a call),
    /// whose value the pass drops like a statement does.
    /// </summary>
    public static bool Discards(AnonymousFunctionExpressionSyntax syntax) => syntax.Body is
        AssignmentExpressionSyntax or InvocationExpressionSyntax or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
        or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression };

    /// <summary>
    /// The lambda's body as the body of a static method in a generated file (<see cref="BodyCopy.Copy"/>). Call only when
    /// <see cref="Unreachable(Call, SemanticModel, System.Threading.CancellationToken)"/> found nothing. A block keeps its
    /// braces; an expression becomes <c>=&gt; expression;</c>.
    /// </summary>
    public static string Copy(Call call, SemanticModel model, System.Threading.CancellationToken ct)
    {
        var body = BodyCopy.Copy(call.Syntax!.Body, call.Lambda!, model, null, ct);
        return call.Syntax.Body is BlockSyntax ? body.Trim() : $"=> {body.Trim()};";
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
