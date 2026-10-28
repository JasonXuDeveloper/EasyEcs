using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// Pair methods (<c>[Neighbor]</c> parameters, PairForm.cs): the pass runs the method for every pair of a row and a
/// neighbor within the cutoff its body tests, through <c>World.ForPairs</c>. The generator writes the order of the
/// compared component (the Morton code of its point by the cutoff, <c>{Name}Order{i}</c>), the job
/// (<c>IPairJob</c>: the cutoff, once per pass or per row, and the clusters of rows against their neighbors, as a vector
/// loop where the body translates, else the method per pair) and the filters of both sides. The model is a body fact:
/// a system's comes from its implementation stage (<see cref="LoopModel"/>), a runner's from its declaration.
/// </summary>
public sealed partial class SystemGenerator
{
    /// <summary>A pair method's job, symbol-free.</summary>
    /// <param name="Position">The compared component (the order's), fully qualified.</param>
    /// <param name="Axes">The paths of its float fields the test compares, 1 to 3 (none without a test: every pair runs).</param>
    /// <param name="Cutoff">The squared cutoff as C# of the job (its fields <c>s{i}</c> / <c>x{i}</c>, and per row <c>__k{i}</c> at row <c>__r</c>), or "" (every pair).</param>
    /// <param name="PerRow">The cutoff reads the row's components: read for every row once per pass.</param>
    /// <param name="Simd">The vector loop of one neighbor against a cluster (Kernel.Neighbors.cs), or null.</param>
    /// <param name="Scalar">Why the pairs run one at a time, when they do.</param>
    /// <param name="NoCutoff">Why no cutoff was found (EECS106), or "".</param>
    /// <param name="Symmetric">The method writes both sides of its pairs (<see cref="PairForm.IsSymmetric"/>): each unordered pair once.</param>
    private sealed record PairModel(string Position, EquatableArray<string> Axes, string Cutoff, bool PerRow, Kernel.Code? Simd, string Scalar, string NoCutoff, bool Symmetric)
    {
        /// <param name="parallel">The pass runs on several threads: the job's singletons are SingletonRef fields.</param>
        public static PairModel Create(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, bool parallel, System.Threading.CancellationToken ct)
        {
            bool outside = Symbols.IsRunner(method);
            var shape = PairForm.Read(method, syntax, model, ct, out string? noCutoff);
            string cutoff = "";
            if (shape != null)
            {
                var names = new Dictionary<IParameterSymbol, string>(SymbolEqualityComparer.Default);
                for (int i = 0; i < method.Parameters.Length; i++)
                    names[method.Parameters[i]] = Symbols.Classify(method.Parameters[i], outside) switch
                    {
                        Symbols.ParamKind.Component => $"global::System.Runtime.CompilerServices.Unsafe.Add(ref __k{i}, __r)",
                        Symbols.ParamKind.Singleton => parallel ? $"s{i}.Value" : $"s{i}",
                        _ => $"x{i}",
                    };
                cutoff = Kernel.ScalarOf(shape.Cutoff, names, out string? why) ?? "";
                if (cutoff.Length == 0)
                {
                    noCutoff = $"its cutoff is not a value the pass can read once: {why}";
                    shape = null;
                }
            }

            // Without a test, the order is the first neighbor component's, unused: every pair runs.
            string position = shape?.Position.ToDisplayString(Symbols.Fqn)
                ?? method.Parameters.FirstOrDefault(p => Symbols.Classify(p, outside) == Symbols.ParamKind.Neighbor && !Symbols.Named(p.Type, Symbols.Entity))?.Type.ToDisplayString(Symbols.Fqn)
                ?? "global::EasyEcs.Entity";
            string? scalar = "the method cannot run per pair (EECS105)";
            var simd = PairForm.Problem(method) == null ? ForEachModel.VectorLoop(method, syntax, model, parallel, ct, out scalar) : null;
            if (simd != null && method.Parameters.FirstOrDefault(p => Symbols.Classify(p, outside) is Symbols.ParamKind.Reduction or Symbols.ParamKind.Mutable) is { } kept)
            {
                // The vector loop's pair mode broadcasts one neighbor to a cluster's lanes: it has no partial result per
                // lane nor a stamp per written lane.
                simd = null;
                scalar = $"'{kept.Name}' is a {(Symbols.Classify(kept, outside) == Symbols.ParamKind.Reduction ? "Reduce<T>" : "Mut<T>")}, which the pair mode of the vector loop does not take";
            }
            return new PairModel(position, new EquatableArray<string>(shape?.Axes ?? []), cutoff, shape?.PerRow == true, simd,
                simd == null ? scalar ?? "" : "", noCutoff ?? "", PairForm.IsSymmetric(method));
        }

        /// <summary>The line of SystemInfo.Loops: the cutoff and the loop.</summary>
        public string Describe(string name) =>
            $"{name}: {(Symmetric ? "each unordered pair once, both sides written, " : "")}pairs {(Cutoff.Length == 0 ? "of every row and neighbor (no cutoff test: " + NoCutoff + ")" : PerRow ? "within each row's cutoff" : "within the cutoff")}, "
            + (Simd != null ? "a cluster of rows against each neighbor (vector)" : $"one pair at a time ({Scalar})");
    }

    /// <summary>A pair method's pass: its job over the rows of its filter against the neighbors.</summary>
    private static void EmitPairPass(StringBuilder sb, ForEachModel method, string forEach, int i, string world, string local, string indent)
    {
        var pair = method.Pair!;
        string job = $"{forEach}.{method.Name}Job{i}";
        sb.Append($"{indent}var {local} = new {job}{Fields(method, world)};\n");
        sb.Append($"{indent}{world}.ForPairs{(method.Parallel ? "Parallel" : "")}<{pair.Position}, {forEach}.{method.Name}Order{i}, {job}>(ref {local}, in {forEach}.{method.Name}Rows{i}, in {forEach}.{method.Name}Neighbors{i}, {System.Math.Max(1, pair.Axes.Length)});\n");
        EmitReductions(sb, method, world, local, indent);
    }

    /// <summary>The order, the job and the filters of a pair method.</summary>
    private static void EmitPairJob(StringBuilder sb, OwnerModel owner, ForEachModel method, int number)
    {
        var pair = method.Pair!;
        const string unsafeType = "global::System.Runtime.CompilerServices.Unsafe", vector = "global::System.Numerics.Vector";
        string t = pair.Position;
        var axes = pair.Axes.ToArray();
        string point = axes.Length == 0 ? "default" : $"new({string.Join(", ", Enumerable.Range(0, 3).Select(k => k < axes.Length ? $"row.{axes[k]}" : "0f"))})";

        sb.Append($"    internal readonly struct {method.Name}Order{number} : global::EasyEcs.IRowOrder<{t}>\n    {{\n");
        sb.Append("        public static int KeyBytes => 8;\n\n");
        sb.Append($"        public static ulong Key(in {t} row, float cell) => global::EasyEcs.RowKeys.Morton(Point(row), {System.Math.Max(1, axes.Length)}, cell);\n\n");
        sb.Append($"        public static global::System.Numerics.Vector3 Point(in {t} row) => {point};\n    }}\n\n");

        // A parallel pass copies the job into each chunk's thread: no ref fields (JobField gives SingletonRefs).
        sb.Append($"    internal {(method.Parallel ? "" : "ref ")}struct {method.Name}Job{number} : global::EasyEcs.IPairJob\n    {{\n");
        for (int i = 0; i < method.Parameters.Length; i++)
            if (JobField(method, i, "") is { } field)
                foreach (var line in field.Declaration.Split('\n')) sb.Append("        ").Append(line).Append('\n');
        if (sb[sb.Length - 2] != '{') sb.Append('\n');

        // The cutoff: once per pass, or each row's (its components at row __r).
        sb.Append($"        public bool CutoffPerRow => {(pair.PerRow ? "true" : "false")};\n\n");
        sb.Append($"        public double CutoffSquared => {(pair.Cutoff.Length == 0 ? "double.PositiveInfinity" : pair.PerRow ? "double.NaN" : $"(double)({pair.Cutoff})")};\n\n");
        sb.Append("        public void CutoffsSquared(in global::EasyEcs.PairRun table, global::System.Span<double> into)\n        {\n");
        if (pair.PerRow)
        {
            for (int i = 0; i < method.Parameters.Length; i++)
                if (pair.Cutoff.Contains($"__k{i},"))
                    sb.Append($"            ref {method.Parameters[i].Type} __k{i} = ref table.Rows<{method.Parameters[i].Type}>();\n");
            sb.Append($"            for (int __r = 0; __r < into.Length; __r++) into[__r] = (double)({pair.Cutoff});\n");
        }
        sb.Append("        }\n\n");

        // The rows of each kind of parameter: the method's columns r{k} in query order, a neighbor's o{i}.
        string Columns(string indent)
        {
            var text = new StringBuilder();
            int k = 0;
            foreach (var p in method.Parameters)
                if (p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.Mutable) text.Append($"{indent}ref {p.Type} r{++k} = ref run.Rows<{p.Type}>();\n");
            for (int i = 0; i < method.Parameters.Length; i++)
                if (method.Parameters[i] is { Kind: Symbols.ParamKind.Neighbor } p && p.Type != "global::EasyEcs.Entity")
                    text.Append($"{indent}ref {p.Type} o{i} = ref run.Others<{p.Type}>();\n");
            return text.ToString();
        }

        // The vector loop's guard: a cluster per step where it holds, a row per step otherwise (Width, which the pass
        // weighs each row's list of neighbors against; a run that carries the lists visits them a row at a time).
        string? accelerated = pair.Simd is { } guarded
            ? $"{vector}.IsHardwareAccelerated{(guarded.Interleaves ? " && global::EasyEcs.Interleaved.IsAccelerated" : "")}"
            : null;
        sb.Append(accelerated == null
            ? "        public int Width => 1;\n\n"
            : $"        public int Width => {accelerated} ? {vector}<float>.Count : 1;\n\n");
        sb.Append("        public void Run(in global::EasyEcs.PairRun run)\n        {\n");
        if (accelerated != null) sb.Append($"            if (!run.Listed && {accelerated} && run.ClusterSize == {vector}<float>.Count) Vectors(in run);\n            else Rows(in run);\n");
        else sb.Append("            Rows(in run);\n");
        sb.Append("        }\n\n");
        sb.Append("        public void Stamp(in global::EasyEcs.PairRun run)\n        {\n");
        foreach (var p in method.Parameters.Where(p => p is { Kind: Symbols.ParamKind.Component, RefKind: "ref" }).Select(p => p.Type).Distinct())
            sb.Append($"            run.Stamp<{p}>(); // the rows the method ran for, as a ref parameter stamps them\n");
        sb.Append("        }\n\n");

        // The method per pair: each row of a cluster against its neighbors, in row order.
        int column = 0;
        var args = string.Join(", ", method.Parameters.Select((p, i) => p.Kind switch
        {
            Symbols.ParamKind.Component => $"{p.RefKind} {unsafeType}.Add(ref r{++column}, i)",
            Symbols.ParamKind.Mutable => $"new global::EasyEcs.Mut<{p.Type}>(ref {unsafeType}.Add(ref r{++column}, i), in m{i}, (int)i)",
            Symbols.ParamKind.Reduction => $"ref a{i}",
            Symbols.ParamKind.Neighbor when p.Type == "global::EasyEcs.Entity" => "run.Other(j)",
            Symbols.ParamKind.Neighbor => $"{p.RefKind} {unsafeType}.Add(ref o{i}, j)",
            Symbols.ParamKind.Entity => "run.Entity((int)i)",
            Symbols.ParamKind.Singleton => method.Parallel ? $"{p.RefKind} s{i}.Value" : $"{p.RefKind} s{i}",
            Symbols.ParamKind.Context => $"{(p.RefKind.Length == 0 ? "" : p.RefKind + " ")}x{i}",
            _ => "default", // EECS105 reports the parameter
        }).Prepend(owner.AccessorArgument));
        // The test's distance from the row's point and the neighbor's, in float as the method computes it: a neighbor
        // past the cluster's reach (over every cutoff of its rows by a margin far above the rounding) fails the method's
        // test, so it is skipped without a call. NaN never skips.
        int rowColumn = 0, rowPoint = 0, otherPoint = -1;
        foreach (var p in method.Parameters)
        {
            if (p.Kind != Symbols.ParamKind.Component) continue;
            rowColumn++;
            if (rowPoint == 0 && p.Type == t) rowPoint = rowColumn;
        }
        for (int i = 0; i < method.Parameters.Length && otherPoint < 0; i++)
            if (method.Parameters[i] is { Kind: Symbols.ParamKind.Neighbor } p && p.Type == t) otherPoint = i;
        bool prune = axes.Length > 0 && pair.Cutoff.Length > 0 && rowPoint > 0 && otherPoint >= 0;
        sb.Append("        private void Rows(in global::EasyEcs.PairRun run)\n        {\n");
        sb.Append(Columns("            "));
        for (int i = 0; i < method.Parameters.Length; i++) // a Mut<T>'s stamps per write: the run's change-tick column, found once
            if (method.Parameters[i].Kind == Symbols.ParamKind.Mutable)
                sb.Append($"            var m{i} = run.MutRows<{method.Parameters[i].Type}>();\n");
        // A symmetric job's run of a table against itself visits the rows after the row only (its lists hold only those).
        sb.Append(pair.Symmetric ? "            bool skips = run.Skips, listed = run.Listed, half = run.Same;\n" : "            bool skips = run.Skips, listed = run.Listed;\n");
        sb.Append("            for (int w = run.First, size = run.ClusterSize; w < run.End; w++)\n            {\n");
        sb.Append("                ulong lanes = run.Lanes(w);\n                if (lanes == 0) continue;\n");
        sb.Append("                var ranges = run.Ranges(w);\n");
        if (prune) sb.Append("                float reach = run.Cutoff(w), reach2 = reach * reach;\n");
        sb.Append("                for (; lanes != 0; lanes &= lanes - 1)\n                {\n");
        sb.Append("                    nint i = (nint)w * size + global::System.Numerics.BitOperations.TrailingZeroCount(lanes);\n");
        if (prune) sb.Append($"                    ref readonly {t} at = ref {unsafeType}.Add(ref r{rowPoint}, i);\n");
        // One neighbor j of row i: the reach test, the neighbors' filter, the method (and the neighbor's stamps).
        var pairBody = new StringBuilder();
        if (prune)
        {
            pairBody.Append($"ref readonly {t} near = ref {unsafeType}.Add(ref o{otherPoint}, j);\n");
            pairBody.Append("float " + string.Join(", ", axes.Select((a, k) => $"d{k} = at.{a} - near.{a}")) + ";\n");
            pairBody.Append("if (" + string.Join(" + ", axes.Select((_, k) => $"d{k} * d{k}")) + " > reach2) continue;\n");
        }
        // A neighbor's component by ref stamps the neighbors the method ran for (one row of the other table per pair). A
        // symmetric job's neighbors are its rows, each stamped by its own table's run (Stamp) as a row taken by ref is.
        var stamps = pair.Symmetric ? System.Array.Empty<string>() : method.Parameters.Where(p => p is { Kind: Symbols.ParamKind.Neighbor, RefKind: "ref" }).Select(p => p.Type).Distinct()
            .Select(type => $" run.StampOther<{type}>(j);").ToArray();
        string call = $"__ForEach{owner.TypeParameters}.{method.Name}_{number}({args});";
        pairBody.Append($"if (!skips || !run.Skip(j)) {(stamps.Length == 0 ? call : "{ " + call + string.Concat(stamps) + " }")}\n");
        void Pair()
        {
            foreach (var line in pairBody.ToString().Split('\n')) if (line.Length > 0) sb.Append("                            ").Append(line).Append('\n');
        }
        // The row's list of neighbors where the run carries them (the same pairs in the same order: a list is its
        // cluster's ranges less rows past the cutoff), else the cluster's ranges.
        sb.Append("                    if (listed)\n                    {\n");
        sb.Append("                        var nearby = run.Near((int)i);\n");
        sb.Append("                        for (int k = 0; k < nearby.Length; k++)\n                        {\n");
        sb.Append("                            int j = nearby[k];\n");
        Pair();
        sb.Append("                        }\n\n                        continue;\n                    }\n\n");
        sb.Append("                    for (int k = 0; k < ranges.Length; k += 2)\n");
        sb.Append(pair.Symmetric
            ? "                        for (int j = half ? global::System.Math.Max(ranges[k], (int)i + 1) : ranges[k], end = ranges[k + 1]; j < end; j++)\n                        {\n"
            : "                        for (int j = ranges[k], end = ranges[k + 1]; j < end; j++)\n                        {\n");
        Pair();
        sb.Append("                        }\n                }\n            }\n        }\n");

        if (pair.Simd is { } simd)
        {
            // A cluster of rows in the lanes against one neighbor at a time; the cluster's writes stored once, its rows only.
            var loop = new StringBuilder();
            loop.Append(Columns(""));
            var components = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Component).Select(p => p.Type).ToArray();
            for (int k = 1; k <= components.Length; k++)
                loop.Append($"ref {simd.Columns[k - 1]} e{k} = ref {unsafeType}.As<{components[k - 1]}, {simd.Columns[k - 1]}>(ref r{k});\n");
            loop.Append(simd.Setup);
            loop.Append("bool __skips = run.Skips;\n");
            loop.Append("for (int __w = run.First; __w < run.End; __w++)\n{\n");
            loop.Append("    ulong __lanes = run.Lanes(__w);\n    if (__lanes == 0) continue;\n");
            loop.Append($"    nint i = (nint)__w * {vector}<float>.Count;\n");
            loop.Append($"    var __pass = global::EasyEcs.Interleaved.Lanes32(__lanes, 0);\n");
            foreach (var line in simd.Body.Split('\n')) if (line.Length > 0) loop.Append("    ").Append(line).Append('\n');
            loop.Append("    var __ranges = run.Ranges(__w);\n");
            loop.Append("    for (int __k = 0; __k < __ranges.Length; __k += 2)\n");
            loop.Append("        for (int j = __ranges[__k], __end = __ranges[__k + 1]; j < __end; j++)\n        {\n");
            loop.Append("            if (__skips && run.Skip(j)) continue;\n");
            for (int i = 0; i < method.Parameters.Length; i++)
                if (method.Parameters[i] is { Kind: Symbols.ParamKind.Neighbor } p && p.Type != "global::EasyEcs.Entity" && simd.MaskedBody.Contains(Kernel.Neighbor(i)))
                    loop.Append($"            ref readonly {p.Type} {Kernel.Neighbor(i)} = ref {unsafeType}.Add(ref o{i}, j);\n");
            foreach (var line in simd.MaskedBody.Split('\n')) if (line.Length > 0) loop.Append("            ").Append(line).Append('\n');
            loop.Append("        }\n");
            foreach (var line in simd.MaskedStores.Split('\n')) if (line.Length > 0) loop.Append("    ").Append(line).Append('\n');
            loop.Append("}\n");
            string text = loop.ToString().Replace(Kernel.SquareRoot, vector + ".SquareRoot(").Replace(Kernel.V, vector).Replace(Kernel.LanesForm, "");
            sb.Append("\n        private void Vectors(in global::EasyEcs.PairRun run)\n        {\n");
            foreach (var line in text.Split('\n')) sb.Append(line.Length > 0 ? "            " + line : "").Append('\n');
            sb.Length--; // the split's last empty line
            sb.Append("        }\n");
        }

        if (method.Parallel && method.Parameters.Any(p => p.Kind == Symbols.ParamKind.Reduction))
        {
            // Each chunk's partial results, combined in chunk order (TSelf is this job: the pass passes its own TJob).
            sb.Append("\n        static bool global::EasyEcs.IPairJob.Merges => true;\n\n");
            sb.Append("        static void global::EasyEcs.IPairJob.Merge<TSelf>(ref TSelf job, in TSelf chunk)\n        {\n");
            sb.Append($"            ref var into = ref {unsafeType}.As<TSelf, {method.Name}Job{number}>(ref job);\n");
            sb.Append($"            ref var part = ref {unsafeType}.As<TSelf, {method.Name}Job{number}>(ref {unsafeType}.AsRef(in chunk));\n");
            MergeLines(sb, method, "into", "part");
            sb.Append("        }\n");
        }

        // Each unordered pair of the rows once (the pass reads the rows' filter for both sides).
        if (pair.Symmetric) sb.Append("\n        static bool global::EasyEcs.IPairJob.Symmetric => true;\n");

        sb.Append("    }\n\n");
        EmitFilter(sb, method, number);
        // The rows: the method's filter and components; the neighbors: the entities holding the [Neighbor] components (a
        // symmetric job's: its rows).
        sb.Append($"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Rows{number} = {FilterChain(method)}{string.Concat(method.Query.Select(q => $".With<{q}>()"))};\n\n");
        var neighbors = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Neighbor && p.Type != "global::EasyEcs.Entity").Select(p => p.Type).Distinct();
        sb.Append(pair.Symmetric
            ? $"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Neighbors{number} = {method.Name}Rows{number};\n\n"
            : $"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Neighbors{number} = global::EasyEcs.QueryFilter.Create(){string.Concat(neighbors.Select(q => $".With<{q}>()"))};\n\n");
    }
}
