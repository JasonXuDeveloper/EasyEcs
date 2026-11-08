using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Wide components: a row of up to 64 bytes (more fields than a vector loop splits, or fields of other types beside the
/// lanes the method uses), whose used lanes are all 4-byte or all 8-byte words (the row's unit). Three forms, each
/// behind its own cost gate (<see cref="Gate"/>), its lines marked for its width (<see cref="Mark512"/>,
/// <see cref="Mark256"/>, <see cref="Mark128"/>):
/// <list type="bullet">
/// <item>AVX-512: the loop loads the rows of a vector (16 of 4-byte words, 8 of 8-byte ones) as K whole vectors (K words
/// per row), gathers each field the method uses into one vector with a pruned tree of two-source permutes
/// (<c>vpermt2ps</c> / <c>vpermt2pd</c>: neighboring vectors merged while their used words fit one vector, then each
/// field picked out of the merged ones), and writes back only the vectors holding written words: one permute per field
/// in each, then a whole store (the other words as loaded).</item>
/// <item>AVX2 (Vector&lt;T&gt; of 256 bits): the same tree over 8 or 4 rows, each two-source permute two single-source
/// <c>vpermd</c> and a blend (<c>Interleaved.Permute256</c>, <c>Merge256</c>), so it costs three times AVX-512's per
/// vector of half the rows: it runs only for a method whose math is heavy (a square root, a division, a transcendental),
/// where the lanes it computes at once outweigh the moves (the widelanes prototype: 0.60-0.76 of the rows' time with
/// heavy math, 1.00-1.13 with light).</item>
/// <item>ARM64 (Vector&lt;T&gt; of 128 bits): the 4 (or 2) rows of a vector are K vectors; the words of a field lie in at
/// most 4 of them, so one NEON TBL over those (<c>Interleaved.Pick</c>) gathers it, and one TBX per written vector
/// (<c>Interleaved.PickInto</c>, at most 4 fields hold its words) puts the written words back before a whole store.</item>
/// </list>
/// Lanes run the scalar code's operations, so results are the method's bit for bit. Where no form runs, and when the
/// runtime lays the component out otherwise than <see cref="Layout"/> computed (a check folded to a constant), every row
/// runs the method.
/// </summary>
internal sealed partial class Kernel
{
    private const int RowBytes = 64; // the widest row a wide component may have: 16 words of 4 bytes, 8 of 8

    /// <summary>
    /// The token that starts a line of the AVX-512 form of a wide component's loads and stores: the Vector512 loop keeps
    /// it, the Vector&lt;T&gt; loops drop it (SystemGenerator.VectorLoops).
    /// </summary>
    internal const string Mark512 = "\u0001W512:";

    /// <summary>The token that starts a line of the AVX2 form: the Vector&lt;T&gt; loop of 256 bits keeps it, the others drop it.</summary>
    internal const string Mark256 = "\u0001W256:";

    /// <summary>The token that starts a line of the 128-bit (ARM64) form: the Vector&lt;T&gt; loop keeps it, the others drop it.</summary>
    internal const string Mark128 = "\u0001W128:";

    /// <summary>
    /// A wide component's layout: <paramref name="K"/> words of <paramref name="Unit"/> bytes per row, the paths to the
    /// lanes the method uses (in word order), their words and the members the code names for each (a field, or the
    /// property it backs).
    /// </summary>
    private sealed record WideLayout(int K, IFieldSymbol[][] Paths, int[] Words, ISymbol[][] Access, int Size, string Type, int Fields, int Unit)
    {
        /// <summary>Why the loop does not split the component (Lanes' reason): the start of the gate's.</summary>
        public string Reason { get; init; } = "";
    }

    private readonly Dictionary<IParameterSymbol, WideLayout> _wideOf = new(SymbolEqualityComparer.Default);
    private readonly StringBuilder _statics = new();
    private readonly StringBuilder _tables128 = new();
    private readonly Dictionary<string, string> _tableNames128 = new();
    // The AVX-512 form's permutes and the AVX2 form's moves (vpermd and blends) per vector of rows.
    private int _permutes, _moves256;
    // The 128-bit form's cost per vector of rows: the registers its table lookups read, its whole-vector loads and stores.
    private int _registers, _moves128, _usedLanes, _writtenLanes;
    private bool _wide512, _wide256, _wide128; // the forms that pay (Gate)

    /// <summary>The bytes of the words of the wide components (all of one unit: the loop's lanes).</summary>
    private int WideUnit => _wideOf.Values.First().Unit;

    /// <summary>The job's statics: the layout checks, and the 128-bit form's lookup tables when it pays.</summary>
    private string WideStatics() => _statics.ToString() + (_wide128 ? _tables128.ToString() : "");

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
                    IPropertyReferenceOperation { Property: var prop } => Symbols.BackingField(prop),
                    _ => null,
                };
                if (field == null || field.IsStatic) { fields = false; break; }
                path.Add(field);
                access.Add(parent.Member);
                at = parent;
            }
            if (!fields || path.Count == 0 || !(IsLaneType(at.Type) || IsBool(at.Type))) // a bool field is a use the split advice can move out
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
    /// [StructLayout], one declaration, not generic) of at most 64 bytes, the lanes the method uses all 4-byte or all
    /// 8-byte values on boundaries of their size that it names through members it can set (the runtime check sets them).
    /// </summary>
    private static WideLayout? WideComponent(IParameterSymbol p, List<(IFieldSymbol[] Path, ISymbol[] Access)>? used, HashSet<IParameterSymbol> whole, Compilation compilation)
    {
        if (used == null || whole.Contains(p) || p.Type is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } type
            || type.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute") || Symbols.FieldsInSeveralParts(type)
            || Layout(type, 0, out int size, out _) is not { } leaves || size > RowBytes)
            return null;
        int unit = SizeOf(used[0].Path[used[0].Path.Length - 1].Type);
        if (unit is not (4 or 8) || size % unit != 0) return null;
        var lanes = new List<(IFieldSymbol[] Path, ISymbol[] Access, int Word)>();
        foreach (var (path, access) in used)
        {
            if (!leaves.TryGetValue(Key(path), out int offset) || offset % unit != 0 || SizeOf(path[path.Length - 1].Type) != unit
                || access.Any(m => !Settable(m, compilation)))
                return null;
            lanes.Add((path, access, offset / unit));
        }
        lanes.Sort((a, b) => a.Word.CompareTo(b.Word));
        return new WideLayout(size / unit, lanes.Select(l => l.Path).ToArray(), lanes.Select(l => l.Word).ToArray(), lanes.Select(l => l.Access).ToArray(),
            size, type.ToDisplayString(Symbols.Fqn), leaves.Count, unit);
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
            if (field.IsFixedSizeBuffer || IsInlineArray(field.Type)) return null; // not one value of its declared field
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
        string word = wide.Unit == 8 ? "long" : "int";
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
                SpecialType.System_Double => $"global::System.BitConverter.Int64BitsToDouble({f + 1})",
                SpecialType.System_UInt32 => $"{f + 1}u",
                SpecialType.System_Int64 => $"{f + 1}L",
                SpecialType.System_UInt64 => $"{f + 1}ul",
                _ => $"{f + 1}",
            };
            _statics.Append($"    t = t with {{ {Set("t", wide.Access[f], 0, value)} }};\n");
            words.Add($"{unsafeType}.Add(ref w, {wide.Words[f]}) == {f + 1}");
        }
        _statics.Append($"    ref {word} w = ref {unsafeType}.As<{wide.Type}, {word}>(ref t);\n    return {string.Join(" && ", words)};\n}}\n\n");
    }

    /// <summary>
    /// A form gathering words by permutes (<see cref="Avx512"/>, <see cref="Avx2"/>): its words per vector, the mark of
    /// its lines, and how it writes a permute of one source or of two (the values: word l of the result is word
    /// <c>v[l]</c> of the first source, or word <c>v[l] − W</c> of the second), counting each one's cost.
    /// </summary>
    private abstract class PermuteForm(Kernel kernel, int unit, int vectorBytes, string mark)
    {
        protected readonly Kernel Kernel = kernel;
        protected readonly int Unit = unit;
        public readonly int W = vectorBytes / unit;
        public readonly string Mark = mark;

        /// <summary>The cost of a permute of one source and of two, in the form's own unit, for the plan of the tree.</summary>
        public abstract int One { get; }

        public abstract int Two { get; }

        /// <summary>A permute of <paramref name="a"/> (and <paramref name="b"/>, when a value is W or more), its cost counted.</summary>
        public abstract string Permute(string a, int[] values, string? b);
    }

    /// <summary>AVX-512: <c>vpermt2ps</c> / <c>vpermt2pd</c> (two sources) and <c>vpermps</c> / <c>vpermpd</c> (one), as literals in place.</summary>
    private sealed class Avx512(Kernel kernel, int unit) : PermuteForm(kernel, unit, 64, Mark512)
    {
        public override int One => 1;
        public override int Two => 1;

        /// <summary>
        /// A permute's index vector (x64 only), written as a vector of literals in place: a constant of the compiled loop.
        /// A static readonly field would need its class initialized, which Native AOT checks on every iteration (and spills
        /// the loop's vectors around the check); a local would count against the inliner's 32 IL locals.
        /// </summary>
        public override string Permute(string a, int[] values, string? b)
        {
            Kernel._permutes++;
            string table = $"global::System.Runtime.Intrinsics.Vector512.Create({string.Join(", ", values.Select(v => Unit == 8 ? v + "L" : v.ToString()))})";
            const string avx512 = "global::System.Runtime.Intrinsics.X86.Avx512F";
            return b == null
                ? $"{avx512}.{(Unit == 8 ? "PermuteVar8x64" : "PermuteVar16x32")}({a}, {table})"
                : $"{avx512}.{(Unit == 8 ? "PermuteVar8x64x2" : "PermuteVar16x32x2")}({a}, {table}, {b})";
        }
    }

    /// <summary>
    /// AVX2: <c>vpermd</c> over 32-bit words (an 8-byte word its two halves), two sources as a <c>vpermd</c> of each and a
    /// blend (<c>Interleaved.Permute256</c>), or one <c>vpermd</c> and a blend where the first source keeps its words in
    /// place (<c>Interleaved.Merge256</c>, a store's write-back); indexes and masks as literals in place.
    /// </summary>
    private sealed class Avx2(Kernel kernel, int unit) : PermuteForm(kernel, unit, 32, Mark256)
    {
        public override int One => 1;
        public override int Two => 3;

        public override string Permute(string a, int[] values, string? b)
        {
            int halves = Unit / 4;
            string Dwords(System.Func<int, int, int> of) =>
                $"global::System.Runtime.Intrinsics.Vector256.Create({string.Join(", ", Enumerable.Range(0, W * halves).Select(d => of(d / halves, d % halves)))})";
            string index = Dwords((l, h) => values[l] % W * halves + h);
            if (b == null || values.All(v => v < W))
            {
                Kernel._moves256 += One;
                return $"{Interleaved}.Permute256({a}, {index})";
            }
            if (values.All(v => v >= W))
            {
                Kernel._moves256 += One;
                return $"{Interleaved}.Permute256({b}, {index})";
            }
            // vpblendd's immediate: dword d from the second source where its word does.
            string mask = $"(byte){Enumerable.Range(0, W * halves).Where(d => values[d / halves] >= W).Sum(d => 1 << d)}";
            if (Enumerable.Range(0, W).All(l => values[l] >= W || values[l] == l))
            {
                Kernel._moves256 += 2;
                return $"{Interleaved}.Merge256({a}, {b}, {index}, {mask})";
            }
            Kernel._moves256 += Two;
            return $"{Interleaved}.Permute256({a}, {b}, {index}, {mask})";
        }
    }

    /// <summary>A vector of the tree: its expression and the lane where each word it holds (lane of the kernel, row) sits.</summary>
    private sealed record Node(string Expr, Dictionary<(int Lane, int Row), int> Slots);

    /// <summary>
    /// The loads of a wide component's rows and the vectors of the lanes the method uses (<c>__c{index}_{f}</c>), in every
    /// form: <see cref="WideLoadsTree"/> (AVX-512, AVX2) and <see cref="WideLoads128"/>.
    /// </summary>
    private void WideLoads(int index, WideLayout wide, string column, StringBuilder loads)
    {
        LayoutCheck(index, wide);
        var form = new StringBuilder();
        foreach (var tree in new PermuteForm[] { new Avx512(this, wide.Unit), new Avx2(this, wide.Unit) })
        {
            WideLoadsTree(tree, index, wide, column, form.Clear());
            Marked(tree.Mark, form, loads);
        }
        WideLoads128(index, wide, column, form.Clear());
        Marked(Mark128, form, loads);
    }

    /// <summary>Appends each line of <paramref name="lines"/> to <paramref name="into"/>, after <paramref name="mark"/>.</summary>
    private static void Marked(string mark, StringBuilder lines, StringBuilder into)
    {
        foreach (var line in lines.ToString().Split('\n'))
            if (line.Length > 0) into.Append(mark).Append(line).Append('\n');
    }

    /// <summary>
    /// The loads of a wide component's rows of a vector and the vectors of the lanes the method uses, by permutes:
    /// neighboring vectors merged while their used words fit one, for the number of levels that costs the least, then each
    /// lane picked out of the vectors holding it.
    /// </summary>
    private void WideLoadsTree(PermuteForm form, int index, WideLayout wide, string column, StringBuilder loads)
    {
        int k = wide.K, w = form.W;
        var inputs = new List<Node>();
        for (int j = 0; j < k; j++)
        {
            var slots = new Dictionary<(int, int), int>();
            for (int l = 0; l < w; l++)
            {
                int e = w * j + l, lane = System.Array.IndexOf(wide.Words, e % k);
                if (lane >= 0) slots[(lane, e / k)] = l;
            }
            if (slots.Count == 0) continue;
            inputs.Add(new Node($"__v{index}_{j}", slots));
            loads.Append($"var __v{index}_{j} = {V}.LoadUnsafe(ref e{index}, (nuint)i * {k} + {w * j});\n");
        }
        int best = 0, cost = int.MaxValue;
        for (int levels = 0; levels < 6; levels++)
        {
            var planned = Merge(form, inputs, levels, null, out int merges);
            int total = merges * form.Two + Enumerable.Range(0, wide.Paths.Length).Sum(f => Extract(form, planned, f, null, null));
            if (total < cost) (best, cost) = (levels, total);
        }
        var nodes = Merge(form, inputs, best, loads, out _);
        for (int f = 0; f < wide.Paths.Length; f++)
        {
            // Picked into the field's own name when it has the column's lane type: no IL local for a copy (a 16-word row
            // loads 16 vectors, and the loop's method must stay within the locals a runner inlines: FitLocals).
            string type = Elem(wide.Paths[f][wide.Paths[f].Length - 1].Type);
            string value = type == column ? $"__c{index}_{f}" : Temp("g");
            Extract(form, nodes, f, value, loads);
            if (type != column) loads.Append($"var __c{index}_{f} = {As(column, type, value)};\n");
        }
    }

    /// <summary>
    /// The 128-bit loads of a wide component's rows (K vectors of 16 bytes, those holding a used word) and each used
    /// lane's vector: lane r from row r's word, out of the at most 4 vectors the rows' words lie in, with one table
    /// lookup (<c>Interleaved.Pick</c>, NEON TBL over a tuple of 1 to 4 registers).
    /// </summary>
    private void WideLoads128(int index, WideLayout wide, string column, StringBuilder loads)
    {
        int k = wide.K, u = wide.Unit, n = 16 / u; // rows (and words) of a vector
        var loaded = new SortedSet<int>(wide.Words.SelectMany(w => Enumerable.Range(0, n).Select(r => (r * k + w) / n)));
        foreach (int j in loaded)
            loads.Append($"var __v{index}_{j} = {V}.LoadUnsafe(ref e{index}, (nuint)i * {k} + {n * j});\n");
        _moves128 += loaded.Count;
        _usedLanes += wide.Paths.Length;
        for (int f = 0; f < wide.Paths.Length; f++)
        {
            var at = Enumerable.Range(0, n).Select(r => (Vector: (r * k + wide.Words[f]) / n, Word: (r * k + wide.Words[f]) % n)).ToArray();
            var sources = at.Select(a => a.Vector).Distinct().ToList();
            var bytes = new int[16];
            for (int r = 0; r < n; r++)
                for (int b = 0; b < u; b++)
                    bytes[u * r + b] = 16 * sources.IndexOf(at[r].Vector) + u * at[r].Word + b;
            string pick = $"{Interleaved}.Pick({string.Join(", ", sources.Select(j => $"__v{index}_{j}"))}, {Table128(bytes, sources.Count)})";
            loads.Append($"var __c{index}_{f} = {As(column, Elem(wide.Paths[f][wide.Paths[f].Length - 1].Type), pick)};\n");
        }
    }

    /// <summary>
    /// A table lookup's byte indexes (255: the byte kept, TBX): a static of the job, built once and shared by every
    /// lookup with the same bytes; its cost, the <paramref name="registers"/> of the table it reads, counted per lookup
    /// for the gate.
    /// </summary>
    private string Table128(int[] bytes, int registers)
    {
        _registers += registers;
        string values = string.Join(", ", bytes);
        if (_tableNames128.TryGetValue(values, out var name)) return name;
        name = $"__y{_tableNames128.Count}";
        _tableNames128.Add(values, name);
        _tables128.Append($"private static readonly global::System.Runtime.Intrinsics.Vector128<byte> {name} = global::System.Runtime.Intrinsics.Vector128.Create((byte){values});\n");
        return name;
    }

    /// <summary>The vectors after up to <paramref name="levels"/> levels of pairwise merges, written to <paramref name="code"/> (planned when null).</summary>
    private List<Node> Merge(PermuteForm form, List<Node> inputs, int levels, StringBuilder? code, out int merges)
    {
        int w = form.W;
        merges = 0;
        var nodes = inputs;
        for (int level = 0; level < levels; level++)
        {
            var next = new List<Node>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i + 1 == nodes.Count || nodes[i].Slots.Count + nodes[i + 1].Slots.Count > w)
                {
                    next.Add(nodes[i]);
                    continue;
                }
                var (a, b) = (nodes[i++], nodes[i]);
                var slots = new Dictionary<(int, int), int>();
                var values = new int[w];
                int lane = 0;
                foreach (var e in a.Slots) { slots[e.Key] = lane; values[lane++] = e.Value; }
                foreach (var e in b.Slots) { slots[e.Key] = lane; values[lane++] = w + e.Value; }
                // A merge of merges takes its first input's local, which nothing reads after it: an IL local per node of
                // the tree's first level only (the loaded vectors keep theirs: the stores take them back).
                bool reuse = level > 0 && a.Expr.StartsWith("__n", System.StringComparison.Ordinal);
                string name = code == null ? "" : reuse ? a.Expr : Temp("n");
                code?.Append($"{(reuse ? "" : "var ")}{name} = {form.Permute(a.Expr, values, b.Expr)};\n");
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
    /// its permutes, written to <paramref name="code"/>; its cost in the form's unit.
    /// </summary>
    private static int Extract(PermuteForm form, List<Node> nodes, int f, string? into, StringBuilder? code)
    {
        int w = form.W;
        var sources = nodes.Where(n => n.Slots.Keys.Any(e => e.Lane == f)).ToList();
        int Slot(Node n, int r) => n.Slots.TryGetValue((f, r), out int l) ? l : -1;
        if (sources.Count == 1 && Enumerable.Range(0, w).All(r => Slot(sources[0], r) == r))
        {
            code?.Append($"var {into} = {sources[0].Expr};\n");
            return 0;
        }
        if (sources.Count == 1)
        {
            code?.Append($"var {into} = {form.Permute(sources[0].Expr, Enumerable.Range(0, w).Select(r => System.Math.Max(Slot(sources[0], r), 0)).ToArray(), null)};\n");
            return form.One;
        }
        if (code != null)
        {
            var (s0, s1) = (sources[0], sources[1]);
            var values = Enumerable.Range(0, w).Select(r => Slot(s0, r) >= 0 ? Slot(s0, r) : Slot(s1, r) >= 0 ? w + Slot(s1, r) : 0).ToArray();
            code.Append($"var {into} = {form.Permute(s0.Expr, values, s1.Expr)};\n");
            foreach (var s in sources.Skip(2))
            {
                values = Enumerable.Range(0, w).Select(r => Slot(s, r) >= 0 ? w + Slot(s, r) : r).ToArray();
                code.Append($"{into} = {form.Permute(into!, values, s.Expr)};\n");
            }
        }
        return (sources.Count - 1) * form.Two;
    }

    /// <summary>
    /// The write-back of a wide component, in every form (<see cref="WideStoresTree"/>, <see cref="WideStores128"/>): each
    /// loaded vector holding words of lanes the body writes takes them, then is stored whole, its other words as loaded.
    /// </summary>
    private void WideStores(int index, WideLayout wide, string column, StringBuilder stores)
    {
        string body = _body.ToString();
        var written = new HashSet<int>(Enumerable.Range(0, wide.Paths.Length).Where(f => body.Contains($"__c{index}_{f} = ")));
        var form = new StringBuilder();
        foreach (var tree in new PermuteForm[] { new Avx512(this, wide.Unit), new Avx2(this, wide.Unit) })
        {
            WideStoresTree(tree, index, wide, column, written, form.Clear());
            Marked(tree.Mark, form, stores);
        }
        WideStores128(index, wide, column, written, form.Clear());
        Marked(Mark128, form, stores);
    }

    /// <summary>
    /// The 128-bit write-back: each vector of the rows holding words of written lanes takes them with one TBX over the
    /// lanes' vectors (<c>Interleaved.PickInto</c>; a TBL, <c>Interleaved.Pick</c>, when it takes all its words), then is
    /// stored whole.
    /// </summary>
    private void WideStores128(int index, WideLayout wide, string column, HashSet<int> written, StringBuilder stores)
    {
        int k = wide.K, u = wide.Unit, n = 16 / u;
        _writtenLanes += written.Count;
        for (int j = 0; j < k; j++)
        {
            // The written words of vector j: word l is lane f's word of row r.
            var words = new List<(int L, int F, int R)>();
            for (int l = 0; l < n; l++)
            {
                int e = n * j + l, f = System.Array.IndexOf(wide.Words, e % k);
                if (f >= 0 && written.Contains(f)) words.Add((l, f, e / k));
            }
            if (words.Count == 0) continue;
            var sources = words.Select(w => w.F).Distinct().OrderBy(f => f).ToList();
            var bytes = Enumerable.Repeat(255, 16).ToArray();
            foreach (var (l, f, r) in words)
                for (int b = 0; b < u; b++)
                    bytes[u * l + b] = 16 * sources.IndexOf(f) + u * r + b;
            string lanes = string.Join(", ", sources.Select(f => As(Elem(wide.Paths[f][wide.Paths[f].Length - 1].Type), column, $"__c{index}_{f}")));
            string table = Table128(bytes, sources.Count);
            stores.Append(words.Count == n
                ? $"__v{index}_{j} = {Interleaved}.Pick({lanes}, {table});\n"
                : $"__v{index}_{j} = {Interleaved}.PickInto(__v{index}_{j}, {lanes}, {table});\n");
            stores.Append($"{V}.StoreUnsafe(__v{index}_{j}, ref e{index}, (nuint)i * {k} + {n * j});\n");
            _moves128++;
        }
    }

    /// <summary>
    /// The write-back by permutes: each loaded vector holding words of written lanes takes them with one permute per
    /// lane. Its source is a lane's vector, or a pack of the words of several lanes that consecutive vectors take (a
    /// permute per lane past the first builds it): whichever costs less.
    /// </summary>
    private void WideStoresTree(PermuteForm form, int index, WideLayout wide, string column, HashSet<int> written, StringBuilder stores)
    {
        int k = wide.K, w = form.W;
        // The vectors to store, and the words of written lanes each holds: (lane, row) at its lane l.
        var need = new List<(int J, List<((int Lane, int Row) Word, int L)> Words)>();
        for (int j = 0; j < k; j++)
        {
            var words = new List<((int, int), int)>();
            for (int l = 0; l < w; l++)
            {
                int e = w * j + l, f = System.Array.IndexOf(wide.Words, e % k);
                if (f >= 0 && written.Contains(f)) words.Add(((f, e / k), l));
            }
            if (words.Count > 0) need.Add((j, words));
        }
        var groups = new List<(List<(int J, List<((int Lane, int Row) Word, int L)> Words)> Vectors, List<(int Lane, int Row)> Union)>();
        foreach (var vector in need)
        {
            var words = vector.Words.Select(x => x.Word).ToList();
            if (groups.Count == 0 || groups[groups.Count - 1].Union.Union(words).Count() > w) groups.Add((new(), new()));
            groups[groups.Count - 1].Vectors.Add(vector);
            foreach (var x in words) if (!groups[groups.Count - 1].Union.Contains(x)) groups[groups.Count - 1].Union.Add(x);
        }
        // A store's permute keeps the vector's own words in place: AVX2's costs a vpermd and a blend (Merge256).
        int into = System.Math.Min(form.Two, 2);
        int simple = need.Sum(v => v.Words.Select(x => x.Word.Lane).Distinct().Count()) * into;
        int packed = groups.Sum(g => System.Math.Max(0, g.Union.Select(x => x.Lane).Distinct().Count() - 1)) * form.Two + need.Count * into;
        string Lane(int f) => As(Elem(wide.Paths[f][wide.Paths[f].Length - 1].Type), column, $"__c{index}_{f}");
        string Store(int j) => $"{V}.StoreUnsafe(__v{index}_{j}, ref e{index}, (nuint)i * {k} + {w * j});\n";
        if (simple <= packed)
        {
            foreach (var (j, words) in need)
            {
                foreach (int f in words.Select(x => x.Word.Lane).Distinct().OrderBy(f => f))
                {
                    var values = Enumerable.Range(0, w).ToArray();
                    foreach (var (word, l) in words) if (word.Lane == f) values[l] = w + word.Row;
                    stores.Append($"__v{index}_{j} = {form.Permute($"__v{index}_{j}", values, Lane(f))};\n");
                }
                stores.Append(Store(j));
            }
            return;
        }
        string? pack = null; // one local for every group's pack: a group's is read only by its own stores
        foreach (var (vectors, union) in groups)
        {
            var lanes = union.Select(x => x.Lane).Distinct().OrderBy(f => f).ToArray();
            var slot = new Dictionary<(int, int), int>();
            string source;
            if (lanes.Length == 1)
            {
                source = Lane(lanes[0]);
                foreach (var x in union) slot[x] = x.Row;
            }
            else
            {
                var order = union.OrderBy(x => x).ToList();
                for (int q = 0; q < order.Count; q++) slot[order[q]] = q;
                bool declared = pack != null;
                source = pack ??= Temp("w");
                var values = new int[w];
                for (int q = 0; q < order.Count; q++)
                    values[q] = order[q].Lane == lanes[0] ? order[q].Row : order[q].Lane == lanes[1] ? w + order[q].Row : 0;
                stores.Append($"{(declared ? "" : "var ")}{source} = {form.Permute(Lane(lanes[0]), values, Lane(lanes[1]))};\n");
                foreach (int c in lanes.Skip(2))
                {
                    values = new int[w];
                    for (int q = 0; q < order.Count; q++) values[q] = order[q].Lane == c ? w + order[q].Row : q;
                    stores.Append($"{source} = {form.Permute(source, values, Lane(c))};\n");
                }
            }
            foreach (var (j, words) in vectors)
            {
                var values = Enumerable.Range(0, w).ToArray();
                foreach (var (word, l) in words) values[l] = w + slot[word];
                stores.Append($"__v{index}_{j} = {form.Permute($"__v{index}_{j}", values, source)};\n");
                stores.Append(Store(j));
            }
        }
    }

    /// <summary>
    /// Which forms pay (<see cref="Code.Wide512"/>, <see cref="Code.Wide256"/>, <see cref="Code.Wide128"/>); the reason,
    /// when none does.
    /// <para>
    /// AVX-512: the vector operations of the method's body against the permutes that move a wide component's words in
    /// and out, per vector of rows. Measured on the prototype (BenchmarkDotNet, 1k to 100k rows, 4-byte words): 3 of 12
    /// fields under 11 operations ran in 0.48 to 0.86 of the scalar time, 4 of 8 under 4 in 0.67 to 0.97; 3 of 12 under 3,
    /// 4 of 16 under 4 and 2 of 8 under 2 in 0.9 to 1.1. The line between them: fewer permutes than 8 per operation (a
    /// permute costs about what 2 of the 15 scalar operations a vector operation saves cost); with 8-byte words a vector
    /// operation saves 7, so fewer than 3.5 per operation, and only with heavy math as on AVX2: on KernelBenchmark (default
    /// job, two interleaved rounds) Dive (3 of 8 doubles, a square root and a division) ran in 0.48 of the rows' time at
    /// 1k rows and 0.64 at 100k, while Ledger (2 of 8 longs, 6 light operations, under the line) ran in 1.01 to 1.14 of
    /// it: 8 rows a vector leave light math too little to gain.
    /// </para>
    /// <para>
    /// AVX2: only with heavy math (a square root, a division, a transcendental), each worth <see cref="Heavy"/> light
    /// operations; its moves (a vpermd or a blend, 1 each) against the lanes − 1 operations each vector operation saves,
    /// halved as AVX-512's are: at most as many.
    /// </para>
    /// <para>
    /// 128 bits (ARM64), per vector of rows, in scalar operations: a vector operation saves rows − 1 (the row loop runs
    /// it once per row), and the row loop's loads and stores of the lanes it uses and writes (one per row each) are traded
    /// for the form's whole-vector loads and stores; a table lookup costs 1.5 per register of its table (TBL / TBX take a
    /// cycle or two per register on Apple M and Neoverse cores, which run up to four scalar operations per cycle beside
    /// them). It pays when the lookups cost less than the rest saves. A model, not yet measured on ARM64: KernelBenchmark's
    /// wide cases decide.
    /// </para>
    /// </summary>
    private void Gate()
    {
        int ops = 0, heavy = 0;
        string body = _body.ToString();
        foreach (var token in new[] { " + ", " - ", " * ", " / ", " & ", " | ", " ^ ", ".Min(", ".Max(", ".ConditionalSelect(", ".Sqrt(", ".SquareRoot(",
                     ".Abs(", ".Floor(", ".Ceiling(", ".Round(", ".Truncate(", ".FusedMultiplyAdd(", ".Sin(", ".Cos(", ".Exp(", ".Log(", ".Convert",
                     ".Equals(", ".LessThan", ".GreaterThan" })
            ops += Count(body, token);
        foreach (var token in new[] { " / ", ".Sqrt(", ".SquareRoot(", ".Sin(", ".Cos(", ".Exp(", ".Log(" })
            heavy += Count(body, token);
        int unit = WideUnit;
        _wide512 = unit == 4 ? _permutes < 8 * ops : heavy > 0 && 2 * _permutes < 7 * ops;
        int lanes256 = 32 / unit;
        _wide256 = heavy > 0 && 2 * _moves256 <= (lanes256 - 1) * (ops + (Heavy(unit) - 1) * heavy);
        int rows128 = 16 / unit;
        int saved = (rows128 - 1) * ops + rows128 * (_usedLanes + _writtenLanes) - _moves128;
        _wide128 = 3 * _registers < 2 * saved;
        if (_wide512 || _wide256 || _wide128) return;
        if (unit == 8 && heavy == 0 && 2 * _permutes < 7 * ops)
            throw Rejected($"takes 8-byte words without heavy math (a square root, a division, a transcendental): its {ops} vector operations over 8 rows gain less than its {_permutes} permutes cost");
        throw Rejected($"takes {_permutes} permutes per {64 / unit} rows, {(ops == 0 ? "for no vector operation" : ops == 1 ? "more than its 1 vector operation gains" : $"more than its {ops} vector operations gain")}");

        static int Count(string body, string token)
        {
            int count = 0;
            for (int at = body.IndexOf(token, System.StringComparison.Ordinal); at >= 0; at = body.IndexOf(token, at + 1, System.StringComparison.Ordinal))
                count++;
            return count;
        }
    }

    /// <summary>
    /// What a heavy operation (a square root, a division, a transcendental) weighs in light ones, for the AVX2 gate: 8 on
    /// doubles, 4 on floats. Calibrated on KernelBenchmark under DOTNET_EnableAVX512=0 (dev job, BenchmarkDotNet): Dive
    /// (3 of 8 doubles, a square root and a division; 37 moves per 4 rows, 11 operations) ran in 0.66 of the rows' time
    /// at 1k rows and 0.94 at 100k; Steer (3 of 12 floats, a square root; 54 moves per 8 rows, 10 operations) in 0.99 to
    /// 0.99, a wash, so it keeps its rows. A double's square root or division takes a scalar unit 4 to 6 cycles per row,
    /// a float's half that, while their vector forms take about the same per vector.
    /// </summary>
    private static int Heavy(int unit) => unit == 8 ? 8 : 4;

    /// <summary>
    /// The AVX-512 and AVX2 forms only while their loops, <paramref name="loop"/> (setup, loads, body and stores of every
    /// form), stay within the IL locals a runner inlines (<see cref="SystemGenerator.InlinedLocals"/>, D135): the loop's
    /// method declares its lines' (<see cref="SystemGenerator.Locals"/>), the column refs and the loop's bound. A row of up
    /// to 16 words loads that many vectors per vector of rows and keeps them for its stores; past the budget the runner
    /// would call the job per run instead of inlining it, so the form is dropped (and the reason reported when no other
    /// form pays).
    /// </summary>
    private void FitLocals(string loop)
    {
        // A loop over whole vectors takes its constant broadcasts in place (SystemGenerator.Constants): no locals. A
        // loop that gathers keeps them (its body runs under a continue).
        int Locals(string keep)
        {
            string lines = string.Join("\n", loop.Split('\n').Where(l => !new[] { Mark512, Mark256, Mark128 }.Any(m => m != keep && l.Contains(m)))) + "\n";
            if (_lookups.Count == 0) lines = SystemGenerator.Constant.Replace(lines, "");
            return SystemGenerator.Locals(lines) + _components.Count + 1;
        }
        int locals512 = _wide512 ? Locals(Mark512) : 0;
        if (locals512 > SystemGenerator.InlinedLocals) _wide512 = false;
        int locals256 = _wide256 ? Locals(Mark256) : 0;
        if (locals256 > SystemGenerator.InlinedLocals) _wide256 = false;
        if (!_wide512 && !_wide256 && !_wide128)
            throw Rejected(locals512 > 0
                ? $"declares {locals512} locals per {64 / WideUnit} rows, past the {SystemGenerator.InlinedLocals} a job's loop may declare to be inlined"
                : $"declares {locals256} locals per {32 / WideUnit} rows, past the {SystemGenerator.InlinedLocals} a job's loop may declare to be inlined");
    }

    /// <summary>Why no form of a wide component pays: <paramref name="cost"/>, what gathering its fields costs.</summary>
    private Unsupported Rejected(string cost)
    {
        var (p, wide) = (_wideOf.First().Key, _wideOf.First().Value);
        return Advised($"{wide.Reason}, and gathering the {wide.Paths.Length} {(wide.Paths.Length == 1 ? "field" : "fields")} it uses {cost}", p.Type, wide.Paths, false);
    }

    /// <summary>
    /// <paramref name="reason"/> ended with the advice to split the fields the method uses (<see cref="Split"/>) when it
    /// applies: then the user can fix it, and EECS091 leaves <paramref name="type"/> to EECS055.
    /// </summary>
    private static Unsupported Advised(string reason, ITypeSymbol type, IFieldSymbol[][]? used, bool isFixable)
    {
        string split = used != null ? Split(type, used) : "";
        return split.Length == 0 ? new Unsupported(reason, isFixable) : new Unsupported(reason + split, true, type.ToDisplayString());
    }

    /// <summary>
    /// The advice that ends a reason when the method uses few of a component's fields: "split them into a component of
    /// their own" (at most half its fields, all lanes of one size, at most <see cref="MaxFields"/>: a component that
    /// gets a vector loop), else "".
    /// </summary>
    private static string Split(ITypeSymbol type, IFieldSymbol[][] used)
    {
        // Only for a component too wide or too mixed for the loop: a plain sequential struct of values.
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } named || Symbols.FieldsInSeveralParts(named)
            || named.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute"))
            return "";
        int all = Layout(named, 0, out _, out _)?.Count ?? 0;
        // A bool moves out alone: a component of one bool is a byte column the loop reads as a mask; two bools together
        // would still be a bool among fields.
        bool flags = used.Any(u => IsBool(u[u.Length - 1].Type));
        if (used.Length == 0 || used.Length > MaxFields || used.Length * 2 > all || flags && used.Length != 1
            || used.Any(u => !(IsLaneType(u[u.Length - 1].Type) || IsBool(u[u.Length - 1].Type)) || SizeOf(u[u.Length - 1].Type) != SizeOf(used[0][used[0].Length - 1].Type)))
            return "";
        string names = string.Join(", ", used.Select(u => string.Join(".", u.Select(Symbols.MemberName))));
        return used.Length == 1
            ? $"; it uses 1 of {type.Name}'s {all} fields ({names}): move it into a component of its own (a component of one field gets a vector loop)"
            : $"; it uses {used.Length} of {type.Name}'s {all} fields ({names}): split them into a component of their own (a component of {used.Length} fields gets a vector loop)";
    }
}
