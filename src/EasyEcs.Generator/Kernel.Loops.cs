using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace EasyEcs.Generator;

/// <summary>
/// Element loops (Burst's loop vectorizer): a <c>for</c> or <c>foreach</c> over the elements of spans inside a [ForEach]
/// method, translated by the same core as the per-row loop. Each element is a lane: a span's element type is read as a
/// component's (its lane fields, or the number itself), and the variables of the method the loop reads and does not
/// write are the loop's values, the same for every element. The loop's body is desugared into a static local function
/// (the elements and values as its parameters), bound in a speculative model of the method's body like a view's
/// desugared form (Kernel.Views.cs), and translated as a lambda pass's body is. The generator then copies the method
/// (BodyCopy) with the loop's vector form before the loop, which runs the elements left as written.
/// <para>
/// Recognized: <c>foreach (ref var x in s)</c> (also <c>ref readonly</c>, by value, explicit types) over a Span, a
/// ReadOnlySpan or a DynamicBuffer, and <c>for (int i = a; i &lt; n; i++)</c> whose body reads its spans only at
/// <c>s[i]</c> and <c>i</c> nowhere else. The body leaves the loop nowhere early (break, continue, return, goto),
/// writes none of the method's variables, and the bound <c>n</c> reads only what the loop does not write.
/// </para>
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// An element loop the generator writes: <paramref name="Code"/> (its columns are the streams, in order), each
    /// stream's element type and how the call site reaches its first element, the values' declarations and arguments.
    /// </summary>
    /// <param name="Streams">Per stream: the C# naming the span at the call site (a local, a parameter, or the foreach's
    /// span), its element type (fully qualified), and whether the loop writes it.</param>
    /// <param name="Values">Per value: its type (fully qualified) and its name at the call site.</param>
    /// <param name="ForEach">The loop is a foreach (else a for over an index).</param>
    internal sealed record ElementLoop(Code Code, (string Span, string Element, bool Written)[] Streams, (string Type, string Name)[] Values, bool ForEach)
    {
        /// <summary>A for loop's end as written (it is read again at the call site), else null.</summary>
        public string? Bound { get; init; }
    }

    /// <summary>
    /// The element loops of a method body: each loop statement (outside lambdas and local functions) with its vector form
    /// or the reason it runs element by element. Nested loops are each their own; a loop holding another stays scalar.
    /// </summary>
    public static List<(StatementSyntax Loop, ElementLoop? Vector, ScalarReason? Reason)> ElementLoops(IMethodSymbol method, MethodDeclarationSyntax syntax,
        SemanticModel model, System.Threading.CancellationToken ct)
    {
        var loops = new List<(StatementSyntax, ElementLoop?, ScalarReason?)>();
        if (syntax.Body == null) return loops;
        foreach (var node in syntax.Body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            if (node is not (ForStatementSyntax or ForEachStatementSyntax)) continue;
            ct.ThrowIfCancellationRequested();
            var loop = (StatementSyntax)node;
            try
            {
                var vector = ElementLoopOf(method, syntax, model, loop, ct, out var reason);
                loops.Add((loop, vector, reason));
            }
            catch (System.Exception e) when (e is not System.OperationCanceledException)
            {
                loops.Add((loop, null, new ScalarReason(FailedTo("translate the loop", e), false, null)));
            }
        }

        return loops;
    }

    /// <summary>
    /// Whether a loop walks elements of spans (a foreach over a Span, a ReadOnlySpan or a DynamicBuffer, or a body
    /// indexing one): a loop meant for an element loop, which EECS055 reports when it stays scalar.
    /// </summary>
    public static bool WalksElements(StatementSyntax loop, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (loop is ForEachStatementSyntax each)
            return model.GetTypeInfo(each.Expression, ct).Type is { } type && (Symbols.SpanElement(type) ?? BufferElement(type)) != null;
        foreach (var access in loop.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).OfType<ElementAccessExpressionSyntax>())
            if (model.GetTypeInfo(access.Expression, ct).Type is { } type && Symbols.SpanElement(type) != null) return true;
        return false;
    }

    /// <summary>One loop's vector form, or null with the reason.</summary>
    private static ElementLoop? ElementLoopOf(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, StatementSyntax loop,
        System.Threading.CancellationToken ct, out ScalarReason? reason)
    {
        reason = null;
        ScalarReason Why(string text, bool fixable = false) => new(text, fixable, null);
        var body = loop is ForStatementSyntax f ? f.Statement : ((ForEachStatementSyntax)loop).Statement;
        if (Leaves(body, loop) is { } leaves)
        {
            reason = Why($"it leaves the loop early ('{leaves}'): a vector loop runs every element");
            return null;
        }
        if (body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .Any(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax))
        {
            reason = Why("it holds a loop of its own: the inner loop is the vector loop");
            return null;
        }

        var flow = model.AnalyzeDataFlow(body);
        if (flow == null || !flow.Succeeded)
        {
            reason = Why("its body could not be analyzed");
            return null;
        }

        // The elements: what each stream symbol stands for in the body, and the names they take as parameters.
        var streams = new List<(ISymbol Symbol, ITypeSymbol Element, bool Writable, string Span)>();
        ILocalSymbol? index = null;
        SyntaxNode desugared;
        string? boundText = null;
        if (loop is ForEachStatementSyntax each)
        {
            if (model.GetDeclaredSymbol(each, ct) is not ILocalSymbol variable || model.GetTypeInfo(each.Expression, ct).Type is not { } spanType)
            {
                reason = Why("its iteration variable could not be bound");
                return null;
            }
            var element = Symbols.SpanElement(spanType) ?? BufferElement(spanType);
            if (element == null)
            {
                reason = Why($"it walks a {spanType.ToDisplayString()}: a vector loop walks a Span, a ReadOnlySpan or a DynamicBuffer");
                return null;
            }
            bool writable = variable.RefKind == RefKind.Ref && Symbols.Named(((INamedTypeSymbol)spanType).ConstructedFrom, "System.Span<T>");
            streams.Add((variable, element, writable, BufferElement(spanType) != null ? ".AsReadOnlySpan()" : "")); // the call site's span
            desugared = body;
        }
        else
        {
            var loopFor = (ForStatementSyntax)loop;
            if (Indexed(loopFor, model, ct) is not var (variable, bound) )
            {
                reason = Why("it is not 'for (int i = start; i < end; i++)': a vector loop steps one index by one", false);
                return null;
            }
            index = variable;
            if (flow.WrittenInside.Contains(variable, SymbolEqualityComparer.Default))
            {
                reason = Why($"it writes its index '{variable.Name}'");
                return null;
            }
            // Every use of the index is the index of a span: s[i].
            var replace = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var name in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, ct).Symbol, variable)) continue;
                if (name.Parent is not ArgumentSyntax { Parent: BracketedArgumentListSyntax { Arguments.Count: 1, Parent: ElementAccessExpressionSyntax access } }
                    || access.Expression is not IdentifierNameSyntax spanName
                    || model.GetSymbolInfo(spanName, ct).Symbol is not { } span || span is not (ILocalSymbol or IParameterSymbol)
                    || Symbols.SpanElement(TypeOf(span)) is not { } element)
                {
                    // The expression using it (an indexer of something else, a call, arithmetic), not the bare argument list.
                    var use = name.Ancestors().OfType<ExpressionSyntax>().FirstOrDefault() ?? name;
                    reason = Why($"it reads its index '{variable.Name}' as a value ('{use}'), not only as the index of a span (s[{variable.Name}])");
                    return null;
                }
                if (!streams.Any(s => SymbolEqualityComparer.Default.Equals(s.Symbol, span)))
                    streams.Add((span, element, Symbols.Named(((INamedTypeSymbol)TypeOf(span)).ConstructedFrom, "System.Span<T>"), span.Name));
                replace[access] = IdentifierName(StreamName(span)).WithTriviaFrom(access);
            }
            if (streams.Count == 0)
            {
                reason = Why($"it reads no span at its index '{variable.Name}'");
                return null;
            }
            // A span is read only at the index: anything else of it (s.Length, s[0]) is no lane.
            foreach (var name in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                if (model.GetSymbolInfo(name, ct).Symbol is { } symbol && streams.Any(s => SymbolEqualityComparer.Default.Equals(s.Symbol, symbol))
                    && !(name.Parent is ElementAccessExpressionSyntax access && access.Expression == name && replace.ContainsKey(access)))
                {
                    reason = Why($"it reads span '{name}' other than at its index ('{name.Parent}')");
                    return null;
                }
            foreach (var s in streams)
                if (flow.WrittenInside.Contains(s.Symbol, SymbolEqualityComparer.Default))
                {
                    reason = Why($"it writes span '{s.Symbol.Name}'");
                    return null;
                }
            // The bound is read again for every element as written: it may read only what the loop leaves alone.
            if (BoundProblem(bound, flow, model, ct) is { } problem)
            {
                reason = Why($"its bound '{bound}' {problem}");
                return null;
            }
            boundText = bound.ToString();
            desugared = new Replacer(replace).Visit(body)!;
        }

        // The values: variables of the method the body reads, declared outside the loop.
        var values = new List<ISymbol>();
        foreach (var read in flow.ReadInside.Concat(flow.WrittenInside))
        {
            if (read is not (ILocalSymbol or IParameterSymbol) || SymbolEqualityComparer.Default.Equals(read, index)
                || streams.Any(s => SymbolEqualityComparer.Default.Equals(s.Symbol, read)) || flow.VariablesDeclared.Contains(read, SymbolEqualityComparer.Default))
                continue;
            if (read is ILocalSymbol { IsConst: true }) continue;
            if (flow.WrittenInside.Contains(read, SymbolEqualityComparer.Default))
            {
                reason = Why($"it writes '{read.Name}', a variable of the method: a vector loop writes only the elements");
                return null;
            }
            if (TypeOf(read).IsRefLikeType)
            {
                reason = Why($"it reads '{read.Name}', a {TypeOf(read).ToDisplayString()}, other than at the loop's index");
                return null;
            }
            if (!values.Contains(read, SymbolEqualityComparer.Default)) values.Add(read);
        }

        // The body as a static local function of the elements and the values, bound where the method's body is.
        var parameters = new List<ParameterSyntax>();
        foreach (var s in streams)
            parameters.Add(Parameter(Identifier(StreamName(s.Symbol, loop is ForEachStatementSyntax)))
                .WithType(ParseTypeName(s.Element.ToDisplayString(Symbols.Fqn)))
                .WithModifiers(TokenList(Token(s.Writable ? SyntaxKind.RefKeyword : SyntaxKind.InKeyword))));
        foreach (var v in values)
            parameters.Add(Parameter(Identifier(global::EasyEcs.Generator.Code.Escape(v.Name))).WithType(ParseTypeName(TypeOf(v).ToDisplayString(Symbols.Fqn))));
        var function = LocalFunctionStatement(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier("__Elements"))
            .WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(ParameterList(SeparatedList(parameters)))
            .WithBody(desugared as BlockSyntax ?? Block((StatementSyntax)desugared));
        var replaced = syntax.WithExpressionBody(null).WithSemicolonToken(default).WithBody(Block(function));
        if (!model.TryGetSpeculativeSemanticModelForMethodBody(syntax.Body!.SpanStart, replaced, out var speculative) || speculative == null
            || replaced.Body!.Statements[0] is not LocalFunctionStatementSyntax declared
            || speculative.GetDeclaredSymbol(declared, ct) is not IMethodSymbol elements
            || speculative.GetOperation(declared, ct) is not ILocalFunctionOperation { Body: { } operation })
        {
            reason = Why("its body could not be bound as a loop over its elements");
            return null;
        }
        if (operation.Descendants().Any(o => o.Kind == OperationKind.Invalid))
        {
            reason = Why("its body could not be bound as a loop over its elements");
            return null;
        }

        var lane = new Lane(elements, true, false, Symbols.FastFloats(method), speculative, true, method)
        {
            Streams = new HashSet<IParameterSymbol>(elements.Parameters.Take(streams.Count), SymbolEqualityComparer.Default),
        };
        var code = Vectorize(lane, operation, static (i, _) => $"x{i}", ct, out reason);
        if (code == null) return null;
        if (code.Wide != null || code.Statics.Length > 0 || code.Gathers || code.VectorTail || code.StructuralChanges.Length > 0 || code.Fold.Length > 0)
        {
            reason = Why("it needs a form of the vector loop element loops do not take yet (a wide element, gathers, records)");
            return null;
        }

        return new ElementLoop(code,
            streams.Select(s => (s.Span, s.Element.ToDisplayString(Symbols.Fqn), code.Written.Contains(streams.IndexOf(s) + 1))).ToArray(),
            values.Select(v => (TypeOf(v).ToDisplayString(Symbols.Fqn), global::EasyEcs.Generator.Code.Escape(v.Name))).ToArray(),
            loop is ForEachStatementSyntax) { Bound = boundText };
    }

    /// <summary>The name a stream takes as a parameter: a foreach's variable keeps its own; a span read at the index is <c>__e_s</c>.</summary>
    private static string StreamName(ISymbol span, bool forEach = false) => forEach ? global::EasyEcs.Generator.Code.Escape(span.Name) : "__e_" + span.Name;

    private static ITypeSymbol TypeOf(ISymbol variable) => variable switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        _ => throw new System.InvalidOperationException(variable.Kind.ToString()),
    };

    /// <summary>The element type of a DynamicBuffer&lt;T&gt;, else null.</summary>
    private static ITypeSymbol? BufferElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Symbols.Named(named.ConstructedFrom, "EasyEcs.DynamicBuffer<T>") ? named.TypeArguments[0] : null;

    /// <summary>The statement that leaves <paramref name="loop"/> early from <paramref name="body"/> (break, continue, return, goto, yield), or null.</summary>
    private static string? Leaves(StatementSyntax body, StatementSyntax loop)
    {
        foreach (var node in body.DescendantNodesAndSelf(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            switch (node)
            {
                case ReturnStatementSyntax or GotoStatementSyntax or YieldStatementSyntax:
                    return node.ToString().TrimEnd(';');
                case BreakStatementSyntax or ContinueStatementSyntax:
                    // a break of a switch inside the loop ends its section, and a break or continue of a loop inside
                    // the loop stays in it (that loop is refused on its own: it holds a loop)
                    for (var up = node.Parent; up != null && up != loop; up = up.Parent)
                        if (up is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                            || up is SwitchSectionSyntax && node is BreakStatementSyntax) goto next;
                    return node.ToString().TrimEnd(';');
            }
            next:;
        }

        return null;
    }

    /// <summary>
    /// <c>for (int i = start; i &lt; end; i++)</c> (or <c>++i</c>, <c>i += 1</c>): its index and the end, else null.
    /// </summary>
    private static (ILocalSymbol Index, ExpressionSyntax End)? Indexed(ForStatementSyntax loop, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (loop.Declaration is not { Variables: { Count: 1 } variables } declaration || loop.Initializers.Count > 0 || variables[0].Initializer == null
            || model.GetDeclaredSymbol(variables[0], ct) is not ILocalSymbol { Type.SpecialType: SpecialType.System_Int32 } index)
            return null;
        _ = declaration;
        if (loop.Condition is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LessThanExpression, Left: IdentifierNameSyntax left } condition
            || left.Identifier.ValueText != index.Name)
            return null;
        if (loop.Incrementors.Count != 1) return null;
        bool steps = loop.Incrementors[0] switch
        {
            PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression, Operand: IdentifierNameSyntax n } => n.Identifier.ValueText == index.Name,
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression, Operand: IdentifierNameSyntax n } => n.Identifier.ValueText == index.Name,
            AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression, Left: IdentifierNameSyntax n, Right: LiteralExpressionSyntax { Token.Value: 1 } } => n.Identifier.ValueText == index.Name,
            _ => false,
        };
        return steps ? (index, condition.Right) : null;
    }

    /// <summary>Why a for loop's bound cannot be read again as written for every element, or null.</summary>
    private static string? BoundProblem(ExpressionSyntax bound, DataFlowAnalysis flow, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (model.GetOperation(bound, ct) is not { } operation) return "could not be bound";
        foreach (var op in operation.DescendantsAndSelf())
        {
            if (op.ConstantValue.HasValue) continue;
            switch (op)
            {
                case ILocalReferenceOperation { Local: var local } when !flow.WrittenInside.Contains(local, SymbolEqualityComparer.Default):
                case IParameterReferenceOperation { Parameter: var parameter } when !flow.WrittenInside.Contains(parameter, SymbolEqualityComparer.Default):
                case IPropertyReferenceOperation { Property.Name: "Length", Instance.Type: { } owner } when Symbols.SpanElement(owner) != null || owner is IArrayTypeSymbol:
                case IBinaryOperation { OperatorMethod: null, IsChecked: false }:
                case IConversionOperation { OperatorMethod: null }:
                case IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } }:
                    continue;
                default:
                    return "is not a value the loop leaves alone (a variable it does not write, a span's Length, arithmetic)";
            }
        }

        return null;
    }

    /// <summary>Replaces <c>s[i]</c> with the element's parameter.</summary>
    private sealed class Replacer(Dictionary<SyntaxNode, SyntaxNode> replace) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? Visit(SyntaxNode? node) => node != null && replace.TryGetValue(node, out var with) ? with : base.Visit(node);
    }
}
