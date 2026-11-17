using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEcs.Generator;

// The lane kernel's form of a flat job (docs/plans/designs/lane-kernel.md, owner L33): the job's vector body written once
// as a walk over Core's lane types (ILane: Lane512 / Lane256 / Lane128), its uniforms and constants broadcast before its loop, the
// widths, the masked words and the stopping row Core's LaneKernel's; every other shape keeps the loops
// SystemGenerator.Loops.cs writes per width.
public sealed partial class SystemGenerator
{
    /// <summary>
    /// A flat job's step: its body over the lane types <c>LF</c> (float) and <c>LI</c> (int), unindented.
    /// </summary>
    /// <param name="Text">The step's statements: loads, the method's arithmetic, stores; a masked word's writes through <c>Keep&lt;M&gt;</c>.</param>
    /// <param name="Fields">Lanes per row: a flat loop's fields (each alike), else 1.</param>
    /// <param name="Masked">Masked words take the step too (its writes kept by the passing lanes); else they keep their loops.</param>
    /// <param name="Uniforms">The run's uniform scalars: read in Execute into the walk's struct, broadcast before its loop.</param>
    /// <param name="Constants">The step's constant vectors (<c>LF.Create(0.5f)</c>), named <c>__k0</c>, <c>__k1</c>, ... in
    /// <paramref name="Text"/>: broadcast before the walk's loop too.</param>
    internal sealed record LaneStep(string Text, int Fields, bool Masked, IReadOnlyList<Uniform> Uniforms, IReadOnlyList<string> Constants);

    /// <summary>A uniform scalar of the run: the step's name for its broadcast, its lane type (float or int), its C# in Execute.</summary>
    internal readonly record struct Uniform(string Name, string Type, string Value);

    /// <summary>
    /// The lane kernel's step of <paramref name="simd"/>, or null when the job is not flat. Flat is the shape the kernel
    /// measured level on (KernelBenchmark's Integrate, two rounds, 0 B):
    /// <list type="bullet">
    /// <item>lanes of 4 bytes, float or int, of components of one field (or a flat loop over several, each alike);</item>
    /// <item>straight-line lane arithmetic (<c>+ - * / &amp; | ^ ~</c>, unary <c>-</c>) of the row's loads and constants:
    /// no condition (a mask kept across statements), no other call;</item>
    /// <item>no reduction, structural change, record, bail, gather, draw, lookup prefetch, streamed column, wide or
    /// interleaved component;</item>
    /// <item>constants and uniform scalars of float or int (a singleton's dt, a parameter's value) as its only setup, each
    /// broadcast before the walk's loop.</item>
    /// </list>
    /// Masked words take the step when their text is the whole run's with each write kept by the passing lanes.
    /// </summary>
    internal static LaneStep? LaneStepOf(Kernel.Code? simd)
    {
        if (simd is not { Size: 4, Bails: false, Restore.Length: 0, Fold.Length: 0, VectorTail: false, Gathers: false, Draws: false,
                Wide: null, Statics.Length: 0, Pairs: false, IndexTop: 0, Cold: null, Streams: false }
            || simd.StructuralChanges.Length > 0 || simd.Chains.Length > 0 || simd.Prefetched.Length > 0) return null;
        var uniforms = new List<Uniform>();
        var constants = new List<string>();
        if (simd.Flat is { } flat)
            return StepText(flat.Setup, flat.Body + flat.Stores, simd.Columns, uniforms, constants) is { } text && Fits(text, uniforms, constants, false)
                ? new(text, flat.Fields, false, uniforms, constants) : null;
        if (simd.Interleaves || simd.FlatOnly) return null;
        if (StepText(simd.Setup, simd.Body + simd.Stores, simd.Columns, uniforms, constants) is not { } whole) return null;
        var maskedConstants = new List<string>();
        if (!simd.MaskedScalar && StepText(simd.Setup, simd.MaskedBody + simd.MaskedStores, simd.Columns, [], maskedConstants) is { } masked
            && Unkept(masked) == whole && Fits(masked, uniforms, maskedConstants, true)) return new(masked, 1, true, uniforms, maskedConstants);
        return Fits(whole, uniforms, constants, false) ? new(whole, 1, false, uniforms, constants) : null;
    }

    /// <summary>Whether the walk of <paramref name="text"/> stays within the IL locals the JIT inlines (into each width's Walk).</summary>
    private static bool Fits(string text, IReadOnlyList<Uniform> uniforms, IReadOnlyList<string> constants, bool masked) =>
        Locals(WalkText(text, uniforms, constants, masked)) <= InlinedLocals;

    /// <summary>
    /// <paramref name="text"/>, a loop's statements over <c>Kernel.V</c>, as a step's over the lane types, its setup's
    /// uniform scalars added to <paramref name="uniforms"/> and its constant vectors to <paramref name="constants"/> (each
    /// named <c>__k</c> and its index in the text); null when it reads anything else.
    /// </summary>
    private static string? StepText(string setup, string text, EquatableArray<string> columns, List<Uniform> uniforms, List<string> constants)
    {
        var aliases = new Dictionary<string, string>();
        foreach (var line in setup.Split('\n'))
        {
            if (line.Length == 0) continue;
            var constant = Constant.Match(line + "\n");
            if (constant.Success && constant.Length == line.Length + 1)
            {
                text = Regex.Replace(text, $@"\b{constant.Groups[1].Value}\b", constant.Groups[2].Value.Replace("$", "$$"));
                continue;
            }
            // a reference a uniform reads through (a singleton's value): written into the uniforms that name it
            var alias = AliasLine.Match(line);
            if (alias.Success)
            {
                aliases[alias.Groups[1].Value] = alias.Groups[2].Value;
                continue;
            }
            // a uniform of the run: a scalar of Execute's scope that names no other line of the setup
            var uniform = UniformLine.Match(line);
            if (!uniform.Success) return null;
            string value = uniform.Groups[3].Value;
            foreach (var pair in aliases) value = Regex.Replace(value, $@"\b{pair.Key}\b", pair.Value.Replace("$", "$$"));
            if (value.Contains(Kernel.V) || UniformNames.IsMatch(value)) return null;
            uniforms.Add(new(uniform.Groups[1].Value, uniform.Groups[2].Value, value));
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
        // each constant a local of the walk, broadcast before its loop: written in place, the JIT keeps it in a register
        // only when its CSE takes it, and where it does not (Haul's walk under SSE) each vector reloads it from memory, a
        // fourth load a vector where the emitted loop's three bound it (lane-kernel.md, package F, "Constants")
        text = LaneConstant.Replace(text, m =>
        {
            int k = constants.IndexOf(m.Value);
            if (k < 0) constants.Add(m.Value);
            return $"__k{(k < 0 ? constants.Count - 1 : k)}";
        });
        // the step inlines into each width's walk: within the IL locals the JIT inlines
        return Locals(text) <= InlinedLocals ? text : null;
    }

    private static readonly Regex UniformLine = new(@"^var (__u\d+) = " + Regex.Escape(Kernel.V) + @"\.Create<(float|int)>\((.+)\);$");

    /// <summary>A reference of Execute's scope the uniforms read through (<c>ref readonly T __p3 = ref s3.Value;</c>): a field path.</summary>
    private static readonly Regex AliasLine = new(@"^ref readonly [\w.:<>, ]+ (__p\d+) = ref ([\w.]+);$");

    /// <summary>What a uniform's value may not name: another line of the setup, the row, a column.</summary>
    private static readonly Regex UniformNames = new(@"\b(?:__\w+|i|e\d+)\b");

    private static readonly Regex PassSelect = new(Regex.Escape(Kernel.V) + @"\.ConditionalSelect\((?:" + Regex.Escape(Kernel.V) + @"\.As<int, (float|int)>\(__pass\)|__pass), ");
    private static readonly Regex LaneLoad = new(Regex.Escape(Kernel.V) + @"\.LoadUnsafe\(ref e(\d+), \(nuint\)i\)");
    private static readonly Regex LaneStore = new(@"(?m)^([ \t]*)" + Regex.Escape(Kernel.V) + @"\.StoreUnsafe\((.*), ref e(\d+), \(nuint\)i\);$");
    private static readonly Regex LaneCreate = new(Regex.Escape(Kernel.V) + @"\.Create<(\w+)>\(");

    /// <summary>A constant vector of a step: a literal's broadcast or a named constant's (<c>float.Epsilon</c>).</summary>
    private static readonly Regex LaneConstant = new(@"\bL[FI]\.Create\((?<p>\()?-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFdDmMuUlL]*(?(p)\))\)|\bL[FI]\.Create\((?:float|int)\.[A-Za-z]\w*\)");

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
    /// The walk of a step's <paramref name="text"/> over one width (<c>ILaneWalk4.Walk</c>'s body): the uniforms and
    /// constants broadcast before its loop (the JIT leaves a broadcast written in the loop there once the loop holds many
    /// vector locals, and a constant in memory where its CSE declines it: lane-kernel.md, package F), then the vectors
    /// from <c>i</c> to <c>end</c>, a masked word's vectors with no passing row skipped.
    /// </summary>
    private static string WalkText(string text, IReadOnlyList<Uniform> uniforms, IReadOnlyList<string> constants, bool masked)
    {
        var walk = new StringBuilder();
        foreach (var u in uniforms) walk.Append($"var {u.Name} = {(u.Type == "float" ? "LF" : "LI")}.Create(this.{u.Name});\n");
        for (int k = 0; k < constants.Count; k++) walk.Append($"var __k{k} = {constants[k]};\n");
        walk.Append("for (; i < end; i += LF.Count)\n{\n");
        if (masked)
        {
            walk.Append("    if (!global::EasyEcs.LaneKernel.Lanes<LI, M>(word, i, out var pass)) continue;\n");
            if (Names(text, "passF")) walk.Append("    var passF = LI.As<LF, float>(pass);\n");
        }
        foreach (var line in text.Split('\n')) if (line.Length > 0) walk.Append("    ").Append(line).Append('\n');
        walk.Append("}\n");
        return walk.ToString();
    }

    /// <summary>
    /// The lane operations of a step's walk: each lane member it names (a load, a store, a kept write), each operator,
    /// each constant and uniform it broadcasts. The JIT imports each as an inlinee, with temporaries of its own, at each
    /// width and each call site of the runner the walk is inlined into, so they measure what the walk takes of the
    /// runner's locals.
    /// </summary>
    internal static int LaneOperations(LaneStep step) =>
        LaneOperation.Matches(step.Text).Count + step.Constants.Count + step.Uniforms.Count;

    private static readonly Regex LaneOperation = new(@"\bL[FI]\.\w+|[+\-*/&|^~]");

    /// <summary>
    /// The most lane operations (<see cref="LaneOperations"/>) of a whole run's walk the runner inlines (Core's
    /// <c>LaneKernel.RunInlined</c>) instead of calling it once per run (<c>LaneKernel.Run</c>, a <c>NoInlining</c> walk), with
    /// at most <see cref="InlinedWalkComponents"/> components. The call costs about 20 cycles a run where the job is over
    /// 16 bytes (passed on the stack: Haul, 2-5 % of a 1,000-row run). A walk inlined lands at each of the runner's three
    /// call sites of Execute, three widths each, and every inlined lane operation brings temporaries: past the edge the
    /// runner reaches the 512 locals the JIT inlines into one method ("too many locals") or its time budget, and lane
    /// operations, the walk itself or the scalar tail stay calls. Measured over a sweep of flat methods of 1 to 9
    /// components (lane-kernel.md, package F, "The call per run"): with AVX-512 the first call appears at 21 operations
    /// for 5 and 6 components (23-25 for 1 to 4), at 17 for 7 to 9; with 256 bits at 26 and more; under SSE4.1 none.
    /// </summary>
    internal const int InlinedWalkOperations = 16;

    /// <summary>
    /// The most components of a job whose walk the runner inlines (<see cref="InlinedWalkOperations"/>): each adds its
    /// column's refs, span and stamp at each call site, and from 7 the runner leaves its scalar tail or lane operations as
    /// calls with the smallest walk.
    /// </summary>
    internal const int InlinedWalkComponents = 6;

    /// <summary>Whether the runner inlines the whole run's walk of <paramref name="step"/> over <paramref name="components"/> components.</summary>
    internal static bool InlinesWalk(LaneStep step, int components) =>
        components <= InlinedWalkComponents && LaneOperations(step) <= InlinedWalkOperations;

    /// <summary>
    /// The run of a flat job in Execute: its column refs and uniforms in the walk's struct (<c>__Lanes</c>, a member of
    /// the job written with the whole run's Execute), Core's LaneKernel over it, and <c>i</c> where its vectors stopped
    /// (whole rows), from which Execute runs the method.
    /// </summary>
    private static void EmitLanes(StringBuilder sb, StringBuilder members, LaneStep step, Kernel.Code simd, string[] components, string indent, bool masked)
    {
        var named = Enumerable.Range(1, components.Length).Where(k => Names(step.Text, $"e{k}")).ToList();
        const string unsafeType = "global::System.Runtime.CompilerServices.Unsafe";
        var fields = named.Select(k => $"e{k} = ref {unsafeType}.As<{components[k - 1]}, {simd.Columns[k - 1]}>(ref r{k})")
            .Concat(step.Uniforms.Select(u => $"{u.Name} = {u.Value}"));
        sb.Append($"{indent}var __lanes = new __Lanes {{ {string.Join(", ", fields)} }};\n");
        string inlined = InlinesWalk(step, components.Length) ? "Inlined" : "";
        string run = masked ? "RunMasked(ref __lanes, pass, rows)"
            : step.Fields > 1 ? $"RunFlat{inlined}(ref __lanes, rows * {step.Fields}, {step.Fields}) / {step.Fields}" // lanes, back to whole rows
            : $"Run{inlined}(ref __lanes, rows)";
        sb.Append($"{indent}nint i = global::EasyEcs.LaneKernel.{run};\n");
        if (masked) return;
        members.Append("\n        private ref struct __Lanes : global::EasyEcs.ILaneWalk4\n        {\n");
        foreach (int k in named) members.Append($"            public ref {simd.Columns[k - 1]} e{k};\n");
        foreach (var u in step.Uniforms) members.Append($"            public {u.Type} {u.Name};\n");
        members.Append("\n            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
        members.Append("            void global::EasyEcs.ILaneWalk4.Walk<LF, LI, M>(nint i, nint end, ulong word)\n            {\n");
        Indented(members, "                ", WalkText(step.Text, step.Uniforms, step.Constants, step.Masked));
        members.Append("            }\n        }\n");
    }
}
