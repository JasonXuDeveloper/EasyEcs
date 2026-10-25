using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEcs.Generator;

// The loops of a job's Execute that Kernel translated (vector loops, flat loops, the vector tail), and where they go:
// into Execute itself while it stays within what the JIT inlines, else each into a method of the job of its own.
public sealed partial class SystemGenerator
{
    /// <summary>
    /// The most IL locals a method the runner inlines may declare: the JIT inlines no method that declares more
    /// (<c>MAX_INL_LCLS</c>, "too many locals"), <c>AggressiveInlining</c> or not, and then calls it for every row of
    /// runs and every 64-row word of a masked table.
    /// </summary>
    internal const int InlinedLocals = 32;

    /// <summary>
    /// One loop of a job's Execute: it runs while <paramref name="Guard"/> holds and moves <c>i</c> (a vector form's
    /// rows, or the tail's last rows).
    /// </summary>
    /// <param name="Name">The name of its method when Execute is split (<see cref="EmitLoops"/>).</param>
    /// <param name="Guard">The condition it runs under (the width the hardware has, rows left for a vector).</param>
    /// <param name="Body">Its statements, unindented; they read and write <c>i</c>.</param>
    /// <param name="Tail">The vector tail: it declares its own column refs and leaves no rows (no <c>i</c> returned).</param>
    private sealed record Loop(string Name, string Guard, string Body, bool Tail = false);

    /// <summary>
    /// A name of Execute's scope a loop's method may need, as the parameter that takes it and the argument that passes
    /// it: the run's locals (a reduction's partial result <c>l2</c>, a Maybe's column <c>m3</c>, a Mut's <c>u1</c>...);
    /// <see cref="EmitLoops"/> adds the run itself (<c>entities</c>, <c>pass</c>, the column refs, <c>rows</c>, <c>i</c>).
    /// </summary>
    internal readonly record struct Scoped(string Name, string Parameter, string Argument);

    /// <summary>
    /// The loops of <paramref name="simd"/> in a job's Execute (from <c>sb</c>'s offset <paramref name="start"/>, its body
    /// so far). They stay in Execute while it declares at most <see cref="InlinedLocals"/> IL locals; past that, each
    /// loop moves into an <c>AggressiveInlining</c> method of the job (<paramref name="members"/>), which Execute calls
    /// where it ran the loop. The width the hardware lacks is removed when the JIT imports Execute, before it imports
    /// that width's method: each width's locals then count apart, the runner inlines Execute and Execute its loops,
    /// and no call is left in the code. Declared names that would push a loop's own method past the limit stay
    /// counted (<see cref="Locals"/>) but are not split further: no form of the generator's has one.
    /// </summary>
    private static void EmitLoops(StringBuilder sb, StringBuilder members, Kernel.Code simd, string[] components, string indent, string call,
        bool masked, string? rows, IReadOnlyList<Scoped> scope, int start)
    {
        var flat = masked ? null : simd.Flat;
        string head = flat != null ? $"nint i = 0, lanes = rows * {flat.Fields}; // i counts lanes: {flat.Fields} per row\n" : "nint i = 0;\n";
        string after = flat != null ? $"i /= {flat.Fields}; // whole rows: the row the rows left start at\n" : "";
        var columns = new StringBuilder();
        for (int k = 1; k <= components.Length; k++)
            columns.Append($"ref {simd.Columns[k - 1]} e{k} = ref global::System.Runtime.CompilerServices.Unsafe.As<{components[k - 1]}, {simd.Columns[k - 1]}>(ref r{k});\n");
        var loops = flat != null ? FlatLoops(flat, simd) : VectorLoops(simd, components, call, masked, rows, split: false);

        var inline = new StringBuilder();
        Indented(inline, indent, columns.ToString() + head);
        foreach (var loop in loops)
        {
            inline.Append($"{indent}if ({loop.Guard})\n{indent}{{\n");
            Indented(inline, indent + "    ", loop.Body);
            inline.Append($"{indent}}}\n");
        }
        Indented(inline, indent, after);
        if (Locals(sb.ToString(start, sb.Length - start) + inline) <= InlinedLocals)
        {
            sb.Append(inline);
            return;
        }

        // Split: Execute keeps the run's setup and the rows left; each loop is a method of its own.
        if (flat == null) loops = VectorLoops(simd, components, call, masked, rows, split: true);
        var names = new List<Scoped> { new("entities", "in global::EasyEcs.EntitySpan entities", "in entities") };
        if (masked) names.Add(new("pass", "ulong pass", "pass"));
        for (int k = 1; k <= components.Length; k++) names.Add(new($"r{k}", $"ref {components[k - 1]} r{k}", $"ref r{k}"));
        names.Add(new("rows", "nint rows", "rows"));
        if (flat != null) names.Add(new("lanes", "nint lanes", "lanes"));
        names.Add(new("i", "nint i", "i"));
        names.AddRange(scope);
        Indented(sb, indent, head);
        string prefix = masked ? "__Masked" : "__";
        foreach (var loop in loops)
        {
            string name = prefix + loop.Name;
            var used = Used(loop.Body, names);
            string arguments = string.Join(", ", used.Select(u => u.Argument));
            sb.Append(loop.Tail ? $"{indent}if ({loop.Guard}) {name}({arguments});\n" : $"{indent}if ({loop.Guard}) i = {name}({arguments});\n");
            members.Append("\n        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
            members.Append($"        private {(loop.Tail ? "void" : "nint")} {name}({string.Join(", ", used.Select(u => u.Parameter))})\n        {{\n");
            const string inner = "            ";
            if (!loop.Tail)
                for (int k = 1; k <= components.Length; k++)
                    if (Names(loop.Body, $"e{k}")) members.Append(inner).Append(columns.ToString().Split('\n')[k - 1]).Append('\n');
            Indented(members, inner, loop.Body);
            if (!loop.Tail) members.Append($"{inner}return i;\n");
            members.Append("        }\n");
        }
        Indented(sb, indent, after);
    }

    /// <summary>
    /// The names of Execute's scope (<paramref name="scope"/>, in the order a loop's method takes them) that
    /// <paramref name="body"/> reads, comments aside: a column's ref <c>r1</c> where it loads or stores <c>e1</c>, and
    /// <c>i</c> always.
    /// </summary>
    private static List<Scoped> Used(string body, IReadOnlyList<Scoped> scope)
    {
        string code = Regex.Replace(body, "//[^\n]*", "");
        return scope.Where(s => s.Name == "i" || Names(code, s.Name) || s.Name.Length > 1 && s.Name[0] == 'r' && char.IsDigit(s.Name[1]) && Names(code, "e" + s.Name.Substring(1))).ToList();
    }

    /// <summary>
    /// The vector loops of a method <see cref="Kernel"/> translated: each component's lane field as a ref of the lane
    /// type, the uniform values broadcast once per run, then one vector of rows per iteration: Vector512 where the
    /// hardware runs it (AVX-512), then Vector&lt;T&gt; for the rows left (or all of them); the rows left after that
    /// run the method. Without hardware acceleration (the JIT would emulate the vectors), every row runs the method.
    /// The two loops are one text: the Vector512 one names Vector512 where the other names Vector. A job whose every
    /// row takes the vector form ends with the tail (<see cref="TailLoop"/>); <paramref name="split"/>: it is a method
    /// of its own, which declares the column refs it moves.
    /// </summary>
    private static List<Loop> VectorLoops(Kernel.Code simd, string[] components, string call, bool masked, string? rows, bool split)
    {
        var loop = new StringBuilder();
        // Kernel.V stands for the vector type in both loops' text, and the tokens below for what their forms name apart.
        string vector = $"{Kernel.V}<{simd.Lane}>"; // the narrowest lanes: a column of lanes twice as wide takes two vectors
        // Components of several fields need the shuffles of Interleaved; without them, every row runs the method.
        string interleaved = simd.Interleaves ? $" && global::EasyEcs.Interleaved.{Accelerated}" : "";
        // A wide component's permutes are AVX-512's, and its layout the generator's (checked once per job type).
        if (simd.Wide != null) interleaved += $" && global::System.Runtime.Intrinsics.X86.Avx512F.IsSupported && {simd.Wide}";
        // Other entities' fields are AVX2 gathers (Kernel.Lookups.cs): without them, every row runs the method.
        if (simd.Gathers) interleaved += " && global::EasyEcs.LookupLanes.IsAccelerated";
        string guard = $"{Kernel.V}.IsHardwareAccelerated{interleaved} && rows - i >= {vector}.Count";
        const string body = "    ";
        foreach (var line in simd.Setup.Split('\n')) if (line.Length > 0) loop.Append(line).Append('\n');
        loop.Append($"for (nint last = rows - {vector}.Count; i <= last; i += {vector}.Count)\n{{\n");
        if (masked)
        {
            // The rows of this vector that pass, one lane each: a vector with none is skipped.
            loop.Append($"{body}var __pass = global::EasyEcs.Interleaved.Lanes{simd.Size * 8}{Kernel.LanesForm}(pass, (int)i);\n");
            loop.Append($"{body}if (__pass == {Kernel.V}<{simd.Mask}>.Zero) continue;\n");
        }
        // A lane would throw (Math.Clamp with min above max): nothing is stored yet, so the method itself runs the
        // rows of this vector (that pass), and throws as written. i walks them, and is set back for the loop's own step.
        // The reductions' lanes go back to before this vector: the method adds those rows itself.
        var each = new StringBuilder("{\n");
        foreach (var line in simd.Restore.Split('\n')) if (line.Length > 0) each.Append("    ").Append(line).Append('\n');
        each.Append($"    for (nint end = i + {vector}.Count; i < end; i++)\n        {(masked ? $"if (((pass >> (int)i) & 1) != 0) {call}" : call)}\n");
        each.Append($"    i -= {vector}.Count;\n    continue;\n}}");
        string runRows = each.ToString();
        // An unresolved lookup's vector and the ones its backoff skips (rows up to __end, Kernel.RunRows): through __Rows
        // when the job has it, the method in the loop otherwise; i is set back for the loop's own step.
        string resolveRows = string.Concat(simd.Restore.Split('\n').Where(l => l.Length > 0).Select(l => l + "\n")) + (rows == null
            ? $"for (; i < __end; i++)\n    {(masked ? $"if (((pass >> (int)i) & 1) != 0) {call}" : call)}\ni -= {vector}.Count;\ncontinue;"
            : $"{string.Format(rows, masked ? "pass" : "ulong.MaxValue")}\ni = __end - {vector}.Count;\ncontinue;");
        foreach (var line in (masked ? simd.MaskedBody : simd.Body).Replace(Kernel.RunRows, resolveRows.Replace("\n", "\n    ")).Split('\n')) if (line.Length > 0) loop.Append(body).Append(line).Append('\n');
        if (simd.Bails)
            foreach (var line in ("if (__bail)\n" + runRows).Split('\n')) loop.Append(body).Append(line).Append('\n');
        foreach (var line in (masked ? simd.MaskedStores : simd.Stores).Split('\n')) if (line.Length > 0) loop.Append(body).Append(line).Append('\n');
        loop.Append("}\n");
        foreach (var line in simd.Fold.Split('\n')) if (line.Length > 0) loop.Append(line).Append('\n');
        // Only the tokens are replaced: setup lines name user types (in Vector3 wind, in Vector<float> bias) as written.
        // Vector512 names the same operations alike, except the square root (Vector.SquareRoot, Vector512.Sqrt).
        string text = loop.Replace(Kernel.Visited, masked ? "pass" : "ulong.MaxValue").Replace(Kernel.Row, "(int)i").ToString();
        string Wide(string s) => s.Replace(Kernel.SquareRoot, Vector512 + ".Sqrt(").Replace(Kernel.V, Vector512)
            .Replace(Accelerated, "IsAccelerated512").Replace(Kernel.LanesForm, "In512");
        string Narrow(string s) => s.Replace(Kernel.V, Vector).Replace(Accelerated, "IsAccelerated").Replace(Kernel.LanesForm, "");
        var loops = new List<Loop> { new("Loop512", Wide(guard), Wide(text)) };
        if (simd.Wide == null) loops.Add(new("Loop", Narrow(guard), Narrow(text)));
        if (simd.VectorTail) loops.Add(TailLoop(simd, components, masked, split));
        return loops;
    }

    /// <summary>
    /// The rows of a run after the last whole vector, when every row must take the vector form (<see cref="Kernel.Code.VectorTail"/>):
    /// copied into a vector's worth of rows (<c>Interleaved.LoadTail</c>), run as a masked vector whose passing lanes
    /// are those rows (and, in a word of passing rows, those of them that pass), and the written columns copied back.
    /// The job runs only where the Vector&lt;T&gt; loop is hardware (its <c>Vectorizes</c>), so the copy takes that form.
    /// In Execute it points the column refs at the copy; as a method of its own (<paramref name="split"/>) it declares them.
    /// </summary>
    private static Loop TailLoop(Kernel.Code simd, string[] components, bool masked, bool split)
    {
        const string unsafeType = "global::System.Runtime.CompilerServices.Unsafe", interleaved = "global::EasyEcs.Interleaved";
        var tail = new StringBuilder();
        tail.Append("nint __at = i;\nint __n = (int)(rows - i);\n");
        for (int k = 1; k <= components.Length; k++)
            tail.Append($"{(split ? $"ref {simd.Columns[k - 1]} " : "")}e{k} = ref {unsafeType}.As<{components[k - 1]}, {simd.Columns[k - 1]}>(ref {interleaved}.LoadTail(ref {unsafeType}.Add(ref r{k}, __at), __n));\n");
        tail.Append("i = 0;\n");
        foreach (var line in simd.Setup.Split('\n')) if (line.Length > 0) tail.Append(line).Append('\n');
        tail.Append($"var __pass = {interleaved}.Lanes{simd.Size * 8}({(masked ? "pass, (int)__at" : "ulong.MaxValue >> (64 - __n), 0")});\n");
        foreach (var line in (simd.MaskedBody + simd.MaskedStores + simd.Fold).Split('\n')) if (line.Length > 0) tail.Append(line).Append('\n');
        foreach (int k in simd.Written) tail.Append($"{interleaved}.StoreTail(ref {unsafeType}.Add(ref r{k}, __at), __n);\n");
        if (!split) tail.Append("i = rows;\n");
        return new("Tail", "i < rows", tail.Replace(Kernel.Visited, masked ? "pass" : "ulong.MaxValue").Replace(Kernel.Row, "(int)__at").Replace(Kernel.V, Vector)
            .Replace(Kernel.LanesForm, "").ToString(), Tail: true);
    }

    /// <summary>
    /// The loops of a method that treats each field alike (<see cref="Kernel.FlatCode"/>): the columns as flat arrays of
    /// <c>rows * fields</c> lanes, no shuffles. The vector loops end at a multiple of <c>Count * fields</c> lanes, whole
    /// rows, so <c>i</c> becomes a row again for the rows left (<see cref="EmitLoops"/>), which run the method.
    /// </summary>
    private static List<Loop> FlatLoops(Kernel.FlatCode flat, Kernel.Code simd)
    {
        var loop = new StringBuilder();
        string count = $"{Kernel.V}<{simd.Lane}>.Count";
        string chunk = $"{count} * {flat.Fields}"; // whole vectors of whole rows
        string guard = $"{Kernel.V}.IsHardwareAccelerated && lanes - i >= {chunk}";
        foreach (var line in flat.Setup.Split('\n')) if (line.Length > 0) loop.Append(line).Append('\n');
        loop.Append($"for (nint end = lanes - (lanes - i) % ({chunk}); i < end; i += {count})\n{{\n");
        foreach (var line in (flat.Body + flat.Stores).Split('\n')) if (line.Length > 0) loop.Append("    ").Append(line).Append('\n');
        loop.Append("}\n");
        string text = loop.ToString();
        return
        [
            new("Flat512", guard.Replace(Kernel.V, Vector512), text.Replace(Kernel.SquareRoot, Vector512 + ".Sqrt(").Replace(Kernel.V, Vector512)),
            new("Flat", guard.Replace(Kernel.V, Vector), text.Replace(Kernel.V, Vector)),
        ];
    }

    /// <summary>Appends each line of <paramref name="text"/> after <paramref name="indent"/>.</summary>
    private static void Indented(StringBuilder sb, string indent, string text)
    {
        foreach (var line in text.Split('\n')) if (line.Length > 0) sb.Append(indent).Append(line).Append('\n');
    }

    /// <summary>Whether <paramref name="text"/> names <paramref name="name"/> as a whole identifier.</summary>
    private static bool Names(string text, string name)
    {
        for (int at = text.IndexOf(name, System.StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, System.StringComparison.Ordinal))
            if ((at == 0 || !IsWord(text[at - 1])) && (at + name.Length == text.Length || !IsWord(text[at + name.Length])))
                return true;
        return false;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    /// <summary>
    /// The IL locals the C# compiler declares for <paramref name="text"/> (statements the generator wrote), counted
    /// from above: every declared name (<c>var</c>, a typed or <c>ref</c> local, an <c>out</c> variable, a loop's
    /// variable) is one, except that a declaration written twice with its type (each width's <c>nint last</c>, its
    /// <c>ref readonly</c> singleton) is one: in scopes that do not overlap the compiler gives locals of one type one
    /// slot (it does for other names too, which is why this is from above). A <c>default</c> argument (the method's
    /// owner) is the one temporary it adds, shared by every statement. Measured exact or above on every job of the
    /// benchmarks and tests.
    /// </summary>
    internal static int Locals(string text)
    {
        int count = Defaults.IsMatch(text) ? 1 : 0;
        var typed = new HashSet<string>();
        foreach (Match declaration in Declarations.Matches(text))
            if (Untyped.IsMatch(declaration.Value) || typed.Add(Spaces.Replace(declaration.Value, " "))) count++;
        return count;
    }

    private static readonly Regex Spaces = new(@"\s+"), Untyped = new(@"^(?:out\s+)?var\b");

    private static readonly Regex Defaults = new(@"\bdefault\b(?!\s*\()");

    private static readonly Regex Declarations = new(
        @"(?<![\w.])(?:(?:var|bool|int|nint|uint|ulong|long|float|double)\s+@?\w+\s*=(?!=)|ref\s+(?:readonly\s+)?[\w.:<>\[\], ]+?\s+@?\w+\s*=\s*ref\b|out\s+(?:var|[\w.:<>\[\], ]+?)\s+(?!_\b)@?\w+(?=\s*[,)])|,\s*lanes\s*=)");
}
