using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Struct values: a component, a nested struct of lanes (<c>System.Numerics.Vector2</c>) or a local of such a type is one
/// vector per lane, in lane order. <c>x = new T(args)</c>, <c>x with { ... }</c>, <c>default</c> and the component-wise
/// operators of the vector types (<c>a + b</c>, <c>v * s</c>, <c>-v</c>) become one statement per lane, each the IEEE
/// operation the scalar code performs on that lane (Vector2's operators compute each field on its own: no reassociation,
/// no fused multiply-add), so the lanes are the method's bit for bit. A constructor maps its parameters to fields when it
/// is a record's positional one or its body only assigns parameters to fields. Calls that combine lanes (Dot, Length)
/// stay scalar.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>The struct locals: one vector local per lane (null: a lane a flat loop's field leaves out).</summary>
    private readonly Dictionary<ILocalSymbol, string?[]> _structs = new(SymbolEqualityComparer.Default);

    private Compilation? _compilation;

    /// <summary>
    /// The vector types whose operators and constructors are component-wise: System.Numerics.Vector2 / 3 / 4, and Godot's
    /// Vector2 / 3 / 4 (each operator computes every field on its own, <c>left.X += right.X</c>).
    /// </summary>
    private static bool KnownVector(ITypeSymbol? t) =>
        t is INamedTypeSymbol { TypeKind: TypeKind.Struct, Name: "Vector2" or "Vector3" or "Vector4", ContainingNamespace: var ns }
        && (ns is { Name: "Numerics", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } }
            || ns is { Name: "Godot", ContainingNamespace.IsGlobalNamespace: true });

    /// <summary>A struct of lanes of this kernel's size: its lane paths, or null.</summary>
    private IFieldSymbol[][]? StructPaths(ITypeSymbol? type) =>
        TryPaths(type) is { } paths && paths.All(p => IsLane(p[p.Length - 1].Type)) ? paths : null;

    /// <summary>Lane f of component k: in a flat loop the field it translates is lane 0, the others are left out.</summary>
    private string? ComponentLane(int index, int f) =>
        _flat < 0 ? $"__c{index}_{f}{(_doubled.Contains(index) && _half >= 0 ? $"_{_half}" : "")}" : f == _flat ? $"__c{index}_0" : null;

    private string?[] Each(int lanes, System.Func<int, string?> lane)
    {
        var result = new string?[lanes];
        for (int f = 0; f < lanes; f++) result[f] = _flat >= 0 && f != _flat ? null : lane(f);
        return result;
    }

    private static string Names(IFieldSymbol[] path) => string.Join(".", path.Select(f => f.AssociatedSymbol?.Name ?? f.Name));

    /// <summary><paramref name="op"/>, a struct of lanes, as one vector per lane.</summary>
    private string?[] StructOf(IOperation op)
    {
        var paths = StructPaths(op.Type) ?? throw new Unsupported($"'{Text(op)}' is a {op.Type?.ToDisplayString()}, not a struct of lanes of {_size} bytes");
        switch (op)
        {
            case IConversionOperation { Conversion.IsIdentity: true } c:
                return StructOf(c.Operand);
            case IParameterReferenceOperation { Parameter: var p } when _components.TryGetValue(p, out var c):
                return Enumerable.Range(0, paths.Length).Select(f => ComponentLane(c.Index, f)).ToArray();
            case ILocalReferenceOperation { Local: var local } when _structs.TryGetValue(local, out var names):
                return (string?[])names.Clone();
            case IPropertyReferenceOperation { Property: { IsStatic: true, Name: var name } } when KnownVector(op.Type) && name is "Zero" or "One" or "UnitX" or "UnitY" or "UnitZ" or "UnitW":
                return Each(paths.Length, f => Broadcast(Literal(System.Convert.ChangeType(
                    name == "Zero" ? 0 : name == "One" ? 1 : name == "Unit" + "XYZW"[f] ? 1 : 0, ClrType(paths[f][0].Type), System.Globalization.CultureInfo.InvariantCulture), paths[f][0].Type), paths[f][0].Type));
            case IPropertyReferenceOperation { Property.Name: "Value", Instance: IParameterReferenceOperation { Parameter: var p } } when _optionals.TryGetValue(p, out var o):
                Guard(o.Index); // m.Value whole: the rows must have it, as for a field of it
                return Enumerable.Range(0, paths.Length).Select(f => _flat < 0 ? $"__q{o.Index}_{f}" : null).ToArray();
            case IFieldReferenceOperation or IPropertyReferenceOperation or IParameterReferenceOperation when IsUniform(op):
            {
                // a singleton's or a value's struct: each lane broadcast once per run
                string scalar = Scalar(op);
                return Each(paths.Length, f => Broadcast($"{scalar}.{Names(paths[f])}", paths[f][paths[f].Length - 1].Type));
            }
            case IFieldReferenceOperation or IPropertyReferenceOperation:
                return Member((IMemberReferenceOperation)op);
            case IDefaultValueOperation:
                return Zeros(paths);
            case IObjectCreationOperation creation:
                return Create(creation, paths);
            case IWithOperation with:
            {
                var lanes = StructOf(with.Operand);
                if (with.Initializer != null) Initialize(lanes, paths, with.Initializer);
                return lanes;
            }
            case IBinaryOperation { OperatorMethod: { } method, IsChecked: false } b when KnownVector(method.ContainingType):
                return VectorBinary(b.OperatorKind, b.LeftOperand, b.RightOperand, b);
            case IUnaryOperation { OperatorMethod: { } method, OperatorKind: UnaryOperatorKind.Minus or UnaryOperatorKind.Plus } u when KnownVector(method.ContainingType):
            {
                var x = StructOf(u.Operand);
                return u.OperatorKind == UnaryOperatorKind.Plus ? x : x.Select(l => l == null ? null : $"(-{l})").ToArray();
            }
            case IInvocationOperation { Instance: null, TargetMethod: var m } call when KnownVector(m.ContainingType):
                return VectorCall(call, paths);
            case IConditionalOperation { IsRef: false, WhenFalse: { } whenFalse } t:
            {
                string condition = Hold(Expr(t.Condition), t.Condition.Type!);
                var (a, b) = (StructOf(t.WhenTrue), StructOf(whenFalse));
                return Each(paths.Length, f => $"{V}.ConditionalSelect({As(_mask, Elem(paths[f][paths[f].Length - 1].Type), condition)}, {a[f]}, {b[f]})");
            }
            default:
                throw Not(op);
        }
    }

    private string?[] Zeros(IFieldSymbol[][] paths) => Each(paths.Length, f => $"{Vec(Elem(paths[f][paths[f].Length - 1].Type))}.Zero");

    /// <summary>A member of a struct value: the lanes under it (one for a lane field).</summary>
    private string?[] Member(IMemberReferenceOperation m)
    {
        var instance = m.Instance ?? throw Not(m);
        var paths = StructPaths(instance.Type) ?? throw Not(m);
        var lanes = StructOf(instance);
        var member = m.Member;
        return Enumerable.Range(0, paths.Length).Where(f => Is(member, paths[f][0])).Select(f => lanes[f]).ToArray();
    }

    /// <summary>A value as lanes: a struct's, or one lane value as a one-element array.</summary>
    private string?[] ValueOf(IOperation op) => StructPaths(op.Type) != null ? StructOf(op) : [Expr(op)];

    /// <summary><c>new T(args) { ... }</c>: the fields its constructor assigns from the arguments, the rest zero, then the initializer.</summary>
    private string?[] Create(IObjectCreationOperation creation, IFieldSymbol[][] paths)
    {
        var lanes = Zeros(paths);
        var ctor = creation.Constructor;
        if (ctor is { Parameters.Length: > 0 })
        {
            IOperation Arg(int i) => creation.Arguments.First(a => a.Parameter?.Ordinal == i).Value;
            if (KnownVector(creation.Type))
            {
                // new Vector3(x, y, z), new Vector3(xy, z), new Vector3(s): the arguments' lanes in order, or one value in all
                var parts = Enumerable.Range(0, ctor.Parameters.Length).SelectMany(i =>
                    IsLane(ctor.Parameters[i].Type) || StructPaths(ctor.Parameters[i].Type) != null ? ValueOf(Arg(i)) : throw Not(creation)).ToArray();
                if (parts.Length == 1 && paths.Length > 1 && IsLane(ctor.Parameters[0].Type))
                {
                    string? one = parts[0] == null ? null : Hold(parts[0]!, ctor.Parameters[0].Type);
                    return Each(paths.Length, _ => one);
                }
                if (parts.Length != paths.Length) throw Not(creation);
                return Each(paths.Length, f => parts[f]);
            }
            var assigned = Assigns(ctor);
            for (int i = 0; i < ctor.Parameters.Length; i++)
                if (assigned[i] is { } member && Reaches(paths, [member])) Set(lanes, paths, [member], ValueOf(Arg(i)), creation);
        }
        if (creation.Initializer != null) Initialize(lanes, paths, creation.Initializer);
        return lanes;
    }

    /// <summary>An initializer's assignments (<c>{ X = 1, Value = v }</c>) over lanes, in order.</summary>
    private void Initialize(string?[] lanes, IFieldSymbol[][] paths, IObjectOrCollectionInitializerOperation initializer)
    {
        foreach (var init in initializer.Initializers)
        {
            if (init is not ISimpleAssignmentOperation { Target: var target, Value: var value } || Chain(target) is not (IInstanceReferenceOperation, { Length: > 0 } members))
                throw Not(init);
            if (Reaches(paths, members)) Set(lanes, paths, members, ValueOf(value), init);
        }
    }

    /// <summary>
    /// Whether the members <paramref name="members"/> hold the lane a flat loop's field translates (every lane outside
    /// one): an argument or initializer of another field is left out, not translated (it reads that field).
    /// </summary>
    private bool Reaches(IFieldSymbol[][] paths, ISymbol[] members) =>
        _flat < 0 || paths[_flat].Length >= members.Length && Enumerable.Range(0, members.Length).All(k => Is(members[k], paths[_flat][k]));

    /// <summary>Lanes under the members <paramref name="members"/> set to <paramref name="value"/>.</summary>
    private void Set(string?[] lanes, IFieldSymbol[][] paths, ISymbol[] members, string?[] value, IOperation op)
    {
        var under = Enumerable.Range(0, paths.Length)
            .Where(f => paths[f].Length >= members.Length && Enumerable.Range(0, members.Length).All(k => Is(members[k], paths[f][k]))).ToArray();
        if (under.Length != value.Length) throw Not(op);
        for (int k = 0; k < under.Length; k++) lanes[under[k]] = value[k];
    }

    /// <summary>
    /// The member each parameter of a constructor sets: a record's positional parameters their properties; a constructor
    /// declared here whose body only assigns parameters to fields or auto-properties, those (null where it sets none).
    /// </summary>
    private ISymbol?[] Assigns(IMethodSymbol ctor)
    {
        var type = ctor.ContainingType;
        var syntax = ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var assigned = new ISymbol?[ctor.Parameters.Length];
        if (syntax is RecordDeclarationSyntax)
        {
            for (int i = 0; i < assigned.Length; i++)
                assigned[i] = type.GetMembers(ctor.Parameters[i].Name).OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ParameterSyntax)
                    ?? throw new Unsupported($"the parameter '{ctor.Parameters[i].Name}' of {type.Name}'s constructor sets no positional property");
            return assigned;
        }
        if (syntax is not ConstructorDeclarationSyntax { Initializer: null } declaration || _compilation == null
            || _compilation.GetSemanticModel(declaration.SyntaxTree).GetOperation(declaration) is not IConstructorBodyOperation body)
            throw new Unsupported($"{type.Name}'s constructor is not declared in this compilation, so the fields it sets are not known");
        var statements = body.BlockBody?.Operations ?? (body.ExpressionBody != null ? [body.ExpressionBody] : []);
        foreach (var statement in statements)
        {
            var e = statement is IExpressionStatementOperation { Operation: var inner } ? inner : statement;
            if (e is not ISimpleAssignmentOperation { Target: var target, Value: var value }
                || Chain(target) is not (IInstanceReferenceOperation, { Length: 1 } chain)
                || Unwrap(value) is not IParameterReferenceOperation { Parameter: var p }
                || !SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, ctor) || assigned[p.Ordinal] != null
                || chain[0] is IPropertySymbol property && !type.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property)))
                throw new Unsupported($"{type.Name}'s constructor does more than set fields from its parameters ('{Text(statement)}')");
            assigned[p.Ordinal] = chain[0];
        }
        return assigned;
    }

    private static IOperation Unwrap(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        return op;
    }

    /// <summary>A component-wise operator of a vector type: vector op vector, vector op scalar, scalar * vector.</summary>
    private string?[] VectorBinary(BinaryOperatorKind kind, IOperation left, IOperation right, IOperation op)
    {
        string text = kind switch
        {
            BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*", BinaryOperatorKind.Divide => "/",
            _ => throw Not(op),
        };
        int lanes = StructPaths(op.Type)!.Length;
        string?[] Side(IOperation side)
        {
            if (StructPaths(side.Type) != null) return StructOf(side);
            if (!IsLane(side.Type)) throw Not(op);
            string one = Hold(Expr(side), side.Type!);
            return Each(lanes, _ => one);
        }
        var (l, r) = (Side(left), Side(right));
        return Each(lanes, f => $"({l[f]} {text} {r[f]})");
    }

    /// <summary>The static functions of System.Numerics' vectors that act on each lane alone; the others name their reason.</summary>
    private string?[] VectorCall(IInvocationOperation call, IFieldSymbol[][] paths)
    {
        var m = call.TargetMethod;
        IOperation Arg(int i) => call.Arguments.First(a => a.Parameter?.Ordinal == i).Value;
        switch (m.Name)
        {
            case "Add" or "Subtract" or "Multiply" or "Divide" when m.Parameters.Length == 2:
                return VectorBinary(m.Name switch
                {
                    "Add" => BinaryOperatorKind.Add, "Subtract" => BinaryOperatorKind.Subtract, "Multiply" => BinaryOperatorKind.Multiply, _ => BinaryOperatorKind.Divide,
                }, Arg(0), Arg(1), call);
            case "Negate" when m.Parameters.Length == 1:
                return StructOf(Arg(0)).Select(l => l == null ? null : $"(-{l})").ToArray();
            case "Min" or "Max" when m.Parameters.Length == 2: // each lane as Math.Min / Max (IEEE 754:2019)
            {
                var (a, b) = (StructOf(Arg(0)), StructOf(Arg(1)));
                return Each(paths.Length, f => $"{V}.{m.Name}({a[f]}, {b[f]})");
            }
            case "Abs" or "SquareRoot" when m.Parameters.Length == 1:
            {
                var a = StructOf(Arg(0));
                return Each(paths.Length, f => m.Name == "Abs" ? $"{V}.Abs({a[f]})" : $"{SquareRoot}{a[f]})");
            }
            default:
                throw Not(call);
        }
    }

    // ---------------- writes ----------------

    /// <summary>A struct local declared: one vector local per lane.</summary>
    private void DeclareStruct(ILocalSymbol local, IOperation init)
    {
        var lanes = StructOf(init);
        var names = new string?[lanes.Length];
        for (int f = 0; f < lanes.Length; f++)
        {
            if (lanes[f] == null) continue;
            names[f] = Temp("l");
            _body.Append($"var {names[f]} = {lanes[f]};\n");
        }
        _structs[local] = names;
    }

    /// <summary>
    /// A struct written (a component, a member of one, a struct local or a member of one) in the lanes of
    /// <paramref name="mask"/>: every value first, then the writes, so lanes that read each other (a swap) read the old ones.
    /// </summary>
    private void WriteStruct(IOperation target, string?[] value, string? mask)
    {
        var (root, members) = Chain(target);
        string?[] destinations;
        IFieldSymbol[][] paths;
        switch (root)
        {
            case IParameterReferenceOperation { Parameter: var p } when _components.TryGetValue(p, out var c):
                if (!c.Ref) throw new Unsupported($"it writes '{Text(target)}', which is neither a local nor the lane of a component taken by ref");
                _written.Add(c.Index);
                paths = _paths[p];
                destinations = Enumerable.Range(0, paths.Length).Select(f => ComponentLane(c.Index, f)).ToArray();
                break;
            case ILocalReferenceOperation { Local: var local } when _structs.TryGetValue(local, out var names):
                paths = StructPaths(local.Type)!;
                destinations = names;
                break;
            default:
                throw new Unsupported($"it writes '{Text(target)}', which is neither a local nor the lane of a component taken by ref");
        }
        var under = Enumerable.Range(0, paths.Length)
            .Where(f => paths[f].Length > members.Length && Enumerable.Range(0, members.Length).All(k => Is(members[k], paths[f][k]))).ToArray();
        if (under.Length != value.Length) throw Not(target);
        var held = new string?[value.Length];
        for (int k = 0; k < value.Length; k++)
        {
            if (value[k] is not { } v) continue;
            held[k] = Temp("t");
            _body.Append($"var {held[k]} = {v};\n"); // read before any lane is written
        }
        for (int k = 0; k < under.Length; k++)
        {
            if (destinations[under[k]] is not { } name) continue;
            Write(name, held[k] ?? throw new Unsupported("a statement mixes fields"), paths[under[k]][paths[under[k]].Length - 1].Type, mask);
        }
    }

    /// <summary>The lane local a write of a struct local's lane field goes to (<c>v.X = -v.X</c>), or null.</summary>
    private string? StructLocalLane(IOperation target)
    {
        if (Chain(target) is not (ILocalReferenceOperation { Local: var local }, { Length: > 0 } members) || !_structs.TryGetValue(local, out var names)) return null;
        int f = Leaf(StructPaths(local.Type)!, members);
        return f < 0 ? null : names[f] ?? throw new Unsupported("a statement mixes fields");
    }

    /// <summary>
    /// The lanes of components a flat loop's statement reads or writes through a struct (<c>position</c>,
    /// <c>position.Value</c>): how many, or 0 when <paramref name="op"/> is no such reference (or a part of a longer one).
    /// </summary>
    private int Covers(IOperation op)
    {
        if (op.Parent is IMemberReferenceOperation { Instance: var instance } && instance == op) return 0; // part of a chain
        if (StructPaths(op.Type) == null) return 0;
        var (root, members) = Chain(op);
        if (root is not IParameterReferenceOperation { Parameter: var p } || !_components.ContainsKey(p)) return 0;
        var paths = _paths[p];
        return Enumerable.Range(0, paths.Length).Count(f => paths[f].Length > members.Length && Enumerable.Range(0, members.Length).All(k => Is(members[k], paths[f][k])));
    }
}
