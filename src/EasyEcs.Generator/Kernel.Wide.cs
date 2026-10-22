using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Wide components: a row of up to 16 four-byte words (more fields than a vector loop splits, or fields of other types
/// beside the 4-byte lanes the method uses). On AVX-512 the loop loads the 16 rows of a vector as K whole vectors
/// (K words per row), gathers each field the method uses into one vector with a pruned tree of two-source permutes
/// (<c>vpermt2ps</c>: neighbouring vectors merged while their used words fit one vector, then each field picked out of
/// the merged ones), and writes back only the vectors holding written words: one permute per field in each, then a whole
/// store (the other words as loaded). Lanes run the scalar code's operations, so results are the method's bit for bit.
/// Only where the permutes cost less than the method's operations gain (<see cref="Gate"/>), and only on AVX-512 (a
/// 256-bit two-source permute moves half the words for the same cost): elsewhere, and when the runtime lays the
/// component out otherwise than <see cref="Layout"/> computed (a check folded to a constant), every row runs the method.
/// </summary>
internal sealed partial class Kernel
{
    private const int Words = 16; // lanes of a Vector512 of 4-byte words

    /// <summary>
    /// A wide component's layout: <paramref name="K"/> words per row, the paths to the lanes the method uses (in word
    /// order), their words and the members the code names for each (a field, or the property it backs).
    /// </summary>
    private sealed record WideLayout(int K, IFieldSymbol[][] Paths, int[] Words, ISymbol[][] Access, int Size, string Type, int Fields)
    {
        /// <summary>Why the loop does not split the component (Lanes' reason): the start of the gate's.</summary>
        public string Reason { get; init; } = "";
    }

    private readonly Dictionary<IParameterSymbol, WideLayout> _wideOf = new(SymbolEqualityComparer.Default);
    private readonly StringBuilder _statics = new();
    private int _tables, _permutes;

    /// <summary>
    /// The lanes a method uses of each parameter: the members it reads or writes (<c>body.X</c>, <c>body.P.Y</c>), each a
    /// field or the auto-property backed by one, ending at a lane value. A parameter used in any other way (whole, or a
    /// part of it that is not a lane) is in <paramref name="whole"/>.
    /// </summary>
    private static Dictionary<IParameterSymbol, List<(IFieldSymbol[] Path, ISymbol[] Access)>> Touched(IOperation body, HashSet<IParameterSymbol> whole)
    {
        var touched = new Dictionary<IParameterSymbol, List<(IFieldSymbol[], ISymbol[])>>(SymbolEqualityComparer.Default);
        foreach (var op in body.DescendantsAndSelf())
        {
            if (op is not IParameterReferenceOperation { Parameter: var p }) continue;
            var path = new List<IFieldSymbol>();
            var access = new List<ISymbol>();
            IOperation at = op;
            bool fields = true;
            while (at.Parent is IMemberReferenceOperation parent && parent.Instance == at)
            {
                var field = parent switch
                {
                    IFieldReferenceOperation { Field: var f } => f,
                    IPropertyReferenceOperation { Property: var prop } => prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
                        .FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop)),
                    _ => null,
                };
                if (field == null || field.IsStatic) { fields = false; break; }
                path.Add(field);
                access.Add(parent.Member);
                at = parent;
            }
            if (!fields || path.Count == 0 || !IsLaneType(at.Type))
            {
                whole.Add(p);
                continue;
            }
            if (!touched.TryGetValue(p, out var list)) touched[p] = list = new List<(IFieldSymbol[], ISymbol[])>();
            if (!list.Any(l => l.Item1.SequenceEqual(path, SymbolEqualityComparer.Default))) list.Add((path.ToArray(), access.ToArray()));
        }
        return touched;
    }

    /// <summary>
    /// The wide layout of a component the vector loop does not split, or null: a sequential struct of plain values (no
    /// [StructLayout], one declaration, not generic) of at most 16 words, the lanes the method uses 4-byte values on word
    /// boundaries that it names through members it can set (the runtime check sets them).
    /// </summary>
    private static WideLayout? WideComponent(IParameterSymbol p, List<(IFieldSymbol[] Path, ISymbol[] Access)>? used, HashSet<IParameterSymbol> whole, Compilation compilation)
    {
        if (used == null || whole.Contains(p) || p.Type is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } type
            || type.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute") || type.DeclaringSyntaxReferences.Length > 1
            || Layout(type, 0, out int size, out _) is not { } leaves || size % 4 != 0 || size / 4 > Words)
            return null;
        var lanes = new List<(IFieldSymbol[] Path, ISymbol[] Access, int Word)>();
        foreach (var (path, access) in used)
        {
            if (!leaves.TryGetValue(Key(path), out int offset) || offset % 4 != 0 || SizeOf(path[path.Length - 1].Type) != 4
                || access.Any(m => !Settable(m, compilation)))
                return null;
            lanes.Add((path, access, offset / 4));
        }
        lanes.Sort((a, b) => a.Word.CompareTo(b.Word));
        return new WideLayout(size / 4, lanes.Select(l => l.Path).ToArray(), lanes.Select(l => l.Word).ToArray(), lanes.Select(l => l.Access).ToArray(),
            size, type.ToDisplayString(Symbols.Fqn), leaves.Count);
    }

    private static bool Settable(ISymbol member, Compilation compilation) => compilation.IsSymbolAccessibleWithin(member, compilation.Assembly) && member switch
    {
        IFieldSymbol f => !f.IsReadOnly,
        IPropertySymbol { SetMethod: { } set } => compilation.IsSymbolAccessibleWithin(set, compilation.Assembly),
        _ => false,
    };

    private static string Key(IFieldSymbol[] path) => string.Join(".", path.Select(f => f.Name));

    /// <summary>
    /// The offsets of the primitive fields of a struct, by path, as a sequential layout places them (each at a multiple
    /// of its alignment, the size a multiple of the largest); null for a field it does not lay out (a reference, a
    /// pointer, a decimal, a fixed buffer, an explicit layout). The runtime checks the words used (<see cref="LayoutCheck"/>).
    /// </summary>
    private static Dictionary<string, int>? Layout(INamedTypeSymbol type, int depth, out int size, out int align)
    {
        size = 0;
        align = 1;
        if (depth > 4) return null;
        var leaves = new Dictionary<string, int>();
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
        {
            if (field.IsFixedSizeBuffer) return null;
            var t = field.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying } ? underlying : field.Type;
            int s, a;
            Dictionary<string, int>? inner = null;
            switch (t.SpecialType)
            {
                case SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte: (s, a) = (1, 1); break;
                case SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16: (s, a) = (2, 2); break;
                case SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single: (s, a) = (4, 4); break;
                case SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double: (s, a) = (8, 8); break;
                case SpecialType.None when t is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } nested && SequentialLayout(nested):
                    inner = Layout(nested, depth + 1, out s, out a);
                    if (inner == null) return null;
                    break;
                default:
                    return null;
            }
            size = (size + a - 1) / a * a;
            if (inner == null) leaves[field.Name] = size;
            else foreach (var e in inner) leaves[field.Name + "." + e.Key] = size + e.Value;
            size += s;
            align = System.Math.Max(align, a);
        }
        if (size == 0) return null;
        size = (size + align - 1) / align * align;
        return leaves;
    }

    /// <summary>
    /// The job's check that the runtime lays the component out as <see cref="Layout"/> did: its size, and each lane the
    /// method uses, set to a value of its own, found in its word. A static readonly bool: a constant to the optimizing
    /// JIT, so the loop it guards costs nothing either way.
    /// </summary>
    private void LayoutCheck(int index, WideLayout wide)
    {
        const string unsafeType = "global::System.Runtime.CompilerServices.Unsafe";
        string Set(string instance, ISymbol[] access, int at, string value) => at == access.Length - 1
            ? $"{access[at].Name} = {value}"
            : $"{access[at].Name} = {instance}.{access[at].Name} with {{ {Set($"{instance}.{access[at].Name}", access, at + 1, value)} }}";
        _statics.Append($"private static readonly bool __layout{index} = __Layout{index}();\n\n");
        _statics.Append($"private static bool __Layout{index}()\n{{\n    if ({unsafeType}.SizeOf<{wide.Type}>() != {wide.Size}) return false;\n");
        _statics.Append($"    var t = default({wide.Type});\n");
        var words = new List<string>();
        for (int f = 0; f < wide.Paths.Length; f++)
        {
            var type = wide.Paths[f][wide.Paths[f].Length - 1].Type;
            string value = type.SpecialType switch
            {
                SpecialType.System_Single => $"global::System.BitConverter.Int32BitsToSingle({f + 1})",
                SpecialType.System_UInt32 => $"{f + 1}u",
                _ => $"{f + 1}",
            };
            _statics.Append($"    t = t with {{ {Set("t", wide.Access[f], 0, value)} }};\n");
            words.Add($"{unsafeType}.Add(ref w, {wide.Words[f]}) == {f + 1}");
        }
        _statics.Append($"    ref int w = ref {unsafeType}.As<{wide.Type}, int>(ref t);\n    return {string.Join(" && ", words)};\n}}\n\n");
    }

    private const string Permute2 = "global::System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2";
    private const string Permute1 = "global::System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32";

    /// <summary>A permute's index vector: a static of the job, built once.</summary>
    private string Table(int[] values)
    {
        string name = $"__x{_tables++}";
        _statics.Append($"private static readonly global::System.Runtime.Intrinsics.Vector512<int> {name} = global::System.Runtime.Intrinsics.Vector512.Create({string.Join(", ", values)});\n");
        _permutes++;
        return name;
    }

    /// <summary>A vector of the tree: its expression and the lane where each word it holds (lane of the kernel, row) sits.</summary>
    private sealed record Node(string Expr, Dictionary<(int Lane, int Row), int> Slots);

    /// <summary>
    /// The loads of a wide component's 16 rows and the vectors of the lanes the method uses (<c>__c{index}_{f}</c>):
    /// neighbouring vectors merged while their used words fit one, for the number of levels that makes the fewest
    /// permutes, then each lane picked out of the vectors holding it.
    /// </summary>
    private void WideLoads(int index, WideLayout wide, string column, StringBuilder loads)
    {
        LayoutCheck(index, wide);
        int k = wide.K;
        var inputs = new List<Node>();
        for (int j = 0; j < k; j++)
        {
            var slots = new Dictionary<(int, int), int>();
            for (int l = 0; l < Words; l++)
            {
                int e = Words * j + l, lane = System.Array.IndexOf(wide.Words, e % k);
                if (lane >= 0) slots[(lane, e / k)] = l;
            }
            if (slots.Count == 0) continue;
            inputs.Add(new Node($"__v{index}_{j}", slots));
            loads.Append($"var __v{index}_{j} = {V}.LoadUnsafe(ref e{index}, (nuint)i * {k} + {Words * j});\n");
        }
        int best = 0, cost = int.MaxValue;
        for (int levels = 0; levels < 6; levels++)
        {
            var planned = Merge(inputs, levels, null, out int merges);
            int total = merges + Enumerable.Range(0, wide.Paths.Length).Sum(f => Extract(planned, f, null, null));
            if (total < cost) (best, cost) = (levels, total);
        }
        var nodes = Merge(inputs, best, loads, out _);
        for (int f = 0; f < wide.Paths.Length; f++)
        {
            string value = Temp("g");
            Extract(nodes, f, value, loads);
            loads.Append($"var __c{index}_{f} = {As(column, Elem(wide.Paths[f][wide.Paths[f].Length - 1].Type), value)};\n");
        }
    }

    /// <summary>The vectors after up to <paramref name="levels"/> levels of pairwise merges, written to <paramref name="code"/> (planned when null).</summary>
    private List<Node> Merge(List<Node> inputs, int levels, StringBuilder? code, out int merges)
    {
        merges = 0;
        var nodes = inputs;
        for (int level = 0; level < levels; level++)
        {
            var next = new List<Node>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i + 1 == nodes.Count || nodes[i].Slots.Count + nodes[i + 1].Slots.Count > Words)
                {
                    next.Add(nodes[i]);
                    continue;
                }
                var (a, b) = (nodes[i++], nodes[i]);
                var slots = new Dictionary<(int, int), int>();
                var values = new int[Words];
                int lane = 0;
                foreach (var e in a.Slots) { slots[e.Key] = lane; values[lane++] = e.Value; }
                foreach (var e in b.Slots) { slots[e.Key] = lane; values[lane++] = Words + e.Value; }
                string name = code == null ? "" : Temp("n");
                code?.Append($"var {name} = {Permute2}({a.Expr}, {Table(values)}, {b.Expr});\n");
                next.Add(new Node(name, slots));
                merges++;
            }
            if (next.Count == nodes.Count) break;
            nodes = next;
        }
        return nodes;
    }

    /// <summary>
    /// The vector of lane <paramref name="f"/> (row r in lane r) from the vectors holding it, into <paramref name="into"/>:
    /// its permutes, written to <paramref name="code"/> (counted when null).
    /// </summary>
    private int Extract(List<Node> nodes, int f, string? into, StringBuilder? code)
    {
        var sources = nodes.Where(n => n.Slots.Keys.Any(e => e.Lane == f)).ToList();
        int Slot(Node n, int r) => n.Slots.TryGetValue((f, r), out int l) ? l : -1;
        if (sources.Count == 1 && Enumerable.Range(0, Words).All(r => Slot(sources[0], r) == r))
        {
            code?.Append($"var {into} = {sources[0].Expr};\n");
            return 0;
        }
        if (sources.Count == 1)
        {
            code?.Append($"var {into} = {Permute1}({sources[0].Expr}, {Table(Enumerable.Range(0, Words).Select(r => System.Math.Max(Slot(sources[0], r), 0)).ToArray())});\n");
            return 1;
        }
        if (code != null)
        {
            var (s0, s1) = (sources[0], sources[1]);
            var values = Enumerable.Range(0, Words).Select(r => Slot(s0, r) >= 0 ? Slot(s0, r) : Slot(s1, r) >= 0 ? Words + Slot(s1, r) : 0).ToArray();
            code.Append($"var {into} = {Permute2}({s0.Expr}, {Table(values)}, {s1.Expr});\n");
            foreach (var s in sources.Skip(2))
            {
                values = Enumerable.Range(0, Words).Select(r => Slot(s, r) >= 0 ? Words + Slot(s, r) : r).ToArray();
                code.Append($"{into} = {Permute2}({into}, {Table(values)}, {s.Expr});\n");
            }
        }
        return sources.Count - 1;
    }

    /// <summary>
    /// The write-back of a wide component: each loaded vector holding words of lanes the body writes takes them with one
    /// permute, then is stored whole, its other words as loaded. Its source is a lane's vector, or a pack of the words
    /// of several lanes that consecutive vectors take (a permute per lane past the first builds it): whichever makes
    /// fewer permutes.
    /// </summary>
    private void WideStores(int index, WideLayout wide, string column, StringBuilder stores)
    {
        int k = wide.K;
        string body = _body.ToString();
        var written = new HashSet<int>(Enumerable.Range(0, wide.Paths.Length).Where(f => body.Contains($"__c{index}_{f} = ")));
        // The vectors to store, and the words of written lanes each holds: (lane, row) at its lane l.
        var need = new List<(int J, List<((int Lane, int Row) Word, int L)> Words)>();
        for (int j = 0; j < k; j++)
        {
            var words = new List<((int, int), int)>();
            for (int l = 0; l < Words; l++)
            {
                int e = Words * j + l, f = System.Array.IndexOf(wide.Words, e % k);
                if (f >= 0 && written.Contains(f)) words.Add(((f, e / k), l));
            }
            if (words.Count > 0) need.Add((j, words));
        }
        var groups = new List<(List<(int J, List<((int Lane, int Row) Word, int L)> Words)> Vectors, List<(int Lane, int Row)> Union)>();
        foreach (var vector in need)
        {
            var words = vector.Words.Select(w => w.Word).ToList();
            if (groups.Count == 0 || groups[groups.Count - 1].Union.Union(words).Count() > Words) groups.Add((new(), new()));
            groups[groups.Count - 1].Vectors.Add(vector);
            foreach (var w in words) if (!groups[groups.Count - 1].Union.Contains(w)) groups[groups.Count - 1].Union.Add(w);
        }
        int simple = need.Sum(v => v.Words.Select(w => w.Word.Lane).Distinct().Count());
        int packed = groups.Sum(g => System.Math.Max(0, g.Union.Select(w => w.Lane).Distinct().Count() - 1)) + need.Count;
        string Lane(int f) => As(Elem(wide.Paths[f][wide.Paths[f].Length - 1].Type), column, $"__c{index}_{f}");
        string Store(int j) => $"{V}.StoreUnsafe(__v{index}_{j}, ref e{index}, (nuint)i * {k} + {Words * j});\n";
        if (simple <= packed)
        {
            foreach (var (j, words) in need)
            {
                foreach (int f in words.Select(w => w.Word.Lane).Distinct().OrderBy(f => f))
                {
                    var values = Enumerable.Range(0, Words).ToArray();
                    foreach (var (word, l) in words) if (word.Lane == f) values[l] = Words + word.Row;
                    stores.Append($"__v{index}_{j} = {Permute2}(__v{index}_{j}, {Table(values)}, {Lane(f)});\n");
                }
                stores.Append(Store(j));
            }
            return;
        }
        foreach (var (vectors, union) in groups)
        {
            var lanes = union.Select(w => w.Lane).Distinct().OrderBy(f => f).ToArray();
            var slot = new Dictionary<(int, int), int>();
            string source;
            if (lanes.Length == 1)
            {
                source = Lane(lanes[0]);
                foreach (var w in union) slot[w] = w.Row;
            }
            else
            {
                var order = union.OrderBy(w => w).ToList();
                for (int q = 0; q < order.Count; q++) slot[order[q]] = q;
                source = Temp("w");
                var values = new int[Words];
                for (int q = 0; q < order.Count; q++)
                    values[q] = order[q].Lane == lanes[0] ? order[q].Row : order[q].Lane == lanes[1] ? Words + order[q].Row : 0;
                stores.Append($"var {source} = {Permute2}({Lane(lanes[0])}, {Table(values)}, {Lane(lanes[1])});\n");
                foreach (int c in lanes.Skip(2))
                {
                    values = new int[Words];
                    for (int q = 0; q < order.Count; q++) values[q] = order[q].Lane == c ? Words + order[q].Row : q;
                    stores.Append($"{source} = {Permute2}({source}, {Table(values)}, {Lane(c)});\n");
                }
            }
            foreach (var (j, words) in vectors)
            {
                var values = Enumerable.Range(0, Words).ToArray();
                foreach (var (word, l) in words) values[l] = Words + slot[word];
                stores.Append($"__v{index}_{j} = {Permute2}(__v{index}_{j}, {Table(values)}, {source});\n");
                stores.Append(Store(j));
            }
        }
    }

    /// <summary>
    /// Whether the permutes pay: the vector operations of the method's body against the permutes that move a wide
    /// component's words in and out, per 16 rows. Measured on the prototype (BenchmarkDotNet, 1k to 100k rows): 3 of 12
    /// fields under 11 operations ran in 0.48 to 0.86 of the scalar time, 4 of 8 under 4 in 0.67 to 0.97; 3 of 12 under 3,
    /// 4 of 16 under 4 and 2 of 8 under 2 in 0.9 to 1.1. The line between them: fewer permutes than 8 per operation.
    /// </summary>
    private void Gate()
    {
        int ops = 0;
        string body = _body.ToString();
        foreach (var token in new[] { " + ", " - ", " * ", " / ", " & ", " | ", " ^ ", ".Min(", ".Max(", ".ConditionalSelect(", ".Sqrt(", ".SquareRoot(",
                     ".Abs(", ".Floor(", ".Ceiling(", ".Round(", ".Truncate(", ".FusedMultiplyAdd(", ".Sin(", ".Cos(", ".Exp(", ".Log(", ".Convert",
                     ".Equals(", ".LessThan", ".GreaterThan" })
            for (int at = body.IndexOf(token, System.StringComparison.Ordinal); at >= 0; at = body.IndexOf(token, at + 1, System.StringComparison.Ordinal))
                ops++;
        if (_permutes < 8 * ops) return;
        var (p, wide) = (_wideOf.First().Key, _wideOf.First().Value);
        throw new Unsupported($"{wide.Reason}, and gathering the {wide.Paths.Length} {(wide.Paths.Length == 1 ? "field" : "fields")} it uses takes "
            + $"{_permutes} permutes per 16 rows, {(ops == 0 ? "for no vector operation" : ops == 1 ? "more than its 1 vector operation gains" : $"more than its {ops} vector operations gain")}{Split(p.Type, wide.Paths)}");
    }

    /// <summary>
    /// The advice that ends a reason when the method uses few of a component's fields: "split them into a component of
    /// their own" (at most half its fields, all lanes of one size, at most <see cref="MaxFields"/>: a component that
    /// gets a vector loop), else "".
    /// </summary>
    private static string Split(ITypeSymbol type, IFieldSymbol[][] used)
    {
        // Only for a component too wide or too mixed for the loop: a plain sequential struct of values.
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false, DeclaringSyntaxReferences.Length: <= 1 } named
            || named.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute"))
            return "";
        int all = Layout(named, 0, out _, out _)?.Count ?? 0;
        if (used.Length == 0 || used.Length > MaxFields || used.Length * 2 > all
            || used.Any(u => !IsLaneType(u[u.Length - 1].Type) || SizeOf(u[u.Length - 1].Type) != SizeOf(used[0][used[0].Length - 1].Type)))
            return "";
        string names = string.Join(", ", used.Select(u => string.Join(".", u.Select(f => f.AssociatedSymbol?.Name ?? f.Name))));
        return used.Length == 1
            ? $"; it uses 1 of {type.Name}'s {all} fields ({names}): move it into a component of its own (a component of one field gets a vector loop)"
            : $"; it uses {used.Length} of {type.Name}'s {all} fields ({names}): split them into a component of their own (a component of {used.Length} fields gets a vector loop)";
    }
}
