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
/// method qualifies when every component it takes holds 1 to 4 fields of primitive lane types of one size (float, int
/// and uint, or double, long and ulong: one lane count; a component of several fields is split into one vector per
/// field, <c>EasyEcs.Interleaved</c>), and its body is arithmetic, comparisons, patterns, <c>?:</c>, <c>if</c> /
/// <c>else</c>, <c>return</c>, conversions between those types and <c>Math</c> / <c>MathF</c> functions with an exact
/// vector counterpart, on those fields, locals and uniform values (constants, static readonly fields, singletons and
/// context values, broadcast once per run). Branches become masks: an assignment under a condition is a
/// <c>ConditionalSelect</c>, a <c>return</c> clears its lanes. The rows left after the last whole vector run the method
/// itself, so the results are those of the scalar loop, bit for bit: every operation used is the same IEEE operation per
/// lane (no reassociation, no fused multiply-add the method did not write), integer arithmetic wraps both ways, and
/// conversions saturate like C#'s. A call that would throw for some lane (<c>Math.Clamp</c> with its min above its max)
/// sends the rows of its vector to the method itself. Anything else (other calls, integer division, checked arithmetic,
/// <c>Math.Abs</c> of an integer) leaves the method scalar, as written.
/// </summary>
internal sealed class Kernel
{
    /// <param name="Columns">The lane type of each component's column (its first field), in job order (<c>float</c>).</param>
    /// <param name="Setup">Statements before the vector loop: the uniform values and their broadcasts.</param>
    /// <param name="Body">The vector loop's body up to the stores: loads, the translated method.</param>
    /// <param name="Stores">The stores of the components the method writes.</param>
    /// <param name="MaskedBody">
    /// <paramref name="Body"/> for a word of rows of which some pass (<c>__pass</c>, a lane mask): the loaded values of
    /// the components the method writes are kept too.
    /// </param>
    /// <param name="MaskedStores">The stores of a masked body: the lanes <c>__pass</c> leaves out get the values loaded.</param>
    /// <param name="Size">The lanes' size in bytes: 4 or 8.</param>
    /// <param name="Mask">The element type of a lane mask: <c>int</c> or <c>long</c>.</param>
    /// <param name="Interleaves">A component has several fields: the loop needs <c>EasyEcs.Interleaved.IsAccelerated</c>.</param>
    /// <param name="Bails">
    /// The body sets <c>__bail</c> when a lane would throw: the rows of that vector then run the method itself, before any
    /// store.
    /// </param>
    internal sealed record Code(EquatableArray<string> Columns, string Setup, string Body, string Stores, string MaskedBody,
        string MaskedStores, int Size, string Mask, bool Interleaves, bool Bails);

    private const string V = "global::System.Numerics.Vector";
    private const string Interleaved = "global::EasyEcs.Interleaved";

    /// <summary>The most fields a component of a vector loop has: the forms of <c>EasyEcs.Interleaved</c>.</summary>
    private const int MaxFields = 4;

    private readonly int _size;      // bytes per lane: 4 or 8
    private readonly string _mask;   // the lane type of masks: int or long (all bits set where true)
    /// <summary>The components: their index (job column k), their lane fields in memory order, and whether they are written.</summary>
    private readonly Dictionary<IParameterSymbol, (int Index, IFieldSymbol[] Fields, bool Ref)> _components = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IParameterSymbol, string> _uniformParams = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ILocalSymbol, string> _locals = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, string> _broadcasts = new();
    private readonly StringBuilder _setup = new(), _body = new();
    private readonly HashSet<int> _written = new();
    private string? _alive; // lanes that have not returned yet, once a return under a condition cleared some
    private bool _bails;
    private int _temps;

    private Kernel(int size)
    {
        _size = size;
        _mask = size == 4 ? "int" : "long";
    }

    /// <summary>Why the method runs one row at a time: the first construct the vector loop cannot express.</summary>
    private sealed class Unsupported(string reason) : System.Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>
    /// The vector loop of a [ForEach] method, or null with the reason it runs one row at a time
    /// (<paramref name="reason"/>: "component Position has 5 fields"). The one test of what qualifies: the generator
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

    private static bool IsLaneType(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    private static int SizeOf(ITypeSymbol type) => type.SpecialType is SpecialType.System_Single or SpecialType.System_Int32 or SpecialType.System_UInt32 ? 4 : 8;

    private static Code Translate(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct)
    {
        if (!method.ReturnsVoid) throw new Unsupported("it returns a value");
        if (method.IsGenericMethod) throw new Unsupported("it is generic");
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        ITypeSymbol? first = null;
        var components = new List<IFieldSymbol[]>();
        bool outside = Symbols.IsRunner(method);
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            switch (kind)
            {
                case Symbols.ParamKind.Component:
                    var fields = Lanes(p.Type);
                    foreach (var field in fields)
                    {
                        if (first != null && SizeOf(first) != SizeOf(field.Type))
                            throw new Unsupported($"lane types {first.ToDisplayString()} and {field.Type.ToDisplayString()} differ in size");
                        first ??= field.Type;
                    }
                    components.Add(fields);
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
        if (first == null) throw new Unsupported("it takes no component");
        if (model.GetOperation(syntax, ct) is not IMethodBodyOperation body) throw new Unsupported("it has no body");

        var kernel = new Kernel(SizeOf(first));
        for (int i = 0, k = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            var kind = Symbols.Classify(p, outside);
            if (kind == Symbols.ParamKind.Component)
            {
                kernel._components[p] = (k + 1, components[k], p.RefKind == RefKind.Ref);
                k++;
                continue;
            }
            string name = $"__p{i}";
            kernel._uniformParams[p] = name;
            string source = fieldOf(i, kind);
            kernel._setup.Append(p.RefKind == RefKind.None
                ? $"var {name} = {source};\n"
                : $"ref readonly {p.Type.ToDisplayString(Symbols.Fqn)} {name} = ref {source};\n");
        }

        // A component of several fields is deinterleaved into one vector per field (Interleaved: shuffles, which move
        // bits, so fields of another type of the same size are reinterpreted after the load), and written back whole:
        // the fields the method does not write keep the values loaded.
        var loads = new StringBuilder();
        var stores = new StringBuilder();
        foreach (var (index, fields, _) in kernel._components.Values.OrderBy(c => c.Index))
        {
            string column = Elem(fields[0].Type);
            if (fields.Length == 1) loads.Append($"var __c{index}_0 = {V}.LoadUnsafe(ref e{index}, (nuint)i);\n");
            else
            {
                loads.Append($"{Interleaved}.Load{fields.Length}(ref e{index}, (nuint)i, {string.Join(", ", fields.Select((_, f) => $"out {Vec(column)} __s{index}_{f}"))});\n");
                for (int f = 0; f < fields.Length; f++)
                    loads.Append($"var __c{index}_{f} = {As(column, Elem(fields[f].Type), $"__s{index}_{f}")};\n");
            }
        }
        kernel.Statement((IOperation?)body.BlockBody ?? body.ExpressionBody!, null);
        // A masked word keeps the values loaded of what the method writes, and stores them back in the lanes that do not
        // pass: the rows are left as they were (their memory is the table's, whether a component is absent or not).
        var kept = new StringBuilder();
        var masked = new StringBuilder();
        foreach (var (index, fields, _) in kernel._components.Values.Where(c => kernel._written.Contains(c.Index)).OrderBy(c => c.Index))
        {
            string column = Elem(fields[0].Type);
            string Blend(int f) => $"{V}.ConditionalSelect({As(kernel._mask, Elem(fields[f].Type), "__pass")}, __c{index}_{f}, __o{index}_{f})";
            for (int f = 0; f < fields.Length; f++) kept.Append($"var __o{index}_{f} = __c{index}_{f};\n");
            stores.Append(fields.Length == 1
                ? $"{V}.StoreUnsafe(__c{index}_0, ref e{index}, (nuint)i);\n"
                : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, $"__c{index}_{f}")))}, ref e{index}, (nuint)i);\n");
            masked.Append(fields.Length == 1
                ? $"{V}.StoreUnsafe({Blend(0)}, ref e{index}, (nuint)i);\n"
                : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, Blend(f))))}, ref e{index}, (nuint)i);\n");
        }
        string prologue = kernel._bails ? "bool __bail = false;\n" : "";
        return new Code(new EquatableArray<string>(kernel._components.Values.OrderBy(c => c.Index).Select(c => Elem(c.Fields[0].Type)).ToArray()),
            kernel._setup.ToString(), prologue + loads + kernel._body, stores.ToString(), prologue + loads + kept + kernel._body, masked.ToString(),
            kernel._size, kernel._mask, kernel._components.Values.Any(c => c.Fields.Length > 1), kernel._bails);
    }

    /// <summary>
    /// The fields of a component, in memory order: 1 to <see cref="MaxFields"/> primitive lane values with C# arithmetic
    /// of their own type (no promotion to int), laid out without padding (sequential, declared in one place); otherwise
    /// the reason it has no lanes.
    /// </summary>
    private static IFieldSymbol[] Lanes(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) throw new Unsupported($"component {type.Name} is not a struct");
        if (named.IsGenericType) throw new Unsupported($"component {type.Name} is generic");
        if (named.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute"))
            throw new Unsupported($"component {type.Name} sets its layout ([StructLayout])");
        if (named.DeclaringSyntaxReferences.Length > 1)
            throw new Unsupported($"component {type.Name} is declared in several parts, so the order of its fields is not defined");
        var fields = named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        if (fields.Length == 0) throw new Unsupported($"component {type.Name} has no fields");
        if (fields.Length > MaxFields) throw new Unsupported($"component {type.Name} has {fields.Length} fields (a vector loop splits up to {MaxFields})");
        foreach (var field in fields)
            if (field.IsFixedSizeBuffer || !IsLaneType(field.Type))
                throw new Unsupported($"field {type.Name}.{field.AssociatedSymbol?.Name ?? field.Name} is a {field.Type.ToDisplayString()}, not a lane type (float, double, int, uint, long or ulong)");
        return fields;
    }

    // ---------------- types ----------------

    /// <summary>A lane type of this kernel: float, int, uint (4 bytes) or double, long, ulong (8), its size.</summary>
    private bool IsLane(ITypeSymbol? type) => IsLaneType(type) && SizeOf(type!) == _size;

    private static bool IsBool(ITypeSymbol? type) => type?.SpecialType == SpecialType.System_Boolean;
    private static bool IsFloat(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Single or SpecialType.System_Double;
    private static bool IsSigned(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 || IsFloat(t);
    private static string Elem(ITypeSymbol type) => type.ToDisplayString(Symbols.Fqn);
    private static string Vec(string element) => $"{V}<{element}>";

    /// <summary>The vector type of a value of <paramref name="type"/>: its lanes, or the mask type for a bool.</summary>
    private string VecOf(ITypeSymbol? type) => Vec(IsBool(type) ? _mask : Elem(type!));

    /// <summary><paramref name="value"/>, a vector of <paramref name="from"/>, as one of <paramref name="to"/> (the same bits).</summary>
    private static string As(string from, string to, string value) => from == to ? value : $"{V}.As<{from}, {to}>({value})";

    /// <summary>A mask (vector of the mask type) as a vector of <paramref name="type"/>'s lanes, for a ConditionalSelect.</summary>
    private string MaskAs(string mask, ITypeSymbol type) => As(_mask, IsBool(type) ? _mask : Elem(type), mask);

    private string Temp(string prefix) => $"__{prefix}{_temps++}";
    private static string? And(string? a, string? b) => a == null ? b : b == null ? a : $"({a} & {b})";

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
                    if (!IsLane(local.Type) && !IsBool(local.Type))
                        throw new Unsupported($"local '{local.Name}' is a {local.Type.ToDisplayString()}, not a lane type of {_size} bytes");
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
                if (a.Value is ISimpleAssignmentOperation inner) // a = b = value: b first, then a from it
                {
                    Assignment(inner, mask);
                    value = Expr(inner.Target);
                }
                else value = Expr(a.Value);
                target = a.Target;
                break;
            case ICompoundAssignmentOperation a when !a.IsChecked && a.OperatorMethod == null && a.InConversion.IsIdentity && a.OutConversion.IsIdentity:
                target = a.Target;
                value = Binary(a.OperatorKind, a.Target, a.Value, a.Target.Type);
                break;
            case IIncrementOrDecrementOperation { IsChecked: false, OperatorMethod: null } a when IsLane(a.Type):
                target = a.Target;
                value = $"({Expr(a.Target)} {(a.Kind == OperationKind.Increment ? "+" : "-")} {VecOf(a.Type)}.One)";
                break;
            default:
                throw Not(op);
        }

        string name = Target(target);
        string? active = And(mask, _alive);
        _body.Append(active == null ? $"{name} = {value};\n"
            : $"{name} = {V}.ConditionalSelect({MaskAs(active, target.Type!)}, {value}, {name});\n");
    }

    /// <summary>The vector a write goes to: a kernel local, or the lane field of a component taken by ref.</summary>
    private string Target(IOperation target)
    {
        if (target is ILocalReferenceOperation { Local: var local } && _locals.TryGetValue(local, out var name)) return name;
        if (LaneOf(target) is { Ref: true } c)
        {
            _written.Add(c.Index);
            return $"__c{c.Index}_{c.Field}";
        }
        throw new Unsupported($"it writes '{Text(target)}', which is neither a local nor the lane of a component taken by ref");
    }

    /// <summary>
    /// The component and field whose lane <paramref name="op"/> is: its field, or the auto-property backed by it (a
    /// record struct's positional member).
    /// </summary>
    private (int Index, int Field, bool Ref)? LaneOf(IOperation op)
    {
        var (instance, member) = op switch
        {
            IFieldReferenceOperation f => (f.Instance, (ISymbol)f.Field),
            IPropertyReferenceOperation { Arguments.Length: 0 } p => (p.Instance, p.Property),
            _ => (null, null),
        };
        if (instance is not IParameterReferenceOperation { Parameter: var parameter } || !_components.TryGetValue(parameter, out var c))
            return null;
        for (int f = 0; f < c.Fields.Length; f++)
            if (SymbolEqualityComparer.Default.Equals(member, c.Fields[f]) || SymbolEqualityComparer.Default.Equals(member, c.Fields[f].AssociatedSymbol))
                return (c.Index, f, c.Ref);
        return null;
    }

    // ---------------- expressions ----------------

    /// <summary>
    /// <paramref name="op"/> as a vector of its type's lanes, or as a mask (the mask type, all bits set where true) for
    /// a bool.
    /// </summary>
    private string Expr(IOperation op)
    {
        if (IsUniform(op))
        {
            string scalar = Scalar(op);
            if (IsBool(op.Type)) return $"({scalar} ? {Vec(_mask)}.AllBitsSet : {Vec(_mask)}.Zero)";
            if (!IsLane(op.Type)) throw new Unsupported($"'{Text(op)}' is a {op.Type?.ToDisplayString()}, not a lane type of {_size} bytes");
            return Broadcast(scalar, op.Type!);
        }

        switch (op)
        {
            case IConversionOperation { Conversion.IsIdentity: true } c when IsLane(c.Type) || IsBool(c.Type):
                return Expr(c.Operand);
            case IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c when IsLane(c.Type) && IsLane(c.Operand.Type):
                return Convert(Expr(c.Operand), c.Operand.Type!, c.Type!) ?? throw Not(c);
            case IFieldReferenceOperation or IPropertyReferenceOperation when LaneOf(op) is { } c:
                return $"__c{c.Index}_{c.Field}";
            case ILocalReferenceOperation { Local: var local } when _locals.TryGetValue(local, out var name):
                return name;
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } b:
                return Binary(b.OperatorKind, b.LeftOperand, b.RightOperand, b.Type);
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } u:
                string operand = Expr(u.Operand);
                return u.OperatorKind switch
                {
                    UnaryOperatorKind.Plus when IsLane(u.Type) => operand,
                    UnaryOperatorKind.Minus when IsLane(u.Type) && IsSigned(u.Type) => $"(-{operand})",
                    UnaryOperatorKind.BitwiseNegation when IsLane(u.Type) && !IsFloat(u.Type) => $"(~{operand})",
                    UnaryOperatorKind.Not when IsBool(u.Type) => $"(~{operand})",
                    _ => throw Not(u),
                };
            case IConditionalOperation { IsRef: false, WhenFalse: { } whenFalse } t when IsLane(t.Type) || IsBool(t.Type):
                return $"{V}.ConditionalSelect({MaskAs(Expr(t.Condition), t.Type!)}, {Expr(t.WhenTrue)}, {Expr(whenFalse)})";
            case IIsPatternOperation { Value: var value } pattern when IsLane(value.Type):
                return Pattern(Expr(value), value.Type!, pattern.Pattern);
            case IInvocationOperation { Instance: null } call when IsMath(call.TargetMethod):
                return Call(call);
            default:
                throw Not(op);
        }
    }

    /// <summary>A uniform scalar of <paramref name="type"/>, broadcast once per run (before the loop).</summary>
    private string Broadcast(string scalar, ITypeSymbol type)
    {
        string key = Elem(type) + " " + scalar;
        if (!_broadcasts.TryGetValue(key, out var name))
        {
            name = Temp("u");
            _broadcasts[key] = name;
            _setup.Append($"var {name} = {V}.Create<{Elem(type)}>({scalar});\n"); // once per run, not per vector
        }
        return name;
    }

    /// <summary>
    /// The conversion of a vector between lane types of one size, as C# converts each lane, or null: between signed and
    /// unsigned integers the bits stay (unchecked), an integer to a float rounds to nearest, and a float to an integer
    /// truncates and saturates (NaN to 0), what <c>Vector.ConvertToInt32</c> and its kin do since .NET 9.
    /// </summary>
    private static string? Convert(string value, ITypeSymbol from, ITypeSymbol to)
    {
        if (!IsFloat(from) && !IsFloat(to)) return As(Elem(from), Elem(to), value);
        if (IsFloat(from) && IsFloat(to)) return null; // float and double: not one size
        string? convert = to.SpecialType switch
        {
            SpecialType.System_Single => "ConvertToSingle",
            SpecialType.System_Double => "ConvertToDouble",
            SpecialType.System_Int32 => "ConvertToInt32",
            SpecialType.System_UInt32 => "ConvertToUInt32",
            SpecialType.System_Int64 => "ConvertToInt64",
            SpecialType.System_UInt64 => "ConvertToUInt64",
            _ => null,
        };
        return convert == null ? null : $"{V}.{convert}({value})";
    }

    /// <summary>A comparison of two vectors of <paramref name="type"/> as a mask.</summary>
    private string Compare(string compare, string left, string right, ITypeSymbol type) =>
        As(Elem(type), _mask, $"{V}.{compare}<{Elem(type)}>({left}, {right})");

    private string Binary(BinaryOperatorKind kind, IOperation leftOp, IOperation right, ITypeSymbol? type)
    {
        type ??= leftOp.Type;
        var leftType = leftOp.Type;
        if (kind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift)
        {
            if (!IsLane(type) || IsFloat(type)) throw new Unsupported($"shifts of a {type?.ToDisplayString()} are not supported");
            if (!IsUniform(right)) throw new Unsupported("a shift by a count that differs per row is not supported");
            string count = $"(int)({Scalar(right)})"; // C# masks the count (& 31 / & 63), and so do the vector shifts
            string shifted = Expr(leftOp);
            return kind == BinaryOperatorKind.LeftShift ? $"{V}.ShiftLeft({shifted}, {count})"
                : $"{V}.{(kind == BinaryOperatorKind.RightShift && IsSigned(type) ? "ShiftRightArithmetic" : "ShiftRightLogical")}({shifted}, {count})";
        }

        bool lanes = IsLane(leftType) && IsLane(right.Type) && SymbolEqualityComparer.Default.Equals(leftType, right.Type);
        bool masks = IsBool(leftType) && IsBool(right.Type);
        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && lanes && !IsFloat(type))
            throw new Unsupported("integer division is not supported (it throws on 0)");
        if (kind == BinaryOperatorKind.Remainder) throw new Unsupported("'%' is not supported (no vector remainder)");
        if (!lanes && !masks) throw new Unsupported($"operator {Operator(kind)} on {leftType?.ToDisplayString()} and {right.Type?.ToDisplayString()} is not supported");
        string l = Expr(leftOp), r = Expr(right);
        return kind switch
        {
            BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply when lanes && IsLane(type)
                => $"({l} {Operator(kind)} {r})",
            BinaryOperatorKind.Divide when lanes && IsFloat(type) => $"({l} / {r})",
            BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr when lanes && !IsFloat(type) || masks
                => $"({l} {Operator(kind)} {r})",
            BinaryOperatorKind.ConditionalAnd when masks => $"({l} & {r})",  // no side effects: both sides are evaluated
            BinaryOperatorKind.ConditionalOr when masks => $"({l} | {r})",
            BinaryOperatorKind.Equals when masks => $"(~({l} ^ {r}))",
            BinaryOperatorKind.NotEquals when masks => $"({l} ^ {r})",
            BinaryOperatorKind.Equals when lanes => Compare("Equals", l, r, leftType!),
            BinaryOperatorKind.NotEquals when lanes => $"(~{Compare("Equals", l, r, leftType!)})", // NaN != NaN: true, as in C#
            BinaryOperatorKind.LessThan when lanes => Compare("LessThan", l, r, leftType!),
            BinaryOperatorKind.LessThanOrEqual when lanes => Compare("LessThanOrEqual", l, r, leftType!),
            BinaryOperatorKind.GreaterThan when lanes => Compare("GreaterThan", l, r, leftType!),
            BinaryOperatorKind.GreaterThanOrEqual when lanes => Compare("GreaterThanOrEqual", l, r, leftType!),
            _ => throw new Unsupported($"operator {Operator(kind)} on {leftType?.ToDisplayString()} is not supported"),
        };
    }

    /// <summary>
    /// <c>value is pattern</c> as a mask: relational (<c>&gt; 0</c>), constant (<c>0</c>, <c>float.NaN</c>), <c>and</c>,
    /// <c>or</c>, <c>not</c>, as C# tests each (a constant NaN matches NaN).
    /// </summary>
    private string Pattern(string value, ITypeSymbol type, IPatternOperation pattern)
    {
        switch (pattern)
        {
            case IRelationalPatternOperation relational:
                string compare = relational.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => "LessThan", BinaryOperatorKind.LessThanOrEqual => "LessThanOrEqual",
                    BinaryOperatorKind.GreaterThan => "GreaterThan", BinaryOperatorKind.GreaterThanOrEqual => "GreaterThanOrEqual",
                    _ => throw Not(pattern),
                };
                return Compare(compare, value, Operand(relational.Value, type), type);
            case IConstantPatternOperation { Value: var constant }:
                if (constant.ConstantValue is { HasValue: true, Value: float.NaN or double.NaN })
                    return $"(~{Compare("Equals", value, value, type)})";
                return Compare("Equals", value, Operand(constant, type), type);
            case IBinaryPatternOperation { OperatorKind: var kind, LeftPattern: var left, RightPattern: var right }:
                return $"({Pattern(value, type, left)} {(kind == BinaryOperatorKind.And ? "&" : "|")} {Pattern(value, type, right)})";
            case INegatedPatternOperation { Pattern: var negated }:
                return $"(~{Pattern(value, type, negated)})";
            default:
                throw Not(pattern);
        }
    }

    /// <summary>A pattern's operand as a vector of the tested value's type (a constant converted to it).</summary>
    private string Operand(IOperation op, ITypeSymbol type) =>
        op.ConstantValue is { HasValue: true, Value: { } constant } && !SymbolEqualityComparer.Default.Equals(op.Type, type)
            ? Broadcast(Literal(System.Convert.ChangeType(constant, ClrType(type), CultureInfo.InvariantCulture), type), type)
            : Expr(op);

    private static System.Type ClrType(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Single => typeof(float), SpecialType.System_Double => typeof(double), SpecialType.System_Int32 => typeof(int),
        SpecialType.System_UInt32 => typeof(uint), SpecialType.System_Int64 => typeof(long), _ => typeof(ulong),
    };

    /// <summary>
    /// A call of <c>Math</c>, <c>MathF</c> or a lane type's own static (<c>float.Clamp</c>) with an exact vector form.
    /// Each form computes what the scalar function returns for every lane, NaN and -0 included.
    /// </summary>
    private string Call(IInvocationOperation call)
    {
        var m = call.TargetMethod;
        var type = m.ReturnType;
        if (m.Name == "IsNaN" && IsBool(type) && m.Parameters.Length == 1 && IsFloat(m.Parameters[0].Type) && IsLane(m.Parameters[0].Type))
        {
            string x = Hold(Expr(call.Arguments[0].Value), m.Parameters[0].Type);
            return $"(~{Compare("Equals", x, x, m.Parameters[0].Type)})"; // NaN is the one value unequal to itself
        }
        if (!IsLane(type) || !m.Parameters.All(p => IsLane(p.Type))) throw Not(call);
        string Arg(int i) => Expr(call.Arguments.First(a => a.Parameter?.Ordinal == i).Value);
        bool single = m.Parameters.Length == 1, isFloat = IsFloat(type);
        switch (m.Name)
        {
            case "Min" or "Max" when m.Parameters.Length == 2:     // IEEE 754:2019 like Math.Min / Max (NaN, -0)
                return $"{V}.{m.Name}({Arg(0)}, {Arg(1)})";
            case "Abs" when single && isFloat:                     // Math.Abs of int.MinValue throws: no integers
                return $"{V}.Abs({Arg(0)})";
            case "Sqrt" when single && isFloat:
                return $"{V}.SquareRoot({Arg(0)})";
            case "Floor" or "Ceiling" or "Truncate" when single && isFloat:
                return $"{V}.{m.Name}({Arg(0)})";
            case "Round" when single && isFloat:                   // to even, like Math.Round(x)
                return $"{V}.Round({Arg(0)})";
            case "CopySign" when m.Parameters.Length == 2 && isFloat: // of integers it throws for MinValue
                return $"{V}.CopySign({Arg(0)}, {Arg(1)})";
            case "FusedMultiplyAdd" when m.Parameters.Length == 3 && isFloat:
                return $"{V}.FusedMultiplyAdd({Arg(0)}, {Arg(1)}, {Arg(2)})";
            case "Lerp" when m.Parameters.Length == 3 && isFloat:
            {
                // float.Lerp is MultiplyAddEstimate(value1, 1 - amount, value2 * amount): fused exactly where the vector
                // form fuses (FMA), so the lanes round as the scalar call does
                string amount = Hold(Arg(2), type);
                return $"{V}.MultiplyAddEstimate({Arg(0)}, {VecOf(type)}.One - {amount}, {Arg(1)} * {amount})";
            }
            case "Clamp" when m.Parameters.Length == 3:
                return Clamp(call, type);
            default:
                throw Not(call);
        }
    }

    /// <summary>
    /// Math.Clamp: the min when the value is below it, else the max when above it, else the value (NaN stays NaN). It
    /// throws when min is above max: bounds known to be in order need nothing, bounds that may not be make a lane where
    /// they are not send its vector's rows to the method itself (which throws as written).
    /// </summary>
    private string Clamp(IInvocationOperation call, ITypeSymbol type)
    {
        IOperation Arg(int i) => call.Arguments.First(a => a.Parameter?.Ordinal == i).Value;
        IOperation min = Arg(1), max = Arg(2);
        string value = Hold(Expr(Arg(0)), type), lo = Hold(Expr(min), type), hi = Hold(Expr(max), type);
        if (min.ConstantValue is { HasValue: true, Value: { } a } && max.ConstantValue is { HasValue: true, Value: { } b })
        {
            if (System.Convert.ToDouble(a, CultureInfo.InvariantCulture) > System.Convert.ToDouble(b, CultureInfo.InvariantCulture)
                && !(IsFloat(type) && (double.IsNaN(System.Convert.ToDouble(a, CultureInfo.InvariantCulture)) || double.IsNaN(System.Convert.ToDouble(b, CultureInfo.InvariantCulture)))))
                throw new Unsupported($"{call.TargetMethod.ContainingType.Name}.Clamp's min is above its max, so the call throws");
        }
        else
        {
            _bails = true;
            _body.Append($"__bail |= {V}.GreaterThanAny({lo}, {hi});\n");
        }
        string below = MaskAs(Compare("LessThan", value, lo, type), type), above = MaskAs(Compare("GreaterThan", value, hi, type), type);
        return $"{V}.ConditionalSelect({below}, {lo}, {V}.ConditionalSelect({above}, {hi}, {value}))";
    }

    /// <summary>A value used twice, in a local of the loop body (a uniform or a lane is already one).</summary>
    private string Hold(string value, ITypeSymbol type)
    {
        if (value.StartsWith("__", System.StringComparison.Ordinal) && value.All(c => char.IsLetterOrDigit(c) || c == '_')) return value;
        string name = Temp("t");
        _body.Append($"var {name} = {value};\n");
        return name;
    }

    private static bool IsMath(IMethodSymbol m) =>
        m.IsStatic && m.ContainingType is { ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } t
        && (t.Name is "Math" or "MathF" || IsLaneType(t));

    // ---------------- uniform values ----------------

    /// <summary>
    /// The same in every lane: it reads no component and no kernel local (constants, statics, singletons, context). A
    /// Math.Clamp is not: it may throw, so it runs per lane, where a bad lane sends its rows to the method.
    /// </summary>
    private bool IsUniform(IOperation op) => op switch
    {
        ILocalReferenceOperation or IInstanceReferenceOperation => false,
        IParameterReferenceOperation { Parameter: var p } => _uniformParams.ContainsKey(p),
        IInvocationOperation { TargetMethod.Name: "Clamp" } => false,
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
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } b when b.OperatorKind is not (BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) || IsFloat(b.Type):
                return $"({Scalar(b.LeftOperand)} {BinaryText(b.OperatorKind)} {Scalar(b.RightOperand)})";
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } u:
                return $"({UnaryText(u.OperatorKind)}{Scalar(u.Operand)})";
            case IInvocationOperation { Instance: null, TargetMethod: var m } call when IsMath(m)
                && (m.Name is "Min" or "Max" or "Sqrt" or "Floor" or "Ceiling" or "Truncate" or "Round" or "FusedMultiplyAdd" or "Lerp"
                    || m.Name is "Abs" or "CopySign" && IsFloat(m.ReturnType)):
                return $"{m.ContainingType.ToDisplayString(Symbols.Fqn)}.{m.Name}({string.Join(", ", call.Arguments.Select(a => Scalar(a.Value)))})";
            default:
                // a call or a property could do anything: not hoisted out of the rows
                throw new Unsupported($"'{Text(op)}' is not a value the loop can compute once per run (a call or a property could do anything)");
        }
    }

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
        IInvocationOperation { TargetMethod: var m } when IsMath(m) => m.Name is "Abs" or "CopySign" && !IsFloat(m.ReturnType)
            ? $"{m.ContainingType.Name}.{m.Name} of an integer is not supported (it throws on MinValue)"
            : $"{m.ContainingType.Name}.{m.Name}{(m.Parameters.Length > 0 && !m.Parameters.All(p => IsLane(p.Type)) ? $" of {string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString()))}" : "")} is not supported",
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
