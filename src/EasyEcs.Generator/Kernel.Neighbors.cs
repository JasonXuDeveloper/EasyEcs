using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A pair method's vector loop (<c>[Neighbor]</c> parameters, PairForm.cs): a cluster of rows in the lanes against one
/// neighbor at a time (GROMACS's cluster pair, i in lanes, j broadcast). The method's own components are the lanes, as
/// in any vector loop; a neighbor's components are values of the j loop, read once per neighbor and broadcast
/// (<see cref="Neighbor"/>), so the method's body translates as it does for a row (its cutoff test a mask). The
/// cluster's writes are kept in registers across its neighbors and stored once, blended with the cluster's rows
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
    /// the neighbor's is one row written by every lane of the cluster (a scatter whose lanes collide), so it runs per pair,
    /// except in a method that writes both sides of its pairs (<paramref name="symmetric"/>): there each sum into the
    /// neighbor (<c>g.X -= f</c>) adds the cluster's lanes into it one at a time, in the order of the pairs
    /// (<see cref="WritesNeighbor"/>).
    /// </summary>
    private static string? NeighborRefused(IParameterSymbol p, bool symmetric) =>
        Symbols.Named(p.Type, Symbols.Entity) ? $"it takes the neighbor's Entity '{p.Name}': its pairs run one at a time"
        : p.RefKind == RefKind.Ref && !symmetric ? $"it writes the neighbor's {p.Type.Name} ('{p.Name}', [Neighbor] ref): its pairs run one at a time"
        : null;

    /// <summary>The neighbor's components a symmetric pair method writes: parameter -> its position i (<c>o{i}</c>, <see cref="Neighbor"/>).</summary>
    private readonly Dictionary<IParameterSymbol, int> _neighborWrites = new(SymbolEqualityComparer.Default);

    /// <summary>The neighbor's fields written so far (parameter position, field): each one once.</summary>
    private readonly HashSet<(int, int)> _neighborFields = new();

    /// <summary>
    /// <paramref name="op"/> as a write of a symmetric method's neighbor (<c>[Neighbor] ref</c>): a sum into one of its
    /// lane fields (<c>g.X -= v</c>, <c>g.X += v</c>) of a value of the field's type, each field once. The value is
    /// computed per lane, then the active lanes are added into the neighbor one at a time from lane 0 up
    /// (<c>PairRun.SubtractLanes</c>), which is the order the method per pair runs the cluster's rows in, so the sum is
    /// its bit for bit (GROMACS's j reduction, kept in order); a neighbor that is a row of the cluster itself
    /// (<c>__dj</c>, the lane holding it, 0 or more) is added into that lane of the cluster's vector. False when
    /// <paramref name="op"/> writes no neighbor; any other write of the neighbor keeps the pairs one at a time.
    /// </summary>
    private bool WritesNeighbor(IOperation op, string? mask)
    {
        if (_neighborWrites.Count == 0) return false;
        var target = op switch
        {
            IAssignmentOperation a => a.Target,
            IIncrementOrDecrementOperation i => i.Target,
            _ => null,
        };
        if (target == null || Chain(target) is not ({ } root, var members) || root is not IParameterReferenceOperation { Parameter: var p }
            || !_neighborWrites.TryGetValue(p, out int position))
            return false;
        // g.X += v, g.X -= v (v of the field's type), g.X++, g.X--.
        bool add;
        IOperation? added = null;
        if (op is ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, IsChecked: false, OperatorMethod: null } sum
            && sum.InConversion.IsIdentity && sum.OutConversion.IsIdentity)
        {
            add = sum.OperatorKind == BinaryOperatorKind.Add;
            added = sum.Value;
        }
        else if (op is IIncrementOrDecrementOperation { IsChecked: false, OperatorMethod: null } step) add = step.Kind == OperationKind.Increment;
        else throw new Unsupported($"'{Text(op)}' writes the neighbor's {p.Type.Name} otherwise than by a sum into a field (+=, -=, ++ or --): its pairs run one at a time") { IsWholeMethod = true };
        // The row's component of the same type: its lanes hold a neighbor that is a row of the cluster.
        var row = _components.FirstOrDefault(c => c.Value.Ref && SymbolEqualityComparer.Default.Equals(c.Key.Type, p.Type));
        int field = row.Key == null || members.Length == 0 ? -1 : Leaf(_paths[row.Key], members);
        if (field < 0 || added != null && !SymbolEqualityComparer.Default.Equals(added.Type, target.Type))
            throw new Unsupported($"'{Text(op)}' adds into the neighbor what is not a lane of its {p.Type.Name} of the field's type: its pairs run one at a time") { IsWholeMethod = true };
        if (!_neighborFields.Add((position, field)))
            throw new Unsupported($"it writes the neighbor's '{Text(target)}' twice: write it once, its pairs run one at a time otherwise") { IsWholeMethod = true };
        int index = row.Value.Index;
        _written.Add(index); // the cluster's lanes of the neighbor rows in it are stored
        string value = Temp("nv");
        _body.Append($"var {value} = {(added == null ? $"{VecOf(target.Type)}.One" : Expr(added))};\n");
        string lanes = add ? "AddLanes" : "SubtractLanes";
        string lane = $"__c{index}_{field}", into = $"{Neighbor(position)}.{string.Join(".", members.Select(m => m.Name))}";
        Live(And(mask, _alive), $"if (__dj >= 0) {lane} = global::EasyEcs.PairRun.{lanes}({lane}, __dj, {value}, {LiveLanes});\n"
            + $"else {into} = global::EasyEcs.PairRun.{lanes}({into}, {value}, {LiveLanes});\n");
        return true;
    }

    /// <summary>Refuses a read of the neighbor's component a symmetric method writes: the lanes' writes reach it only after them.</summary>
    private void ReadsWrittenNeighbor(IOperation op)
    {
        if (Chain(op) is ({ } root, _) && root is IParameterReferenceOperation { Parameter: var p } && _neighborWrites.ContainsKey(p))
            throw new Unsupported($"it reads the neighbor's {p.Type.Name} it writes ('{Text(op)}'): the lanes add into the neighbor after the pairs, so its pairs run one at a time") { IsWholeMethod = true };
    }

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
    /// The loop of a pair method from its translation: <paramref name="loads"/> (the cluster's rows), the translated body
    /// with its per-neighbor broadcasts first, <paramref name="stores"/> (the cluster's writes, blended), the setup
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
            : _pairs || _size != 4 ? "its lanes are not of 4 bytes (a cluster is a vector of floats)"
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
