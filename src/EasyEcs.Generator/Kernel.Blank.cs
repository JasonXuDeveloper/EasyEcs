using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The proof that lets a cell method's pass skip a row: with the cells of its driving input blank (every field 0, as a
/// row the walk skips holds), the method's out cells come out the same whatever else it reads (its sampled inputs, its
/// place, its values). A constant evaluator over the bound, inlined body: the driver's fields are 0, everything else
/// is unknown, and an operation is known only when its result does not depend on what is unknown (0 &amp; x, a
/// condition both of whose arms agree, false &amp;&amp; x, x * 0 for integers). Anything it does not read (a call left,
/// a loop, a write of anything but its locals and out cells) fails the proof: the pass then walks every row, as
/// before. Exact by construction: a skipped row gets what the method computes on blank cells, which the proof shows is
/// what it computes on that row.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// A constant whose value the evaluator does not track (a struct built from known values): each is its own object,
    /// so a value agrees only with itself (two structs built apart may differ).
    /// </summary>
    private sealed class OpaqueValue;

    /// <summary>
    /// Whether <paramref name="body"/>, with the parameters of <paramref name="blank"/> 0 and those of
    /// <paramref name="outputs"/> its results, assigns each out cell a value that does not depend on the rest.
    /// </summary>
    internal static bool ConstantOnBlank(IOperation body, ISet<IParameterSymbol> blank, ISet<IParameterSymbol> outputs)
    {
        var state = new Dictionary<ISymbol, object?>(SymbolEqualityComparer.Default);
        foreach (var o in outputs) state[o] = null; // unassigned: C# requires the body to assign each one
        var eval = new BlankEval(blank, outputs);
        if (!eval.Run(body, state)) return false;
        return outputs.All(o => state.TryGetValue(o, out var v) && v != null && eval.Assigned.Contains(o));
    }

    private sealed class BlankEval(ISet<IParameterSymbol> blank, ISet<IParameterSymbol> outputs)
    {
        /// <summary>The out cells the body assigns (on every path that reaches the end).</summary>
        public readonly HashSet<IParameterSymbol> Assigned = new(SymbolEqualityComparer.Default);

        private int _budget = 4096; // operations evaluated: a body larger than this is not worth proving

        /// <summary>Runs <paramref name="op"/> over <paramref name="state"/> (a symbol's value, null: unknown); false when the proof fails.</summary>
        public bool Run(IOperation op, Dictionary<ISymbol, object?> state)
        {
            if (--_budget < 0) return false;
            switch (op)
            {
                case IBlockOperation block:
                    foreach (var s in block.Operations)
                        if (!Run(s, state)) return false;
                    return true;
                case IVariableDeclarationGroupOperation group:
                    foreach (var d in group.Declarations)
                    foreach (var v in d.Declarators)
                    {
                        var init = v.Initializer ?? d.Initializer;
                        state[v.Symbol] = init == null ? Default(v.Symbol.Type) : Value(init.Value, state);
                    }
                    return true;
                case IExpressionStatementOperation statement:
                    return Effect(statement.Operation, state);
                case IConditionalOperation { Type: null } branch: // an if statement
                {
                    var condition = Value(branch.Condition, state);
                    if (condition is bool known)
                        return known ? Run(branch.WhenTrue, state) : branch.WhenFalse == null || Run(branch.WhenFalse, state);
                    // Unknown: both arms from the same state, then each symbol keeps a value only where they agree.
                    var other = new Dictionary<ISymbol, object?>(state, SymbolEqualityComparer.Default);
                    var assigned = new HashSet<IParameterSymbol>(Assigned, SymbolEqualityComparer.Default);
                    if (!Run(branch.WhenTrue, state)) return false;
                    var afterTrue = new HashSet<IParameterSymbol>(Assigned, SymbolEqualityComparer.Default);
                    Assigned.Clear();
                    Assigned.UnionWith(assigned);
                    if (branch.WhenFalse != null && !Run(branch.WhenFalse, other)) return false;
                    Assigned.IntersectWith(afterTrue);
                    foreach (var key in state.Keys.ToList())
                        if (!other.TryGetValue(key, out var v) || !Same(state[key], v)) state[key] = null;
                    foreach (var key in other.Keys)
                        if (!state.ContainsKey(key)) state[key] = null;
                    return true;
                }
                case IEmptyOperation:
                case IReturnOperation { ReturnedValue: null, IsImplicit: true }: // the end of a void body
                    return true;
                default:
                    return false; // loops, jumps, returns, throws, locks, ...: not proved
            }
        }

        /// <summary>A statement's expression: assignments of locals and out cells; anything else fails.</summary>
        private bool Effect(IOperation op, Dictionary<ISymbol, object?> state)
        {
            switch (op)
            {
                case ISimpleAssignmentOperation { Target: var target } assign:
                    return Assign(target, Value(assign.Value, state), state);
                case ICompoundAssignmentOperation compound:
                    return Assign(compound.Target, Binary(compound.OperatorKind, Value(compound.Target, state), Value(compound.Value, state), compound.Type), state);
                case IIncrementOrDecrementOperation step:
                    return Assign(step.Target, Binary(step.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract,
                        Value(step.Target, state), Convert(1, step.Target.Type), step.Type), state);
                default:
                    return false;
            }
        }

        private bool Assign(IOperation target, object? value, Dictionary<ISymbol, object?> state)
        {
            switch (target)
            {
                case ILocalReferenceOperation local:
                    state[local.Local] = value;
                    return true;
                case IParameterReferenceOperation { Parameter: var p } when outputs.Contains(p):
                    state[p] = value;
                    Assigned.Add(p);
                    return true;
                case IFieldReferenceOperation { Instance: ILocalReferenceOperation local }:
                    // A field of a local struct: the struct stays known while it was and the field's value is.
                    state[local.Local] = value != null && state.TryGetValue(local.Local, out var was) && was != null ? new OpaqueValue() : null;
                    return true;
                case IFieldReferenceOperation { Instance: IParameterReferenceOperation { Parameter: var p } } when outputs.Contains(p):
                    // A field of an out cell: C# requires the cell assigned before, so the rest is what the body left.
                    state[p] = value != null && state.TryGetValue(p, out var before) && before != null ? new OpaqueValue() : null;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The value of <paramref name="op"/>: a boxed constant, an <see cref="OpaqueValue"/>, or null (unknown).</summary>
        private object? Value(IOperation op, Dictionary<ISymbol, object?> state)
        {
            if (--_budget < 0) return null;
            if (op.ConstantValue is { HasValue: true } constant) return constant.Value ?? new OpaqueValue();
            switch (op)
            {
                case IParenthesizedOperation p: return Value(p.Operand, state);
                case ILocalReferenceOperation local: return state.TryGetValue(local.Local, out var v) ? v : null;
                case IParameterReferenceOperation { Parameter: var p }:
                    if (blank.Contains(p)) return Default(p.Type);
                    return outputs.Contains(p) && state.TryGetValue(p, out var o) ? o : null;
                case IFieldReferenceOperation { Instance: { } instance } field:
                    if (instance is IParameterReferenceOperation { Parameter: var cell } && blank.Contains(cell)) return Default(field.Type!);
                    return null; // a field of anything else: not followed
                case IPropertyReferenceOperation { Instance: IParameterReferenceOperation { Parameter: var owner }, Property: { Parameters.Length: 0 } property }
                    when blank.Contains(owner) && property.ContainingType.GetMembers().OfType<IFieldSymbol>()
                        .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property)):
                    return Default(property.Type); // an auto-property (a record's positional member): its backing field, 0
                case IConversionOperation c:
                    return c.OperatorMethod == null ? Convert(Value(c.Operand, state), c.Type) : null;
                case IUnaryOperation u:
                    return Unary(u.OperatorKind, Value(u.Operand, state), u.Type);
                case IBinaryOperation b when b.OperatorMethod == null:
                {
                    var left = Value(b.LeftOperand, state);
                    // Short circuits: the right side is not evaluated when the left decides.
                    if (b.OperatorKind == BinaryOperatorKind.ConditionalAnd && left is false) return false;
                    if (b.OperatorKind == BinaryOperatorKind.ConditionalOr && left is true) return true;
                    return Binary(b.OperatorKind, left, Value(b.RightOperand, state), b.Type);
                }
                case IConditionalOperation c when c.WhenFalse != null:
                {
                    var condition = Value(c.Condition, state);
                    if (condition is bool known) return Value(known ? c.WhenTrue : c.WhenFalse, state);
                    var t = Value(c.WhenTrue, state);
                    return t != null && Same(t, Value(c.WhenFalse, state)) ? t : null;
                }
                case IObjectCreationOperation { Initializer: null } create when create.Type is { IsValueType: true }:
                    return create.Arguments.All(a => Value(a.Value, state) != null) ? new OpaqueValue() : null;
                case IDefaultValueOperation d:
                    return Default(d.Type!);
                case IInvocationOperation call when call.TargetMethod is { IsStatic: true, ContainingType: { } type }
                    && type.ToDisplayString() is "System.Math" or "System.MathF":
                {
                    var args = call.Arguments.Select(a => Value(a.Value, state)).ToArray();
                    return call.TargetMethod.Name switch
                    {
                        "Min" or "Max" when args.Length == 2 && Number(args[0]) && Number(args[1])
                            => Arith(call.TargetMethod.Name, args[0]!, args[1]!, call.Type),
                        _ => args.All(a => a != null) ? new OpaqueValue() : null, // a pure function of known values
                    };
                }
                default:
                    return null;
            }
        }

        private static object? Unary(UnaryOperatorKind kind, object? x, ITypeSymbol? type)
        {
            if (!Number(x) && x is not bool) return x is OpaqueValue ? new OpaqueValue() : null;
            return kind switch
            {
                UnaryOperatorKind.Not when x is bool b => !b,
                UnaryOperatorKind.Plus => x,
                UnaryOperatorKind.Minus => IsFloat(x!) ? Convert(-ToDouble(x!), type) : Convert(unchecked(-ToLong(x!)), type),
                UnaryOperatorKind.BitwiseNegation when !IsFloat(x!) => Convert(~ToLong(x!), type),
                _ => null,
            };
        }

        private static object? Binary(BinaryOperatorKind kind, object? x, object? y, ITypeSymbol? type)
        {
            // What one known side decides whatever the other holds (integers and bools only: 0 * NaN is NaN).
            if (!(Number(x) || x is bool) || !(Number(y) || y is bool))
            {
                if (x is OpaqueValue && y is OpaqueValue) return new OpaqueValue(); // two constants: a constant
                object? known = Number(x) || x is bool ? x : Number(y) || y is bool ? y : null;
                if (known == null || IsFloat(known)) return null;
                return kind switch
                {
                    BinaryOperatorKind.And when known is false => false,
                    BinaryOperatorKind.And or BinaryOperatorKind.Multiply when known is not bool && ToLong(known) == 0 => Convert(0, type),
                    BinaryOperatorKind.Or when known is true => true,
                    BinaryOperatorKind.ConditionalAnd when x is false => false,
                    BinaryOperatorKind.ConditionalOr when x is true => true,
                    _ => null,
                };
            }
            if (x is bool a && y is bool b)
                return kind switch
                {
                    BinaryOperatorKind.And or BinaryOperatorKind.ConditionalAnd => a && b,
                    BinaryOperatorKind.Or or BinaryOperatorKind.ConditionalOr => a || b,
                    BinaryOperatorKind.ExclusiveOr or BinaryOperatorKind.NotEquals => a != b,
                    BinaryOperatorKind.Equals => a == b,
                    _ => null,
                };
            return Arith(kind.ToString(), x!, y!, type);
        }

        /// <summary>A binary operation (or Math.Min / Max) of two known numbers, in C#'s unchecked semantics; null where it would throw.</summary>
        private static object? Arith(string kind, object x, object y, ITypeSymbol? type)
        {
            if (x is bool || y is bool) return null;
            if (IsFloat(x) || IsFloat(y))
            {
                double a = ToDouble(x), b = ToDouble(y);
                return kind switch
                {
                    "Add" => Convert(a + b, type), "Subtract" => Convert(a - b, type), "Multiply" => Convert(a * b, type),
                    "Divide" => Convert(a / b, type), "Remainder" => Convert(a % b, type),
                    "Min" => Convert(Math.Min(a, b), type), "Max" => Convert(Math.Max(a, b), type),
                    "Equals" => a == b, "NotEquals" => a != b, "LessThan" => a < b, "LessThanOrEqual" => a <= b,
                    "GreaterThan" => a > b, "GreaterThanOrEqual" => a >= b,
                    _ => null,
                };
            }
            // Integers: compared as numbers (unsigned 64-bit values above long.MaxValue are not followed), computed in
            // 64 bits and wrapped to the result type.
            if (x is ulong { } ux && ux > long.MaxValue || y is ulong { } uy && uy > long.MaxValue) return null;
            long l = ToLong(x), r = ToLong(y);
            return kind switch
            {
                "Add" => Convert(unchecked(l + r), type), "Subtract" => Convert(unchecked(l - r), type), "Multiply" => Convert(unchecked(l * r), type),
                "Divide" when r != 0 && !(r == -1 && l == long.MinValue) => Convert(l / r, type),
                "Remainder" when r != 0 && r != -1 => Convert(l % r, type),
                "And" => Convert(l & r, type), "Or" => Convert(l | r, type), "ExclusiveOr" => Convert(l ^ r, type),
                // A shift's count is masked by its type's width (C#: 31 for int and uint, 63 for long and ulong).
                "LeftShift" => Convert(l << (int)(r & Width(type)), type), "RightShift" => Convert(l >> (int)(r & Width(type)), type),
                "Min" => Convert(Math.Min(l, r), type), "Max" => Convert(Math.Max(l, r), type),
                "Equals" => l == r, "NotEquals" => l != r, "LessThan" => l < r, "LessThanOrEqual" => l <= r,
                "GreaterThan" => l > r, "GreaterThanOrEqual" => l >= r,
                _ => null,
            };
        }

        private static long Width(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 ? 63 : 31;

        /// <summary>Whether <paramref name="x"/> is a known number (not a bool, not an untracked constant).</summary>
        private static bool Number(object? x) => x is not null and not bool and not OpaqueValue;

        private static bool IsFloat(object x) => x is float or double or decimal;
        private static double ToDouble(object x) => x is float f ? f : System.Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture);
        private static long ToLong(object x) => x is ulong u ? unchecked((long)u) : x is char c ? c : System.Convert.ToInt64(x, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary><paramref name="x"/> as a value of <paramref name="type"/>, unchecked; null for a type it does not follow.</summary>
        private static object? Convert(object? x, ITypeSymbol? type)
        {
            if (x is null or OpaqueValue || type == null) return x;
            if (type.TypeKind == TypeKind.Enum) type = ((INamedTypeSymbol)type).EnumUnderlyingType;
            if (x is bool) return type?.SpecialType == SpecialType.System_Boolean ? x : null;
            switch (type?.SpecialType)
            {
                case SpecialType.System_Single: return (float)ToDouble(x);
                case SpecialType.System_Double: return ToDouble(x);
            }
            if (IsFloat(x)) return null; // a float to an integer: saturating or not by platform before .NET 9; not followed
            long v = ToLong(x);
            return type?.SpecialType switch
            {
                SpecialType.System_Byte => unchecked((byte)v), SpecialType.System_SByte => unchecked((sbyte)v),
                SpecialType.System_Int16 => unchecked((short)v), SpecialType.System_UInt16 => unchecked((ushort)v),
                SpecialType.System_Char => unchecked((char)v),
                SpecialType.System_Int32 => unchecked((int)v), SpecialType.System_UInt32 => unchecked((uint)v),
                SpecialType.System_Int64 => v, SpecialType.System_UInt64 => unchecked((ulong)v),
                _ => null,
            };
        }

        /// <summary>The zero of <paramref name="type"/>: a number, false, or a struct (an <see cref="OpaqueValue"/>, known).</summary>
        private static object? Default(ITypeSymbol type) => type.SpecialType == SpecialType.System_Boolean ? false
            : Convert(0L, type) ?? (type.IsValueType ? new OpaqueValue() : null);

        private static bool Same(object? a, object? b) => a != null && b != null && (a is OpaqueValue ? ReferenceEquals(a, b) : Equals(a, b));
    }
}
