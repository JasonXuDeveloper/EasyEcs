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
/// what it computes on that row. A result per tile (<c>ref Reduce&lt;T&gt;</c>) must come out unchanged too: each field
/// starts at a value of its own (unknown) that only its operation's identity keeps (x + 0, x | 0, x &amp; ~0, Min with
/// the type's largest value, Max with its smallest), so a skipped row adds nothing to it.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// A constant whose value the evaluator does not track (a struct built from known values): each is its own object,
    /// so a value agrees only with itself (two structs built apart may differ).
    /// </summary>
    private sealed class OpaqueValue;

    /// <summary>A result field's value before the body ran: kept only by its operation's identity.</summary>
    private sealed class StartValue(ITypeSymbol type)
    {
        public readonly ITypeSymbol Type = type;
    }

    /// <summary>
    /// Whether <paramref name="body"/>, with the parameters of <paramref name="blank"/> 0 and those of
    /// <paramref name="outputs"/> its results, assigns each out cell a value that does not depend on the rest.
    /// </summary>
    /// <param name="results">The <c>ref Reduce&lt;T&gt;</c> results per tile, whose fields must end where they started.</param>
    internal static bool ConstantOnBlank(IOperation body, ISet<IParameterSymbol> blank, ISet<IParameterSymbol> outputs, ISet<IParameterSymbol> results)
    {
        var state = new Dictionary<ISymbol, object?>(SymbolEqualityComparer.Default);
        foreach (var o in outputs) state[o] = null; // unassigned: C# requires the body to assign each one
        var eval = new BlankEval(blank, outputs, results);
        if (!eval.Run(body, state)) return false;
        return outputs.All(o => state.TryGetValue(o, out var v) && v != null && eval.Assigned.Contains(o))
            && eval.Fields.All(f => f.Value is StartValue start && ReferenceEquals(eval.Starts[f.Key], start));
    }

    private sealed class BlankEval(ISet<IParameterSymbol> blank, ISet<IParameterSymbol> outputs, ISet<IParameterSymbol> results)
    {
        /// <summary>Each result field the body reads or writes ("r.F"): its value now, and the one it started at.</summary>
        public readonly Dictionary<string, object?> Fields = new();
        public readonly Dictionary<string, StartValue> Starts = new();

        /// <summary>The key of <paramref name="op"/> when it is a field of a result (<c>r.Value.F</c>), with the field's type; else null.</summary>
        private string? ResultField(IOperation op, out ITypeSymbol? type)
        {
            type = null;
            if (op is not IFieldReferenceOperation { Field: var field, Instance: IFieldReferenceOperation { Field.Name: "Value", Instance: IParameterReferenceOperation { Parameter: var r } } }
                || !results.Contains(r)) return null;
            string key = r.Name + "." + field.Name;
            type = field.Type;
            if (!Starts.ContainsKey(key)) Fields[key] = Starts[key] = new StartValue(field.Type);
            return key;
        }

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
                    var fields = new Dictionary<string, object?>(Fields);
                    if (!Run(branch.WhenTrue, state)) return false;
                    var fieldsTrue = new Dictionary<string, object?>(Fields);
                    Fields.Clear();
                    foreach (var f in fields) Fields[f.Key] = f.Value;
                    var afterTrue = new HashSet<IParameterSymbol>(Assigned, SymbolEqualityComparer.Default);
                    Assigned.Clear();
                    Assigned.UnionWith(assigned);
                    if (branch.WhenFalse != null && !Run(branch.WhenFalse, other)) return false;
                    Assigned.IntersectWith(afterTrue);
                    foreach (var key in state.Keys.ToList())
                        if (!other.TryGetValue(key, out var v) || !Same(state[key], v)) state[key] = null;
                    foreach (var key in other.Keys)
                        if (!state.ContainsKey(key)) state[key] = null;
                    foreach (var f in fieldsTrue) // a field read first in one arm started there: its start is the same object
                        Fields[f.Key] = Fields.TryGetValue(f.Key, out var v) && Same(v, f.Value) ? v : null;
                    foreach (var key in Fields.Keys.ToList())
                        if (!fieldsTrue.ContainsKey(key)) Fields[key] = null;
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
            if (ResultField(target, out _) is { } key)
            {
                Fields[key] = value;
                return true;
            }
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
                case IFieldReferenceOperation when ResultField(op, out _) is { } key:
                    return Fields[key];
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
                        "Min" or "Max" when args.Length == 2 && (args[0] as StartValue ?? args[1] as StartValue) is { } start
                            && (args[0] is StartValue ? args[1] : args[0]) is var bound && Number(bound) && Keeps(call.TargetMethod.Name, start.Type, bound!)
                            => start,
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
            // A result's start combined with its operation's identity is still the start.
            if (x is StartValue start && Number(y) && Keeps(kind.ToString(), start.Type, y!)) return start;
            if (y is StartValue other && Number(x) && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Or or BinaryOperatorKind.And
                && Keeps(kind.ToString(), other.Type, x!)) return other;
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
        private static bool Number(object? x) => x is not null and not bool and not OpaqueValue and not StartValue;

        /// <summary>
        /// Whether <paramref name="op"/> with <paramref name="value"/> keeps a <paramref name="type"/> result as it was: its
        /// identity (0 for + and |, all ones for &amp;, the type's largest value for Min and smallest for Max; a float sum
        /// is not kept, -0 + 0 being +0).
        /// </summary>
        private static bool Keeps(string op, ITypeSymbol type, object value)
        {
            if (IsFloat(value))
            {
                double d = ToDouble(value);
                return op == "Min" && double.IsPositiveInfinity(d) || op == "Max" && double.IsNegativeInfinity(d);
            }
            if (Convert(value, type) is not { } t || t is bool) return false;
            return op switch
            {
                "Add" or "Subtract" or "Or" => ToLong(t) == 0,
                "And" => Equals(t, Convert(-1L, type)),
                "Min" => Equals(t, Bound(type, true)),
                "Max" => Equals(t, Bound(type, false)),
                _ => false,
            };
        }

        /// <summary>The largest (or smallest) value of integer type <paramref name="type"/>, boxed as that type; else null.</summary>
        private static object? Bound(ITypeSymbol type, bool max) => type.SpecialType switch
        {
            SpecialType.System_Byte => max ? byte.MaxValue : byte.MinValue, SpecialType.System_SByte => max ? sbyte.MaxValue : sbyte.MinValue,
            SpecialType.System_Int16 => max ? short.MaxValue : short.MinValue, SpecialType.System_UInt16 => max ? ushort.MaxValue : ushort.MinValue,
            SpecialType.System_Int32 => max ? int.MaxValue : int.MinValue, SpecialType.System_UInt32 => max ? uint.MaxValue : uint.MinValue,
            SpecialType.System_Int64 => max ? long.MaxValue : long.MinValue, SpecialType.System_UInt64 => max ? ulong.MaxValue : ulong.MinValue,
            _ => null,
        };

        private static bool IsFloat(object x) => x is float or double or decimal;
        private static double ToDouble(object x) => x is float f ? f : System.Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture);
        private static long ToLong(object x) => x is ulong u ? unchecked((long)u) : x is char c ? c : System.Convert.ToInt64(x, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary><paramref name="x"/> as a value of <paramref name="type"/>, unchecked; null for a type it does not follow.</summary>
        private static object? Convert(object? x, ITypeSymbol? type)
        {
            if (x is StartValue) return null; // a result converted: not followed
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
