using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A [ForEach] method written for one row, translated into a <c>System.Numerics.Vector&lt;T&gt;</c> loop over its runs of
/// rows (the idea of Burst and ISPC: the method is the kernel of one lane, the generator writes the vector loop). A
/// method qualifies when every component it takes holds one field of one primitive type <c>T</c> (all the same: one
/// lane count), and its body is arithmetic, comparisons, <c>?:</c>, <c>if</c> / <c>else</c>, <c>return</c> and
/// <c>Math</c> / <c>MathF</c> functions with an exact vector counterpart, on those fields, locals and uniform values
/// (constants, static readonly fields, singletons and context values, broadcast once per run). Branches become masks:
/// an assignment under a condition is a <c>ConditionalSelect</c>, a <c>return</c> clears its lanes. The rows left
/// after the last whole vector run the method itself, so the results are those of the scalar loop, bit for bit: every
/// operation used is the same IEEE operation per lane (no reassociation, no fused multiply-add the method did not
/// write), and integer arithmetic wraps both ways. Anything else (calls, casts between lane types, integer division,
/// checked arithmetic, <c>Math.Abs</c> of an integer, which throws) leaves the method scalar, as written.
/// </summary>
internal sealed class Kernel
{
    /// <param name="Element">The lane type (<c>float</c>).</param>
    /// <param name="Setup">Statements before the vector loop: the uniform values and their broadcasts.</param>
    /// <param name="Body">The vector loop's body: loads, the translated method, stores.</param>
    internal sealed record Code(string Element, string Setup, string Body);

    private const string V = "global::System.Numerics.Vector";
    private readonly string _e, _vec;
    private readonly ITypeSymbol _element;
    private readonly Dictionary<IParameterSymbol, (int Index, IFieldSymbol Field, bool Ref)> _components = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IParameterSymbol, string> _uniformParams = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ILocalSymbol, string> _locals = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, string> _broadcasts = new();
    private readonly StringBuilder _setup = new(), _body = new();
    private readonly HashSet<int> _written = new();
    private string? _alive; // lanes that have not returned yet, once a return under a condition cleared some
    private int _temps;

    private Kernel(ITypeSymbol element)
    {
        _element = element;
        _e = element.ToDisplayString(Symbols.Fqn);
        _vec = $"{V}<{_e}>";
    }

    /// <summary>Why the method runs one row at a time: the first construct the vector loop cannot express.</summary>
    private sealed class Unsupported(string reason) : System.Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>
    /// The vector loop of a [ForEach] method, or null with the reason it runs one row at a time
    /// (<paramref name="reason"/>: "component Position has 3 fields"). The one test of what qualifies: the generator
    /// emits the loop it returns, and the analyzer reports the reason (EECS055, EECS056), so a method the generator
    /// leaves scalar is exactly a method reported. <paramref name="fieldOf"/> names the job field holding parameter i
    /// (a singleton's <c>s{i}</c>, a context value's <c>x{i}</c>).
    /// </summary>
    public static Code? Vectorize(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct, out string? reason)
    {
        reason = Symbols.Simd(method) == Symbols.SimdOff ? "Simd = SimdMode.Off"
            : !method.IsStatic ? "it is not static"
            : Symbols.IsParallel(method) ? "it runs in parallel (Parallel = true), and parallel jobs have no vector loop"
            : !Symbols.IsStable(method, syntax, model) ? "it can make structural changes"
            : null;
        if (reason != null) return null;
        try
        {
            return Translate(method, syntax, model, fieldOf, ct);
        }
        catch (Unsupported unsupported)
        {
            reason = unsupported.Reason;
            return null;
        }
    }

    /// <summary>
    /// Whether a method <see cref="Vectorize"/> leaves scalar looks meant for a vector loop, so EECS055 names the reason:
    /// it is static, runs on one thread, takes only components (by <c>ref</c> or <c>in</c>), singletons and values (no
    /// Entity, World, span, Maybe or Mut), and each component is a struct of lane-type fields only (float, double,
    /// int, uint, long, ulong). The reason is then about its components or its body.
    /// </summary>
    public static bool LooksEligible(IMethodSymbol method)
    {
        if (!method.IsStatic || Symbols.IsParallel(method)) return false;
        bool outside = Symbols.IsRunner(method), any = false;
        foreach (var p in method.Parameters)
            switch (Symbols.Classify(p, outside))
            {
                case Symbols.ParamKind.Component:
                    var fields = p.Type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
                    if (fields.Length == 0 || fields.Any(f => !IsLaneType(f.Type))) return false;
                    any = true;
                    break;
                case Symbols.ParamKind.Singleton or Symbols.ParamKind.Context:
                    break;
                default:
                    return false;
            }
        return any;
    }

    private static bool IsLaneType(ITypeSymbol type) => type.SpecialType is SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    private static Code Translate(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct)
    {
        if (!method.ReturnsVoid) throw new Unsupported("it returns a value");
        if (method.IsGenericMethod) throw new Unsupported("it is generic");
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        ITypeSymbol? element = null;
        var components = new List<(IParameterSymbol, IFieldSymbol)>();
        bool outside = Symbols.IsRunner(method);
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            switch (kind)
            {
                case Symbols.ParamKind.Component:
                    var field = Lane(p.Type);
                    if (element != null && !SymbolEqualityComparer.Default.Equals(element, field.Type))
                        throw new Unsupported($"lane types {element.ToDisplayString()} and {field.Type.ToDisplayString()} differ");
                    element = field.Type;
                    components.Add((p, field));
                    break;
                case Symbols.ParamKind.Singleton:
                    break;
                case Symbols.ParamKind.Context when p.RefKind == RefKind.Ref:
                    throw new Unsupported($"it takes '{p.Name}' by ref, and a vector loop has no reductions");
                case Symbols.ParamKind.Context:
                    break;
                default:
                    throw new Unsupported(kind switch
                    {
                        Symbols.ParamKind.Entity => "it takes the Entity",
                        Symbols.ParamKind.Entities => "it takes the EntitySpan",
                        Symbols.ParamKind.World => "it takes the World",
                        Symbols.ParamKind.ComponentSpan => "it takes spans: a batch method writes its own loop",
                        Symbols.ParamKind.Optional => $"it takes a Maybe<{Symbols.MaybeElement(p.Type)?.Name}>",
                        Symbols.ParamKind.Mutable => $"it takes a Mut<{Symbols.MutElement(p.Type)?.Name}>",
                        _ => $"parameter '{p.Name}' is not a component, a singleton or a value",
                    });
            }
        }
        if (element == null) throw new Unsupported("it takes no component");
        if (model.GetOperation(syntax, ct) is not IMethodBodyOperation body) throw new Unsupported("it has no body");

        var kernel = new Kernel(element);
        for (int i = 0, k = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            var kind = Symbols.Classify(p, outside);
            if (kind == Symbols.ParamKind.Component)
            {
                var (_, field) = components[k];
                kernel._components[p] = (++k, field, p.RefKind == RefKind.Ref);
                continue;
            }
            string name = $"__p{i}";
            kernel._uniformParams[p] = name;
            string source = fieldOf(i, kind);
            kernel._setup.Append(p.RefKind == RefKind.None
                ? $"var {name} = {source};\n"
                : $"ref readonly {p.Type.ToDisplayString(Symbols.Fqn)} {name} = ref {source};\n");
        }

        foreach (var c in kernel._components.Values.OrderBy(c => c.Index))
            kernel._body.Append($"var __c{c.Index} = {V}.LoadUnsafe(ref e{c.Index}, (nuint)i);\n");
        kernel.Statement((IOperation?)body.BlockBody ?? body.ExpressionBody!, null);
        foreach (int c in kernel._written.OrderBy(c => c))
            kernel._body.Append($"{V}.StoreUnsafe(__c{c}, ref e{c}, (nuint)i);\n");
        return new Code(kernel._e, kernel._setup.ToString(), kernel._body.ToString());
    }

    /// <summary>
    /// The one field of a component holding one primitive lane value with C# arithmetic of its own type (no promotion
    /// to int), laid out without padding; otherwise the reason it is no lane.
    /// </summary>
    private static IFieldSymbol Lane(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) throw new Unsupported($"component {type.Name} is not a struct");
        if (named.IsGenericType) throw new Unsupported($"component {type.Name} is generic");
        if (named.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute"))
            throw new Unsupported($"component {type.Name} sets its layout ([StructLayout])");
        var fields = named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        if (fields.Length != 1) throw new Unsupported($"component {type.Name} has {fields.Length} fields");
        var field = fields[0];
        if (field.IsFixedSizeBuffer || !IsLaneType(field.Type))
            throw new Unsupported($"field {type.Name}.{field.AssociatedSymbol?.Name ?? field.Name} is a {field.Type.ToDisplayString()}, not a lane type (float, double, int, uint, long or ulong)");
        return field;
    }

    private bool IsFloat => _element.SpecialType is SpecialType.System_Single or SpecialType.System_Double;
    private bool IsSigned => _element.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 || IsFloat;
    private bool IsLane(ITypeSymbol? type) => SymbolEqualityComparer.Default.Equals(type, _element);
    private static bool IsBool(ITypeSymbol? type) => type?.SpecialType == SpecialType.System_Boolean;
    private string Temp(string prefix) => $"__{prefix}{_temps++}";
    private static string And(string? a, string? b) => a == null ? b! : b == null ? a : $"({a} & {b})";

    // ---------------- statements ----------------

    /// <summary>Translates <paramref name="op"/> for the lanes of <paramref name="mask"/> (null: every lane).</summary>
    /// <returns>False after a return every remaining lane takes: what follows is not reached.</returns>
    private bool Statement(IOperation op, string? mask)
    {
        switch (op)
        {
            case IBlockOperation block:
                foreach (var s in block.Operations)
                    if (!Statement(s, mask)) return false;
                return true;
            case IEmptyOperation:
                return true;
            case IVariableDeclarationGroupOperation group:
                foreach (var declaration in group.Declarations)
                foreach (var declarator in declaration.Declarators)
                {
                    var local = declarator.Symbol;
                    var init = declarator.GetVariableInitializer()?.Value ?? declaration.Initializer?.Value;
                    if (local.IsRef) throw new Unsupported($"ref local '{local.Name}' is not supported");
                    if (init == null) throw new Unsupported($"local '{local.Name}' has no initializer");
                    if (!IsLane(local.Type) && !IsBool(local.Type)) throw new Unsupported($"local '{local.Name}' is a {local.Type.ToDisplayString()}, not the lane type {_element.ToDisplayString()}");
                    string name = Temp("l");
                    _locals[local] = name;
                    _body.Append($"var {name} = {Expr(init)};\n"); // a fresh local: its value in other lanes is never read
                }
                return true;
            case IExpressionStatementOperation { Operation: var e }:
                Assignment(e, mask);
                return true;
            case IConditionalOperation { WhenTrue: var whenTrue, WhenFalse: var whenFalse } branch when branch.Type == null:
            {
                string condition = Temp("m");
                _body.Append($"var {condition} = {Expr(branch.Condition)};\n"); // before either branch writes what it reads
                Statement(whenTrue, And(mask, condition));
                if (whenFalse != null) Statement(whenFalse, And(mask, $"~{condition}"));
                return true;
            }
            case IReturnOperation { ReturnedValue: null }:
                if (mask == null) return false;
                if (_alive == null)
                {
                    _alive = Temp("alive");
                    _body.Append($"var {_alive} = ~{mask};\n");
                }
                else _body.Append($"{_alive} &= ~{mask};\n");
                return true;
            default:
                throw Not(op);
        }
    }

    private void Assignment(IOperation op, string? mask)
    {
        IOperation target;
        string value;
        switch (op)
        {
            case ISimpleAssignmentOperation { IsRef: false } a:
                target = a.Target;
                value = Expr(a.Value);
                break;
            case ICompoundAssignmentOperation a when !a.IsChecked && a.OperatorMethod == null && a.InConversion.IsIdentity && a.OutConversion.IsIdentity:
                target = a.Target;
                value = Binary(a.OperatorKind, Expr(a.Target), a.Target.Type, a.Value);
                break;
            case IIncrementOrDecrementOperation { IsChecked: false, OperatorMethod: null } a when IsLane(a.Type):
                target = a.Target;
                value = $"({Expr(a.Target)} {(a.Kind == OperationKind.Increment ? "+" : "-")} {_vec}.One)";
                break;
            default:
                throw Not(op);
        }

        string name = Target(target);
        string? active = And(mask, _alive);
        _body.Append(active == null ? $"{name} = {value};\n" : $"{name} = {V}.ConditionalSelect<{_e}>({active}, {value}, {name});\n");
    }

    /// <summary>The vector a write goes to: a kernel local, or the lane field of a component taken by ref.</summary>
    private string Target(IOperation target)
    {
        if (target is ILocalReferenceOperation { Local: var local } && _locals.TryGetValue(local, out var name)) return name;
        if (LaneOf(target) is { Ref: true } c)
        {
            _written.Add(c.Index);
            return $"__c{c.Index}";
        }
        throw new Unsupported($"it writes '{Text(target)}', which is neither a local nor the lane of a component taken by ref");
    }

    /// <summary>
    /// The component whose lane <paramref name="op"/> is: its field, or the auto-property backed by it (a record
    /// struct's positional member).
    /// </summary>
    private (int Index, IFieldSymbol Field, bool Ref)? LaneOf(IOperation op)
    {
        var (instance, member) = op switch
        {
            IFieldReferenceOperation f => (f.Instance, (ISymbol)f.Field),
            IPropertyReferenceOperation { Arguments.Length: 0 } p => (p.Instance, p.Property),
            _ => (null, null),
        };
        if (instance is not IParameterReferenceOperation { Parameter: var parameter } || !_components.TryGetValue(parameter, out var c))
            return null;
        return SymbolEqualityComparer.Default.Equals(member, c.Field) || SymbolEqualityComparer.Default.Equals(member, c.Field.AssociatedSymbol)
            ? c : null;
    }

    // ---------------- expressions ----------------

    /// <summary>
    /// <paramref name="op"/> as a Vector&lt;T&gt; of the lane type: a value, or a mask (all bits set where true) for a
    /// bool.
    /// </summary>
    private string Expr(IOperation op)
    {
        if (IsUniform(op))
        {
            if (IsBool(op.Type)) return $"({Scalar(op)} ? {_vec}.AllBitsSet : {_vec}.Zero)";
            if (!IsLane(op.Type)) throw new Unsupported($"'{Text(op)}' is a {op.Type?.ToDisplayString()}, not the lane type {_element.ToDisplayString()}");
            string scalar = Scalar(op);
            if (!_broadcasts.TryGetValue(scalar, out var name))
            {
                name = Temp("u");
                _broadcasts[scalar] = name;
                _setup.Append($"var {name} = new {_vec}({scalar});\n"); // once per run, not per vector
            }
            return name;
        }

        switch (op)
        {
            case IConversionOperation { Conversion.IsIdentity: true } c when IsLane(c.Type) || IsBool(c.Type):
                return Expr(c.Operand);
            case IFieldReferenceOperation or IPropertyReferenceOperation when LaneOf(op) is { } c:
                return $"__c{c.Index}";
            case ILocalReferenceOperation { Local: var local } when _locals.TryGetValue(local, out var name):
                return name;
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } b:
                return Binary(b.OperatorKind, Expr(b.LeftOperand), b.LeftOperand.Type, b.RightOperand, b.Type);
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } u:
                string operand = Expr(u.Operand);
                return u.OperatorKind switch
                {
                    UnaryOperatorKind.Plus when IsLane(u.Type) => operand,
                    UnaryOperatorKind.Minus when IsLane(u.Type) && IsSigned => $"(-{operand})",
                    UnaryOperatorKind.BitwiseNegation when IsLane(u.Type) && !IsFloat => $"(~{operand})",
                    UnaryOperatorKind.Not when IsBool(u.Type) => $"(~{operand})",
                    _ => throw Not(u),
                };
            case IConditionalOperation { IsRef: false, WhenFalse: { } whenFalse } t when IsLane(t.Type) || IsBool(t.Type):
                return $"{V}.ConditionalSelect<{_e}>({Expr(t.Condition)}, {Expr(t.WhenTrue)}, {Expr(whenFalse)})";
            case IInvocationOperation { Instance: null, TargetMethod: var m } call when IsMath(m) && IsLane(m.ReturnType)
                && m.Parameters.All(p => IsLane(p.Type)):
                string args = string.Join(", ", call.Arguments.Select(a => Expr(a.Value)));
                string? vector = m.Name switch
                {
                    "Min" or "Max" => m.Name,                                     // IEEE 754:2019 like Math.Min / Max (NaN, -0)
                    "Abs" when IsFloat => "Abs",                                  // Math.Abs of int.MinValue throws: no integers
                    "Sqrt" when IsFloat => "SquareRoot",
                    "Floor" or "Ceiling" when IsFloat => m.Name,
                    "FusedMultiplyAdd" when IsFloat => "FusedMultiplyAdd",
                    _ => null,
                };
                return vector == null ? throw Not(call) : $"{V}.{vector}({args})";
            default:
                throw Not(op);
        }
    }

    private string Binary(BinaryOperatorKind kind, string left, ITypeSymbol? leftType, IOperation right, ITypeSymbol? type = null)
    {
        type ??= leftType;
        if (kind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift)
        {
            if (!IsLane(type) || IsFloat) throw new Unsupported($"shifts of a {type?.ToDisplayString()} are not supported");
            if (!IsUniform(right)) throw new Unsupported("a shift by a count that differs per row is not supported");
            string count = $"(int)({Scalar(right)})";
            return kind == BinaryOperatorKind.LeftShift ? $"{V}.ShiftLeft({left}, {count})"
                : $"{V}.{(IsSigned ? "ShiftRightArithmetic" : "ShiftRightLogical")}({left}, {count})";
        }

        string r = Expr(right);
        bool lanes = IsLane(leftType) && IsLane(right.Type), masks = IsBool(leftType) && IsBool(right.Type);
        return kind switch
        {
            BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply when lanes && IsLane(type)
                => $"({left} {Symbol(kind)} {r})",
            BinaryOperatorKind.Divide when lanes && IsLane(type) && IsFloat => $"({left} / {r})", // integer division throws on 0
            BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr when lanes && !IsFloat || masks
                => $"({left} {Symbol(kind)} {r})",
            BinaryOperatorKind.ConditionalAnd when masks => $"({left} & {r})",  // no side effects: both sides are evaluated
            BinaryOperatorKind.ConditionalOr when masks => $"({left} | {r})",
            BinaryOperatorKind.Equals when lanes => $"{V}.Equals<{_e}>({left}, {r})",
            BinaryOperatorKind.NotEquals when lanes => $"(~{V}.Equals<{_e}>({left}, {r}))", // NaN != NaN: true, as in C#
            BinaryOperatorKind.LessThan when lanes => $"{V}.LessThan<{_e}>({left}, {r})",
            BinaryOperatorKind.LessThanOrEqual when lanes => $"{V}.LessThanOrEqual<{_e}>({left}, {r})",
            BinaryOperatorKind.GreaterThan when lanes => $"{V}.GreaterThan<{_e}>({left}, {r})",
            BinaryOperatorKind.GreaterThanOrEqual when lanes => $"{V}.GreaterThanOrEqual<{_e}>({left}, {r})",
            BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder when !IsFloat => throw new Unsupported("integer division is not supported (it throws on 0)"),
            _ => throw new Unsupported($"operator {Operator(kind)} on {leftType?.ToDisplayString()} and {right.Type?.ToDisplayString()} is not supported"),
        };
    }

    private static string Symbol(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*",
        BinaryOperatorKind.And => "&", BinaryOperatorKind.Or => "|", _ => "^",
    };

    private static bool IsMath(IMethodSymbol m) => m.ContainingType is { Name: "Math" or "MathF", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };

    // ---------------- uniform values ----------------

    /// <summary>The same in every lane: it reads no component and no kernel local (constants, statics, singletons, context).</summary>
    private bool IsUniform(IOperation op) => op switch
    {
        ILocalReferenceOperation or IInstanceReferenceOperation => false,
        IParameterReferenceOperation { Parameter: var p } => _uniformParams.ContainsKey(p),
        _ => op.ChildOperations.All(IsUniform),
    };

    /// <summary>A uniform value as scalar C# the generated file can compile (fully qualified, constants folded).</summary>
    private string Scalar(IOperation op)
    {
        if (op.ConstantValue is { HasValue: true, Value: var constant }) return Literal(constant, op.Type);
        switch (op)
        {
            case IParameterReferenceOperation { Parameter: var p }:
                return _uniformParams[p];
            case IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } f }:
                return $"{f.ContainingType.ToDisplayString(Symbols.Fqn)}.{f.Name}";
            case IFieldReferenceOperation { Field: { IsStatic: false } f, Instance: { } instance }:
                return $"{Scalar(instance)}.{f.Name}";
            case IPropertyReferenceOperation { Property: { IsStatic: false, Parameters.Length: 0 } p, Instance: { } instance }
                when p.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p)):
                return $"{Scalar(instance)}.{p.Name}"; // an auto-property: a field read, no code of its own
            case IConversionOperation { OperatorMethod: null, IsTryCast: false, Conversion.IsNumeric: true } or IConversionOperation { Conversion.IsIdentity: true }:
                var c = (IConversionOperation)op;
                return $"(({c.Type!.ToDisplayString(Symbols.Fqn)})({Scalar(c.Operand)}))";
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } b when b.OperatorKind is not (BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) || IsFloatType(b.Type):
                return $"({Scalar(b.LeftOperand)} {BinaryText(b.OperatorKind)} {Scalar(b.RightOperand)})";
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } u:
                return $"({UnaryText(u.OperatorKind)}{Scalar(u.Operand)})";
            case IInvocationOperation { Instance: null, TargetMethod: var m } call when IsMath(m) && m.Name is "Min" or "Max" or "Sqrt" or "Floor" or "Ceiling":
                return $"{m.ContainingType.ToDisplayString(Symbols.Fqn)}.{m.Name}({string.Join(", ", call.Arguments.Select(a => Scalar(a.Value)))})";
            default:
                // a call or a property could do anything: not hoisted out of the rows
                throw new Unsupported($"'{Text(op)}' is not a value the loop can compute once per run (a call or a property could do anything)");
        }
    }

    private static bool IsFloatType(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Single or SpecialType.System_Double;

    /// <summary>The C# operator of <paramref name="kind"/> in a uniform value (string concatenation and the like are not).</summary>
    private static string BinaryText(BinaryOperatorKind kind) =>
        Operator(kind) is var text && !char.IsLetter(text[0]) ? text : throw new Unsupported($"operator {text} is not supported");

    private static string UnaryText(UnaryOperatorKind kind) => kind switch
    {
        UnaryOperatorKind.Plus => "+", UnaryOperatorKind.Minus => "-", UnaryOperatorKind.BitwiseNegation => "~", UnaryOperatorKind.Not => "!",
        _ => throw new Unsupported($"operator {kind} is not supported"),
    };

    private static string Literal(object? value, ITypeSymbol? type) => value switch
    {
        float f when float.IsNaN(f) => "float.NaN",
        float f when float.IsPositiveInfinity(f) => "float.PositiveInfinity",
        float f when float.IsNegativeInfinity(f) => "float.NegativeInfinity",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
        double d when double.IsNaN(d) => "double.NaN",
        double d when double.IsPositiveInfinity(d) => "double.PositiveInfinity",
        double d when double.IsNegativeInfinity(d) => "double.NegativeInfinity",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
        bool b => b ? "true" : "false",
        int i => i == int.MinValue ? "int.MinValue" : $"({i.ToString(CultureInfo.InvariantCulture)})",
        uint u => u.ToString(CultureInfo.InvariantCulture) + "u",
        long l => l == long.MinValue ? "long.MinValue" : $"({l.ToString(CultureInfo.InvariantCulture)}L)",
        ulong u => u.ToString(CultureInfo.InvariantCulture) + "UL",
        _ when type is { SpecialType: not SpecialType.None } => $"(({type.ToDisplayString(Symbols.Fqn)})({System.Convert.ToString(value, CultureInfo.InvariantCulture)}))",
        _ => throw new Unsupported($"the constant {value} is not supported"),
    };

    // ---------------- reasons ----------------

    /// <summary>The reason <paramref name="op"/> keeps the method scalar, named the way the method writes it.</summary>
    private Unsupported Not(IOperation op) => new(op switch
    {
        IInvocationOperation { TargetMethod: var m } when IsMath(m) => m.Name == "Abs" && !IsFloatType(m.ReturnType)
            ? $"{m.ContainingType.Name}.Abs of an integer is not supported (it throws on MinValue)"
            : $"{m.ContainingType.Name}.{m.Name} is not supported",
        IInvocationOperation { TargetMethod: var m } => $"it calls {m.ContainingType.Name}.{m.Name}",
        IConversionOperation c => $"the conversion of '{Text(c.Operand)}' from {c.Operand.Type?.ToDisplayString()} to {c.Type?.ToDisplayString()} is not supported",
        IBinaryOperation { IsChecked: true } or IUnaryOperation { IsChecked: true } or ICompoundAssignmentOperation { IsChecked: true }
            => $"checked arithmetic ('{Text(op)}') is not supported",
        IBinaryOperation { OperatorMethod: not null } or IUnaryOperation { OperatorMethod: not null } or ICompoundAssignmentOperation { OperatorMethod: not null }
            => $"it calls a user-defined operator ('{Text(op)}')",
        _ => $"'{Text(op)}' is not supported",
    });

    /// <summary>The source of <paramref name="op"/> on one line, cut short.</summary>
    private static string Text(IOperation op)
    {
        string text = string.Join(" ", op.Syntax.ToString().Split(['\r', '\n', '\t', ' '], System.StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
    }

    private static string Operator(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*", BinaryOperatorKind.Divide => "/",
        BinaryOperatorKind.Remainder => "%", BinaryOperatorKind.And => "&", BinaryOperatorKind.Or => "|", BinaryOperatorKind.ExclusiveOr => "^",
        BinaryOperatorKind.ConditionalAnd => "&&", BinaryOperatorKind.ConditionalOr => "||",
        BinaryOperatorKind.LeftShift => "<<", BinaryOperatorKind.RightShift => ">>", BinaryOperatorKind.UnsignedRightShift => ">>>",
        BinaryOperatorKind.Equals => "==", BinaryOperatorKind.NotEquals => "!=", BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=", BinaryOperatorKind.GreaterThan => ">", BinaryOperatorKind.GreaterThanOrEqual => ">=",
        _ => kind.ToString(),
    };
}
