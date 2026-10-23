using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Lanes of two sizes: a method that sums int rows into a long (<c>total.Value.Sum += rank.Value</c>), keeps a double of
/// float rows or writes a component of long lanes next to int ones. The loop's lanes are the narrowest columns' (one
/// vector of rows per step), and a value twice as wide is a pair of vectors: the lower lanes of the rows and the upper
/// ones (<c>Vector.WidenLower</c> / <c>WidenUpper</c> of a narrow vector, two loads of a wide column). Such a value is
/// computed half by half, each half the same operations on its lanes as the method performs on each row's value, so
/// every lane is the method's bit for bit: a widening conversion is exact (int to long, uint to ulong, float to double,
/// int to double; short to int, byte to ushort), and a narrowing one keeps what C# keeps (<c>(int)</c> of a long its low
/// bits, <c>(float)</c> of a double the nearest float, <c>Vector.Narrow</c>). A comparison of wide values is a mask of
/// each half, narrowed into one of the lanes; a condition of the lanes' width is widened into each half. Writes of a wide
/// value (a local, a component's lane, a reduction's field) store each half; what both halves read of the lanes' width is
/// computed once, before either is written (<see cref="Narrowed"/>). Lanes four times as wide stay scalar.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>The narrowest columns' lane size: the loop's lanes, one vector of rows per step.</summary>
    private readonly int _base;

    /// <summary>The half of a pair being translated: 0 for the lower lanes, 1 for the upper ones, -1 for the lanes.</summary>
    private int _half = -1;

    /// <summary>The components whose lanes are twice <see cref="_base"/>: two vectors per step (<c>__c{k}_{f}_0</c>, <c>_1</c>).</summary>
    private readonly HashSet<int> _doubled = new();

    /// <summary>The locals twice as wide as the lanes: two vector locals each (<c>name_0</c>, <c>name_1</c>).</summary>
    private readonly HashSet<ILocalSymbol> _pairLocals = new(SymbolEqualityComparer.Default);

    /// <summary>The values of the lanes' width a statement's halves read, computed once (cleared per statement).</summary>
    private readonly Dictionary<IOperation, string> _held = new();

    /// <summary>
    /// The method writes a component or a reduction twice as wide as the lanes: the int locals of 2-byte lanes are then
    /// pairs (exact ints, not the low bits <see cref="Wide"/> follows).
    /// </summary>
    private bool _pairs;

    /// <summary>A masked store or reduction of a pair reads the passing rows as masks of its halves (<c>__pw_0</c>, <c>__pw_1</c>).</summary>
    private bool _halvesPass;

    /// <summary>A lane type twice as wide as the loop's lanes.</summary>
    private bool IsDoubled(ITypeSymbol? type) => _base < 8 && IsLaneType(type) && SizeOf(type!) == 2 * _base;

    /// <summary>The lane sizes a value can have, for a reason.</summary>
    private string LaneSizes => _base < 8 ? $"{_base} or {2 * _base}" : "8";

    private string WidenHalf => _half == 0 ? "WidenLower" : "WidenUpper";

    /// <summary>
    /// A mask of the lanes as a mask of half <see cref="_half"/>: each lane's bits extended to twice their width. A mask
    /// narrowed from halves (a comparison of pairs) is its half again, and a conjunction (<see cref="And"/>) each of its
    /// atoms.
    /// </summary>
    private string WidenMask(string mask)
    {
        if (!mask.All(c => char.IsLetterOrDigit(c) || c is '_' or '~' or '(' or ')' or ' ' or '&')) return $"{V}.{WidenHalf}({mask})";
        var atoms = Atoms(mask).Select(atom =>
        {
            string name = atom.TrimStart('~');
            string half = _maskHalves.TryGetValue(name, out var halves) ? (_half == 0 ? halves.Lower : halves.Upper) : $"{V}.{WidenHalf}({name})";
            return atom[0] == '~' ? $"(~{half})" : half;
        }).ToArray();
        return atoms.Length == 1 ? atoms[0] : $"({string.Join(" & ", atoms)})";
    }

    /// <summary>The halves of the masks narrowed from a comparison of pairs (by text, and by the locals that hold them).</summary>
    private readonly Dictionary<string, (string Lower, string Upper)> _maskHalves = new();

    /// <summary><paramref name="name"/> holds <paramref name="value"/>: a mask narrowed from halves keeps them.</summary>
    private void Alias(string name, string value)
    {
        if (_maskHalves.TryGetValue(value, out var halves)) _maskHalves[name] = halves;
    }

    /// <summary>The suffixes of a value's vectors: one, or the two halves of a pair.</summary>
    private static string[] Suffixes(bool pair) => pair ? ["_0", "_1"] : [""];

    /// <summary>The vectors a step reads of component <paramref name="index"/>'s column: their suffix and the row they start at.</summary>
    private (string Half, string Row)[] Rows(int index, string column) => _doubled.Contains(index)
        ? [("_0", "(nuint)i"), ("_1", $"(nuint)i + (nuint){Vec(column)}.Count")]
        : [("", "(nuint)i")];

    /// <summary>The passing rows of a masked word (<c>__pass</c>) as a mask of lanes of <paramref name="element"/>, for half <paramref name="half"/>.</summary>
    private string PassAs(string half, string element)
    {
        if (half.Length == 0) return As(_mask, element, "__pass");
        _halvesPass = true;
        return As(MaskOf(2 * _base), element, $"__pw{half}");
    }

    /// <summary>The name of half <see cref="_half"/> of a pair's vectors.</summary>
    private string Half(string name, IOperation op) =>
        _half < 0 ? throw new Unsupported($"'{Text(op)}' is twice as wide as the {_base}-byte lanes, where the loop reads lanes") : $"{name}_{_half}";

    /// <summary>Lane <paramref name="field"/> of component <paramref name="index"/>: its vector, or the half of its pair.</summary>
    private string ComponentLane(int index, int field, IOperation op) =>
        _doubled.Contains(index) ? Half($"__c{index}_{field}", op) : $"__c{index}_{field}";

    /// <summary>A local's vector: itself, or the half of a pair local.</summary>
    private string Local(ILocalSymbol local, string name, IOperation op) => _pairLocals.Contains(local) ? Half(name, op) : name;

    /// <summary>Field <paramref name="field"/> of a reduction's lane accumulators: its vector, or the half of its pair.</summary>
    private string Acc(Reduction r, int field) =>
        IsDoubled(r.Fields[field].Type) ? $"__a{r.Index}_{field}_{(_half < 0 ? throw new Unsupported("a statement mixes lane widths") : _half)}" : $"__a{r.Index}_{field}";

    // ---------------- modes ----------------

    /// <summary><paramref name="translate"/> for half <paramref name="half"/> of a pair: lanes twice as wide.</summary>
    private T InHalf<T>(int half, System.Func<T> translate)
    {
        var saved = (_size, _mask, _half);
        (_size, _mask, _half) = (2 * _base, MaskOf(2 * _base), half);
        try
        {
            return translate();
        }
        finally
        {
            (_size, _mask, _half) = saved;
        }
    }

    /// <summary><paramref name="translate"/> for the loop's own lanes (a value of their width inside a half).</summary>
    private T InBase<T>(System.Func<T> translate)
    {
        var saved = (_size, _mask, _half);
        (_size, _mask, _half) = (_base, MaskOf(_base), -1);
        try
        {
            return translate();
        }
        finally
        {
            (_size, _mask, _half) = saved;
        }
    }

    private (string Lower, string Upper) Halves(System.Func<string> translate) => (InHalf(0, translate), InHalf(1, translate));

    private void EachHalf(System.Action translate)
    {
        InHalf(0, () => { translate(); return 0; });
        InHalf(1, () => { translate(); return 0; });
    }

    /// <summary>
    /// A value of the lanes' width read inside a half, in a local: computed once for both halves, before either writes
    /// (half 1 reads the local half 0 made).
    /// </summary>
    private string Narrowed(IOperation op)
    {
        if (_held.TryGetValue(op, out var held)) return held;
        held = InBase(() => Hold(Expr(op), op.Type!));
        _held[op] = held;
        return held;
    }

    // ---------------- which values are pairs ----------------

    /// <summary>
    /// Whether <paramref name="op"/> reads a value only pairs hold: a pair local, a lane of a wide component, or any value
    /// twice as wide as the lanes (except the ints of 2-byte lanes, which <see cref="Wide"/> follows unless they read a pair).
    /// </summary>
    private bool Paired(IOperation op)
    {
        foreach (var d in op.DescendantsAndSelf())
        {
            if (d is ILocalReferenceOperation { Local: var local } && _pairLocals.Contains(local)) return true;
            if (_doubled.Count > 0 && d is IFieldReferenceOperation or IPropertyReferenceOperation && LaneOf(d) is { } c && _doubled.Contains(c.Index)) return true;
            if (IsDoubled(d.Type) && !(_base == 2 && IsInt(d.Type))) return true;
        }
        return false;
    }

    /// <summary>
    /// An operand a comparison computes in halves: a pair value, or an int that reads one in a kernel of byte lanes (C#
    /// compares the ints its ushort lanes promote to).
    /// </summary>
    private bool PairOperand(IOperation op) => Paired(op) && (IsDoubled(op.Type) || _base == 1 && IsInt(op.Type));

    /// <summary>Whether <paramref name="op"/> writes a pair: a pair local, a lane of a wide component or a wide reduction field.</summary>
    private bool PairTarget(IOperation op)
    {
        var target = op switch
        {
            ISimpleAssignmentOperation { IsRef: false } a => a.Target,
            ICompoundAssignmentOperation a => a.Target,
            IIncrementOrDecrementOperation a => a.Target,
            _ => null,
        };
        if (target == null) return false;
        if (target is ILocalReferenceOperation { Local: var local }) return _pairLocals.Contains(local);
        if (Accumulator(target) is { } acc) return IsDoubled(acc.Reduction.Fields[acc.Field].Type);
        return _doubled.Count > 0 && LaneOf(target) is { } c && _doubled.Contains(c.Index);
    }

    /// <summary>
    /// Whether a local is a pair: twice as wide as the lanes; an int of 2-byte lanes only when the method writes pairs or
    /// its value reads one (else <see cref="Wide"/> follows it, in the lanes).
    /// </summary>
    private bool PairLocal(ILocalSymbol local, IOperation init) =>
        IsDoubled(local.Type) && !(_base == 2 && IsInt(local.Type) && !_pairs && !Paired(init));

    // ---------------- translation ----------------

    /// <summary>
    /// What <see cref="Expr"/> computes across the two widths, or null: a condition of one width read in the other, a
    /// conversion between them, or the reason a value of one width stands where the other is computed.
    /// </summary>
    private string? PairExpr(IOperation op)
    {
        if (IsBool(op.Type))
        {
            // a comparison, pattern or test (not a combination of conditions) is computed in the width of its operands
            if (op is IBinaryOperation b && IsBool(b.LeftOperand.Type) && IsBool(b.RightOperand.Type)
                || op is IUnaryOperation or IConditionalOperation or IConversionOperation { Conversion.IsIdentity: true }) return null;
            bool paired = Operands(op).Any(PairOperand);
            if (_half < 0 && paired)
            {
                var (lower, upper) = Halves(() => Expr(op));
                (lower, upper) = (Hold(lower, op.Type!), Hold(upper, op.Type!));
                string mask = $"{V}.Narrow({lower}, {upper})"; // all bits of a lane set or none: the lanes' mask
                _maskHalves[mask] = (lower, upper);
                return mask;
            }
            return _half >= 0 && !paired ? WidenMask(Narrowed(op)) : null;
        }
        if (op is IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c && IsLaneType(c.Type) && IsLaneType(c.Operand.Type))
        {
            if (_half < 0 && IsLane(c.Type) && IsDoubled(c.Operand.Type) && Paired(c.Operand))
            {
                var (lower, upper) = Halves(() => Expr(c.Operand));
                return Narrowed(lower, upper, c.Operand.Type!, c.Type!) ?? throw Not(c);
            }
            if (_half >= 0 && IsLane(c.Type) && SizeOf(c.Operand.Type!) == _base)
                return Widened(Narrowed(c.Operand), c.Operand.Type!, c.Type!) ?? throw Not(c);
        }
        if (_half < 0 && IsDoubled(op.Type) && !(_narrow && IsInt(op.Type)))
            throw new Unsupported($"'{Text(op)}' is a {op.Type!.ToDisplayString()}, twice as wide as the {_base}-byte lanes, where the loop needs a lane: a vector loop computes it in two halves to store it, compare it or convert it back");
        if (_half >= 0 && IsLaneType(op.Type) && SizeOf(op.Type!) == _base)
            throw new Unsupported($"'{Text(op)}' is a {op.Type!.ToDisplayString()} of the {_base}-byte lanes, where the loop computes lanes twice as wide");
        // an int local of 2-byte lanes (Wide) read where int lanes are computed
        if (_half >= 0 && !_narrow && op is ILocalReferenceOperation { Local: var local } && _wides.TryGetValue(local, out var wide))
            return As(MaskOf(_size), Elem(op.Type!), HalfOf(wide, op));
        return null;
    }

    private static IEnumerable<IOperation> Operands(IOperation op) => op switch
    {
        IBinaryOperation b => [b.LeftOperand, b.RightOperand],
        IIsPatternOperation p => [p.Value],
        IInvocationOperation call => call.Arguments.Select(a => a.Value),
        _ => [],
    };

    /// <summary>The lane type twice as wide as <paramref name="type"/>, or null.</summary>
    private static SpecialType Twice(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Int32 => SpecialType.System_Int64,
        SpecialType.System_UInt32 => SpecialType.System_UInt64,
        SpecialType.System_Single => SpecialType.System_Double,
        SpecialType.System_Int16 => SpecialType.System_Int32,
        SpecialType.System_UInt16 => SpecialType.System_UInt32,
        SpecialType.System_SByte => SpecialType.System_Int16,
        SpecialType.System_Byte => SpecialType.System_UInt16,
        _ => SpecialType.None,
    };

    /// <summary>
    /// Half <see cref="_half"/> of a vector of <paramref name="from"/> lanes, converted to <paramref name="to"/> as C#
    /// converts each (exactly: every value of the narrower type is one of the wider), or null: integers sign- or
    /// zero-extended as their type is signed or not, then reinterpreted (int to ulong keeps the sign's bits, as unchecked
    /// C# does); a float to a double; an integer to a float of twice its size through the integer of that size.
    /// </summary>
    private string? Widened(string value, ITypeSymbol from, ITypeSymbol to)
    {
        var twice = Twice(from);
        if (twice == SpecialType.None) return null;
        var wide = _compilation!.GetSpecialType(twice);
        string half = $"{V}.{WidenHalf}({value})";
        if (IsFloat(from)) return to.SpecialType == SpecialType.System_Double ? half : null;
        return IsFloat(to) ? Convert(half, wide, to) : As(Elem(wide), Elem(to), half);
    }

    /// <summary>
    /// A pair of <paramref name="from"/> lanes as one vector of <paramref name="to"/>, the lanes' width, as C# converts
    /// each, or null: an integer keeps its low bits (unchecked), a double rounds to the nearest float. A float of an integer
    /// twice as wide or an integer of a float would round or saturate twice: null.
    /// </summary>
    private string? Narrowed(string lower, string upper, ITypeSymbol from, ITypeSymbol to)
    {
        if (IsFloat(from)) return from.SpecialType == SpecialType.System_Double && to.SpecialType == SpecialType.System_Single ? $"{V}.Narrow({lower}, {upper})" : null;
        if (IsFloat(to)) return null;
        string signed = MaskOf(2 * _base);
        return As(MaskOf(_base), Elem(to), $"{V}.Narrow({As(Elem(from), signed, lower)}, {As(Elem(from), signed, upper)})");
    }

    /// <summary>
    /// Half <see cref="_half"/> of an int a narrow kernel follows as <see cref="Wide"/>, as lanes of the signed integer twice
    /// the lanes' size: widened from the lanes where they hold it exactly, else the reason.
    /// </summary>
    private string HalfOf(Wide w, IOperation op)
    {
        var kinds = InBase(() => Kinds(w).ToArray());
        if (kinds.Length == 0)
            throw new Unsupported($"'{Text(op)}' is an int of which the {_base * 8}-bit lanes hold only the low bits, where the loop computes lanes of {_base * 16} bits");
        bool signed = kinds[0];
        string low = InBase(() => In(w, signed));
        var twice = Twice(_compilation!.GetSpecialType(signed ? (_base == 1 ? SpecialType.System_SByte : SpecialType.System_Int16) : (_base == 1 ? SpecialType.System_Byte : SpecialType.System_UInt16)));
        return As(Elem(_compilation.GetSpecialType(twice)), MaskOf(2 * _base), $"{V}.{WidenHalf}({low})");
    }

    /// <summary>An integer lane value <see cref="Wide"/> reads across the widths: the lanes' own inside a half, or a pair in the lanes.</summary>
    private bool PairLane(IOperation op) => IsLaneType(op.Type) && !IsFloat(op.Type)
        && (_half >= 0 ? SizeOf(op.Type!) == _base : IsDoubled(op.Type) && Paired(op));

    /// <summary>
    /// <see cref="PairLane"/>'s value as a <see cref="Wide"/>: inside a half, the lanes' value widened; in the lanes, the
    /// low bits of a pair (its halves narrowed). Either way the type's range.
    /// </summary>
    private Wide PairWide(IOperation op)
    {
        var (lo, hi) = Range(op.Type);
        if (_half >= 0)
        {
            string twice = Elem(_compilation!.GetSpecialType(Twice(op.Type!)));
            return new Wide(As(twice, Unsigned, $"{V}.{WidenHalf}({Narrowed(op)})"), lo, hi, null);
        }
        var (lower, upper) = Halves(() => Expr(op));
        return new Wide(Held(As(MaskOf(_base), Unsigned, $"{V}.Narrow({As(Elem(op.Type!), MaskOf(2 * _base), lower)}, {As(Elem(op.Type!), MaskOf(2 * _base), upper)})")), lo, hi, null);
    }
}
