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

        var cell = method.Parameters.First(p => p.RefKind == RefKind.None && Symbols.IsCell(p.Type));
        var output = method.Parameters.First(p => Symbols.CellOut(p) != null);
        var cellType = (INamedTypeSymbol)cell.Type;
        string cellName = cellType.TypeArguments[0].ToDisplayString(Symbols.Fqn), shape = cellType.TypeArguments[1].ToDisplayString(Symbols.Fqn);
        string outName = output.Type.ToDisplayString(Symbols.Fqn);
        string at = $"global::EasyEcs.Cell<{cellName}, {shape}>";
        // The pass names its parameters __arg{n} and its locals with two underscores: the vector loop's text (e1.., i, rows,
        // at, x{n}) runs inline in it, and no name of the method's can meet them.
        const string self = "__owner", w = "__tile", next = "__next";
        var parameters = new List<string>();
        var passed = new Dictionary<string, string>(); // a parameter's name in the method -> in the pass
        var call = new StringBuilder(); // the method's own call at one cell: {0} the cell, {1} its out cell
        foreach (var p in method.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(p, cell))
            {
                parameters.Add($"global::EasyEcs.Tile<{cellName}, {shape}> {w}");
                call.Append(", {0}");
            }
            else if (SymbolEqualityComparer.Default.Equals(p, output))
            {
                parameters.Add($"global::EasyEcs.Mut<global::EasyEcs.DynamicBuffer<{outName}>> {next}");
                call.Append(", out {1}");
            }
            else
            {
                string name = $"__arg{p.Ordinal}", modifier = p.RefKind switch { RefKind.Ref => "ref ", RefKind.In => "in ", RefKind.RefReadOnlyParameter => "ref readonly ", _ => "" };
                parameters.Add($"{modifier}{p.Type.ToDisplayString(Symbols.Fqn)} {name}");
                call.Append($", {(p.RefKind == RefKind.RefReadOnlyParameter ? "in " : modifier)}{name}");
                passed[Code.Escape(p.Name)] = name;
            }
        }
        string Call(string cellArg, string outArg) => $"{AccessorPrefix}__Cell({self}" + call.ToString().Replace("{0}", cellArg).Replace("{1}", outArg) + ")";

        // The rows the offsets read: the row itself, its neighbors north and south, the layers above and below.
        int stencil = 1;
        foreach (var (dx, dy, dz) in form.Offsets)
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
        foreach (var (dx, dy, dz) in form.Offsets) body.Append($"{at}.Check({dx}, {dy}, {dz});\n"); // constants: the JIT drops them
        body.Append($"{w}.ExchangeHalo();\n");
        body.Append($"var __into = global::EasyEcs.DynamicBufferExtensions.SetCount(ref {next}.Write(), {at}.Count);\n");
        body.Append($"ref {outName} __out = ref {Marshal}.GetReference(__into);\n");
        var row = new StringBuilder();
        row.Append($"ref {cellName} __c = ref {Unsafe}.AsRef(in {w}.At(__row)[0, 0, 0]);\n");
        row.Append($"ref {outName} __o = ref {Unsafe}.Add(ref __out, __row.Index);\n");
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
                string start = k < form.Offsets.Length ? $"{Unsafe}.Add(ref __c, {at}.Offset({form.Offsets[k].Dx}, {form.Offsets[k].Dy}, {form.Offsets[k].Dz}))" : "__o";
                vector.Append($"ref {columns[k]} e{k + 1} = ref {Unsafe}.As<{loop.Streams[k].Element}, {columns[k]}>(ref {start});\n");
            }
            vector.Append($"nint i = 0, rows = {at}.Width;\nint at = 0;\n");
            for (int v = 0; v < form.Values.Length; v++)
                vector.Append($"{loop.Values[v].Type} x{loop.Streams.Length + v} = {(form.Values[v] switch { "Y" => "__row.Y", "Z" => "__row.Z", var name => passed[name] })};\n");
            string loops = ElementLoops(loop, true, "goto __rest;");
            with128 = loops.Contains(Vector128 + ".");
            vector.Append(loops);
            row.Append("{\n").Append(Indent(vector.ToString())).Append("__rest:\n    __x = (int)i;\n}\n");
        }
        row.Append($"for (; __x < {at}.Width; __x++)\n    {Call($"new {at}(in {Unsafe}.Add(ref __c, __x), __x, __row.Y, __row.Z)", $"{Unsafe}.Add(ref __o, __x)")};\n");
        if (!form.ReadsPlace)
        {
            // A row the walk skips reads only zeros: it gets what the method computes there, exactly (with this entity's
            // values), computed at the first such row (a tile without one never runs the method on zeros) and written
            // into the gaps between the rows walked (in order of Index), so every cell is written once.
            string blank = $"if (!__blank)\n{{\n    {Call($"{at}.Blank", "__f0")};\n    __blank = true;\n}}\n";
            body.Append($"int __done = 0;\nbool __blank = false;\n{outName} __f0 = default;\n");
            body.Append($"foreach (var __row in {w}.Rows((global::EasyEcs.TileStencil){stencil}))\n{{\n");
            body.Append(Indent($"if (__row.Index > __done)\n{{\n{Indent(blank)}    __into.Slice(__done, __row.Index - __done).Fill(__f0);\n}}\n"));
            body.Append(Indent(row.ToString())).Append($"    __done = __row.Index + {at}.Width;\n}}\n");
            body.Append($"if (__done < __into.Length)\n{{\n{Indent(blank)}    __into.Slice(__done).Fill(__f0);\n}}\n");
        }
        else
        {
            body.Append($"for (int __y = 0; __y < {at}.Height; __y++)\nfor (int __z = 0; __z < {at}.Depth; __z++)\n{{\n");
            body.Append(Indent($"var __row = new global::EasyEcs.TileRow<{shape}>(__y, __z);\n" + row)).Append("}\n");
        }
        sb.Append(Indent(Indent(body.ToString()))).Append("    }\n#pragma warning restore\n\n");

        string line = form.Loop is { } l
            ? $"cells of '{cell.Name}': vector (Vector512{(with128 ? $", Vector<{l.Code.Lane}> or Vector128" : $" or Vector<{l.Code.Lane}>")} per row; offsets {string.Join(" ", form.Offsets.Select(o => $"({o.Dx},{o.Dy},{o.Dz})"))}{(form.ReadsPlace ? "; every row: it reads its place" : "")})"
            : $"cells of '{cell.Name}': scalar ({form.Reason})";
        return new ElementCode(sb.ToString(), new([]), new(new[] { line }), "", Cells: true);
    }
}
