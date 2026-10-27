using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace EasyEcs.Generator;

/// <summary>
/// A pair method's vector loop (<c>[Neighbor]</c> parameters, PairForm.cs): a window of rows in the lanes against one
/// neighbor at a time (GROMACS's cluster pair, i in lanes, j broadcast). The method's own components are the lanes, as
/// in any vector loop; a neighbor's components are values of the j loop, read once per neighbor and broadcast
/// (<see cref="Neighbor"/>), so the method's body translates as it does for a row (its cutoff test a mask). The
/// window's writes are kept in registers across its neighbors and stored once, blended with the window's rows
/// (<c>__pass</c>): per lane the pairs run in the order the scalar loop runs them, so the results are its bit for bit.
/// What the loop cannot hold across neighbors (a reduction, a lookup, an optional column, a structural change, a lane
/// that would throw) leaves the pairs scalar.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>The prefix of a neighbor parameter's name in the j loop: <c>__pair{i}</c> for parameter i.</summary>
    internal const string NeighborPrefix = "__pair";

    /// <summary>The name the j loop gives neighbor parameter <paramref name="i"/> (a <c>ref readonly</c> to its row).</summary>
    internal static string Neighbor(int i) => NeighborPrefix + i;

    /// <summary>
    /// Why a [Neighbor] parameter rules a vector loop out, or null: a component read is a value per neighbor. A write of
    /// the neighbor's is one row written by every lane of the window (a scatter whose lanes collide), so it runs per pair.
    /// </summary>
    private static string? NeighborRefused(IParameterSymbol p) =>
        Symbols.Named(p.Type, Symbols.Entity) ? $"it takes the neighbor's Entity '{p.Name}': its pairs run one at a time"
        : p.RefKind == RefKind.Ref ? $"it writes the neighbor's {p.Type.Name} ('{p.Name}', [Neighbor] ref): its pairs run one at a time"
        : null;

    /// <summary>
    /// <paramref name="op"/> as scalar C# the generated file compiles (what a vector loop computes once per run), its
    /// parameters named by <paramref name="names"/>; null with the reason when it is not such a value: a pair job's
    /// cutoff (PairForm).
    /// </summary>
    internal static string? ScalarOf(IOperation op, IReadOnlyDictionary<IParameterSymbol, string> names, out string? reason)
    {
        var kernel = new Kernel(4);
        foreach (var name in names) kernel._uniformParams[name.Key] = name.Value;
        reason = null;
        try
        {
            return kernel.Scalar(op);
        }
        catch (Unsupported unsupported)
        {
            reason = unsupported.Reason;
            return null;
        }
    }

    /// <summary>
    /// The loop of a pair method from its translation: <paramref name="loads"/> (the window's rows), the translated body
    /// with its per-neighbor broadcasts first, <paramref name="stores"/> (the window's writes, blended), the setup
    /// without those broadcasts.
    /// </summary>
    private Code ForPairs(string loads, string stores, string lane)
    {
        string? why = _reductions.Count > 0 ? "it takes a Reduce<T>: a pair method's vector loop holds no partial result"
            : _optionals.Count > 0 ? "it takes a Maybe<T>: a pair method's vector loop reads no optional column"
            : _lookups.Count > 0 ? "it reads other entities through a Lookup<T>"
            : _change != null || _emits.Count > 0 ? "it changes structure or emits records"
            : _bails || _cold != null ? "an operation of its body may throw for some lane, or a branch is cold"
            : _wideOf.Count > 0 ? "a component is wide"
            : _pairs || _size != 4 ? "its lanes are not of 4 bytes (a window is a vector of floats)"
            : null;
        if (why != null) throw new Unsupported(why);

        // A broadcast of a neighbor's value runs once per neighbor, the others once per pass.
        var setup = new StringBuilder();
        var each = new StringBuilder();
        foreach (var line in _setup.ToString().Split('\n'))
            if (line.Length > 0) (line.Contains(NeighborPrefix) ? each : setup).Append(line).Append('\n');
        return new Code(new EquatableArray<string>(_components.Values.OrderBy(c => c.Index).Select(c => Elem(c.Fields[0].Type)).ToArray()),
            setup.ToString(), loads, "", each + _body.Masked, stores, "", "", _size, _mask, lane,
            _components.Any(c => c.Value.Fields.Length > 1), false, null)
        {
            Written = new EquatableArray<int>(_written.OrderBy(k => k).ToArray()),
            Pairs = true,
        };
    }
}
