using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

// Cell methods (the shader form, Kernel.Cells.cs): a [ForEach] method taking a Cell<TCell, TTile> and assigning an out
// TOut gets a pass the generator writes in place of its accessor. The job hands it the tile (its Cell parameter is a
// tile parameter: filters, neighbors, the grid's index) and a Mut<DynamicBuffer<TOut>> (its out parameter is a write of
// that buffer); the pass exchanges the halo, walks the rows the offsets read (a skipped row gets what the method
// computes from zeros), and runs each row as a vector loop over its cells, then the method itself on the cells left.
public sealed partial class SystemGenerator
{
    /// <summary>The pass of cell method <paramref name="method"/>, named after <see cref="AccessorPrefix"/>, and its schedule line.</summary>
    private static ElementCode CellsOf(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        Kernel.CellForm form;
        try
        {
            form = Kernel.CellFormOf(method, syntax, model, ct);
        }
        catch (System.Exception e) when (e is not System.OperationCanceledException)
        {
            form = new Kernel.CellForm(new([]), true, null, new([]), $"the generator failed to translate the cell ({e.GetType().Name}: {e.Message})");
        }

        var cells = Symbols.Cells(method);
        var outputs = method.Parameters.Where(p => Symbols.CellOut(p) != null).ToList();
        var results = method.Parameters.Where(p => Symbols.TileResult(p) != null).ToList();
        string Result(int n) => Symbols.TileResult(results[n])!.ToDisplayString(Symbols.Fqn);
        string shape = ((INamedTypeSymbol)cells[0].Type).TypeArguments[1].ToDisplayString(Symbols.Fqn);
        string CellName(int k) => ((INamedTypeSymbol)cells[k].Type).TypeArguments[0].ToDisplayString(Symbols.Fqn);
        string At(int k) => $"global::EasyEcs.Cell<{CellName(k)}, {shape}>";
        string OutName(int j) => outputs[j].Type.ToDisplayString(Symbols.Fqn);
        string at = At(0); // the shape's constants (every input has the driver's tile)
        // An out cell of the driver's own type is its next value: the pass steps the driver's tile (the job hands it by
        // ref, a Jacobi step), its rows written into the tile's second buffer (Write(row)), which TileSteps swaps in
        // once every tile stepped and whose rows the step skips are zero; no buffer of its own, no commit.
        int step = outputs.FindIndex(Symbols.IsCellStep);
        // The pass names its parameters __arg{n} and its locals with two underscores: the vector loop's text (e1.., i, rows,
        // at, x{n}) runs inline in it, and no name of the method's can meet them.
        const string self = "__owner";
        var parameters = new List<string>();
        var passed = new Dictionary<string, string>(); // a parameter's name in the method -> in the pass
        var call = new StringBuilder(); // the method's own call at one cell, each cell and out cell a placeholder Call replaces
        foreach (var p in method.Parameters)
        {
            int k = cells.FindIndex(c => SymbolEqualityComparer.Default.Equals(c, p)), j = outputs.FindIndex(o => SymbolEqualityComparer.Default.Equals(o, p)),
                n = results.FindIndex(r => SymbolEqualityComparer.Default.Equals(r, p));
            if (k >= 0)
            {
                parameters.Add($"{(k == 0 && step >= 0 ? "ref " : "")}global::EasyEcs.Tile<{CellName(k)}, {shape}> __tile{k}");
                call.Append($", \u0001c{k}\u0002");
            }
            else if (j >= 0)
            {
                if (j != step) parameters.Add($"global::EasyEcs.Mut<global::EasyEcs.DynamicBuffer<{OutName(j)}>> __next{j}");
                call.Append($", out \u0001o{j}\u0002");
            }
            else if (n >= 0)
            {
                parameters.Add($"global::EasyEcs.Mut<{Result(n)}> __res{n}");
                call.Append($", ref \u0001r{n}\u0002");
            }
            else
            {
                string name = $"__arg{p.Ordinal}", modifier = p.RefKind switch { RefKind.Ref => "ref ", RefKind.In => "in ", RefKind.RefReadOnlyParameter => "ref readonly ", _ => "" };
                parameters.Add($"{modifier}{p.Type.ToDisplayString(Symbols.Fqn)} {name}");
                call.Append($", {(p.RefKind == RefKind.RefReadOnlyParameter ? "in " : modifier)}{name}");
                passed[Code.Escape(p.Name)] = name;
            }
        }
        string Call(System.Func<int, string> cell, System.Func<int, string> output, string result = "__red")
        {
            var text = new StringBuilder($"{AccessorPrefix}__Cell({self}").Append(call).Append(')');
            for (int k = 0; k < cells.Count; k++) text.Replace($"\u0001c{k}\u0002", cell(k));
            for (int j = 0; j < outputs.Count; j++) text.Replace($"\u0001o{j}\u0002", output(j));
            for (int n = 0; n < results.Count; n++) text.Replace($"\u0001r{n}\u0002", result + n);
            return text.ToString();
        }

        // The rows the driver's offsets read: the row itself, its neighbors north and south, the layers above and below.
        // A row the walk skips is blank in the driver only: it may be skipped when the driver is the one input and the
        // method does not read its place (what it computes there is what it computes on Cell.Blank), or when the
        // generator proved the out cells the same on every blank row of the driver, whatever else the method reads.
        // A result per tile is skipped only with the proof: the rows' f0 says nothing of what they add to it.
        bool skips = form.ConstantOnBlank || !form.ReadsPlace && cells.Count == 1 && results.Count == 0;
        int stencil = 1;
        foreach (var (_, dx, dy, dz) in form.Offsets)
        {
            if (dz != 0) stencil |= 2;
            if (dy > 0) stencil |= 4;
            if (dy < 0) stencil |= 8;
            if (dy != 0 && dz != 0) stencil |= 16;
        }

        var sb = new StringBuilder();
        sb.Append("#pragma warning disable\n");
        sb.Append("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n");
        sb.Append($"    internal static void {AccessorPrefix}({OwnerModel.From(method.ContainingType).AccessorParameter} {self}, {string.Join(", ", parameters)})\n    {{\n");
        var body = new StringBuilder();
        foreach (var (k, dx, dy, dz) in form.Offsets) body.Append($"{At(k)}.Check({dx}, {dy}, {dz});\n"); // constants: the JIT drops them
        // The exchange is a call of its own (NoInlining): inlined, its faces take the inliner's budget for the pass, and
        // the row walk's helpers (the masks, Index, Width, Write's checks) are then left as calls in every row.
        var exchanges = new StringBuilder();
        for (int k = 0; k < cells.Count; k++)
        {
            body.Append($"{AccessorPrefix}__Exchange{k}(ref __tile{k});\n");
            exchanges.Append("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n")
                .Append($"    private static void {AccessorPrefix}__Exchange{k}(ref global::EasyEcs.Tile<{CellName(k)}, {shape}> tile) => ")
                .Append(k == 0 && skips ? "tile.ExchangeHalo();\n\n" : $"tile.ExchangeHalo(0, {at}.Height - 1, mask: false);\n\n");
        }
        // A stepped tile's next buffer and its words of rows written, checked once per tile (Write checks each row).
        if (step >= 0) body.Append($"ref {OutName(step)} __stepped = ref __tile0.JacobiNext(out ulong[] __written);\n");
        for (int j = 0; j < outputs.Count; j++)
        {
            if (j == step) continue; // its rows are the stepped tile's (Write(row), per row)
            body.Append($"var __into{j} = global::EasyEcs.DynamicBufferExtensions.SetCount(ref __next{j}.Write(), {at}.Count);\n");
            body.Append($"ref {OutName(j)} __out{j} = ref {Marshal}.GetReference(__into{j});\n");
        }
        // Each result starts at its identity for the tile (and a spare for the blank cells, which add nothing to it).
        for (int n = 0; n < results.Count; n++)
            body.Append($"var __red{n} = new global::EasyEcs.Reduce<{Result(n)}> {{ Value = {Result(n)}.Identity }};\nvar __spare{n} = __red{n};\n");
        var row = new StringBuilder();
        for (int k = 0; k < cells.Count; k++)
            row.Append($"ref {CellName(k)} __c{k} = ref {Unsafe}.AsRef(in __tile{k}.At(__row)[0, 0, 0]);\n");
        for (int j = 0; j < outputs.Count; j++)
            row.Append(j == step
                ? $"__written[__y] |= 1UL << __z;\nref {OutName(j)} __o{j} = ref {Unsafe}.Add(ref __stepped, __index);\n"
                : $"ref {OutName(j)} __o{j} = ref {Unsafe}.Add(ref __out{j}, __index);\n");
        row.Append("int __x = 0;\n");
        bool with128 = false;
        if (form.Loop is { } loop)
        {
            // The row's vector loop, inline (a routine of its own would not be inlined: it has more locals than the JIT
            // inlines, and a call per row of 16 cells costs as much as the row): its streams, from the row's start, its
            // index, its values (the row's Y and Z, the method's other parameters), then the loops of each width.
            var vector = new StringBuilder();
            var columns = loop.Code.Columns;
            for (int k = 0; k < loop.Streams.Length; k++)
            {
                string start = k < form.Offsets.Length
                    ? $"{Unsafe}.Add(ref __c{form.Offsets[k].Cell}, {at}.Offset({form.Offsets[k].Dx}, {form.Offsets[k].Dy}, {form.Offsets[k].Dz}))"
                    : $"__o{k - form.Offsets.Length}";
                vector.Append($"ref {columns[k]} e{k + 1} = ref {Unsafe}.As<{loop.Streams[k].Element}, {columns[k]}>(ref {start});\n");
            }
            vector.Append($"nint i = 0, rows = {at}.Width;\nint at = 0;\n");
            for (int n = 0; n < results.Count; n++) // the lanes fold into the tile's result after the row's loop
                vector.Append($"ref var x{loop.Streams.Length + n} = ref __red{n};\n");
            for (int v = 0; v < form.Values.Length; v++)
                vector.Append($"{loop.Values[v].Type} x{loop.Streams.Length + results.Count + v} = {(form.Values[v] switch { "Y" => "__row.Y", "Z" => "__row.Z", var name => passed[name] })};\n");
            string loops = ElementLoops(loop, true, "goto __rest;");
            with128 = loops.Contains(Vector128 + ".");
            vector.Append(loops);
            // X in narrow lanes is exact only while the row's cells number no more than they count (a JIT constant).
            if (loop.Code.IndexTop > 0) row.Append($"if ({at}.Width - 1 <= {loop.Code.IndexTop})\n");
            row.Append("{\n").Append(Indent(vector.ToString())).Append("__rest:\n    __x = (int)i;\n}\n");
        }
        row.Append($"for (; __x < {at}.Width; __x++)\n    {Call(k => $"new {At(k)}(in {Unsafe}.Add(ref __c{k}, __x), __x, __row.Y, __row.Z)", j => $"{Unsafe}.Add(ref __o{j}, __x)")};\n");
        string? constants = null;
        if (skips)
        {
            // A row the walk skips reads only zeros: it gets what the method computes there, exactly (with this entity's
            // values), written into the gaps between the rows walked (in order of Index), so every cell is written once.
            // A stepped tile's rows the step does not write are zero once swapped in: they are written only when that
            // value is not zero. A method that takes nothing besides its cells and out cells computes the same there
            // for every tile: once per system, in static readonly fields (constants once the type is initialized, so
            // Tier1 drops the fill of a zero step); else at the first such row of each tile (a tile without one never
            // runs the method on zeros).
            bool once = passed.Count == 0 && results.Count == 0;
            string F(int j) => once ? $"{AccessorPrefix}__Blank.F{j}" : $"__f{j}";
            string nonzero = once ? $"{AccessorPrefix}__Blank.Nonzero" : "__nonzero";
            string zero = step >= 0
                ? $"    {nonzero} = global::System.MemoryExtensions.IndexOfAnyExcept({Marshal}.AsBytes(new global::System.ReadOnlySpan<{OutName(step)}>(in {F(step)})), (byte)0) >= 0;\n" : "";
            string blank = once ? "" : $"if (!__blank)\n{{\n    {Call(k => $"{At(k)}.Blank", j => $"__f{j}", "__spare")};\n{zero}    __blank = true;\n}}\n";
            if (once)
            {
                var fields = new StringBuilder();
                for (int j = 0; j < outputs.Count; j++) fields.Append($"        internal static readonly {OutName(j)} F{j};\n");
                if (step >= 0) fields.Append("        internal static readonly bool Nonzero;\n");
                constants = $"    /// <summary>What the method computes on Cell.Blank: the cells of the rows the walk skips.</summary>\n"
                    + $"    private static class {AccessorPrefix}__Blank\n    {{\n{fields}\n        static {AccessorPrefix}__Blank()\n        {{\n"
                    + $"            {Call(k => $"{At(k)}.Blank", j => $"F{j}").Replace(self, "default")};\n"
                    + (step >= 0 ? $"            Nonzero = global::System.MemoryExtensions.IndexOfAnyExcept({Marshal}.AsBytes(new global::System.ReadOnlySpan<{OutName(step)}>(in F{step})), (byte)0) >= 0;\n" : "")
                    + "        }\n    }\n\n";
            }
            string Fill(string from, string to) => string.Concat(outputs.Select((_, j) => j == step
                ? $"    if ({nonzero})\n        for (int __r = {from} / {at}.Width; __r < {to} / {at}.Width; __r++)\n"
                  + $"            __tile0.Write(new global::EasyEcs.TileRow<{shape}>(__r / {at}.Depth, __r % {at}.Depth)).Fill({F(j)});\n"
                : $"    __into{j}.Slice({from}, {to} - {from}).Fill({F(j)});\n"));
            body.Append("int __done = 0;\n");
            if (!once)
            {
                body.Append("bool __blank = false;\n");
                if (step >= 0) body.Append("bool __nonzero = false;\n");
                for (int j = 0; j < outputs.Count; j++) body.Append($"{OutName(j)} __f{j} = default;\n");
            }
            // The walk of Rows as plain loops, in its order: a word of rows per layer, a trailing-zero count per row.
            body.Append($"var __walk = __tile0.Rows((global::EasyEcs.TileStencil){stencil});\n");
            body.Append("for (int __l = __walk.First, __last = __walk.Last; __l <= __last; __l++)\n{\n    int __y = __walk.LayerOf(__l);\n");
            body.Append("    for (ulong __m = __walk.RowsOf(__l); __m != 0; __m &= __m - 1)\n    {\n");
            var inner = new StringBuilder();
            inner.Append($"int __z = global::System.Numerics.BitOperations.TrailingZeroCount(__m);\nvar __row = new global::EasyEcs.TileRow<{shape}>(__y, __z);\n");
            inner.Append("int __index = (__y * __depth + __z) * __width;\n");
            inner.Append($"if (__index > __done)\n{{\n{Indent(blank)}{Fill("__done", "__index")}}}\n");
            inner.Append(row).Append("__done = __index + __width;\n");
            body.Append(Indent(Indent(inner.ToString()))).Append("    }\n}\n");
            body.Append($"if (__done < {at}.Count)\n{{\n{Indent(blank)}{Fill("__done", $"{at}.Count")}}}\n");
        }
        else
        {
            body.Append($"for (int __y = 0; __y < {at}.Height; __y++)\nfor (int __z = 0; __z < {at}.Depth; __z++)\n{{\n");
            body.Append(Indent($"var __row = new global::EasyEcs.TileRow<{shape}>(__y, __z);\nint __index = (__y * __depth + __z) * __width;\n" + row)).Append("}\n");
        }
        for (int n = 0; n < results.Count; n++) body.Append($"__res{n}.Write() = __red{n}.Value;\n");
        // The shape's constants and the offsets read once, before the walk (each read is an inline, and the pass is large
        // enough for the inliner's budget to run out): the JIT folds them, and with the width the vector loops a row of
        // that many bytes never enters, so only the width that runs is compiled.
        string text = body.ToString().Replace($"{at}.Width", "__width").Replace($"{at}.Depth", "__depth").Replace($"{at}.Height", "__height").Replace($"{at}.Count", "__count");
        var head = new StringBuilder($"int __width = {at}.Width, __depth = {at}.Depth, __height = {at}.Height, __count = {at}.Count;\n");
        for (int k = 0; k < form.Offsets.Length; k++)
        {
            var o = form.Offsets[k];
            string offset = $"{at}.Offset({o.Dx}, {o.Dy}, {o.Dz})";
            if (!text.Contains(offset)) continue;
            head.Append($"nint __d{k} = {offset};\n");
            text = text.Replace(offset, $"__d{k}");
        }
        sb.Append(Indent(Indent(head + text))).Append("    }\n#pragma warning restore\n\n");
        sb.Append("#pragma warning disable\n").Append(exchanges).Append("#pragma warning restore\n\n");
        if (constants != null) sb.Append("#pragma warning disable\n").Append(constants).Append("#pragma warning restore\n\n");

        string names = string.Join("', '", cells.Select(c => c.Name));
        string walk = skips ? (form.ConstantOnBlank && (form.ReadsPlace || cells.Count > 1 || results.Count > 0) ? $"; rows of '{cells[0].Name}': blank rows proved constant" : "")
            : form.ReadsPlace ? "; every row: it reads its place" : cells.Count > 1 ? "; every row: several cells" : "; every row: a result per tile";
        if (step >= 0) walk += $"; steps the tile of '{cells[0].Name}' (its second buffer swapped in)";
        string line = form.Loop is { } l
            ? $"cells of '{names}': vector (Vector512{(with128 ? $", Vector<{l.Code.Lane}> or Vector128" : $" or Vector<{l.Code.Lane}>")} per row; offsets {string.Join(" ", form.Offsets.Select(o => cells.Count > 1 ? $"{cells[o.Cell].Name}({o.Dx},{o.Dy},{o.Dz})" : $"({o.Dx},{o.Dy},{o.Dz})"))}{walk})"
            : $"cells of '{names}': scalar ({form.Reason}{walk})";
        return new ElementCode(sb.ToString(), new([]), new(new[] { line }), "", Cells: true);
    }
}
