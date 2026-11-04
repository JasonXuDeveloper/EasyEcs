using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

// Element loops (Kernel.Loops.cs): a method whose loops over spans have vector forms is copied into the generated file
// (BodyCopy) with each such loop's vector form before it, in place of the UnsafeAccessor that reaches the method.
public sealed partial class SystemGenerator
{
    /// <summary>
    /// A method's element loops: <paramref name="Body"/>, the method's body as C# with the loops' vector forms (its
    /// helpers at the end), or "" when it is not copied; the using directives its extension calls need; one line per
    /// loop (for the schedule); the UnsafeAccessor externs through which the copy reaches the type's private static
    /// helpers and fields (BodyCopy.Accessors), named after <see cref="AccessorPrefix"/>. <paramref name="Cells"/>: the
    /// pass of a cell method (SystemGenerator.Cells.cs), <paramref name="Body"/> the whole method with its header.
    /// </summary>
    private sealed record ElementCode(string Body, EquatableArray<string> Usings, EquatableArray<string> Loops, string Accessors = "", bool Cells = false);

    /// <summary>The token EmitAccessor replaces with the accessor's name (<c>Cool_3</c>): the prefix of the copy's own accessors.</summary>
    internal const string AccessorPrefix = "__EasyEcsAccessorOf"; // an identifier: the copy is parsed with it

    /// <summary>The name of an accessor's parameter for the type, free of the method's parameter names (CS0100).</summary>
    private static string SelfName(IEnumerable<string> parameters)
    {
        string self = "owner";
        while (parameters.Contains(self)) self = "_" + self;
        return self;
    }

    private const string Unsafe = "global::System.Runtime.CompilerServices.Unsafe", Marshal = "global::System.Runtime.InteropServices.MemoryMarshal",
        NoInlining = "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]";

    /// <summary>
    /// The method's copy with its element loops' vector forms, or null when it has none: no loop of it vectorizes, or the
    /// method cannot be copied (generic, a value it returns, a tile, a name another file cannot reach, SimdMode.Off).
    /// <paramref name="loops"/> gets one line per loop either way.
    /// </summary>
    private static ElementCode? ElementsOf(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (Symbols.IsCellMethod(method)) return CellsOf(method, syntax, model, ct);
        var loops = new List<string>();
        var code = Copied(method, syntax, model, ct, loops);
        return loops.Count == 0 ? null : new ElementCode(code?.Body ?? "", code?.Usings ?? new([]), new(loops.ToArray()), code?.Accessors ?? "");
    }

    private static ElementCode? Copied(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct,
        List<string> loops)
    {
        if (syntax.Body == null || Symbols.Simd(method) == Symbols.SimdOff) return null;
        if (!syntax.Body.DescendantNodes().Any(static n => n is ForStatementSyntax or ForEachStatementSyntax)) return null;
        var found = Kernel.ElementLoops(method, syntax.Body, model, ct);
        foreach (var (loop, vector, reason) in found)
            if (vector != null || Kernel.WalksElements(loop, model, ct)) // a loop over no span is not meant for one (EECS055 leaves it too)
                loops.Add(vector != null ? $"loop '{LoopHeader(loop)}': vector ({(vector.Code.Flat != null ? "flat, " : "")}Vector512 or Vector<{vector.Code.Lane}>{(vector.Code.Cold is { } cold ? $"; the elements of a vector with a lane that takes a cold branch run as written: {cold}" : "")})"
                : $"loop '{LoopHeader(loop)}': scalar ({reason?.Text})");
        if (!found.Any(f => f.Vector != null)) return null;
        string? copyProblem = !method.ReturnsVoid ? "it returns a value"
            : method.IsGenericMethod || Symbols.ContainsTypeParameter(method.ContainingType) ? "it is generic"
            : method.Parameters.Any(p => Symbols.Classify(p, Symbols.IsRunner(method)) == Symbols.ParamKind.Tile) ? "it takes a tile"
            : method.PartialDefinitionPart != null || method.PartialImplementationPart != null ? "it is partial"
            : BodyCopy.Unreachable(syntax.Body, method, model, ct, out _, accessors: true);
        if (copyProblem != null)
        {
            for (int i = 0; i < loops.Count; i++)
                loops[i] = loops[i].Replace(": vector (", $": scalar (the method is not copied: {copyProblem}; it would run as vector (");
            return null;
        }

        var accessors = new BodyCopy.Accessors(SelfName(method.Parameters.Select(p => p.Name)), OwnerModel.From(method.ContainingType).AccessorParameter, AccessorPrefix);
        return new ElementCode(WithElementLoops(method, syntax.Body, model, found, syntax.SyntaxTree.FilePath, ct, accessors),
            new(BodyCopy.Usings(syntax).Distinct().OrderBy(u => u, System.StringComparer.Ordinal).ToArray()), new([]), accessors.Declarations);
    }

    /// <summary>
    /// <paramref name="block"/>, the body of a method or a lambda pass's lambda, copied (BodyCopy) with each loop of
    /// <paramref name="found"/> that has a vector form preceded by the call of its helper, the helpers at its end; with
    /// <c>#line</c> back to <paramref name="path"/> when it is not "".
    /// </summary>
    internal static string WithElementLoops(IMethodSymbol method, BlockSyntax block, SemanticModel model,
        List<(StatementSyntax Loop, Kernel.ElementLoop? Vector, Kernel.ScalarReason? Reason)> found, string path, System.Threading.CancellationToken ct,
        BodyCopy.Accessors? accessors = null)
    {
        var numbers = new Dictionary<SyntaxNode, (int K, Kernel.ElementLoop Loop)>();
        foreach (var (loop, vector, _) in found)
            if (vector != null) numbers[loop] = (numbers.Count, vector);
        bool lines = path.Length > 0;
        string Line(SyntaxNode node) => lines ? $"#line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} \"{path.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\n" : "";
        string hidden = lines ? "#line hidden\n" : "";
        bool apart = false;
        string? Replace(SyntaxNode original, SyntaxNode rewritten)
        {
            if (!numbers.TryGetValue(original, out var found)) return null;
            var (k, loop) = found;
            var call = new StringBuilder();
            string values = string.Concat(loop.Values.Select(v => (v.Sum ? ", ref " : ", ") + v.Name));
            if (rewritten is ForEachStatementSyntax each)
            {
                call.Append($"{{\n{hidden}var __span{k} = ({each.Expression.WithoutTrivia()}){loop.Streams[0].Span};\n");
                call.Append($"int __at{k} = {Whole(loop, $"__span{k}.Length")} ? (int)__Elements{k}(ref {Marshal}.GetReference(__span{k}), 0, __span{k}.Length, 0{values}) : 0;\n");
                call.Append(Line(original));
                call.Append(each.WithExpression(SyntaxFactory.ParseExpression($"__span{k}.Slice(__at{k})")).WithoutLeadingTrivia().WithoutTrailingTrivia().ToFullString());
                call.Append(" }");
                return call.ToString();
            }

            var loopFor = (ForStatementSyntax)rewritten;
            var variable = loopFor.Declaration!.Variables[0];
            string index = variable.Identifier.ValueText, end = $"({((BinaryExpressionSyntax)loopFor.Condition!).Right.WithoutTrivia()})";
            // Every stream's elements index + k .. end + k lie in its span (each comparison free of overflow: index >= 0,
            // end <= a length, |k| <= 64), so the vector form reads none past either end.
            var guard = new List<string> { $"{index} >= 0", Whole(loop, $"{end} - {index}") };
            foreach (var stream in loop.Streams)
            {
                if (stream.Offset < 0) guard.Add($"{index} >= {-stream.Offset}");
                guard.Add(stream.Offset > 0 ? $"{end} <= {stream.Span}.Length - {stream.Offset}" : $"{end}{Kernel.Shift(stream.Offset)} <= {stream.Span}.Length");
            }
            // A written span that overlaps another one at a shift would read its own writes in the loop as written. Two
            // streams of one span are both read (Kernel keeps a span written and read at another offset scalar).
            for (int a = 0; a < loop.Streams.Length; a++)
            for (int b = 0; b < loop.Streams.Length; b++)
                if (a != b && loop.Streams[a].Written && (b > a || !loop.Streams[b].Written) && loop.Streams[a].Span != loop.Streams[b].Span)
                {
                    guard.Add($"__Apart<{loop.Streams[a].Element}, {loop.Streams[b].Element}>({loop.Streams[a].Span}, {loop.Streams[b].Span})");
                    apart = true;
                }
            call.Append($"{{\n{hidden}{loopFor.Declaration.WithoutTrivia()};\n");
            // Each stream from its element at index + k, so the vector form walks 0 .. end - index of all of them (a ref
            // before a span's first element would point outside its object), and adds the index to its own lanes.
            string refs = string.Concat(loop.Streams.Select(s => $"ref {Unsafe}.Add(ref {Marshal}.GetReference({s.Span}), {index}{Kernel.Shift(s.Offset)}), "));
            call.Append($"if ({string.Join(" && ", guard.Distinct())}) {index} += (int)__Elements{k}({refs}0, {end} - {index}, {index}{values});\n");
            call.Append(Line(original));
            call.Append(loopFor.WithDeclaration(null).WithoutLeadingTrivia().WithoutTrailingTrivia().ToFullString());
            call.Append(" }");
            return call.ToString();
        }

        string body = BodyCopy.Copy(block, method, model, Replace, ct, accessors);
        var helpers = new StringBuilder();
        foreach (var (k, loop) in numbers.Values.OrderBy(n => n.K)) helpers.Append(ElementHelper(k, loop));
        if (apart)
            helpers.Append($"static bool __Apart<A, B>(global::System.ReadOnlySpan<A> a, global::System.ReadOnlySpan<B> b)\n{{\n"
                + $"    nint d = {Unsafe}.ByteOffset(ref {Unsafe}.As<A, byte>(ref {Unsafe}.AsRef(in {Marshal}.GetReference(a))), ref {Unsafe}.As<B, byte>(ref {Unsafe}.AsRef(in {Marshal}.GetReference(b))));\n"
                + $"    return d == 0 && {Unsafe}.SizeOf<A>() == {Unsafe}.SizeOf<B>() || d >= (nint)a.Length * {Unsafe}.SizeOf<A>() || -d >= (nint)b.Length * {Unsafe}.SizeOf<B>();\n}}\n");
        int close = body.LastIndexOf('}');
        body = body.Substring(0, close) + hidden + helpers + body.Substring(close);
        return (lines ? Line(block) : "") + body.TrimStart(' ', '\t') + (lines ? "\n#line default" : "");
    }

    /// <summary>
    /// The loop's header as written (<c>for (int i = 0; i &lt; s.Length; i++)</c>), naming it in the schedule: text,
    /// not a position, so an edit elsewhere in the file leaves the method's model as it was (the generator's cache).
    /// </summary>
    private static string LoopHeader(StatementSyntax loop)
    {
        string header = loop switch
        {
            ForStatementSyntax f => $"for ({f.Declaration}{string.Join(", ", f.Initializers)}; {f.Condition}; {string.Join(", ", f.Incrementors)})",
            ForEachStatementSyntax e => $"foreach ({e.Type} {e.Identifier.ValueText} in {e.Expression})",
            _ => loop.ToString(),
        };
        header = System.Text.RegularExpressions.Regex.Replace(header, @"\s+", " ");
        return header.Length > 80 ? header.Substring(0, 77) + "..." : header;
    }

    /// <summary>The loop's keyword, where EECS055 points.</summary>
    internal static Location LoopKeyword(StatementSyntax loop) =>
        loop is ForStatementSyntax f ? f.ForKeyword.GetLocation() : ((ForEachStatementSyntax)loop).ForEachKeyword.GetLocation();

    /// <summary>
    /// The vector form of element loop <paramref name="k"/>: a local function of the copy taking each span's first
    /// element, the index to start at and the end, and the loop's values; it runs whole vectors (Vector512 where the
    /// hardware has it, then Vector&lt;T&gt;) and returns the index of the first element it left, which the loop as
    /// written then runs. A vector with a lane that would throw or take a cold branch is left to the loop as written,
    /// with the elements after it. A JIT root of its own (NoInlining, D77's rule for loop routines): the copy stays as
    /// small as the method and inlines into its job like it, and a span shorter than a vector never calls it.
    /// </summary>
    private static string ElementHelper(int k, Kernel.ElementLoop loop)
    {
        var simd = loop.Code;
        var sb = new StringBuilder();
        var parameters = new List<string>();
        for (int s = 0; s < loop.Streams.Length; s++) parameters.Add($"ref {loop.Streams[s].Element} r{s + 1}");
        parameters.Add("nint i");
        parameters.Add("nint rows");
        parameters.Add("int at");
        for (int v = 0; v < loop.Values.Length; v++) parameters.Add($"{(loop.Values[v].Sum ? "ref " : "")}{loop.Values[v].Type} x{loop.Streams.Length + v}");
        sb.Append(NoInlining).Append('\n');
        sb.Append($"static nint __Elements{k}({string.Join(", ", parameters)})\n{{\n");
        var text = new StringBuilder();
        for (int s = 1; s <= loop.Streams.Length; s++)
            text.Append($"ref {simd.Columns[s - 1]} e{s} = ref {Unsafe}.As<{loop.Streams[s - 1].Element}, {simd.Columns[s - 1]}>(ref r{s});\n");
        text.Append(ElementLoops(loop, false, "return i;"));
        text.Append("return i;\n");
        sb.Append(Indent(text.ToString())).Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The loops of an element loop's vector form over streams <c>e1</c>.. from index <c>i</c> to <c>rows</c> (<c>at</c>
    /// the index of element 0, its values <c>x{n}</c>), leaving in <c>i</c> the first element they did not run;
    /// <paramref name="bail"/> leaves at a vector the loop as written must run. <paramref name="with128"/> adds a
    /// Vector128 loop after Vector&lt;T&gt;'s.
    /// </summary>
    private static string ElementLoops(Kernel.ElementLoop loop, bool with128, string bail, Hoisted? hoist = null)
    {
        var simd = loop.Code;
        var text = new StringBuilder();
        if (hoist != null && !Hoists(simd)) hoist = null;
        if (simd.Flat is { } flat)
        {
            text.Append($"nint lanes = rows * {flat.Fields};\ni *= {flat.Fields};\n");
            foreach (var l in FlatLoops(flat, simd)) text.Append($"if ({l.Guard})\n{{\n").Append(Indent(l.Body)).Append("}\n");
            text.Append($"i /= {flat.Fields};\n");
        }
        else
        {
            string vector = $"{Kernel.V}<{simd.Lane}>";
            var loopText = new StringBuilder();
            var accumulators = new StringBuilder();
            foreach (var line in simd.Setup.Split('\n'))
                if (line.Length > 0) (hoist != null && Accumulator.IsMatch(line) ? accumulators : loopText).Append(line).Append('\n');
            Paired(loopText, simd, vector, loop.Streams.Length); // a float sum, Min or Max: two chains
            loopText.Append($"for (nint last = rows - {vector}.Count; i <= last; i += {vector}.Count)\n{{\n");
            loopText.Append(Indent(Kernel.Unloaded(simd.Body, simd.Columns, simd.Streamed))); // what it only writes: not loaded
            // A sum's lanes fold into its variable when the vectors stop: after the loop, or at a bail with the vector's
            // own lanes set back (the loop as written then adds that vector's elements).
            if (simd.Bails)
                loopText.Append("    if (__bail)\n    {\n").Append(Indent(Indent(simd.Restore + simd.Fold)))
                    .Append($"        {bail} // the loop as written runs this vector's elements and the rest\n    }}\n");
            loopText.Append(Indent(simd.Stores)).Append("}\n");
            if (hoist == null) loopText.Append(simd.Fold);
            Constants(loopText);
            string body = loopText.Replace(Kernel.Row, "(at + (int)i)").Replace(Kernel.Visited, "ulong.MaxValue").ToString();
            string guard = $"{Hardware(simd)} && rows - i >= {vector}.Count";
            // Each width's text, and with a hoist its accumulators and fold, named apart per width (they share the pass's scope).
            void Width(System.Func<string, string> of, string tag)
            {
                if (hoist == null)
                {
                    text.Append($"if ({of(guard)})\n{{\n").Append(Indent(of(body))).Append("}\n");
                    return;
                }
                text.Append($"if ({of(guard)})\n{{\n").Append(Indent(Tagged(of(body), tag))).Append("}\n");
                hoist.Setup.Append(Tagged(of(accumulators.ToString()), tag));
                hoist.Fold.Append($"if ({of(Hardware(simd))} && {hoist.Count} >= {of(vector)}.Count)\n{{\n").Append(Indent(Tagged(of(simd.Fold), tag))).Append("}\n");
            }
            Width(s => Wide(s, null), "w");
            Width(s => Narrow(s, null), "n");
            // Vector128 after Vector<T>: a row of a cell method narrower than Vector<T> (16 bytes of a tile's row on AVX2);
            // only for text that calls no helper of one width (LaneMath, Interleaved: their Vector<T> and 512 forms).
            if (with128 && !OfOneWidth.Any(h => body.IndexOf(h, System.StringComparison.Ordinal) >= 0)) Width(Width128, "q");
        }
        return text.ToString();
    }

    /// <summary>
    /// Where a cell pass puts a reduction's accumulators and fold: once per tile around the walk (<see cref="Setup"/>
    /// before it, <see cref="Fold"/> after it, each width under its own guard, <see cref="Count"/> the row's cells), not
    /// once per row. A row of a voxel chunk is one vector of 16 bytes, so the per-row start of nine two-byte fields and
    /// their fold (14 horizontal sums, four Min / Max lane by lane) cost more than the row's arithmetic.
    /// </summary>
    internal sealed class Hoisted(string count)
    {
        public readonly StringBuilder Setup = new(), Fold = new();
        public readonly string Count = count;
    }

    /// <summary>
    /// Whether a loop's accumulators may live across its runs: integer sums wrap and Min / Max take any order, so a
    /// fold per tile equals one per row; a float sum is FloatMode.Fast's (any order). Not with a bail (it folds and
    /// restores mid-loop) or paired chains (they copy the accumulator per run).
    /// </summary>
    private static bool Hoists(Kernel.Code simd) =>
        simd.Flat == null && !simd.Bails && simd.Chains.Length == 0 && simd.Fold.Length > 0;

    /// <summary>A setup line declaring a reduction's identity or one of its accumulators (Kernel.Reduced / Summed).</summary>
    private static readonly System.Text.RegularExpressions.Regex Accumulator = new(@"^var __(?:id|a)\d+[_ ]");

    /// <summary>The reduction names of one width's text (<c>__a3_0</c>, <c>__id3</c>) given that width's tag.</summary>
    private static string Tagged(string text, string tag) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\b__(id|a)(\d+)(?!\d)", "__$1$2" + tag);

    /// <summary>The Vector128 loop's text: <see cref="Narrow"/>'s, the vector type Vector128.</summary>
    private static string Width128(string s) => Lines(s, Kernel.Mark128, Kernel.Mark512).Replace(WideGuard, "")
        .Replace(Kernel.SquareRoot, Vector128 + ".Sqrt(").Replace(Kernel.V, Vector128).Replace(Kernel.LanesForm, "");

    private const string Vector128 = "global::System.Runtime.Intrinsics.Vector128";

    /// <summary>
    /// What a vector loop's text calls with Vector&lt;T&gt; and Vector512 forms only (Interleaved,
    /// Lanes, the lookups' gathers, the marked helpers): such a loop gets no Vector128 copy.
    /// </summary>
    private static readonly string[] OfOneWidth =
        [Accelerated, Kernel.LanesForm, "global::EasyEcs.Interleaved.", "global::EasyEcs.Lanes.", "global::EasyEcs.LookupLanes."];

    /// <summary>
    /// Whether <paramref name="count"/> elements hold one whole vector of the narrowest width the loop runs: shorter
    /// spans skip the call of its vector form, so a method over short buffers costs what the loop as written costs.
    /// </summary>
    private static string Whole(Kernel.ElementLoop loop, string count) =>
        $"({count}){(loop.Code.Flat is { } flat ? $" * {flat.Fields}" : "")} >= global::System.Numerics.Vector<{loop.Code.Lane}>.Count";

    private static string Indent(string text)
    {
        var sb = new StringBuilder();
        Indented(sb, "    ", text);
        return sb.ToString();
    }
}
