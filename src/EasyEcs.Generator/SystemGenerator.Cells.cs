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
                parameters.Add($"global::EasyEcs.Tile<{CellName(k)}, {shape}> __tile{k}");
                call.Append($", \u0001c{k}\u0002");
            }
            else if (j >= 0)
            {
                parameters.Add($"global::EasyEcs.Mut<global::EasyEcs.DynamicBuffer<{OutName(j)}>> __next{j}");
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
        for (int k = 0; k < cells.Count; k++)
            body.Append(k == 0 && skips ? "__tile0.ExchangeHalo();\n" : $"__tile{k}.ExchangeHalo(0, {at}.Height - 1, mask: false);\n");
        for (int j = 0; j < outputs.Count; j++)
        {
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
            row.Append($"ref {OutName(j)} __o{j} = ref {Unsafe}.Add(ref __out{j}, __row.Index);\n");
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
            row.Append("{\n").Append(Indent(vector.ToString())).Append("__rest:\n    __x = (int)i;\n}\n");
        }
        row.Append($"for (; __x < {at}.Width; __x++)\n    {Call(k => $"new {At(k)}(in {Unsafe}.Add(ref __c{k}, __x), __x, __row.Y, __row.Z)", j => $"{Unsafe}.Add(ref __o{j}, __x)")};\n");
        if (skips)
        {
            // A row the walk skips reads only zeros: it gets what the method computes there, exactly (with this entity's
            // values), computed at the first such row (a tile without one never runs the method on zeros) and written
            // into the gaps between the rows walked (in order of Index), so every cell is written once.
            string blank = $"if (!__blank)\n{{\n    {Call(k => $"{At(k)}.Blank", j => $"__f{j}", "__spare")};\n    __blank = true;\n}}\n";
            string Fill(string slice) => string.Concat(outputs.Select((_, j) => $"    __into{j}.{slice}.Fill(__f{j});\n"));
            body.Append("int __done = 0;\nbool __blank = false;\n");
            for (int j = 0; j < outputs.Count; j++) body.Append($"{OutName(j)} __f{j} = default;\n");
            body.Append($"foreach (var __row in __tile0.Rows((global::EasyEcs.TileStencil){stencil}))\n{{\n");
            body.Append(Indent($"if (__row.Index > __done)\n{{\n{Indent(blank)}{Fill("Slice(__done, __row.Index - __done)")}}}\n"));
            body.Append(Indent(row.ToString())).Append($"    __done = __row.Index + {at}.Width;\n}}\n");
            body.Append($"if (__done < {at}.Count)\n{{\n{Indent(blank)}{Fill("Slice(__done)")}}}\n");
        }
        else
        {
            body.Append($"for (int __y = 0; __y < {at}.Height; __y++)\nfor (int __z = 0; __z < {at}.Depth; __z++)\n{{\n");
            body.Append(Indent($"var __row = new global::EasyEcs.TileRow<{shape}>(__y, __z);\n" + row)).Append("}\n");
        }
        for (int n = 0; n < results.Count; n++) body.Append($"__res{n}.Write() = __red{n}.Value;\n");
        sb.Append(Indent(Indent(body.ToString()))).Append("    }\n#pragma warning restore\n\n");

        string names = string.Join("', '", cells.Select(c => c.Name));
        string walk = skips ? (form.ConstantOnBlank && (form.ReadsPlace || cells.Count > 1 || results.Count > 0) ? $"; rows of '{cells[0].Name}': blank rows proved constant" : "")
            : form.ReadsPlace ? "; every row: it reads its place" : cells.Count > 1 ? "; every row: several cells" : "; every row: a result per tile";
        string line = form.Loop is { } l
            ? $"cells of '{names}': vector (Vector512{(with128 ? $", Vector<{l.Code.Lane}> or Vector128" : $" or Vector<{l.Code.Lane}>")} per row; offsets {string.Join(" ", form.Offsets.Select(o => cells.Count > 1 ? $"{cells[o.Cell].Name}({o.Dx},{o.Dy},{o.Dz})" : $"({o.Dx},{o.Dy},{o.Dz})"))}{walk})"
            : $"cells of '{names}': scalar ({form.Reason}{walk})";
        return new ElementCode(sb.ToString(), new([]), new(new[] { line }), "", Cells: true);
    }
}
