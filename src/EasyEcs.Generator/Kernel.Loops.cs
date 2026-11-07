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
/// method or a lambda pass, translated by the same core as the per-row loop. Each element is a lane: a span's element
/// type is read as a component's (its lane fields, or the number itself), and the variables of the method the loop
/// reads and does not write are the loop's values, the same for every element. The loop's body is desugared into a
/// static local function (the elements and values as its parameters), bound in the loop's place in a speculative model,
/// its calls inlined as a [ForEach] method's (Kernel.Inlining.cs), and translated as a lambda pass's body is. The
/// generator then copies the method (BodyCopy) with the loop's vector form before the loop, which runs the elements
/// left as written.
/// <para>
/// Recognized: <c>foreach (ref var x in s)</c> (also <c>ref readonly</c>, by value, explicit types) over a Span, a
/// ReadOnlySpan or a DynamicBuffer, and <c>for (int i = a; i &lt; n; i++)</c> whose body reads its spans only at
/// <c>s[i]</c> (and <c>i</c> as a value, outside another index: <c>_index</c>). The body leaves the loop nowhere early
/// (break, continue, return, goto), writes the method's variables only as sums, Mins or Maxes (<see cref="SumOf"/>),
/// and the bound <c>n</c> reads only what the loop does not write.
/// </para>
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// An element loop the generator writes: <paramref name="Code"/> (its columns are the streams, in order), each
    /// stream's element type and how the call site reaches its first element, the values' declarations and arguments.
    /// </summary>
    /// <param name="Streams">Per stream: the C# naming the span at the call site (a local, a parameter, or the foreach's
    /// span), its element type (fully qualified), whether the loop writes it, and the constant the loop adds to its
    /// index to read it (<c>s[i - 1]</c>: -1; a span read at several offsets is a stream per offset).</param>
    /// <param name="Values">Per value: its type (fully qualified), its name at the call site, and whether the loop sums
    /// into it (passed by ref; its lanes fold into it).</param>
    /// <param name="ForEach">The loop is a foreach (else a for over an index).</param>
    internal sealed record ElementLoop(Code Code, (string Span, string Element, bool Written, int Offset)[] Streams, (string Type, string Name, bool Sum)[] Values, bool ForEach)
    {
        /// <summary>A for loop's end as written (it is read again at the call site), else null.</summary>
        public string? Bound { get; init; }
    }

    /// <summary>
    /// The element loops of the body of a method or a lambda pass's lambda: each loop statement (outside lambdas and local
    /// functions) with its vector form or the reason it runs element by element. Nested loops are each their own; a loop
    /// holding another stays scalar.
    /// </summary>
    public static List<(StatementSyntax Loop, ElementLoop? Vector, ScalarReason? Reason)> ElementLoops(IMethodSymbol method, BlockSyntax? body,
        SemanticModel model, System.Threading.CancellationToken ct)
    {
        var loops = new List<(StatementSyntax, ElementLoop?, ScalarReason?)>();
        if (body == null) return loops;
        foreach (var node in body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            if (node is not (ForStatementSyntax or ForEachStatementSyntax)) continue;
            ct.ThrowIfCancellationRequested();
            var loop = (StatementSyntax)node;
            try
            {
                var vector = ElementLoopOf(method, model, loop, ct, out var reason);
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
    /// indexing one): a loop meant for an element loop, which EECS055 reports when it stays scalar. A loop over vectors,
    /// or one computing with them, is a vector loop written by hand (EECS103's in a batch method): not meant for one.
    /// </summary>
    public static bool WalksElements(StatementSyntax loop, SemanticModel model, System.Threading.CancellationToken ct)
    {
        var nodes = loop.DescendantNodesAndSelf(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));
        foreach (var node in nodes)
            if (node is ExpressionSyntax expression && IsVectorType(model.GetTypeInfo(expression, ct).Type)
                || node != loop && node is ForStatementSyntax or ForEachStatementSyntax) // the loops it holds are each their own
                return false;
        if (loop is ForEachStatementSyntax each)
            return model.GetTypeInfo(each.Expression, ct).Type is { } type && (Symbols.SpanElement(type) ?? BufferElement(type)) is { } element && Plain(element);
        foreach (var access in nodes.OfType<ElementAccessExpressionSyntax>())
            if (model.GetTypeInfo(access.Expression, ct).Type is { } type && Symbols.SpanElement(type) is { } element && Plain(element)) return true;
        return false;

        // a number or a struct of plain values (LaneFields): elements a vector loop could take, so its reason is worth a message
        static bool Plain(ITypeSymbol element) => IsLaneType(element) || LaneFields(element);
    }

    /// <summary>Vector&lt;T&gt;, Vector64..512&lt;T&gt;, or a span of them.</summary>
    internal static bool IsVectorType(ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsGenericType: true } named
        && (named.Name is "Vector" or "Vector64" or "Vector128" or "Vector256" or "Vector512"
            && named.ContainingNamespace?.ToDisplayString() is "System.Numerics" or "System.Runtime.Intrinsics"
            || Symbols.SpanElement(named) is { } element && IsVectorType(element));

    /// <summary>One loop's vector form, or null with the reason.</summary>
    private static ElementLoop? ElementLoopOf(IMethodSymbol method, SemanticModel model, StatementSyntax loop,
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
        var streams = new List<(ISymbol Symbol, ITypeSymbol Element, bool Writable, string Span, int Offset)>();
        ILocalSymbol? index = null;
        bool indexValue = false; // the index read as a value (s[i] = i): a parameter of the body whose lanes are the indexes
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
            streams.Add((variable, element, writable, BufferElement(spanType) != null ? ".AsReadOnlySpan()" : "", 0)); // the call site's span
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
            // Every use of the index is the index of a span (s[i], s[i + 1], s[i - 2]: a constant apart) or a value
            // (i * 2), not inside another index.
            var replace = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var name in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, ct).Symbol, variable)) continue;
                var (argument, offset) = OffsetOf(name, model, ct);
                if (argument.Parent is not ArgumentSyntax { Parent: BracketedArgumentListSyntax { Arguments.Count: 1, Parent: ElementAccessExpressionSyntax access } }
                    || access.Expression is not IdentifierNameSyntax spanName
                    || model.GetSymbolInfo(spanName, ct).Symbol is not { } span || span is not (ILocalSymbol or IParameterSymbol)
                    || Symbols.SpanElement(TypeOf(span)) is not { } element)
                {
                    if (!name.Ancestors().TakeWhile(a => a != body).Any(a => a is BracketedArgumentListSyntax))
                    {
                        indexValue = true;
                        continue;
                    }
                    // An index of its own (s[i - 1], a[i] of an array): the expression using it, not the bare argument list.
                    var use = name.Ancestors().OfType<ExpressionSyntax>().FirstOrDefault() ?? name;
                    reason = Why($"it reads its index '{variable.Name}' as a value ('{use}'), not only as the index of a span (s[{variable.Name}])");
                    return null;
                }
                if (!streams.Any(s => SymbolEqualityComparer.Default.Equals(s.Symbol, span) && s.Offset == offset))
                    streams.Add((span, element, Symbols.Named(((INamedTypeSymbol)TypeOf(span)).ConstructedFrom, "System.Span<T>"), span.Name, offset));
                replace[access] = IdentifierName(StreamName(span, offset: offset)).WithTriviaFrom(access);
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

        // The values: variables of the method the body reads, declared outside the loop; and those it sums into.
        var values = new List<ISymbol>();
        var sums = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var loopOperation = model.GetOperation(loop, ct);
        foreach (var read in flow.ReadInside.Concat(flow.WrittenInside))
        {
            if (read is not (ILocalSymbol or IParameterSymbol) || SymbolEqualityComparer.Default.Equals(read, index)
                || streams.Any(s => SymbolEqualityComparer.Default.Equals(s.Symbol, read)) || flow.VariablesDeclared.Contains(read, SymbolEqualityComparer.Default))
                continue;
            if (read is ILocalSymbol { IsConst: true }) continue;
            if (flow.WrittenInside.Contains(read, SymbolEqualityComparer.Default))
            {
                if (loopOperation == null || read is IParameterSymbol { RefKind: RefKind.In or RefKind.RefReadOnlyParameter } || SumOf(read, loopOperation) is not { } op)
                {
                    reason = Why($"it writes '{read.Name}', a variable of the method, other than as a sum, a Min or a Max ('{read.Name} += e', '{read.Name} = Math.Min({read.Name}, e)'): a vector loop writes the elements and such variables only");
                    return null;
                }
                sums[read] = op;
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
            parameters.Add(Parameter(Identifier(StreamName(s.Symbol, loop is ForEachStatementSyntax, s.Offset)))
                .WithType(ParseTypeName(s.Element.ToDisplayString(Symbols.Fqn)))
                .WithModifiers(TokenList(Token(s.Writable ? SyntaxKind.RefKeyword : SyntaxKind.InKeyword))));
        foreach (var v in values)
            parameters.Add(Parameter(Identifier(global::EasyEcs.Generator.Code.Escape(v.Name))).WithType(ParseTypeName(TypeOf(v).ToDisplayString(Symbols.Fqn)))
                .WithModifiers(sums.ContainsKey(v) ? TokenList(Token(SyntaxKind.RefKeyword)) : default));
        if (indexValue) parameters.Add(Parameter(Identifier(global::EasyEcs.Generator.Code.Escape(index!.Name))).WithType(PredefinedType(Token(SyntaxKind.IntKeyword))));
        var function = LocalFunctionStatement(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier("__Elements"))
            .WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(ParameterList(SeparatedList(parameters)))
            .WithBody(desugared as BlockSyntax ?? Block((StatementSyntax)desugared));
        // Bound as a statement in the loop's place: the method's names are in scope, and each variable the body reads is
        // a parameter of the static function, which hides the method's.
        if (!model.TryGetSpeculativeSemanticModel(loop.SpanStart, function, out var speculative) || speculative == null
            || speculative.GetDeclaredSymbol(function, ct) is not IMethodSymbol elements
            || speculative.GetOperation(function, ct) is not ILocalFunctionOperation { Body: { } operation })
        {
            reason = Why("its body could not be bound as a loop over its elements");
            return null;
        }
        if (operation.Descendants().Any(o => o.Kind == OperationKind.Invalid))
        {
            reason = Why("its body could not be bound as a loop over its elements");
            return null;
        }
        // The user's own calls inlined (Kernel.Inlining.cs), as in a [ForEach] method's body, bound again in the loop's place.
        var callees = Callees.Of(model.Compilation);
        if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is MethodDeclarationSyntax host) callees.Offer(model, host);
        if (operation.DescendantsAndSelf().Any(o => callees.Candidate(o) != null))
        {
            var inliner = new Inliner(new HashSet<string>(function.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText)),
                elements, model.Compilation, ct);
            var block = inliner.Method(speculative).Body(function.Body!);
            if (block != null && inliner.Count + inliner.Left > 0)
            {
                var inlined = function.WithBody(inliner.WithAccessors(Inliner.WithoutInlinedLocalFunctions(block)));
                if (model.TryGetSpeculativeSemanticModel(loop.SpanStart, inlined, out var rebound) && rebound != null
                    && rebound.GetDeclaredSymbol(inlined, ct) is IMethodSymbol inlinedElements
                    && rebound.GetOperation(inlined, ct) is ILocalFunctionOperation { Body: { } inlinedBody }
                    && !inlinedBody.Descendants().Any(o => o.Kind == OperationKind.Invalid))
                    (speculative, elements, operation) = (rebound, inlinedElements, inlinedBody);
            }
        }

        var lane = new Lane(elements, true, false, Symbols.FastFloats(method), speculative, true, method)
        {
            Streams = new HashSet<IParameterSymbol>(elements.Parameters.Take(streams.Count), SymbolEqualityComparer.Default),
            Sums = values.Select((v, i) => (v, i)).Where(x => sums.ContainsKey(x.v))
                .ToDictionary(x => elements.Parameters[streams.Count + x.i], x => sums[x.v], (IEqualityComparer<IParameterSymbol>)SymbolEqualityComparer.Default),
            Index = indexValue ? elements.Parameters[streams.Count + values.Count] : null,
        };
        var code = Vectorize(lane, operation, static (i, _) => $"x{i}", ct, out reason);
        if (code == null) return null;
        if (code.Wide != null || code.Statics.Length > 0 || code.Gathers || code.VectorTail || code.StructuralChanges.Length > 0)
        {
            reason = Why("it needs a form of the vector loop element loops do not take yet (a wide element, gathers, records)");
            return null;
        }

        // A span written and read at another offset: the loop as written reads elements it wrote (s[i] = s[i - 1]), or
        // reads ahead what a vector's stores would have changed first; the vector form loads a whole vector before it
        // stores one, so it stays scalar.
        for (int s = 0; s < streams.Count; s++)
            if (code.Written.Contains(s + 1) && streams.FirstOrDefault(o => SymbolEqualityComparer.Default.Equals(o.Symbol, streams[s].Symbol) && o.Offset != streams[s].Offset) is { Symbol: not null } other)
            {
                reason = Why($"it writes span '{streams[s].Symbol.Name}' and reads it at another index ('{streams[s].Symbol.Name}[{index!.Name}{Shift(other.Offset)}]'): each element would read what the loop wrote, or is about to write, one element before; write into another span");
                return null;
            }

        return new ElementLoop(code,
            streams.Select(s => (s.Span, s.Element.ToDisplayString(Symbols.Fqn), code.Written.Contains(streams.IndexOf(s) + 1), s.Offset)).ToArray(),
            values.Select(v => (TypeOf(v).ToDisplayString(Symbols.Fqn), global::EasyEcs.Generator.Code.Escape(v.Name), sums.ContainsKey(v))).ToArray(),
            loop is ForEachStatementSyntax) { Bound = boundText };
    }

    /// <summary>
    /// How <paramref name="loop"/> writes <paramref name="variable"/>, a variable of the method, when every use of it is a
    /// statement that sums into it (<c>t += e</c>, <c>t -= e</c>, <c>t++</c>, <c>t--</c>: "+") or takes its Min or Max
    /// with a value (<c>t = Math.Min(t, e)</c>, either side: "Min" / "Max"), all the same operation, the value not
    /// reading it: the loop's result then does not depend on the order of its elements (the sum of integers wraps;
    /// Kernel.Summed has the floats). Else null: the variable is read, or written otherwise.
    /// </summary>
    private static string? SumOf(ISymbol variable, IOperation loop)
    {
        string? op = null;
        foreach (var use in loop.Descendants())
        {
            if (!IsVariable(use, variable)) continue;
            string? found = use.Parent switch
            {
                ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, OperatorMethod: null } a
                    when a.Target == use && a.Parent is IExpressionStatementOperation => "+",
                IIncrementOrDecrementOperation { OperatorMethod: null } a when a.Parent is IExpressionStatementOperation => "+",
                ISimpleAssignmentOperation { Parent: IExpressionStatementOperation } a when a.Target == use => MinMax(a.Value, variable),
                IArgumentOperation { Parent: IInvocationOperation { Parent: ISimpleAssignmentOperation { Parent: IExpressionStatementOperation } a } call }
                    when a.Value == call && IsVariable(a.Target, variable) => MinMax(call, variable),
                _ => null,
            };
            if (found == null || op != null && op != found) return null;
            op = found;
        }
        return op;

        // Math.Min(t, e) / Math.Max(e, t) (MathF, or the number type's own): exactly one argument is the variable.
        static string? MinMax(IOperation value, ISymbol variable) =>
            value is IInvocationOperation { Instance: null, Arguments.Length: 2, TargetMethod: { Name: "Min" or "Max" } m } call && IsMath(m)
            && IsVariable(call.Arguments[0].Value, variable) != IsVariable(call.Arguments[1].Value, variable) ? m.Name : null;

        static bool IsVariable(IOperation op, ISymbol variable) => op switch
        {
            ILocalReferenceOperation l => SymbolEqualityComparer.Default.Equals(l.Local, variable),
            IParameterReferenceOperation p => SymbolEqualityComparer.Default.Equals(p.Parameter, variable),
            _ => false,
        };
    }

    /// <summary>
    /// An int expression of an element loop's index and uniform ints (<c>i * 5 + k</c>) in lanes of the 8-byte integer
    /// <paramref name="t"/>: +, -, *, &amp;, |, ^, ~ and negation, whose low 32 bits are the int's whatever the high ones;
    /// <c>Exact</c> when it is the index alone (no int wraps). Null for anything else.
    /// </summary>
    private (string Text, bool Exact)? WideIndex(IOperation op, string t)
    {
        while (op is IParenthesizedOperation p) op = p.Operand;
        if (op is IParameterReferenceOperation { Parameter: var index } && SymbolEqualityComparer.Default.Equals(index, _index))
            return ($"({V}.Create<{t}>(({t}){Row}) + {Vec(t)}.Indices)", true);
        if (!IsInt(op.Type)) return null;
        if (IsUniform(op)) return ($"{V}.Create<{t}>(({t}){Scalar(op)})", false);
        switch (op)
        {
            case IBinaryOperation { OperatorMethod: null, IsChecked: false, OperatorKind: var kind } b
                when kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply or BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr:
                if (WideIndex(b.LeftOperand, t) is not { } l || WideIndex(b.RightOperand, t) is not { } r) return null;
                string sign = kind switch
                {
                    BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*",
                    BinaryOperatorKind.And => "&", BinaryOperatorKind.Or => "|", _ => "^",
                };
                return ($"({l.Text} {sign} {r.Text})", false);
            case IUnaryOperation { OperatorMethod: null, IsChecked: false, OperatorKind: UnaryOperatorKind.Minus or UnaryOperatorKind.BitwiseNegation } u
                when WideIndex(u.Operand, t) is { } operand:
                return ($"({(u.OperatorKind == UnaryOperatorKind.Minus ? "-" : "~")}{operand.Text})", false);
            default:
                return null;
        }
    }

    /// <summary>The name a stream takes as a parameter: a foreach's variable keeps its own; a span read at the index is <c>__e_s</c>.</summary>
    private static string StreamName(ISymbol span, bool forEach = false, int offset = 0) =>
        forEach ? global::EasyEcs.Generator.Code.Escape(span.Name) : "__e_" + span.Name + (offset == 0 ? "" : offset < 0 ? $"_m{-offset}" : $"_p{offset}");

    /// <summary><c> + k</c> / <c> - k</c> as C# writes an offset after the index, "" for 0.</summary>
    internal static string Shift(int offset) => offset == 0 ? "" : offset < 0 ? $" - {-offset}" : $" + {offset}";

    /// <summary>
    /// The expression an index use <paramref name="name"/> is the whole of (<c>i</c>, <c>i + k</c>, <c>k + i</c>, <c>i - k</c>
    /// for an int constant k up to 64), with k; else the name itself and 0.
    /// </summary>
    private static (ExpressionSyntax Expression, int Offset) OffsetOf(IdentifierNameSyntax name, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (name.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression or (int)SyntaxKind.SubtractExpression } b
            && (b.Left == name || b.IsKind(SyntaxKind.AddExpression))
            && model.GetConstantValue(b.Left == name ? b.Right : b.Left, ct) is { HasValue: true, Value: int k } && k is >= -64 and <= 64)
            return (b, b.IsKind(SyntaxKind.SubtractExpression) ? -k : k);
        return (name, 0);
    }

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
