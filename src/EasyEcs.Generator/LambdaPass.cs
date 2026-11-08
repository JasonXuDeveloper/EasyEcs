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

    // ---------------- an IForSpan job that is one element loop, as the lambda pass (EECS133) ----------------

    /// <summary>
    /// <c>world.ParallelFor(ref items, ref job)</c> written as the lambda pass the generator gives a vector loop:
    /// <paramref name="Text"/> replaces the call, and <paramref name="Declaration"/>, the job's declaration right before
    /// it, goes.
    /// </summary>
    public sealed record JobRewrite(string Text, LocalDeclarationStatementSyntax Declaration);

    /// <summary>
    /// The lambda pass a ParallelFor over a buffer's elements with an <c>IForSpan&lt;T&gt;</c> job becomes, or null
    /// unless the rewrite is mechanical and gets a vector loop: the job is a struct of this compilation built in the
    /// statement right before the call (<c>var job = new J { F = e };</c>, the local used nowhere else), its Execute is one
    /// loop over its chunk (<c>foreach (ref T x in items)</c>, or <c>for (int i = 0; i &lt; items.Length; i++)</c> reading
    /// the chunk only at <c>items[i]</c>, <c>start</c> unused), reading the job's fields without writing them or using the
    /// job otherwise. The fields it reads become the lambda's values (their initializers at the call, <c>default</c> for
    /// the others), its loop's body the lambda's; the rewritten call, bound in a copy of the compilation, must be one the
    /// generator compiles (<see cref="Problem"/>, <see cref="Unreachable(Call, SemanticModel, System.Threading.CancellationToken)"/>)
    /// with a vector loop (<see cref="Kernel.VectorizeElements"/>), as HandSimd's forms are checked.
    /// </summary>
    public static JobRewrite? FromJob(IInvocationOperation operation, System.Threading.CancellationToken ct)
    {
        var target = operation.TargetMethod;
        if (target is not { Name: ParallelFor, TypeArguments.Length: 2, Parameters.Length: 3 } || target.Parameters[1].RefKind != RefKind.Ref
            || !Symbols.Named(target.ContainingType, Symbols.World) || operation.SemanticModel is not { } model
            || operation.Syntax is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 2 } invocation
            || invocation.Parent is not ExpressionStatementSyntax { Parent: BlockSyntax block } statement)
            return null;
        var (element, job) = (target.TypeArguments[0], target.TypeArguments[1] as INamedTypeSymbol);
        // The job's local, declared by the statement right before the call and named nowhere else.
        int at = block.Statements.IndexOf(statement);
        if (operation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 1)?.Value is not ILocalReferenceOperation { Local: var local }
            || job is not { TypeKind: TypeKind.Struct, IsGenericType: false } || at < 1
            || block.Statements[at - 1] is not LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer.Value: var created } declarator] } declaration
            || !SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declarator, ct), local)
            || block.DescendantNodes().OfType<IdentifierNameSyntax>().Count(n => n.Identifier.ValueText == local.Name
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, ct).Symbol, local)) != 1)
            return null;
        var initialized = new Dictionary<string, ExpressionSyntax>();
        switch (created)
        {
            case BaseObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 }: return null;
            case BaseObjectCreationExpressionSyntax creation:
                foreach (var e in creation.Initializer?.Expressions ?? default)
                {
                    if (e is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax field, Right: var value }) return null;
                    initialized[field.Identifier.ValueText] = value;
                }
                break;
            case DefaultExpressionSyntax or LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression }: break;
            default: return null;
        }
        // Execute: one loop over the chunk.
        var execute = job.GetMembers("Execute").OfType<IMethodSymbol>().SingleOrDefault(m => m.Parameters.Length == 2);
        if (execute is not { DeclaringSyntaxReferences: [var reference] } || reference.GetSyntax(ct) is not MethodDeclarationSyntax { Body.Statements: [var loop] } method
            || job.DeclaringSyntaxReferences.Length != 1)
            return null;
        var jobModel = model.Compilation.GetSemanticModel(method.SyntaxTree);
        var (startParameter, items) = (execute.Parameters[0], execute.Parameters[1]);
        string elementName;
        StatementSyntax body;
        var replace = new Dictionary<SyntaxNode, string>();
        if (loop is ForEachStatementSyntax { Expression: IdentifierNameSyntax over } each
            && SymbolEqualityComparer.Default.Equals(jobModel.GetSymbolInfo(over, ct).Symbol, items))
            (elementName, body) = (each.Identifier.ValueText, each.Statement);
        else if (loop is ForStatementSyntax
                 {
                     Declaration.Variables: [{ Initializer.Value: LiteralExpressionSyntax { Token.ValueText: "0" } } counter],
                     Condition: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LessThanExpression, Left: IdentifierNameSyntax tested, Right: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax bounded, Name.Identifier.ValueText: "Length" } },
                     Incrementors: [PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax],
                 } walk
                 && tested.Identifier.ValueText == counter.Identifier.ValueText && SymbolEqualityComparer.Default.Equals(jobModel.GetSymbolInfo(bounded, ct).Symbol, items))
        {
            var index = jobModel.GetDeclaredSymbol(counter, ct);
            body = walk.Statement;
            elementName = Fresh("item", body, null);
            foreach (var name in body.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => SymbolEqualityComparer.Default.Equals(jobModel.GetSymbolInfo(n, ct).Symbol, index)))
            {
                if (name.Parent is not ArgumentSyntax { Parent: BracketedArgumentListSyntax { Arguments.Count: 1, Parent: ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax indexed } access } }
                    || !SymbolEqualityComparer.Default.Equals(jobModel.GetSymbolInfo(indexed, ct).Symbol, items))
                    return null; // the counter as a value, or the index of something else
                replace[access] = elementName;
            }
        }
        else return null;
        // What else the body names: the chunk only through the loop, never the start; the job's fields read, never written.
        var reads = new List<IFieldSymbol>();
        foreach (var node in body.DescendantNodesAndSelf())
        {
            // A jump leaves the loop or Execute (break, return), or needs the loop (continue): the lambda's body is one
            // element, where each means something else or does not compile.
            if (node is ThisExpressionSyntax or BaseExpressionSyntax or ReturnStatementSyntax or BreakStatementSyntax
                or ContinueStatementSyntax or GotoStatementSyntax or YieldStatementSyntax) return null;
            if (node is not IdentifierNameSyntax name || replace.Keys.Any(r => r.Span.Contains(name.Span))) continue;
            var symbol = jobModel.GetSymbolInfo(name, ct).Symbol;
            if (SymbolEqualityComparer.Default.Equals(symbol, items) || SymbolEqualityComparer.Default.Equals(symbol, startParameter)) return null;
            if (symbol is IMethodSymbol or IPropertySymbol { IsStatic: false } && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, job)) return null;
            if (symbol is not IFieldSymbol { IsStatic: false } field || !SymbolEqualityComparer.Default.Equals(field.ContainingType, job)) continue;
            if (jobModel.GetOperation(name, ct) is { } use && Uses.MayWrite(use)) return null;
            if (field.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) is VariableDeclaratorSyntax { Initializer: not null })) return null;
            if (!reads.Contains(field, SymbolEqualityComparer.Default)) reads.Add(field);
        }
        if (reads.Count > ArityGenerator.MaxLambdaValues || initialized.Keys.Any(k => !reads.Any(f => f.Name == k) && !IsPure(initialized[k]))) return null;
        // The lambda: the element by ref, then each field read as a value, named after it.
        var taken = new HashSet<string> { elementName };
        var parameters = new List<string> { $"ref {element.ToMinimalDisplayString(model, invocation.SpanStart)} {elementName}" };
        var values = new List<string>();
        foreach (var field in reads)
        {
            string name = Fresh(char.ToLowerInvariant(field.Name[0]) + field.Name.Substring(1), body, taken);
            taken.Add(name);
            string type = field.Type.ToMinimalDisplayString(model, invocation.SpanStart);
            parameters.Add($"{type} {name}");
            values.Add(initialized.TryGetValue(field.Name, out var value) ? value.WithoutTrivia().ToString() : $"default({type})");
            foreach (var use in body.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => SymbolEqualityComparer.Default.Equals(jobModel.GetSymbolInfo(n, ct).Symbol, field)))
                replace[use] = name;
        }
        string lambdaBody = Reindent(body.ReplaceNodes(replace.Keys, (old, _) => SyntaxFactory.IdentifierName(replace[old]).WithTriviaFrom(old)), statement);
        var receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression.WithoutTrivia();
        string text = $"{receiver}.ParallelFor({invocation.ArgumentList.Arguments[0].WithoutTrivia()}{string.Concat(values.Select(v => ", " + v))}, static ({string.Join(", ", parameters)}) => {lambdaBody})";

        // The check: the call rewritten in a copy of the compilation is a lambda pass with a vector loop.
        var tree = invocation.SyntaxTree;
        var root = tree.GetRoot(ct);
        var annotation = new SyntaxAnnotation();
        if (SyntaxFactory.ParseExpression(text) is not InvocationExpressionSyntax rewritten || rewritten.ContainsDiagnostics) return null;
        var newRoot = root.TrackNodes(invocation, declaration);
        newRoot = newRoot.ReplaceNode(newRoot.GetCurrentNode(invocation)!, rewritten.WithAdditionalAnnotations(annotation));
        newRoot = newRoot.RemoveNode(newRoot.GetCurrentNode(declaration)!, SyntaxRemoveOptions.KeepNoTrivia)!;
        var copy = model.Compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(newRoot, tree.Options));
        var newTree = copy.SyntaxTrees.First(t => t.FilePath == tree.FilePath && t.GetRoot(ct).GetAnnotatedNodes(annotation).Any());
        var copyModel = copy.GetSemanticModel(newTree);
        var placed = newTree.GetRoot(ct).GetAnnotatedNodes(annotation).First();
        if (copyModel.GetDiagnostics(placed.Span, ct).Any(d => d.Severity == DiagnosticSeverity.Error)) return null;
        if (copyModel.GetOperation(placed, ct) is not IInvocationOperation call
            || Read(call) is not { Function: not null, Lambda: not null } pass || Problem(pass, copyModel, ct, out _) != null || Unreachable(pass, copyModel, ct) != null
            || Kernel.VectorizeElements(pass.Lambda, pass.Function, copyModel, ct, out _) == null)
            return null;
        return new JobRewrite(text, declaration);

        // A name the body does not use yet.
        static string Fresh(string name, SyntaxNode body, HashSet<string>? taken)
        {
            var used = new HashSet<string>(body.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText));
            string fresh = name;
            for (int k = 2; used.Contains(fresh) || taken?.Contains(fresh) == true || SyntaxFacts.GetKeywordKind(fresh) != SyntaxKind.None; k++) fresh = name + k.ToString(CultureInfo.InvariantCulture);
            return fresh;
        }
    }

    /// <summary>An initializer whose value the rewrite may drop (a field the loop never reads): a literal, a name or a member access.</summary>
    private static bool IsPure(ExpressionSyntax value) => value is LiteralExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax };

    /// <summary>
    /// <paramref name="body"/> (a block, or one statement made a block) as the lambda's body at <paramref name="at"/>'s
    /// indentation: its lines moved by the difference between its own indentation and the statement's.
    /// </summary>
    private static string Reindent(StatementSyntax body, StatementSyntax at)
    {
        string Indent(SyntaxNode node) => string.Concat(node.GetLeadingTrivia().Reverse().TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Select(t => t.ToFullString()));
        string target = Indent(at);
        if (body is not BlockSyntax block)
            return "{ " + body.WithoutTrivia() + " }";
        string own = string.Concat(block.OpenBraceToken.LeadingTrivia.Reverse().TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Select(t => t.ToFullString()));
        var lines = block.WithoutTrivia().ToFullString().Replace("\r\n", "\n").Split('\n');
        for (int i = 1; i < lines.Length; i++)
            lines[i] = lines[i].StartsWith(own, System.StringComparison.Ordinal) ? target + lines[i].Substring(own.Length) : lines[i];
        return string.Join("\n", lines);
    }
}
