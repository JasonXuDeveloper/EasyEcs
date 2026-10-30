using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A batch [ForEach] method whose body is a vector loop written by hand (EECS103), read once for the analyzer and its
/// code fix: <c>Vector&lt;T&gt;</c> / <c>Vector64..512&lt;T&gt;</c> values loaded from its component spans, combined lane by
/// lane and stored back, plus a scalar tail. Such a body is the method for one row applied to every lane, so it is
/// rewritten as that method (<see cref="PerRow"/>): each span a <c>ref</c> / <c>in</c> component, each vector statement one
/// statement per field of the components, each broadcast a scalar local, the casts and the tail dropped. The rewrite is
/// reported only when it is exact and the generator's own loop takes it: it is bound in a copy of the compilation (the
/// technique of Kernel.Views.cs) and <see cref="Kernel.Vectorize(IMethodSymbol, MethodDeclarationSyntax, SemanticModel, System.Func{int, Symbols.ParamKind, string}, System.Threading.CancellationToken, out Kernel.ScalarReason?)"/>
/// must return a loop for it under the method's own SimdMode and FloatMode.
/// </summary>
/// <remarks>
/// What qualifies (anything else leaves the method alone): spans, singletons and values as parameters; a prelude of
/// local declarations (streams: <c>MemoryMarshal.Cast&lt;C, T&gt;</c>, <c>Cast&lt;C, VectorN&lt;T&gt;&gt;</c>,
/// <c>Cast&lt;T, VectorN&lt;T&gt;&gt;</c>, <c>ref T r = ref Unsafe.As&lt;C, T&gt;(ref MemoryMarshal.GetReference(span))</c>; the
/// index and its bound; uniform values: constants, singletons and values, static readonly fields, Math, broadcasts),
/// optionally inside <c>if (VectorN.IsHardwareAccelerated)</c>; one loop, either from 0 by 1 over a vector stream's
/// length (<c>p[i]</c>) or by <c>VectorN&lt;T&gt;.Count</c> up to <c>length - Count</c> (loads <c>new Vector&lt;T&gt;(s.Slice(i))</c>,
/// <c>VectorN.Create(s.Slice(i[, Count]))</c>, <c>VectorN.LoadUnsafe(ref r, (nuint)i)</c>; stores <c>v.CopyTo(s.Slice(i))</c>,
/// <c>v.StoreUnsafe(ref r, (nuint)i)</c>); in it, only lane-wise operations with a scalar twin; after it, a scalar tail
/// that writes only the spans' elements and its own locals. Every component a stream reads has the same number F of
/// fields, all of the lane type (F a power of two no larger than the narrowest vector's lanes), so lane j is field
/// j % F of a row.
/// </remarks>
internal static class HandSimd
{
    /// <summary>The method for one row: its parameter list (without parentheses) and its statements, one per line.</summary>
    public sealed record PerRow(string Parameters, string[] Statements)
    {
        /// <summary>The method on one line, for a message: <c>Hurt(ref Health health) { health.Value -= 1; }</c>.</summary>
        public string Summary(string name, int max = 200)
        {
            string text = $"{name}({Parameters}) {{ {string.Join(" ", Statements)} }}";
            return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
        }
    }

    /// <summary>
    /// The syntactic prefilter: a method with attributes whose body names a vector type. Only these are bound further,
    /// so the rule costs a build a scan of the tokens of attributed method bodies.
    /// </summary>
    public static bool MayBe(MethodDeclarationSyntax syntax)
    {
        if (syntax.Body is not { } body || syntax.AttributeLists.Count == 0 || syntax.ParameterList.Parameters.Count == 0) return false;
        foreach (var token in body.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText is "Vector" or "Vector64" or "Vector128" or "Vector256" or "Vector512")
                return true;
        return false;
    }

    /// <summary>
    /// The method for one row that <paramref name="method"/>'s hand-written vector loop computes, when it has one and the
    /// generator's vector loop takes it (EECS103); null otherwise.
    /// </summary>
    public static PerRow? Of(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (!MayBe(syntax) || !Symbols.IsForEach(method) || Symbols.IsEntityMethod(method) || Symbols.Simd(method) == Symbols.SimdOff
            || Symbols.IsGenerated(method) || !method.IsStatic || !method.ReturnsVoid || method.IsGenericMethod
            || method.PartialDefinitionPart != null || method.PartialImplementationPart != null) return null;
        PerRow form;
        try
        {
            form = new Reader(method, syntax, model, ct).Read();
        }
        catch (NotHand)
        {
            return null;
        }

        return Vectorizes(syntax, model, form, ct) ? form : null;
    }

    /// <summary>
    /// Whether <paramref name="form"/>, in place of the method in a copy of the compilation, compiles and gets the
    /// generator's vector loop (the method's attributes kept: its SimdMode and FloatMode decide).
    /// </summary>
    private static bool Vectorizes(MethodDeclarationSyntax syntax, SemanticModel model, PerRow form, System.Threading.CancellationToken ct)
    {
        var parameters = SyntaxFactory.ParseParameterList("(" + form.Parameters + ")");
        if (SyntaxFactory.ParseStatement("{\n" + string.Join("\n", form.Statements) + "\n}") is not BlockSyntax block
            || parameters.ContainsDiagnostics || block.ContainsDiagnostics) return false;
        var annotation = new SyntaxAnnotation("EasyEcs.PerRow");
        var perRow = syntax.WithParameterList(parameters).WithExpressionBody(null).WithSemicolonToken(default).WithBody(block)
            .WithAdditionalAnnotations(annotation);
        var tree = syntax.SyntaxTree;
        var copy = tree.WithRootAndOptions(tree.GetRoot(ct).ReplaceNode(syntax, perRow), tree.Options);
        var copyModel = model.Compilation.ReplaceSyntaxTree(tree, copy).GetSemanticModel(copy);
        var copySyntax = copy.GetRoot(ct).GetAnnotatedNodes(annotation).OfType<MethodDeclarationSyntax>().First();
        if (copyModel.GetDeclaredSymbol(copySyntax, ct) is not { } copyMethod
            || copyModel.GetDiagnostics(copySyntax.Span, ct).Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        return Kernel.Vectorize(copyMethod, copySyntax, copyModel, static (_, _) => "", ct, out Kernel.ScalarReason? _) != null;
    }

    /// <summary>The body is not a hand-written vector loop this rule rewrites.</summary>
    private sealed class NotHand : System.Exception;

    /// <summary>A component span of the method: the component, its lane fields (by name, in memory order), its row parameter.</summary>
    private sealed class Row(IParameterSymbol span, INamedTypeSymbol component, bool readOnly)
    {
        public IParameterSymbol Span { get; } = span;
        public INamedTypeSymbol Component { get; } = component;
        public bool ReadOnly { get; } = readOnly;
        public string[]? Fields;
        public ITypeSymbol? Lane;
        public string Name = "";
        public bool Used, Written;
    }

    /// <summary>A local viewing a span as lanes: of type T (a scalar stream), VectorN&lt;T&gt; (a vector stream) or a ref to T.</summary>
    private sealed record Stream(Row Row, ITypeSymbol Lane, INamedTypeSymbol? Vector, bool Ref);

    /// <summary>A uniform local of the prelude: the statement the method for one row keeps, the uniform locals it reads.</summary>
    private sealed class Uniform(string statement, List<ILocalSymbol> reads, SyntaxNode syntax)
    {
        public string Statement { get; } = statement;
        public List<ILocalSymbol> Reads { get; } = reads;
        public SyntaxNode Syntax { get; } = syntax;
        public bool Used;
    }

    private const int Primary = 16, Unary = 14, Multiplicative = 13, Additive = 12, Shift = 11, Relational = 10, Equality = 9,
        And = 8, Xor = 7, Or = 6, ConditionalAnd = 5, ConditionalOr = 4, Conditional = 3;

    private sealed class Reader(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        private readonly Dictionary<IParameterSymbol, Row> _rows = new(SymbolEqualityComparer.Default);
        private readonly HashSet<IParameterSymbol> _uniformParams = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ILocalSymbol, Stream> _streams = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ILocalSymbol, Uniform> _uniforms = new(SymbolEqualityComparer.Default);
        private readonly List<ILocalSymbol> _order = [];
        private readonly HashSet<ILocalSymbol> _zeroes = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ILocalSymbol, IOperation> _lengths = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ILocalSymbol, string[]> _loopLocals = new(SymbolEqualityComparer.Default);
        private readonly List<StatementSyntax> _tail = [];
        private ForStatementSyntax? _loop;
        private ILocalSymbol? _index;
        private bool _byCount;            // the loop steps by Count (loads and stores at i), else by 1 over a vector stream (p[i])
        private INamedTypeSymbol? _vector; // the loop's vector type (definition), from its bound or step
        private int _laneSize, _fields;
        private string[] _suffixes = [];

        private static NotHand No() => new();

        public PerRow Read()
        {
            bool outside = Symbols.IsRunner(method);
            foreach (var p in method.Parameters)
            {
                switch (Symbols.Classify(p, outside))
                {
                    case Symbols.ParamKind.ComponentSpan when Symbols.SpanElement(p.Type) is INamedTypeSymbol component:
                        _rows[p] = new Row(p, component, Symbols.Named(((INamedTypeSymbol)p.Type).ConstructedFrom, "System.ReadOnlySpan<T>"));
                        break;
                    case Symbols.ParamKind.Singleton when p.RefKind is RefKind.In or RefKind.RefReadOnlyParameter:
                    case Symbols.ParamKind.Context when p.RefKind is RefKind.None or RefKind.In:
                        _uniformParams.Add(p);
                        break;
                    default:
                        throw No();
                }
            }

            if (_rows.Count == 0) throw No();
            Walk(syntax.Body!.Statements, true);
            if (_loop == null) throw No();
            Loop(_loop);
            CheckTail(); // after the loop: its index is no uniform, and the tail counts on with it
            return Assemble();
        }

        // ---------------- the shape: prelude, loop, tail ----------------

        private void Walk(SyntaxList<StatementSyntax> statements, bool top)
        {
            foreach (var statement in statements)
            {
                if (_loop != null)
                {
                    _tail.Add(statement);
                    continue;
                }

                switch (statement)
                {
                    case LocalDeclarationStatementSyntax declaration:
                        Prelude(declaration);
                        break;
                    case ForStatementSyntax loop:
                        _loop = loop;
                        break;
                    case IfStatementSyntax { Else: null, Statement: BlockSyntax block } branch when top && Accelerated(branch.Condition):
                        Walk(block.Statements, false);
                        if (_loop == null) throw No();
                        break;
                    default:
                        throw No();
                }
            }
        }

        /// <summary><c>VectorN.IsHardwareAccelerated</c> (or <c>Vector.IsHardwareAccelerated</c>): a guard of the vector part.</summary>
        private bool Accelerated(ExpressionSyntax condition) =>
            Op(condition) is IPropertyReferenceOperation { Instance: null, Property: { Name: "IsHardwareAccelerated" } property } && IsVectorClass(property.ContainingType);

        /// <summary>
        /// The tail: scalar code (no vector value) that writes only elements of spans and locals, run for the rows
        /// the loop left; the method for one row computes them as it computes the others.
        /// </summary>
        private void CheckTail()
        {
            foreach (var statement in _tail)
            {
                if (model.GetOperation(statement, ct) is not { } op) throw No();
                foreach (var o in op.DescendantsAndSelf())
                {
                    if (IsVector(o.Type)) throw No(); // a second vector loop: VectorN<T>.Count alone is scalar
                    var target = o switch
                    {
                        IAssignmentOperation a => a.Target,
                        IIncrementOrDecrementOperation i => i.Target,
                        _ => null,
                    };
                    if (target == null) continue;
                    // A field of an element, or a positional record's property: the element is written.
                    while (target is IFieldReferenceOperation { Instance: not null, Field.IsStatic: false }
                           or IPropertyReferenceOperation { Instance.Type.IsValueType: true, Property.IsIndexer: false })
                        target = ((IMemberReferenceOperation)target).Instance!;
                    if (target is ILocalReferenceOperation { Local: var local } && !_uniforms.ContainsKey(local)
                        || target is IPropertyReferenceOperation { Property.IsIndexer: true, Instance.Type: INamedTypeSymbol span } && Symbols.SpanElement(span) != null)
                        continue;
                    throw No();
                }
            }
        }

        // ---------------- the prelude ----------------

        private void Prelude(LocalDeclarationStatementSyntax declaration)
        {
            if (declaration.UsingKeyword != default || declaration.Modifiers.Count > 0) throw No();
            bool byRef = declaration.Declaration.Type is RefTypeSyntax;
            foreach (var declarator in declaration.Declaration.Variables)
            {
                if (declarator.Initializer is not { Value: var value } || model.GetDeclaredSymbol(declarator, ct) is not ILocalSymbol local) throw No();
                var init = Strip(Op(value is RefExpressionSyntax reference ? reference.Expression : value));
                if (byRef)
                {
                    _streams[local] = RefStream(init) ?? throw No();
                    continue;
                }

                if (Cast(init, local) is { } stream)
                {
                    _streams[local] = stream;
                    continue;
                }

                if (init.ConstantValue is { HasValue: true, Value: 0 } && local.Type.SpecialType is SpecialType.System_Int32 or SpecialType.System_IntPtr)
                    _zeroes.Add(local); // an index: it may also be read as a uniform 0 until a loop counts with it
                if (init is IPropertyReferenceOperation { Property.Name: "Length", Instance: { } of } && LengthOf(of) != null
                    && local.Type.SpecialType is SpecialType.System_Int32 or SpecialType.System_IntPtr)
                {
                    _lengths[local] = of;
                    continue;
                }

                var reads = new List<ILocalSymbol>();
                string statement;
                if (IsVector(local.Type))
                {
                    // A broadcast: the scalar it repeats, as a local of the lane type.
                    var lane = ((INamedTypeSymbol)local.Type).TypeArguments[0];
                    string scalar = Broadcast(init, lane, reads) ?? throw No();
                    statement = $"{Display(lane)} {local.Name} = {scalar};";
                }
                else
                {
                    if (!IsUniform(init, reads)) throw No(); // a call into user code: the method for one row would call it per row
                    statement = $"{declaration.Declaration.Type} {local.Name} = {value};";
                }

                _uniforms[local] = new Uniform(statement, reads, declarator);
                _order.Add(local);
            }
        }

        /// <summary><c>ref T r = ref Unsafe.As&lt;C, T&gt;(ref MemoryMarshal.GetReference(span))</c> or <c>ref MemoryMarshal.GetReference(stream)</c>.</summary>
        private Stream? RefStream(IOperation init)
        {
            if (init is IInvocationOperation { TargetMethod: { Name: "As", TypeArguments.Length: 2 } asMethod, Arguments.Length: 1 } unsafeAs
                && Symbols.Named(asMethod.ContainingType, "System.Runtime.CompilerServices.Unsafe")
                && GetReference(unsafeAs.Arguments[0].Value) is ({ } row, null)
                && SymbolEqualityComparer.Default.Equals(asMethod.TypeArguments[0], row.Component) && IsLane(asMethod.TypeArguments[1]))
                return Lanes(row, asMethod.TypeArguments[1], null, true);
            if (GetReference(init) is (_, { Vector: null, Ref: false } scalar))
                return scalar with { Ref = true };
            return null;
        }

        /// <summary><c>MemoryMarshal.GetReference(x)</c>: x a span parameter (its row) or a scalar stream.</summary>
        private (Row?, Stream?)? GetReference(IOperation op)
        {
            if (Strip(op) is not IInvocationOperation { TargetMethod.Name: "GetReference", Arguments.Length: 1 } call
                || !Symbols.Named(call.TargetMethod.ContainingType, "System.Runtime.InteropServices.MemoryMarshal")) return null;
            return Strip(call.Arguments[0].Value) switch
            {
                IParameterReferenceOperation { Parameter: var p } when _rows.TryGetValue(p, out var row) => (row, null),
                ILocalReferenceOperation { Local: var l } when _streams.TryGetValue(l, out var stream) => (null, stream),
                _ => null,
            };
        }

        /// <summary><c>MemoryMarshal.Cast&lt;A, B&gt;(x)</c>: a span parameter or a scalar stream viewed as lanes or vectors.</summary>
        private Stream? Cast(IOperation init, ILocalSymbol local)
        {
            if (init is not IInvocationOperation { TargetMethod: { Name: "Cast", TypeArguments.Length: 2 } cast, Arguments.Length: 1 } call
                || !Symbols.Named(cast.ContainingType, "System.Runtime.InteropServices.MemoryMarshal")) return null;
            var to = cast.TypeArguments[1];
            var lane = IsVector(to) ? ((INamedTypeSymbol)to).TypeArguments[0] : to;
            if (!IsLane(lane)) throw No();
            var vector = IsVector(to) ? (INamedTypeSymbol)to : null;
            switch (Strip(call.Arguments[0].Value))
            {
                case IParameterReferenceOperation { Parameter: var p } when _rows.TryGetValue(p, out var row)
                                                                          && SymbolEqualityComparer.Default.Equals(cast.TypeArguments[0], row.Component):
                    return Lanes(row, lane, vector, false);
                case ILocalReferenceOperation { Local: var l } when _streams.TryGetValue(l, out var source) && source is { Vector: null, Ref: false }
                                                                   && vector != null && SymbolEqualityComparer.Default.Equals(source.Lane, lane):
                    return source with { Vector = vector };
                default:
                    throw No();
            }
        }

        /// <summary>A stream of <paramref name="lane"/> over <paramref name="row"/>: every field of its component is a <paramref name="lane"/>.</summary>
        private Stream Lanes(Row row, ITypeSymbol lane, INamedTypeSymbol? vector, bool byRef)
        {
            if (row.Fields == null)
            {
                if (row.Component.GetAttributes().Any(a => Symbols.Named(a.AttributeClass, "System.Runtime.InteropServices.StructLayoutAttribute")
                                                           || Symbols.Named(a.AttributeClass, "System.Runtime.CompilerServices.InlineArrayAttribute")))
                    throw No();
                var fields = new List<string>();
                foreach (var field in row.Component.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic || field.IsConst) continue;
                    if (!SymbolEqualityComparer.Default.Equals(field.Type, lane)) throw No(); // mixed field types: no flat lanes
                    // A positional record's field is its property's backing field: the method reads the property.
                    ISymbol member = Symbols.Associated(field) ?? (field.IsImplicitlyDeclared ? throw No() : field);
                    if (!model.IsAccessible(syntax.SpanStart, member)) throw No();
                    fields.Add(member.Name);
                }

                if (fields.Count == 0) throw No();
                row.Fields = fields.ToArray();
                row.Lane = lane;
            }
            else if (!SymbolEqualityComparer.Default.Equals(row.Lane, lane)) throw No();

            return new Stream(row, lane, vector, byRef);
        }

        // ---------------- the loop ----------------

        /// <summary>Reads the loop: its index, step and bound, the streams' layout, and its statements once (what they use and write).</summary>
        private void Loop(ForStatementSyntax loop)
        {
            // The index: declared by the loop as 0, or a 0 the prelude declared and the loop leaves as it is.
            if (loop.Declaration is { Variables.Count: 1 } declaration && declaration.Variables[0] is { Initializer.Value: var start } variable
                && declaration.Type is not RefTypeSyntax)
            {
                if (model.GetDeclaredSymbol(variable, ct) is not ILocalSymbol index || Op(start).ConstantValue is not { HasValue: true, Value: 0 }
                    || index.Type.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_IntPtr)) throw No();
                _index = index;
            }
            else if (loop.Declaration != null || loop.Initializers.Count > 0) throw No();
            if (loop.Condition is not { } condition || loop.Incrementors.Count != 1) throw No();
            var bound = Strip(Op(condition));
            var step = Op(loop.Incrementors[0]);
            // A 0 of the prelude the loop counts with (its step's target).
            var stepped = (step as IIncrementOrDecrementOperation)?.Target ?? (step as ICompoundAssignmentOperation)?.Target;
            if (_index == null && stepped != null && Strip(stepped) is ILocalReferenceOperation { Local: var candidate } && _zeroes.Contains(candidate))
            {
                _index = candidate;
                _uniforms.Remove(candidate);
            }

            if (_index == null) throw No();
            Step(step);
            Bound(bound);

            var body = loop.Statement is BlockSyntax block ? block.Statements : new SyntaxList<StatementSyntax>(loop.Statement);
            var ops = body.Select(s => model.GetOperation(s, ct) ?? throw No()).ToList();
            Layout(ops);
            var result = new List<string>();
            foreach (var op in ops) Statement(op, result);
        }

        /// <summary>The step: <c>i++</c> (by one over a vector stream) or <c>i += VectorN&lt;T&gt;.Count</c>.</summary>
        private void Step(IOperation step)
        {
            switch (step)
            {
                case IIncrementOrDecrementOperation { Kind: OperationKind.Increment, Target: var t } when IsIndex(t):
                    return;
                case ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Target: var t, Value: var v } when IsIndex(t):
                    if (Strip(v).ConstantValue is { HasValue: true, Value: 1 }) return;
                    _vector = CountOf(v) ?? throw No(); // any other step is no lane-wise loop
                    _byCount = true;
                    return;
                default:
                    throw No();
            }
        }

        /// <summary>
        /// The bound: <c>i &lt; p.Length</c> for a vector stream p (by one); <c>i &lt;= n - Count</c> or <c>i + Count &lt;= n</c>
        /// for the lanes' length n (by Count).
        /// </summary>
        private void Bound(IOperation bound)
        {
            if (bound is not IBinaryOperation { OperatorMethod: null } compare) throw No();
            if (!_byCount)
            {
                if (compare.OperatorKind != BinaryOperatorKind.LessThan || !IsIndex(compare.LeftOperand)
                    || Strip(compare.RightOperand) is not IPropertyReferenceOperation { Property.Name: "Length", Instance: ILocalReferenceOperation { Local: var l } }
                    || !_streams.TryGetValue(l, out var stream) || stream.Vector == null) throw No();
                _vector = stream.Vector.OriginalDefinition;
                Size(stream.Vector);
                return;
            }

            IOperation? length = null;
            if (compare.OperatorKind == BinaryOperatorKind.LessThanOrEqual && IsIndex(compare.LeftOperand)
                && Strip(compare.RightOperand) is IBinaryOperation { OperatorKind: BinaryOperatorKind.Subtract, LeftOperand: var n, RightOperand: var c }
                && SymbolEqualityComparer.Default.Equals(CountOf(c), _vector))
                length = n;
            else if (compare.OperatorKind == BinaryOperatorKind.LessThanOrEqual
                     && Strip(compare.LeftOperand) is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, LeftOperand: var i, RightOperand: var count }
                     && IsIndex(i) && SymbolEqualityComparer.Default.Equals(CountOf(count), _vector))
                length = compare.RightOperand;
            if (length == null) throw No();
            length = Strip(length);
            if (length is ILocalReferenceOperation { Local: var local } && _lengths.TryGetValue(local, out var of)) length = of;
            else if (length is IPropertyReferenceOperation { Property.Name: "Length", Instance: { } instance }) length = instance;
            else throw No();
            _boundOf = LengthOf(length) ?? throw No();
        }

        /// <summary>The lanes a length counts: a scalar stream's, or a span parameter's (rows: one lane each, checked by <see cref="Layout"/>).</summary>
        private object? _boundOf;

        private object? LengthOf(IOperation of) => Strip(of) switch
        {
            ILocalReferenceOperation { Local: var l } when _streams.TryGetValue(l, out var s) && s is { Vector: null, Ref: false } => s,
            IParameterReferenceOperation { Parameter: var p } when _rows.TryGetValue(p, out var row) => row,
            _ => null,
        };

        /// <summary><c>VectorN&lt;T&gt;.Count</c>: its vector type's definition (the loop's width), and the lane size.</summary>
        private INamedTypeSymbol? CountOf(IOperation op)
        {
            if (Strip(op) is not IPropertyReferenceOperation { Instance: null, Property: { Name: "Count" } count } || !IsVector(count.ContainingType)) return null;
            Size(count.ContainingType);
            return count.ContainingType.OriginalDefinition;
        }

        private void Size(INamedTypeSymbol vector)
        {
            var lane = vector.TypeArguments[0];
            if (!IsLane(lane)) throw No();
            int size = SizeOf(lane);
            if (_laneSize != 0 && _laneSize != size) throw No();
            _laneSize = size;
        }

        /// <summary>
        /// The streams the loop reads and writes: every component of them has the same F lane fields (the flat-lane
        /// check), F a power of two no larger than the lanes of the narrowest vector of the loop's type, so lane j of a
        /// vector is field j % F of a row and each vector statement is one statement per field.
        /// </summary>
        private void Layout(List<IOperation> ops)
        {
            foreach (var op in ops)
                foreach (var o in op.DescendantsAndSelf())
                    if (o is ILocalReferenceOperation { Local: var l } && _streams.TryGetValue(l, out var stream))
                    {
                        int f = stream.Row.Fields!.Length;
                        if (_fields != 0 && f != _fields) throw No();
                        _fields = f;
                    }

            if (_fields == 0 || (_fields & (_fields - 1)) != 0) throw No();
            int bytes = Symbols.Named(_vector, "System.Runtime.Intrinsics.Vector64<T>") ? 8
                : Symbols.Named(_vector, "System.Runtime.Intrinsics.Vector256<T>") ? 32
                : Symbols.Named(_vector, "System.Runtime.Intrinsics.Vector512<T>") ? 64
                : 16; // Vector128<T>, and Vector<T> at its narrowest
            if (_fields * _laneSize > bytes) throw No();
            if (_boundOf is Row { Fields.Length: not 1 }) throw No(); // a row count bounds lanes only at one lane per row
            if (_boundOf is Row { Fields: null }) throw No();

            // The suffixes of a vector local's per-field names: the fields of the first component read.
            var first = _streams.Values.First(s => s.Row.Fields!.Length == _fields).Row;
            _suffixes = _fields == 1 ? [""] : first.Fields!.Select(f => char.ToUpperInvariant(f[0]) + f.Substring(1)).ToArray();
        }

        // ---------------- statements ----------------

        private void Statement(IOperation op, List<string> output)
        {
            switch (op)
            {
                case IVariableDeclarationGroupOperation group:
                    foreach (var declaration in group.Declarations)
                        foreach (var declarator in declaration.Declarators)
                        {
                            var local = declarator.Symbol;
                            if ((declarator.Initializer?.Value ?? declaration.Initializer?.Value) is not { } value || !IsVector(local.Type) || local.IsRef) throw No();
                            Check(local.Type);
                            var lane = ((INamedTypeSymbol)local.Type).TypeArguments[0];
                            var names = _suffixes.Select(s => local.Name + s).ToArray();
                            for (int k = 0; k < _fields; k++) output.Add($"{Display(lane)} {names[k]} = {Expr(value, k).Text};");
                            _loopLocals[local] = names; // after the initializer: it cannot read itself
                        }
                    return;
                case IExpressionStatementOperation { Operation: var expression }:
                    for (int k = 0; k < _fields; k++) output.Add(Expression(expression, k));
                    return;
                default:
                    throw No();
            }
        }

        private string Expression(IOperation op, int k)
        {
            switch (op)
            {
                case ISimpleAssignmentOperation { Target: var target, Value: var value, IsRef: false }:
                    return $"{Target(target, k)} = {Expr(value, k).Text};";
                case ICompoundAssignmentOperation { Target: var target, Value: var value } compound:
                {
                    string symbol = Operator(compound.OperatorKind, target.Type!) ?? throw No();
                    return $"{Target(target, k)} {symbol}= {Expr(value, k).Text};";
                }
                case IInvocationOperation call when Store(call) is var (value, row):
                    row.Written = true;
                    if (row.ReadOnly) throw No();
                    return $"{row.Name}.{row.Fields![k]} = {Expr(value, k).Text};";
                default:
                    throw No();
            }
        }

        /// <summary>A store: <c>v.CopyTo(s.Slice(i))</c> or <c>v.StoreUnsafe(ref r, (nuint)i)</c>; the value and its row.</summary>
        private (IOperation, Row)? Store(IInvocationOperation call)
        {
            if (!_byCount) return null;
            var arguments = call.Arguments.Select(a => a.Value).ToList();
            if (call.Instance != null) arguments.Insert(0, call.Instance);
            switch (call.TargetMethod.Name)
            {
                case "CopyTo" when arguments.Count == 2 && IsVector(arguments[0].Type) && Slice(arguments[1]) is { } stream:
                    return (arguments[0], stream.Row);
                case "StoreUnsafe" when arguments.Count == 3 && IsVectorClass(call.TargetMethod.ContainingType)
                                        && Strip(arguments[1]) is ILocalReferenceOperation { Local: var l } && _streams.TryGetValue(l, out var r) && r.Ref
                                        && IsIndex(arguments[2]):
                    return (arguments[0], r.Row);
                default:
                    return null;
            }
        }

        /// <summary>What a statement assigns: a vector local of the loop (a field's name) or a vector stream's element (the row's field).</summary>
        private string Target(IOperation target, int k)
        {
            switch (target)
            {
                case ILocalReferenceOperation { Local: var l } when _loopLocals.TryGetValue(l, out var names):
                    return names[k];
                case IPropertyReferenceOperation { Property.IsIndexer: true } element when Element(element) is { } row:
                    if (row.ReadOnly) throw No();
                    row.Written = true;
                    return $"{row.Name}.{row.Fields![k]}";
                default:
                    throw No(); // a uniform written in the loop is an accumulator across vectors: another order than per row
            }
        }

        /// <summary><c>p[i]</c> of a vector stream, in a loop by one: its row.</summary>
        private Row? Element(IPropertyReferenceOperation element)
        {
            if (_byCount || element.Arguments.Length != 1 || !IsIndex(element.Arguments[0].Value)
                || Strip(element.Instance!) is not ILocalReferenceOperation { Local: var l } || !_streams.TryGetValue(l, out var stream)
                || stream.Vector == null || stream.Ref) return null;
            Check(stream.Vector);
            stream.Row.Used = true;
            return stream.Row;
        }

        /// <summary><c>s.Slice(i)</c> or <c>s.Slice(i, Count)</c> of a scalar stream: the stream.</summary>
        private Stream? Slice(IOperation op)
        {
            if (Strip(op) is not IInvocationOperation { TargetMethod.Name: "Slice", Instance: { } instance } slice
                || Symbols.SpanElement(instance.Type!) == null
                || Strip(instance) is not ILocalReferenceOperation { Local: var l } || !_streams.TryGetValue(l, out var stream)
                || stream.Vector != null || stream.Ref || slice.Arguments.Length is not (1 or 2) || !IsIndex(slice.Arguments[0].Value)
                || slice.Arguments.Length == 2 && !SymbolEqualityComparer.Default.Equals(CountOf(slice.Arguments[1].Value), _vector)) return null;
            stream.Row.Used = true;
            return stream;
        }

        // ---------------- expressions ----------------

        /// <summary>Lane <paramref name="k"/> of a vector expression as the scalar expression of field k of a row, with its precedence.</summary>
        private (string Text, int Precedence) Expr(IOperation op, int k)
        {
            if (op.Type is not { } type || !IsVector(type)) return Scalar(op);
            Check(type);
            var lane = ((INamedTypeSymbol)type).TypeArguments[0];
            switch (op)
            {
                case IParenthesizedOperation p:
                    return Expr(p.Operand, k);
                case IConversionOperation { OperatorMethod: null, Operand: var operand } when operand.Type is { } from && SymbolEqualityComparer.Default.Equals(from, type):
                    return Expr(operand, k);
                case ILocalReferenceOperation { Local: var l } when _loopLocals.TryGetValue(l, out var names):
                    return (names[k], Primary);
                case ILocalReferenceOperation { Local: var l } when _uniforms.TryGetValue(l, out var uniform):
                    Use(uniform);
                    return (l.Name, Primary);
                case IPropertyReferenceOperation { Instance: null, Property.Name: var name }:
                    return Constant(name, lane);
                case IPropertyReferenceOperation { Property.IsIndexer: true } element when Element(element) is { } row:
                    return ($"{row.Name}.{row.Fields![k]}", Primary);
                case IObjectCreationOperation { Arguments.Length: 1 } creation:
                    return Load(creation.Arguments[0].Value, lane, k);
                case IInvocationOperation call when IsVectorClass(call.TargetMethod.ContainingType):
                    return Call(call, lane, k);
                case IBinaryOperation { OperatorKind: var kind } binary:
                {
                    if (kind == BinaryOperatorKind.Divide && !IsFloat(lane)) throw No(); // integer division by 0 throws per row only
                    string symbol = Operator(kind, type) ?? throw No();
                    return Binary(symbol, Expr(binary.LeftOperand, k), Expr(binary.RightOperand, k));
                }
                case IUnaryOperation { OperatorKind: UnaryOperatorKind.Plus, Operand: var operand }:
                    return Expr(operand, k);
                case IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus, Operand: var operand }:
                    return Prefix("-", Expr(operand, k));
                case IUnaryOperation { OperatorKind: UnaryOperatorKind.BitwiseNegation, Operand: var operand } when !IsFloat(lane):
                    return Prefix("~", Expr(operand, k));
                default:
                    throw No();
            }
        }

        /// <summary>A static method of Vector / Vector64..512: a load, a broadcast or a lane-wise operation with a scalar twin.</summary>
        private (string, int) Call(IInvocationOperation call, ITypeSymbol lane, int k)
        {
            var args = call.Arguments.Select(a => a.Value).ToList();
            if (call.Instance != null) throw No();
            string math = IsFloat(lane) ? MathOf(lane) : "";
            switch (call.TargetMethod.Name, args.Count)
            {
                case ("Create", 1):
                    return Load(args[0], lane, k);
                case ("LoadUnsafe", 2) when _byCount && Strip(args[0]) is ILocalReferenceOperation { Local: var l } && _streams.TryGetValue(l, out var r)
                                            && r.Ref && IsIndex(args[1]):
                    r.Row.Used = true;
                    return ($"{r.Row.Name}.{r.Row.Fields![k]}", Primary);
                case ("Add", 2): return Binary("+", Expr(args[0], k), Expr(args[1], k));
                case ("Subtract", 2): return Binary("-", Expr(args[0], k), Expr(args[1], k));
                case ("Multiply", 2): return Binary("*", Expr(args[0], k), Expr(args[1], k));
                case ("Divide", 2) when IsFloat(lane): return Binary("/", Expr(args[0], k), Expr(args[1], k));
                case ("BitwiseAnd", 2) when !IsFloat(lane): return Binary("&", Expr(args[0], k), Expr(args[1], k));
                case ("BitwiseOr", 2) when !IsFloat(lane): return Binary("|", Expr(args[0], k), Expr(args[1], k));
                case ("Xor", 2) when !IsFloat(lane): return Binary("^", Expr(args[0], k), Expr(args[1], k));
                case ("AndNot", 2) when !IsFloat(lane): return Binary("&", Expr(args[0], k), Prefix("~", Expr(args[1], k)));
                case ("Negate", 1): return Prefix("-", Expr(args[0], k));
                case ("OnesComplement", 1) when !IsFloat(lane): return Prefix("~", Expr(args[0], k));
                case ("ShiftLeft", 2) when !IsFloat(lane): return Binary("<<", Expr(args[0], k), Scalar(args[1]));
                case ("ShiftRightArithmetic", 2) when !IsFloat(lane): return Binary(">>", Expr(args[0], k), Scalar(args[1]));
                case ("ShiftRightLogical", 2) when !IsFloat(lane): return Binary(">>>", Expr(args[0], k), Scalar(args[1]));
                case ("Min" or "Max", 2):
                    return ($"{(IsFloat(lane) ? math : MathOf(null))}.{call.TargetMethod.Name}({Expr(args[0], k).Text}, {Expr(args[1], k).Text})", Primary);
                case ("Abs" or "Floor" or "Ceiling", 1) when IsFloat(lane): // integer Abs of MinValue throws per row only
                    return ($"{math}.{call.TargetMethod.Name}({Expr(args[0], k).Text})", Primary);
                case ("Sqrt" or "SquareRoot", 1) when IsFloat(lane):
                    return ($"{math}.Sqrt({Expr(args[0], k).Text})", Primary);
                case ("FusedMultiplyAdd", 3) when IsFloat(lane):
                    return ($"{math}.FusedMultiplyAdd({Expr(args[0], k).Text}, {Expr(args[1], k).Text}, {Expr(args[2], k).Text})", Primary);
                case ("ConditionalSelect", 3):
                    return Choose(Condition(args[0], k), Expr(args[1], k), Expr(args[2], k));
                case ("ConvertToInt32" or "ConvertToUInt32" or "ConvertToInt64" or "ConvertToUInt64" or "ConvertToSingle" or "ConvertToDouble", 1)
                    when args[0].Type is INamedTypeSymbol from && IsVector(from) && SizeOf(from.TypeArguments[0]) == SizeOf(lane):
                    return ($"({Display(lane)}){Paren(Expr(args[0], k), Unary)}", Unary);
                default:
                    throw No(); // a shuffle, a horizontal operation, a comparison as a value, an intrinsic of its own
            }
        }

        /// <summary>A vector from a span (a load: field k of the row) or from one scalar (a broadcast: the scalar).</summary>
        private (string, int) Load(IOperation argument, ITypeSymbol lane, int k)
        {
            if (Slice(argument) is { } stream && _byCount) return ($"{stream.Row.Name}.{stream.Row.Fields![k]}", Primary);
            var reads = new List<ILocalSymbol>();
            if (Broadcast1(argument, lane, reads) is null) throw No();
            foreach (var l in reads) Use(_uniforms[l]);
            return Lane(argument, lane);
        }

        /// <summary>The mask of a ConditionalSelect as a condition: comparisons joined by and, or and not.</summary>
        private (string, int) Condition(IOperation op, int k)
        {
            op = Strip(op);
            if (op is IInvocationOperation call && IsVectorClass(call.TargetMethod.ContainingType) && call.Instance == null)
            {
                var a = call.Arguments.Select(x => x.Value).ToList();
                switch (call.TargetMethod.Name, a.Count)
                {
                    case ("GreaterThan", 2): return Compare(">", a, k);
                    case ("GreaterThanOrEqual", 2): return Compare(">=", a, k);
                    case ("LessThan", 2): return Compare("<", a, k);
                    case ("LessThanOrEqual", 2): return Compare("<=", a, k);
                    case ("Equals", 2): return Compare("==", a, k);
                    case ("BitwiseAnd", 2): return Binary("&&", Condition(a[0], k), Condition(a[1], k));
                    case ("BitwiseOr", 2): return Binary("||", Condition(a[0], k), Condition(a[1], k));
                    case ("AndNot", 2): return Binary("&&", Condition(a[0], k), Prefix("!", Condition(a[1], k)));
                    case ("OnesComplement", 1): return Prefix("!", Condition(a[0], k));
                }
            }
            else if (op is IBinaryOperation { OperatorKind: BinaryOperatorKind.And or BinaryOperatorKind.Or } both && IsVector(op.Type))
                return Binary(both.OperatorKind == BinaryOperatorKind.And ? "&&" : "||", Condition(both.LeftOperand, k), Condition(both.RightOperand, k));
            else if (op is IUnaryOperation { OperatorKind: UnaryOperatorKind.BitwiseNegation, Operand: var operand } && IsVector(op.Type))
                return Prefix("!", Condition(operand, k));

            throw No();
        }

        private (string, int) Compare(string symbol, List<IOperation> operands, int k) => Binary(symbol, Expr(operands[0], k), Expr(operands[1], k));

        /// <summary>A scalar operand of a lane-wise operation: uniform (the same in every lane), written as it is.</summary>
        private (string, int) Scalar(IOperation op)
        {
            var reads = new List<ILocalSymbol>();
            if (!IsUniform(op, reads)) throw No(); // the index, a stream's element or length, a call
            foreach (var l in reads) Use(_uniforms[l]);
            return op.Type is { } type && IsLane(type) ? Lane(op, type) : (Text(op.Syntax), Precedence(op.Syntax));
        }

        /// <summary>
        /// A uniform scalar of lane type <paramref name="lane"/> as written, cast to it where C# converted it and would not
        /// convert it alike in a scalar expression (an int literal next to a uint is a long).
        /// </summary>
        private (string, int) Lane(IOperation op, ITypeSymbol lane)
        {
            var written = (Text(op.Syntax), Precedence(op.Syntax));
            if (SymbolEqualityComparer.Default.Equals(Strip(op).Type, lane) || lane.SpecialType is not (SpecialType.System_UInt32 or SpecialType.System_UInt64))
                return written;
            return ($"({Display(lane)}){Paren(written, Unary)}", Unary);
        }

        /// <summary>VectorN&lt;T&gt;.One / Zero / AllBitsSet as a literal of T.</summary>
        private static (string, int) Constant(string name, ITypeSymbol lane)
        {
            string? text = (name, lane.SpecialType) switch
            {
                ("One", SpecialType.System_Single) => "1f",
                ("Zero", SpecialType.System_Single) => "0f",
                ("One", SpecialType.System_Double) => "1d",
                ("Zero", SpecialType.System_Double) => "0d",
                ("One", SpecialType.System_Int32) => "1",
                ("Zero", SpecialType.System_Int32) => "0",
                ("AllBitsSet", SpecialType.System_Int32) => "-1",
                ("One", SpecialType.System_UInt32) => "1u",
                ("Zero", SpecialType.System_UInt32) => "0u",
                ("AllBitsSet", SpecialType.System_UInt32) => "uint.MaxValue",
                ("One", SpecialType.System_Int64) => "1L",
                ("Zero", SpecialType.System_Int64) => "0L",
                ("AllBitsSet", SpecialType.System_Int64) => "-1L",
                ("One", SpecialType.System_UInt64) => "1UL",
                ("Zero", SpecialType.System_UInt64) => "0UL",
                ("AllBitsSet", SpecialType.System_UInt64) => "ulong.MaxValue",
                _ => null,
            };
            return text == null ? throw No() : (text, text[0] == '-' ? Unary : Primary);
        }

        // ---------------- uniforms ----------------

        /// <summary>The scalar a prelude vector repeats: <c>new Vector&lt;T&gt;(u)</c>, <c>VectorN.Create(u)</c>, <c>VectorN&lt;T&gt;.One</c> / <c>Zero</c>.</summary>
        private string? Broadcast(IOperation init, ITypeSymbol lane, List<ILocalSymbol> reads)
        {
            switch (init)
            {
                case IObjectCreationOperation { Arguments.Length: 1 } creation:
                    return Broadcast1(creation.Arguments[0].Value, lane, reads);
                case IInvocationOperation { TargetMethod.Name: "Create", Arguments.Length: 1, Instance: null } call when IsVectorClass(call.TargetMethod.ContainingType):
                    return Broadcast1(call.Arguments[0].Value, lane, reads);
                case IPropertyReferenceOperation { Instance: null, Property.Name: var name } when IsVector(init.Type!):
                    return Constant(name, lane).Item1;
                default:
                    return null; // several values: not a broadcast
            }
        }

        private string? Broadcast1(IOperation value, ITypeSymbol lane, List<ILocalSymbol> reads) =>
            SymbolEqualityComparer.Default.Equals(value.Type, lane) && IsUniform(value, reads) ? Text(value.Syntax) : null;

        /// <summary>
        /// The same value in every lane and on every row: constants, the method's singletons and values (and their
        /// fields), static readonly fields, uniform locals, arithmetic and Math / MathF of them. A call into user code is
        /// not (the method for one row would call it per row).
        /// </summary>
        private bool IsUniform(IOperation op, List<ILocalSymbol> reads)
        {
            if (op.ConstantValue.HasValue) return true;
            if (op.Type != null && IsVector(op.Type)) return false;
            switch (op)
            {
                case IParenthesizedOperation p:
                    return IsUniform(p.Operand, reads);
                case IConversionOperation { OperatorMethod: null } c:
                    return IsUniform(c.Operand, reads);
                case IParameterReferenceOperation { Parameter: var p }:
                    return _uniformParams.Contains(p);
                case ILocalReferenceOperation { Local: var l } when _uniforms.ContainsKey(l):
                    reads.Add(l);
                    return true;
                case IFieldReferenceOperation { Field: var f, Instance: var instance }:
                    return f.IsStatic ? f.IsReadOnly || f.IsConst : instance != null && IsUniform(instance, reads);
                case IPropertyReferenceOperation { Property: { IsStatic: false, IsIndexer: false } property, Instance: { } instance }
                    when Symbols.BackingField(property) != null:
                    return IsUniform(instance, reads); // an auto-property (a positional record's): a field
                case IBinaryOperation { OperatorMethod: null } b:
                    return IsUniform(b.LeftOperand, reads) && IsUniform(b.RightOperand, reads);
                case IUnaryOperation { OperatorMethod: null } u:
                    return IsUniform(u.Operand, reads);
                case IConditionalOperation { WhenFalse: { } whenFalse } c:
                    return IsUniform(c.Condition, reads) && IsUniform(c.WhenTrue, reads) && IsUniform(whenFalse, reads);
                case IInvocationOperation { Instance: null, TargetMethod: var m } call
                    when Symbols.Named(m.ContainingType, "System.Math") || Symbols.Named(m.ContainingType, "System.MathF"):
                    return call.Arguments.All(a => a.ArgumentKind == ArgumentKind.Explicit && IsUniform(a.Value, reads));
                default:
                    return false;
            }
        }

        /// <summary>A uniform local the method for one row reads: kept, with the uniform locals it reads.</summary>
        private void Use(Uniform uniform)
        {
            if (uniform.Used) return;
            uniform.Used = true;
            foreach (var l in uniform.Reads) Use(_uniforms[l]);
        }

        // ---------------- the method for one row ----------------

        private PerRow Assemble()
        {
            // The names the kept text uses: a row parameter may not shadow one of them.
            var taken = new HashSet<string>();
            void Names(SyntaxNode node)
            {
                foreach (var token in node.DescendantTokens())
                    if (token.IsKind(SyntaxKind.IdentifierToken)) taken.Add(token.ValueText);
            }
            Names(_loop!.Statement);
            foreach (var l in _order)
                if (_uniforms.TryGetValue(l, out var u) && u.Used) Names(u.Syntax);
            foreach (var names in _loopLocals.Values) taken.UnionWith(names);
            foreach (var p in method.Parameters)
                if (!_rows.ContainsKey(p)) taken.Add(p.Name);
            foreach (var row in _rows.Values)
            {
                taken.Remove(row.Span.Name);
                foreach (var stream in _streams.Where(s => s.Value.Row == row)) taken.Remove(stream.Key.Name);
            }
            if (_index != null) taken.Remove(_index.Name);

            var parameters = new List<string>();
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var p = method.Parameters[i];
                if (!_rows.TryGetValue(p, out var row))
                {
                    parameters.Add(syntax.ParameterList.Parameters[i].ToString());
                    continue;
                }

                if (!row.Used || row.Fields == null) throw No(); // a span the loop does not read: the method for one row would not use it
                string camel = char.ToLowerInvariant(row.Component.Name[0]) + row.Component.Name.Substring(1);
                string name = Free(camel, taken) ? camel : Free(p.Name, taken) ? p.Name : throw No();
                taken.Add(name);
                row.Name = name;
                parameters.Add($"{(row.Written ? "ref" : "in")} {row.Component.ToMinimalDisplayString(model, syntax.SpanStart)} {name}");
            }

            // The rows' names are known only now: the loop's statements are written again with them.
            var statements = new List<string>();
            foreach (var l in _order)
                if (_uniforms.TryGetValue(l, out var u) && u.Used) statements.Add(u.Statement);
            _loopLocals.Clear();
            foreach (var row in _rows.Values) row.Written = false;
            var body = _loop.Statement is BlockSyntax block ? block.Statements : new SyntaxList<StatementSyntax>(_loop.Statement);
            foreach (var statement in body) Statement(model.GetOperation(statement, ct)!, statements);
            return new PerRow(string.Join(", ", parameters), statements.ToArray());
        }

        private static bool Free(string name, HashSet<string> taken) => !taken.Contains(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None
                                                                        && SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None;

        // ---------------- helpers ----------------

        private IOperation Op(SyntaxNode node) => model.GetOperation(node, ct) ?? throw No();

        private bool IsIndex(IOperation op) => Strip(op) is ILocalReferenceOperation { Local: var l } && SymbolEqualityComparer.Default.Equals(l, _index);

        /// <summary>Every vector of the loop has its type (width) and lane size.</summary>
        private void Check(ITypeSymbol type)
        {
            var vector = (INamedTypeSymbol)type;
            if (!SymbolEqualityComparer.Default.Equals(vector.OriginalDefinition, _vector) || !IsLane(vector.TypeArguments[0])
                || SizeOf(vector.TypeArguments[0]) != _laneSize) throw No();
        }

        /// <summary>The Math class of a lane type's functions, as the method's file can name it.</summary>
        private string MathOf(ITypeSymbol? lane)
        {
            string name = lane?.SpecialType == SpecialType.System_Single ? "MathF" : "Math";
            return model.LookupNamespacesAndTypes(syntax.SpanStart, null, name).Any(s => Symbols.Named(s, "System." + name)) ? name : "System." + name;
        }

        private string Display(ITypeSymbol type) => type.ToMinimalDisplayString(model, syntax.SpanStart);
    }

    // ---------------- text ----------------

    private static IOperation Strip(IOperation op)
    {
        while (true)
        {
            switch (op)
            {
                case IConversionOperation { OperatorMethod: null, Operand: var operand }:
                    op = operand;
                    continue;
                case IParenthesizedOperation { Operand: var operand }:
                    op = operand;
                    continue;
                default:
                    return op;
            }
        }
    }

    private static string Text(SyntaxNode syntax) => syntax.WithoutTrivia().ToString();

    private static int Precedence(SyntaxNode syntax) => syntax switch
    {
        IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax
            or ParenthesizedExpressionSyntax or PredefinedTypeSyntax or ThisExpressionSyntax => Primary,
        LiteralExpressionSyntax literal => literal.Token.ValueText.StartsWith("-") ? Unary : Primary,
        PrefixUnaryExpressionSyntax or CastExpressionSyntax => Unary,
        BinaryExpressionSyntax binary => BinaryPrecedence(binary.OperatorToken.ValueText),
        ConditionalExpressionSyntax => Conditional,
        _ => 0,
    };

    private static int BinaryPrecedence(string symbol) => symbol switch
    {
        "*" or "/" or "%" => Multiplicative,
        "+" or "-" => Additive,
        "<<" or ">>" or ">>>" => Shift,
        "<" or ">" or "<=" or ">=" => Relational,
        "==" or "!=" => Equality,
        "&" => And,
        "^" => Xor,
        "|" => Or,
        "&&" => ConditionalAnd,
        "||" => ConditionalOr,
        _ => 0,
    };

    private static string Paren((string Text, int Precedence) operand, int needed) =>
        operand.Precedence < needed ? $"({operand.Text})" : operand.Text;

    private static (string, int) Binary(string symbol, (string, int) left, (string, int) right)
    {
        int p = BinaryPrecedence(symbol);
        return ($"{Paren(left, p)} {symbol} {Paren(right, p + 1)}", p);
    }

    private static (string, int) Prefix(string symbol, (string Text, int Precedence) operand)
    {
        string text = Paren(operand, Unary);
        if (text.StartsWith("-") || text.StartsWith("+") || text.StartsWith("~") && symbol == "~") text = $"({text})";
        return (symbol + text, Unary);
    }

    private static (string, int) Choose((string, int) condition, (string, int) whenTrue, (string, int) whenFalse) =>
        ($"{Paren(condition, ConditionalOr)} ? {Paren(whenTrue, ConditionalOr)} : {Paren(whenFalse, Conditional)}", Conditional);

    /// <summary>The scalar operator of a lane-wise vector operator, or null (a comparison is a whole-vector bool).</summary>
    private static string? Operator(BinaryOperatorKind kind, ITypeSymbol type)
    {
        bool floats = type is INamedTypeSymbol { TypeArguments.Length: 1 } v && IsFloat(v.TypeArguments[0]);
        return kind switch
        {
            BinaryOperatorKind.Add => "+",
            BinaryOperatorKind.Subtract => "-",
            BinaryOperatorKind.Multiply => "*",
            BinaryOperatorKind.Divide when floats => "/",
            BinaryOperatorKind.And when !floats => "&",
            BinaryOperatorKind.Or when !floats => "|",
            BinaryOperatorKind.ExclusiveOr when !floats => "^",
            BinaryOperatorKind.LeftShift when !floats => "<<",
            BinaryOperatorKind.RightShift when !floats => ">>",
            BinaryOperatorKind.UnsignedRightShift when !floats => ">>>",
            _ => null,
        };
    }

    // ---------------- types ----------------

    /// <summary>System.Numerics.Vector&lt;T&gt; or System.Runtime.Intrinsics.Vector64..512&lt;T&gt;.</summary>
    private static bool IsVector(ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
        && named.OriginalDefinition is var d && (Symbols.Named(d, "System.Numerics.Vector<T>") || Symbols.Named(d, "System.Runtime.Intrinsics.Vector64<T>")
                                                 || Symbols.Named(d, "System.Runtime.Intrinsics.Vector128<T>") || Symbols.Named(d, "System.Runtime.Intrinsics.Vector256<T>")
                                                 || Symbols.Named(d, "System.Runtime.Intrinsics.Vector512<T>"));

    /// <summary>The static classes of the vector operations: System.Numerics.Vector, System.Runtime.Intrinsics.Vector64..512.</summary>
    private static bool IsVectorClass(ITypeSymbol? type) =>
        Symbols.Named(type, "System.Numerics.Vector") || Symbols.Named(type, "System.Runtime.Intrinsics.Vector64")
        || Symbols.Named(type, "System.Runtime.Intrinsics.Vector128") || Symbols.Named(type, "System.Runtime.Intrinsics.Vector256")
        || Symbols.Named(type, "System.Runtime.Intrinsics.Vector512");

    /// <summary>The lanes whose arithmetic C# keeps in their own type (no promotion to int): float, double, int, uint, long, ulong.</summary>
    private static bool IsLane(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    private static bool IsFloat(ITypeSymbol type) => type.SpecialType is SpecialType.System_Single or SpecialType.System_Double;

    private static int SizeOf(ITypeSymbol type) => type.SpecialType is SpecialType.System_Double or SpecialType.System_Int64 or SpecialType.System_UInt64 ? 8 : 4;
}
