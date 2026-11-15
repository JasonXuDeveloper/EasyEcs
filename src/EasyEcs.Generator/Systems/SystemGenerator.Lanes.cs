using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEcs.Generator;

// The lane kernel's form of a flat job (docs/plans/designs/lane-kernel.md, owner L33): the job's vector body written once
// as a step over Core's lane types (ILane: Lane512 / Lane256 / Lane128), the widths, the masked words and the stopping
// row Core's LaneKernel's; every other shape keeps the loops SystemGenerator.Loops.cs writes per width.
public sealed partial class SystemGenerator
{
    /// <summary>
    /// A flat job's step: its body over the lane types <c>LF</c> (float) and <c>LI</c> (int), unindented.
    /// </summary>
    /// <param name="Text">The step's statements: loads, the method's arithmetic, stores; a masked word's writes through <c>Keep&lt;M&gt;</c>.</param>
    /// <param name="Fields">Lanes per row: a flat loop's fields (each alike), else 1.</param>
    /// <param name="Masked">Masked words take the step too (its writes kept by the passing lanes); else they keep their loops.</param>
    internal sealed record LaneStep(string Text, int Fields, bool Masked);

    /// <summary>
    /// The lane kernel's step of <paramref name="simd"/>, or null when the job is not flat. Flat is the shape the kernel
    /// measured level on (KernelBenchmark's Integrate, two rounds, 0 B):
    /// <list type="bullet">
    /// <item>lanes of 4 bytes, float or int, of components of one field (or a flat loop over several, each alike);</item>
    /// <item>straight-line lane arithmetic (<c>+ - * / &amp; | ^ ~</c>, unary <c>-</c>) of the row's loads and constants:
    /// no condition (a mask kept across statements), no other call;</item>
    /// <item>no reduction, structural change, record, bail, gather, draw, lookup prefetch, streamed column, wide or
    /// interleaved component, and no uniform but a constant (a broadcast in the step is not hoisted out of the walk).</item>
    /// </list>
    /// Masked words take the step when their text is the whole run's with each write kept by the passing lanes.
    /// </summary>
    internal static LaneStep? LaneStepOf(Kernel.Code? simd)
    {
        if (simd is not { Size: 4, Bails: false, Restore.Length: 0, Fold.Length: 0, VectorTail: false, Gathers: false, Draws: false,
                Wide: null, Statics.Length: 0, Pairs: false, IndexTop: 0, Cold: null, Streams: false }
            || simd.StructuralChanges.Length > 0 || simd.Chains.Length > 0 || simd.Prefetched.Length > 0) return null;
        if (simd.Flat is { } flat)
            return StepText(flat.Setup, flat.Body + flat.Stores, simd.Columns) is { } text ? new(text, flat.Fields, false) : null;
        if (simd.Interleaves || simd.FlatOnly) return null;
        if (StepText(simd.Setup, simd.Body + simd.Stores, simd.Columns) is not { } whole) return null;
        return !simd.MaskedScalar && StepText(simd.Setup, simd.MaskedBody + simd.MaskedStores, simd.Columns) is { } masked && Unkept(masked) == whole
            ? new(masked, 1, true)
            : new(whole, 1, false);
    }

    /// <summary>
    /// <paramref name="text"/>, a loop's statements over <c>Kernel.V</c>, as a step's over the lane types, its setup's
    /// constants written where they are used; null when it reads anything else.
    /// </summary>
    private static string? StepText(string setup, string text, EquatableArray<string> columns)
    {
        foreach (var line in setup.Split('\n'))
        {
            if (line.Length == 0) continue;
            var constant = Constant.Match(line + "\n");
            if (!constant.Success || constant.Length != line.Length + 1) return null; // a uniform of the run
            text = Regex.Replace(text, $@"\b{constant.Groups[1].Value}\b", constant.Groups[2].Value.Replace("$", "$$"));
        }
        bool lanes = true; // every lane float or int: LF, LI
        string Lane(string type)
        {
            if (type is "float" or "int") return type == "float" ? "LF" : "LI";
            lanes = false;
            return "";
        }
        string Column(string k) => int.Parse(k) is var c && c >= 1 && c <= columns.Length ? Lane(columns[c - 1]) : Lane("");
        text = PassSelect.Replace(text, m => m.Groups[1].Value is "float" ? "LF.Keep<M>(passF, " : "LI.Keep<M>(pass, ");
        text = LaneLoad.Replace(text, m => $"{Column(m.Groups[1].Value)}.Load(ref e{m.Groups[1].Value}, i)");
        text = LaneStore.Replace(text, m => $"{m.Groups[1].Value}{Column(m.Groups[3].Value)}.Store({m.Groups[2].Value}, ref e{m.Groups[3].Value}, i);");
        text = LaneCreate.Replace(text, m => $"{Lane(m.Groups[1].Value)}.Create(");
        if (!lanes || text.IndexOf('\u0001') >= 0 || Code.Word(text, "__pass") >= 0) return null;
        if (!Regex.IsMatch(StepTokens.Replace(text, " "), @"^[\s+\-*/&|^~()=,;]*$")) return null;
        // the step inlines into each width's walk: within the IL locals the JIT inlines
        return Locals(text) <= InlinedLocals ? text : null;
    }

    private static readonly Regex PassSelect = new(Regex.Escape(Kernel.V) + @"\.ConditionalSelect\((?:" + Regex.Escape(Kernel.V) + @"\.As<int, (float|int)>\(__pass\)|__pass), ");
    private static readonly Regex LaneLoad = new(Regex.Escape(Kernel.V) + @"\.LoadUnsafe\(ref e(\d+), \(nuint\)i\)");
    private static readonly Regex LaneStore = new(@"(?m)^([ \t]*)" + Regex.Escape(Kernel.V) + @"\.StoreUnsafe\((.*), ref e(\d+), \(nuint\)i\);$");
    private static readonly Regex LaneCreate = new(Regex.Escape(Kernel.V) + @"\.Create<(\w+)>\(");

    /// <summary>What a step may name: the lane operations, its locals, columns and passing lanes, literals.</summary>
    private static readonly Regex StepTokens = new(
        @"\bL[FI]\.(?:Load|Store|Create|Keep<M>)|\b(?:var|ref|i|pass|passF|e\d+|__\w+)\b|\b(?:float|int)\.[A-Za-z]\w*|(?<![\w.])(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFdDmMuUlL]*");

    /// <summary>A masked step's text with each <c>Keep&lt;M&gt;(pass, value, old)</c> as its value: the whole run's.</summary>
    private static string Unkept(string text)
    {
        const string keep = ".Keep<M>(";
        for (int at = text.IndexOf(keep, System.StringComparison.Ordinal); at >= 2; at = text.IndexOf(keep, System.StringComparison.Ordinal))
        {
            int start = at - 2, open = at + keep.Length - 1;
            var commas = new List<int>();
            int depth = 0, close = -1;
            for (int k = open; k < text.Length && close < 0; k++)
                switch (text[k])
                {
                    case '(': depth++; break;
                    case ')': if (--depth == 0) close = k; break;
                    case ',' when depth == 1: commas.Add(k); break;
                }
            if (close < 0 || commas.Count != 2) return "";
            text = text.Substring(0, start) + text.Substring(commas[0] + 2, commas[1] - commas[0] - 2) + text.Substring(close + 1);
        }
        return text;
    }

    /// <summary>
    /// The run of a flat job in Execute: its column refs in the step's struct (<c>__Lanes</c>, a member of the job written
    /// with the whole run's Execute), Core's LaneKernel over it, and <c>i</c> where its vectors stopped (whole rows), from
    /// which Execute runs the method.
    /// </summary>
    private static void EmitLanes(StringBuilder sb, StringBuilder members, LaneStep step, Kernel.Code simd, string[] components, string indent, bool masked)
    {
        var named = Enumerable.Range(1, components.Length).Where(k => Names(step.Text, $"e{k}")).ToList();
        const string unsafeType = "global::System.Runtime.CompilerServices.Unsafe";
        sb.Append($"{indent}var __lanes = new __Lanes {{ {string.Join(", ", named.Select(k => $"e{k} = ref {unsafeType}.As<{components[k - 1]}, {simd.Columns[k - 1]}>(ref r{k})"))} }};\n");
        string run = masked ? "RunMasked(ref __lanes, pass, rows)"
            : step.Fields > 1 ? $"RunFlat(ref __lanes, rows * {step.Fields}, {step.Fields}) / {step.Fields}" // lanes, back to whole rows
            : "Run(ref __lanes, rows)";
        sb.Append($"{indent}nint i = global::EasyEcs.LaneKernel.{run};\n");
        if (masked) return;
        members.Append("\n        private ref struct __Lanes : global::EasyEcs.ILaneStep4\n        {\n");
        foreach (int k in named) members.Append($"            public ref {simd.Columns[k - 1]} e{k};\n");
        members.Append("\n            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
        members.Append("            void global::EasyEcs.ILaneStep4.Step<LF, LI, M>(nint i, LI pass, LF passF)\n            {\n");
        Indented(members, "                ", step.Text);
        members.Append("            }\n        }\n");
    }
}
