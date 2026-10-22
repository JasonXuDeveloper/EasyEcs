using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Narrow lanes: components of byte, sbyte, short or ushort fields. C# computes on such fields in int (<c>a.X + b.X</c>
/// is an int, <c>(byte)(a.X + b.X)</c> its low 8 bits), so the vector loop follows each int value in two parts: the
/// low bits of its lanes, which wrap-around arithmetic (<c>+ - * &amp; | ^ ~ &lt;&lt;</c>) computes exactly in lanes
/// of the field's size, and an interval of the values it can take. A value whose interval lies within the unsigned or
/// the signed range of the lanes is exact there: its low bits are the value, so comparisons, Min / Max and right shifts
/// of it are the lanes' own. A sum or difference of exact values that may leave the range keeps its operands, and a
/// chain of Min / Max that brings it back into the range is the saturating form (<c>Math.Min(a.X + b.X, 255)</c> is
/// <c>AddSaturate</c>), by this argument per lane: a chain of Min / Max with bounds in the range is a clamp to
/// [p, q] within it, so when its result lies in the range, the sum above (below) it gives what the range's top
/// (bottom) gives. Anything whose value the lanes cannot hold (a right shift of a product, <c>(byte)Math.Min(a - b, 10)</c>)
/// leaves the method scalar, with that reason.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>Lanes of 1 or 2 bytes: their arithmetic is C#'s int arithmetic, followed as <see cref="Wide"/> values.</summary>
    private bool _narrow => _size < 4;

    /// <summary>The int locals of a narrow kernel: their values (each a snapshot, never written in place).</summary>
    private readonly Dictionary<ILocalSymbol, Wide> _wides = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// An int value of a narrow kernel. <paramref name="Low"/>: its low bits in lanes of the unsigned type of the
    /// kernel's size (null when they are not known); [<paramref name="Lo"/>, <paramref name="Hi"/>]: the values it can
    /// take; <paramref name="Pending"/>: the sum it is, when its operands are exact, for a saturating form.
    /// </summary>
    private sealed record Wide(string? Low, long Lo, long Hi, Sum? Pending);

    /// <summary>
    /// <paramref name="X"/> + <paramref name="Y"/> (or -), exact operands, then Min / Max with each bound of
    /// <paramref name="Chain"/> in order.
    /// </summary>
    private sealed record Sum(bool Add, Wide X, Wide Y, (bool Min, Wide Bound)[] Chain);

    private int Bits => _size * 8;
    private string Unsigned => _size == 1 ? "byte" : "ushort";
    private string SignedLane => _size == 1 ? "sbyte" : "short";
    private long Top(bool signed) => signed ? (1L << (Bits - 1)) - 1 : (1L << Bits) - 1;
    private long Bottom(bool signed) => signed ? -(1L << (Bits - 1)) : 0;
    private string Kind(bool signed) => signed ? SignedLane : Unsigned;

    private static bool IsInt(ITypeSymbol? t) => t?.SpecialType == SpecialType.System_Int32;

    /// <summary>The values a lane type (or int) holds.</summary>
    private static (long Lo, long Hi) Range(ITypeSymbol? type) => type?.SpecialType switch
    {
        SpecialType.System_Byte => (byte.MinValue, byte.MaxValue),
        SpecialType.System_SByte => (sbyte.MinValue, sbyte.MaxValue),
        SpecialType.System_Int16 => (short.MinValue, short.MaxValue),
        SpecialType.System_UInt16 => (ushort.MinValue, ushort.MaxValue),
        _ => (int.MinValue, int.MaxValue),
    };

    /// <summary>A value of [lo, hi]; outside int's range, C#'s int arithmetic wraps: any int, the low bits still exact.</summary>
    private static Wide Of(string? low, long lo, long hi, Sum? pending = null) =>
        lo < int.MinValue || hi > int.MaxValue ? new Wide(low, int.MinValue, int.MaxValue, null) : new Wide(low, lo, hi, pending);

    /// <summary>Whether every value <paramref name="w"/> can take lies in the signed (unsigned) range of the lanes.</summary>
    private bool Fits(Wide w, bool signed) => w.Low != null && w.Lo >= Bottom(signed) && w.Hi <= Top(signed);

    /// <summary>The kinds <paramref name="w"/> is exact in, unsigned first.</summary>
    private IEnumerable<bool> Kinds(params Wide[] values)
    {
        foreach (bool signed in new[] { false, true })
            if (values.All(v => Fits(v, signed))) yield return signed;
    }

    /// <summary>The low bits of <paramref name="w"/> as lanes of the signed (unsigned) type.</summary>
    private string In(Wide w, bool signed) => As(Unsigned, Kind(signed), w.Low!);

    /// <summary>A computed vector in a temp: values are evaluated where they are written, before later writes.</summary>
    private string Held(string value)
    {
        if (value.StartsWith("__", System.StringComparison.Ordinal) && value.All(c => char.IsLetterOrDigit(c) || c == '_')) return value;
        string name = Temp("w");
        _body.Append($"var {name} = {value};\n");
        return name;
    }

    private Wide Constant(long value) =>
        new(Broadcast($"unchecked(({Unsigned})({value}))", Unsigned), value, value, null);

    // ---------------- values ----------------

    /// <summary><paramref name="op"/>, an int or a lane of this kernel, as a <see cref="Wide"/> value.</summary>
    private Wide WideOf(IOperation op)
    {
        if (IsUniform(op))
        {
            if (op.ConstantValue is { HasValue: true, Value: { } c } && c is int or byte or sbyte or short or ushort)
                return Constant(System.Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture));
            if (!IsInt(op.Type) && !(IsLane(op.Type) && !IsFloat(op.Type)))
                throw new Unsupported($"'{Text(op)}' is a {op.Type?.ToDisplayString()}, not an int or a lane type of {_size} bytes");
            var (lo, hi) = Range(op.Type);
            return new Wide(Broadcast($"unchecked(({Unsigned})({Scalar(op)}))", Unsigned), lo, hi, null);
        }
        switch (op)
        {
            case IConversionOperation { Conversion.IsIdentity: true } c:
                return WideOf(c.Operand);
            case IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c when IsInt(c.Type) && IsLane(c.Operand.Type):
                return FromLane(c.Operand);
            case ILocalReferenceOperation { Local: var local } when _wides.TryGetValue(local, out var value):
                return value;
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } b when IsInt(b.Type):
                return WideBinary(b.OperatorKind, WideOf(b.LeftOperand), WideOf(b.RightOperand), b);
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } u when IsInt(u.Type):
            {
                var x = WideOf(u.Operand);
                string? low = x.Low;
                return u.OperatorKind switch
                {
                    UnaryOperatorKind.Plus => x,
                    UnaryOperatorKind.Minus => Of(low == null ? null : Held($"({Vec(Unsigned)}.Zero - {low})"), -x.Hi, -x.Lo),
                    UnaryOperatorKind.BitwiseNegation => Of(low == null ? null : Held($"(~{low})"), -x.Hi - 1, -x.Lo - 1),
                    _ => throw Not(u),
                };
            }
            case IConditionalOperation { IsRef: false, WhenFalse: { } whenFalse } t when IsInt(t.Type):
            {
                string condition = Hold(Expr(t.Condition), t.Condition.Type!);
                return Select(condition, WideOf(t.WhenTrue), WideOf(whenFalse));
            }
            case IInvocationOperation { Instance: null } call when IsMath(call.TargetMethod) && IsInt(call.TargetMethod.ReturnType)
                && call.TargetMethod.Parameters.All(p => IsInt(p.Type)):
                return WideCall(call);
            default:
                if (IsLane(op.Type) && !IsFloat(op.Type)) return FromLane(op);
                throw Not(op);
        }
    }

    /// <summary>A lane value promoted to int: its bits, and its type's range.</summary>
    private Wide FromLane(IOperation op)
    {
        var (lo, hi) = Range(op.Type);
        return new Wide(As(Elem(op.Type!), Unsigned, Expr(op)), lo, hi, null);
    }

    /// <summary>
    /// An int operation as C# computes it: + - * &amp; | ^ and left shifts on the low bits (they depend on no higher bit),
    /// right shifts by a constant on exact values.
    /// </summary>
    private Wide WideBinary(BinaryOperatorKind kind, Wide l, Wide r, IOperation op)
    {
        string? Both(string text) => l.Low == null || r.Low == null ? null : Held($"({l.Low} {text} {r.Low})");
        switch (kind)
        {
            case BinaryOperatorKind.Add or BinaryOperatorKind.Subtract:
            {
                bool add = kind == BinaryOperatorKind.Add;
                long lo = add ? l.Lo + r.Lo : l.Lo - r.Hi, hi = add ? l.Hi + r.Hi : l.Hi - r.Lo;
                // the operands, when exact, for Math.Min / Max to make it a saturating sum
                Sum? sum = Kinds(l, r).Any() ? new Sum(add, l, r, []) : null;
                return Of(Both(add ? "+" : "-"), lo, hi, sum);
            }
            case BinaryOperatorKind.Multiply:
            {
                long[] corners = [l.Lo * r.Lo, l.Lo * r.Hi, l.Hi * r.Lo, l.Hi * r.Hi]; // ints: their products fit a long
                return Of(Both("*"), corners.Min(), corners.Max());
            }
            case BinaryOperatorKind.And:
                // a non-negative side bounds the result: x & 0b111 is in [0, 7] whatever x is
                return l.Lo >= 0 || r.Lo >= 0
                    ? Of(Both("&"), 0, l.Lo >= 0 && r.Lo >= 0 ? System.Math.Min(l.Hi, r.Hi) : l.Lo >= 0 ? l.Hi : r.Hi)
                    : Of(Both("&"), int.MinValue, int.MaxValue);
            case BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr:
            {
                string text = kind == BinaryOperatorKind.Or ? "|" : "^";
                if (l.Lo < 0 || r.Lo < 0) return Of(Both(text), int.MinValue, int.MaxValue);
                long ones = 1;
                while (ones <= System.Math.Max(l.Hi, r.Hi)) ones <<= 1;
                return Of(Both(text), 0, ones - 1);
            }
            case BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift:
            {
                if (r.Lo != r.Hi) throw new Unsupported($"the shift '{Text(op)}' is by a count that is not a constant");
                int count = (int)(r.Lo & 31); // C# masks an int's count; vector shifts of narrow lanes mask by their width
                if (kind == BinaryOperatorKind.LeftShift)
                {
                    string? low = l.Low == null ? null : count >= Bits ? $"{Vec(Unsigned)}.Zero" : Held($"{V}.ShiftLeft({l.Low}, {count})");
                    return Of(low, l.Lo << count, l.Hi << count);
                }
                if (kind == BinaryOperatorKind.UnsignedRightShift && l.Lo < 0)
                    throw new Unsupported($"the shift '{Text(op)}' moves the sign bits of an int into {Bits}-bit lanes");
                // the bits shifted in come from above the lanes: only an exact value has them (its sign, or zeros)
                if (Fits(l, false))
                    return Of(count >= Bits ? $"{Vec(Unsigned)}.Zero" : Held($"{V}.ShiftRightLogical({l.Low}, {count})"), l.Lo >> count, l.Hi >> count);
                if (Fits(l, true))
                    return Of(Held(As(SignedLane, Unsigned, $"{V}.ShiftRightArithmetic({In(l, true)}, {System.Math.Min(count, Bits - 1)})")), l.Lo >> count, l.Hi >> count);
                throw new Unsupported($"the shift '{Text(op)}' needs bits of an int that {Bits}-bit lanes do not hold");
            }
            case BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder:
                throw new Unsupported("integer division is not supported (it throws on 0)");
            default:
                throw Not(op);
        }
    }

    /// <summary>Math.Min, Max, Clamp and Abs of ints.</summary>
    private Wide WideCall(IInvocationOperation call)
    {
        var m = call.TargetMethod;
        IOperation Arg(int i) => call.Arguments.First(a => a.Parameter?.Ordinal == i).Value;
        switch (m.Name)
        {
            case "Min" or "Max" when m.Parameters.Length == 2:
                return MinMax(m.Name == "Min", WideOf(Arg(0)), WideOf(Arg(1)));
            case "Clamp" when m.Parameters.Length == 3:
            {
                // with min <= max, Clamp(x, min, max) is Min(Max(x, min), max); bounds out of order throw
                var (lo, hi) = (WideOf(Arg(1)), WideOf(Arg(2)));
                if (lo.Hi > hi.Lo) throw new Unsupported($"the bounds of '{Text(call)}' are not known to be in order (Clamp throws when min > max)");
                return MinMax(true, MinMax(false, WideOf(Arg(0)), lo), hi);
            }
            case "Abs" when m.Parameters.Length == 1:
            {
                var x = WideOf(Arg(0));
                if (x.Lo >= 0) return x;
                // an exact signed value is never int.MinValue (which Abs throws on); |-128| = 128 wraps to the same low bits
                if (!Fits(x, true)) throw new Unsupported($"'{Text(call)}' takes the absolute value of an int that {Bits}-bit lanes do not hold");
                return Of(Held(As(SignedLane, Unsigned, $"{V}.Abs({In(x, true)})")), x.Hi <= 0 ? -x.Hi : 0, System.Math.Max(-x.Lo, x.Hi));
            }
            default:
                throw Not(call);
        }
    }

    /// <summary>
    /// Math.Min / Max of ints: the lanes' own when both sides are exact in one kind, else a bound of a pending sum (its
    /// saturating form when the result is back in the range).
    /// </summary>
    private Wide MinMax(bool min, Wide a, Wide b)
    {
        // what the intervals decide needs no operation
        if (min ? a.Hi <= b.Lo : a.Lo >= b.Hi) return a;
        if (min ? b.Hi <= a.Lo : b.Lo >= a.Hi) return b;
        long lo = min ? System.Math.Min(a.Lo, b.Lo) : System.Math.Max(a.Lo, b.Lo);
        long hi = min ? System.Math.Min(a.Hi, b.Hi) : System.Math.Max(a.Hi, b.Hi);
        string name = min ? "Min" : "Max";
        foreach (bool signed in Kinds(a, b))
            return Of(Held(As(Kind(signed), Unsigned, $"{V}.{name}({In(a, signed)}, {In(b, signed)})")), lo, hi);
        var (sum, bound) = a.Pending != null && Kinds(b).Any() ? (a.Pending, b) : b.Pending != null && Kinds(a).Any() ? (b.Pending, a) : (null, null);
        if (sum == null) return new Wide(null, lo, hi, null); // its low bits are not known: only a comparison its interval decides
        var pending = sum with { Chain = [.. sum.Chain, (min, bound!)] };
        return new Wide(Saturated(pending, lo, hi), lo, hi, pending);
    }

    /// <summary>
    /// The low bits of a sum and its chain of Min / Max with the result in [lo, hi], as the saturating sum in a kind where
    /// the operands, the bounds and the result are all exact; null when there is none.
    /// </summary>
    private string? Saturated(Sum sum, long lo, long hi)
    {
        foreach (bool signed in new[] { false, true })
        {
            if (lo < Bottom(signed) || hi > Top(signed) || !Fits(sum.X, signed) || !Fits(sum.Y, signed)
                || sum.Chain.Any(c => !Fits(c.Bound, signed))) continue;
            string value = $"{V}.{(sum.Add ? "AddSaturate" : "SubtractSaturate")}({In(sum.X, signed)}, {In(sum.Y, signed)})";
            foreach (var (isMin, bound) in sum.Chain)
            {
                // a bound at the range's edge changes no saturated value
                if (isMin ? bound.Lo >= Top(signed) : bound.Hi <= Bottom(signed)) continue;
                value = $"{V}.{(isMin ? "Min" : "Max")}({value}, {In(bound, signed)})";
            }
            return Held(As(Kind(signed), Unsigned, value));
        }
        return null;
    }

    /// <summary>The value of <paramref name="whenTrue"/> in the lanes of a mask, else of <paramref name="whenFalse"/>.</summary>
    private Wide Select(string mask, Wide whenTrue, Wide whenFalse)
    {
        string? low = whenTrue.Low == null || whenFalse.Low == null ? null
            : Held($"{V}.ConditionalSelect({As(_mask, Unsigned, mask)}, {whenTrue.Low}, {whenFalse.Low})");
        return Of(low, System.Math.Min(whenTrue.Lo, whenFalse.Lo), System.Math.Max(whenTrue.Hi, whenFalse.Hi));
    }

    /// <summary>
    /// <c>(byte)value</c> and its kin, unchecked: the low bits, reinterpreted as the target type. A value whose low bits
    /// are not known leaves the method scalar.
    /// </summary>
    private string Truncate(Wide value, ITypeSymbol type, IOperation op)
    {
        if (value.Low == null)
            throw new Unsupported($"'{Text(op)}' keeps the low bits of an int the vector loop cannot compute in {Bits}-bit lanes (it may leave [{Bottom(false)}, {Top(false)}] and [{Bottom(true)}, {Top(true)}])");
        return As(Unsigned, Elem(type), value.Low);
    }

    // ---------------- comparisons ----------------

    /// <summary>A comparison of ints as a mask: decided by the intervals, or by the lanes where both sides are exact.</summary>
    private string WideCompare(BinaryOperatorKind kind, Wide l, Wide r, IOperation leftOp, IOperation rightOp)
    {
        var op = leftOp.Parent ?? leftOp;
        if (kind is BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
            (kind, l, r) = (kind == BinaryOperatorKind.GreaterThan ? BinaryOperatorKind.LessThan : BinaryOperatorKind.LessThanOrEqual, r, l);
        bool? decided = kind switch
        {
            BinaryOperatorKind.LessThan => l.Hi < r.Lo ? true : l.Lo >= r.Hi ? false : null,
            BinaryOperatorKind.LessThanOrEqual => l.Hi <= r.Lo ? true : l.Lo > r.Hi ? false : null,
            BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals =>
                l.Lo == l.Hi && r.Lo == r.Hi && l.Lo == r.Lo ? kind == BinaryOperatorKind.Equals
                : l.Hi < r.Lo || r.Hi < l.Lo ? kind != BinaryOperatorKind.Equals : null,
            _ => throw Not(op),
        };
        if (decided is { } known) return $"{Vec(_mask)}.{(known ? "AllBitsSet" : "Zero")}";

        string Lanes(Wide a, Wide b, bool signed, System.Func<Wide, bool, string> of)
        {
            string k = Kind(signed);
            string Cmp(string name) => As(k, _mask, $"{V}.{name}<{k}>({of(a, signed)}, {of(b, signed)})");
            return kind switch
            {
                BinaryOperatorKind.LessThan => Cmp("LessThan"),
                BinaryOperatorKind.LessThanOrEqual => Cmp("LessThanOrEqual"),
                BinaryOperatorKind.Equals => Cmp("Equals"),
                _ => $"(~{Cmp("Equals")})",
            };
        }
        foreach (bool signed in Kinds(l, r)) return Lanes(l, r, signed, In);
        // x - y against 0 is x against y, both exact, and an int difference of them never wraps
        if (r is { Lo: 0, Hi: 0 } && l.Pending is { Add: false, Chain.Length: 0 } d)
            foreach (bool signed in Kinds(d.X, d.Y)) return Lanes(d.X, d.Y, signed, In);
        if (l is { Lo: 0, Hi: 0 } && r.Pending is { Add: false, Chain.Length: 0 } e)
            foreach (bool signed in Kinds(e.X, e.Y)) return Lanes(e.Y, e.X, signed, In);
        // A sum that may leave the range, against a value strictly inside it: the saturated sum compares alike (above
        // the range it is the top, which is above that value too).
        foreach (bool signed in new[] { false, true })
        {
            bool Inside(Wide w) => Fits(w, signed) && w.Lo > Bottom(signed) && w.Hi < Top(signed);
            bool Saturates(Wide w) => w.Pending is { Chain.Length: 0 } s && Fits(s.X, signed) && Fits(s.Y, signed);
            if (Saturates(l) && Inside(r) || Saturates(r) && Inside(l))
                return Lanes(l, r, signed, (w, k) => w.Pending is { Chain.Length: 0 } s && !Inside(w)
                    ? $"{V}.{(s.Add ? "AddSaturate" : "SubtractSaturate")}({In(s.X, k)}, {In(s.Y, k)})"
                    : In(w, k));
        }
        throw new Unsupported($"'{Text(op)}' compares ints that {Bits}-bit lanes do not hold");
    }

    /// <summary><c>value is pattern</c> of an int, as comparisons with its constants.</summary>
    private string WidePattern(Wide value, IPatternOperation pattern, IOperation op)
    {
        Wide Value(IOperation constant) => constant.ConstantValue is { HasValue: true, Value: int c } ? Constant(c) : throw Not(op);
        switch (pattern)
        {
            case IRelationalPatternOperation relational:
                return WideCompare(relational.OperatorKind, value, Value(relational.Value), op, op);
            case IConstantPatternOperation { Value: var constant }:
                return WideCompare(BinaryOperatorKind.Equals, value, Value(constant), op, op);
            case IBinaryPatternOperation { OperatorKind: var kind, LeftPattern: var left, RightPattern: var right }:
                return $"({WidePattern(value, left, op)} {(kind == BinaryOperatorKind.And ? "&" : "|")} {WidePattern(value, right, op)})";
            case INegatedPatternOperation { Pattern: var negated }:
                return $"(~{WidePattern(value, negated, op)})";
            default:
                throw Not(pattern);
        }
    }

    // ---------------- int locals ----------------

    /// <summary>
    /// A value kept: its lanes copied where they name a vector the method may write later (a component's lane, a
    /// local), with the operands of its pending sum.
    /// </summary>
    private Wide Snapshot(Wide w)
    {
        string? Copy(string? low)
        {
            if (low == null || !(low.StartsWith("__c", System.StringComparison.Ordinal) || low.StartsWith("__l", System.StringComparison.Ordinal))) return low;
            string name = Temp("w");
            _body.Append($"var {name} = {low};\n");
            return name;
        }
        var pending = w.Pending is { } s ? new Sum(s.Add, Snapshot(s.X), Snapshot(s.Y), s.Chain.Select(c => (c.Min, Snapshot(c.Bound))).ToArray()) : null;
        return new Wide(Copy(w.Low), w.Lo, w.Hi, pending);
    }

    private void DeclareWide(ILocalSymbol local, Wide value) => _wides[local] = Snapshot(value); // other lanes: never read

    /// <summary>An int local set in the lanes of <paramref name="mask"/> that have not returned.</summary>
    private void WriteWide(IOperation target, Wide value, string? mask)
    {
        if (target is not ILocalReferenceOperation { Local: var local } || !_wides.TryGetValue(local, out var old))
            throw new Unsupported($"it writes '{Text(target)}', which is neither a local nor the lane of a component taken by ref");
        string? active = And(mask, _alive);
        _wides[local] = active == null ? Snapshot(value) : Snapshot(Select(Held(active), value, old));
    }
}
