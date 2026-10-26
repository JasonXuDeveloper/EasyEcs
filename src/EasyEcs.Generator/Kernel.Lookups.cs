using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Reads of other entities through a <c>Lookup&lt;T&gt;</c> parameter (D131): <c>lookup[id]</c> (its <c>HasValue</c> and
/// <c>Value</c>, read directly or through a local), <c>lookup.Read(id)</c> and <c>lookup.Has(id)</c>, where the id is an
/// <c>EntityId</c> of the lanes (a component's field). Per vector, <c>Lookup&lt;T&gt;.Resolve</c> gathers the ids'
/// records (AVX2): the lanes of live entities that have T (a presence mask like an optional's, <c>__has{n}</c>), and the
/// table they live in; each field the method reads is gathered from that table's column (<c>LookupLanes.Gather</c>). A
/// vector whose entities live in several tables (or in a table holding T for some rows only) sends its rows to the
/// method, so every lane reads what the method reads, bit for bit. 4-byte lanes only (T's fields and the loop's).
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>The Lookup&lt;T&gt; parameters: the uniform holding each (<c>__p{i}</c>) and T.</summary>
    private readonly Dictionary<IParameterSymbol, (string Name, ITypeSymbol Element)> _lookupParams = new(SymbolEqualityComparer.Default);

    /// <summary>Each Lookup parameter's index: its job field <c>f{i}</c> says whether the pass prefetches (<see cref="Prefetch"/>).</summary>
    private readonly Dictionary<IParameterSymbol, int> _lookupFields = new(SymbolEqualityComparer.Default);

    /// <summary>The Lookup parameters the loop prefetches for (<see cref="Code.Prefetched"/>).</summary>
    private readonly HashSet<int> _prefetched = new();

    /// <summary>
    /// How many rows ahead of a vector the loop prefetches its chases' rows (their records twice as far): 16, the best
    /// distance of the screen (LookupChaseDistanceBenchmark, 2-4 vectors of 8 at 1M and 16M entities).
    /// </summary>
    private const int PrefetchDistance = 16;

    /// <summary>The lookups resolved so far: their number g by parameter and id lanes, when the lanes cannot change.</summary>
    private readonly Dictionary<string, int> _resolved = new();

    /// <summary>The locals holding a lookup's value (<c>var at = lookup[id]</c>): its number g.</summary>
    private readonly Dictionary<ILocalSymbol, int> _lookupLocals = new(SymbolEqualityComparer.Default);

    /// <summary>The fields gathered so far ("g f": the vector holding field f of lookup g).</summary>
    private readonly Dictionary<string, string> _gathered = new();

    /// <summary>Each lookup's T and its lane paths, by g.</summary>
    private readonly List<(ITypeSymbol Element, IFieldSymbol[][] Paths)> _lookups = new();

    /// <summary>The number of lookup g in the presence masks of <see cref="Guard"/> and <see cref="Implied"/>: past every parameter's.</summary>
    private static int Presence(int g) => 1000 + g;

    /// <summary>Optionals or lookups: values some lanes may lack, whose reads under a condition the conditions prove.</summary>
    private bool TracksPresence => _optionals.Count > 0 || _lookupParams.Count > 0;

    private static readonly Regex ComponentLaneName = new(@"^__c(\d+)_\d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The lookup <paramref name="op"/> reads: <c>lookup[id]</c>, <c>lookup.Read(id)</c>, <c>lookup.Has(id)</c> (resolved
    /// here when <paramref name="resolve"/>, else only one resolved before), or a local holding <c>lookup[id]</c>.
    /// </summary>
    private int? LookupOf(IOperation op, bool resolve = true)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        switch (op)
        {
            case ILocalReferenceOperation { Local: var local } when _lookupLocals.TryGetValue(local, out var g):
                return g;
            case IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1, Instance: IParameterReferenceOperation { Parameter: var p } } x
                when _lookupParams.ContainsKey(p):
                return Resolve(p, x.Arguments[0].Value, resolve);
            case IInvocationOperation { TargetMethod.Name: "Read" or "Has", Arguments.Length: 1, Instance: IParameterReferenceOperation { Parameter: var p } } call
                when _lookupParams.ContainsKey(p):
                return Resolve(p, call.Arguments[0].Value, resolve);
            default:
                return null;
        }
    }

    /// <summary>
    /// The lookup of <paramref name="p"/> at the ids <paramref name="id"/> holds: resolved once per vector (a bail when the
    /// entities live in several tables), and reused while the id's lanes are a component's the method does not write.
    /// </summary>
    private int? Resolve(IParameterSymbol p, IOperation id, bool resolve)
    {
        var (lookup, element) = _lookupParams[p];
        if (_flat >= 0 || _half >= 0) throw new Unsupported("a statement mixes fields"); // Flat and pairs refuse it; never reported
        if (_size != 4 || _pairs || _narrow)
            throw new Unsupported($"it reads Lookup<{element.Name}> in a loop of {LaneTypes()}: a vector loop gathers other entities' fields into lanes of 4 bytes");
        var paths = StructPaths(element)
            ?? throw new Unsupported($"it reads Lookup<{element.Name}>, whose fields are not all lanes of 4 bytes (float, int, uint): a vector loop gathers only those");
        var idPaths = StructPaths(id.Type);
        if (id.Type is not { Name: "EntityId", ContainingNamespace.Name: "EasyEcs" } || idPaths == null)
            throw new Unsupported($"it reads Lookup<{element.Name}> at '{Text(id)}', which is not an EntityId");
        var lanes = StructOf(id);
        string version = lanes[System.Array.FindIndex(idPaths, f => f[f.Length - 1].Name == "Version")]!;
        string index = lanes[System.Array.FindIndex(idPaths, f => f[f.Length - 1].Name == "Index")]!;
        string key = $"{lookup} {version} {index}";
        if (_resolved.TryGetValue(key, out int g)) return g;
        if (!resolve) return null;
        g = _lookups.Count;
        _lookups.Add((element, paths));
        int n = Presence(g);
        _implied[$"__has{n}"] = [n];
        Prefetch(p, lookup, index);
        // Entities of several tables: the method runs this vector's rows, and those of the vectors its backoff skips.
        _body.Append($"if (!{lookup}.Resolve({version}, {index}, ref __b{g}, out var __has{n}, out var __gr{g}, out var __gc{g}))\n{{\n");
        _body.Append($"    nint __end = global::System.Math.Min(i + (1 + __b{g}) * {Vec(_mask)}.Count, rows);\n    {RunRows}\n}}\n");
        if (Unchanging(version) && Unchanging(index)) _resolved[key] = g;
        return g;
    }

    /// <summary>
    /// Before a vector resolves its lookup, the chases of the vectors after it (group prefetching, AMAC): the records of
    /// the rows 2 <see cref="PrefetchDistance"/> past it and the rows of T of the rows <see cref="PrefetchDistance"/> past
    /// it (<c>Lookup&lt;T&gt;.PrefetchAhead</c>), when the pass is past the last-level cache (the job's <c>f{i}</c>, read
    /// once per pass: <c>Lookup&lt;T&gt;.Prefetches</c>). Only for ids of a component the method takes by <c>in</c>
    /// (rows ahead hold the ids their own vectors will read) and not wide: <paramref name="index"/> names its column and lane.
    /// </summary>
    private void Prefetch(IParameterSymbol p, string lookup, string index)
    {
        if (!Unchanging(index) || ComponentLaneName.Match(index) is not { Success: true } m) return;
        int column = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int lane = int.Parse(index.Substring(index.LastIndexOf('_') + 1), System.Globalization.CultureInfo.InvariantCulture);
        if (_components.FirstOrDefault(c => c.Value.Index == column) is not { Key: { } component, Value.Fields: { } fields } || _wideOf.ContainsKey(component)) return;
        int field = _lookupFields[p];
        _prefetched.Add(field);
        _body.Append($"if (f{field}) {lookup}.PrefetchAhead(ref e{column}, {fields.Length}, {lane}, i, rows, {PrefetchDistance}, {Vec(_mask)}.Count);\n");
    }

    /// <summary>A lane of a component taken by <c>in</c>: the method cannot write it, so it is the same wherever it is read.</summary>
    private bool Unchanging(string lane) =>
        ComponentLaneName.Match(lane) is { Success: true } m
        && !_components.Values.Any(c => c.Ref && c.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) == m.Groups[1].Value);

    private string LaneTypes() => _size == 4 ? "lanes of two sizes" : $"lanes of {_size} bytes";

    /// <summary>Field <paramref name="f"/> of lookup <paramref name="g"/>, gathered once per vector.</summary>
    private string Gathered(int g, int f)
    {
        string key = $"{g} {f}";
        if (_gathered.TryGetValue(key, out var name)) return name;
        name = $"__gv{g}_{f}";
        var paths = _lookups[g].Paths;
        string type = Elem(paths[f][paths[f].Length - 1].Type);
        // The lanes lie in declaration order, 4 bytes each (Paths, as a component's): field f at byte 4 f.
        _body.Append($"var {name} = global::EasyEcs.LookupLanes.Gather<{type}>(__gc{g}, __gr{g}, __has{Presence(g)}, {4 * f});\n");
        _gathered[key] = name;
        return name;
    }

    /// <summary>
    /// A lane of another entity's T: <c>x.Value.F</c> of a lookup's value x (a read every active lane must have, as
    /// Value throws otherwise) or <c>lookup.Read(id).F</c> (which throws for a lane without it), or null.
    /// </summary>
    private string? LookupLane(IOperation op)
    {
        if (_lookupParams.Count == 0 || Chain(op) is not ({ } root, { Length: > 0 } members)) return null;
        int? g;
        if (root is IInvocationOperation { TargetMethod.Name: "Read" }) g = LookupOf(root);
        else if (members[0].Name == "Value" && members.Length > 1 && LookupOf(root) is { } held)
        {
            g = held;
            members = members.Skip(1).ToArray();
        }
        else return null;
        if (g is not { } found) return null;
        int f = Leaf(_lookups[found].Paths, members);
        if (f < 0) return null; // a struct of lanes: StructOf
        Guard(Presence(found));
        return Gathered(found, f);
    }

    /// <summary>A lookup's whole value (<c>x.Value</c>, <c>lookup.Read(id)</c>) as its lanes, read where every active lane has it, or null.</summary>
    private string?[]? LookupValue(IOperation op)
    {
        if (_lookupParams.Count == 0) return null;
        int? g = op switch
        {
            IPropertyReferenceOperation { Property.Name: "Value", Instance: { } x } => LookupOf(x),
            IInvocationOperation { TargetMethod.Name: "Read" } => LookupOf(op),
            _ => null,
        };
        if (g is not { } found) return null;
        Guard(Presence(found));
        return Enumerable.Range(0, _lookups[found].Paths.Length).Select(f => (string?)Gathered(found, f)).ToArray();
    }

    /// <summary>The presence mask <paramref name="op"/> is: <c>x.HasValue</c> of a lookup's value, or <c>lookup.Has(id)</c>; or null.</summary>
    private string? LookupHas(IOperation op, bool resolve = true) => _lookupParams.Count == 0 ? null : op switch
    {
        IPropertyReferenceOperation { Property.Name: "HasValue", Instance: { } x } when LookupOf(x, resolve) is { } g => $"__has{Presence(g)}",
        IInvocationOperation { TargetMethod.Name: "Has" } when LookupOf(op, resolve) is { } g => $"__has{Presence(g)}",
        _ => null,
    };

    /// <summary>A local declared as <c>lookup[id]</c>: it names that lookup's value. False for any other local.</summary>
    private bool DeclareLookup(ILocalSymbol local, IOperation init)
    {
        var value = init;
        while (value is IConversionOperation { Conversion.IsIdentity: true } c) value = c.Operand;
        if (_lookupParams.Count == 0 || value is not IPropertyReferenceOperation { Property.IsIndexer: true } || LookupOf(value) is not { } g) return false;
        _lookupLocals[local] = g;
        return true;
    }
}
