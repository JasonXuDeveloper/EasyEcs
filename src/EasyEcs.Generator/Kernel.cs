using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A [ForEach] method written for one row, translated into a <c>System.Numerics.Vector&lt;T&gt;</c> loop over its runs of
/// rows (the idea of Burst and ISPC: the method is the kernel of one lane, the generator writes the vector loop). A
/// method qualifies when every component it takes holds 1 to 4 fields of primitive lane types of one size (float, int
/// and uint, or double, long and ulong: one lane count; a component of several fields is split into one vector per
/// field, <c>EasyEcs.Interleaved</c>), and its body is arithmetic, comparisons, patterns, <c>?:</c>, <c>if</c> /
/// <c>else</c>, <c>return</c>, conversions between those types and <c>Math</c> / <c>MathF</c> functions with an exact
/// vector counterpart, on those fields, locals and uniform values (constants, static readonly fields, singletons and
/// context values, broadcast once per run). Branches become masks: an assignment under a condition is a
/// <c>ConditionalSelect</c>, a <c>return</c> clears its lanes. The rows left after the last whole vector run the method
/// itself, so the results are those of the scalar loop, bit for bit: every operation used is the same IEEE operation per
/// lane (no reassociation, no fused multiply-add the method did not write), integer arithmetic wraps both ways, and
/// conversions saturate like C#'s. An operation that would throw for some lane (<c>Math.Clamp</c> with its min above its
/// max, an integer divided by 0, <c>Math.Abs(int.MinValue)</c>) sends the rows of its vector to the method itself.
/// Anything else (other calls, checked arithmetic, the integer division of 8-byte lanes by what is not a power of two)
/// leaves the method scalar, as written. Lanes of 1 and 2 bytes (byte, sbyte, short,
/// ushort), whose arithmetic C# computes in int, are Kernel.Narrow.cs.
/// </summary>
internal sealed partial class Kernel
{
    /// <param name="Columns">The lane type of each component's column (its first field), in job order (<c>float</c>).</param>
    /// <param name="Setup">Statements before the vector loop: the uniform values and their broadcasts.</param>
    /// <param name="Body">The vector loop's body up to the stores: loads, the translated method.</param>
    /// <param name="Stores">The stores of the components the method writes.</param>
    /// <param name="MaskedBody">
    /// <paramref name="Body"/> for a word of rows of which some pass (<c>__pass</c>, a lane mask): the loaded values of
    /// the components the method writes are kept too.
    /// </param>
    /// <param name="MaskedStores">The stores of a masked body: the lanes <c>__pass</c> leaves out get the values loaded.</param>
    /// <param name="Restore">Statements that set the reductions' lane accumulators back to before this vector (a bail).</param>
    /// <param name="Fold">
    /// Statements after the loop: each reduction's lanes combined into its partial result in lane order
    /// (<c>Reduce&lt;T&gt;.Combine</c>).
    /// </param>
    /// <param name="Size">The lanes' size in bytes: 1, 2, 4 or 8.</param>
    /// <param name="Mask">The element type of a lane mask: <c>sbyte</c>, <c>short</c>, <c>int</c> or <c>long</c>.</param>
    /// <param name="Lane">
    /// The lane type of the narrowest columns, whose vector count is the loop's step (a column of lanes twice as wide is
    /// read as two vectors per step).
    /// </param>
    /// <param name="Interleaves">A component has several fields: the loop needs <c>EasyEcs.Interleaved.IsAccelerated</c>.</param>
    /// <param name="Bails">
    /// The body sets <c>__bail</c> when a lane would throw: the rows of that vector then run the method itself, before any
    /// store.
    /// </param>
    /// <param name="Flat">
    /// The loop over flat lanes when the method treats each field alike (<see cref="FlatCode"/>), else null: words of rows
    /// of which only some pass still take the loop above.
    /// </param>
    /// <param name="Cold">The reason of the first cold branch (what the loop cannot express under a condition it computes:
    /// a vector with a lane that takes it runs the method), or null.</param>
    /// <param name="Wide">
    /// A component is wide (Kernel.Wide.cs): the job's layout checks the loop also needs (<c>__layout1 &amp;&amp; ...</c>),
    /// in the forms that pay (<see cref="Code.Wide512"/>, <see cref="Code.Wide128"/>; masked words run the method); else null.
    /// </param>
    /// <param name="Statics">The job's static members: the permutes' and table lookups' indexes and the layout checks.</param>
    internal sealed record Code(EquatableArray<string> Columns, string Setup, string Body, string Stores, string MaskedBody,
        string MaskedStores, string Restore, string Fold, int Size, string Mask, string Lane, bool Interleaves, bool Bails, FlatCode? Flat, string? Cold = null,
        string? Wide = null, string Statics = "")
    {
        /// <summary>
        /// The structural change the loop marks rows for, applied after the pass ("Set global::Dead", "Destroy"; at
        /// most one), with the job's fields <c>d0</c> (the marks) and <c>v0</c> (a Set's value): Kernel.Structural.cs.
        /// </summary>
        public EquatableArray<string> StructuralChanges { get; init; } = new([]);

        /// <summary>
        /// Every row must take the vector form (it marks rows or emits records, which the method itself would do at
        /// once): the rows past the last whole vector run it through a copy, and hardware without the loop's vectors
        /// runs the method's own job.
        /// </summary>
        public bool VectorTail { get; init; }

        /// <summary>The job columns (1-based) the loop writes: a tail copies them back.</summary>
        public EquatableArray<int> Written { get; init; } = new([]);

        /// <summary>The loop gathers other entities' fields (Kernel.Lookups.cs): it runs where <c>LookupLanes.IsAccelerated</c>.</summary>
        public bool Gathers { get; init; }

        /// <summary>
        /// The reductions' accumulators that carry a chain from one vector to the next as long as a floating-point add,
        /// Min or Max (3-4 cycles, against 1 for integers), as <c>name|op</c> (<c>__a1_0|+</c>, <c>__a2_1|Min</c>):
        /// the loop over whole vectors runs two vectors per iteration into two sets of them, which halves the chain
        /// (<c>SystemGenerator.VectorLoops</c>). A float sum is here only under FloatMode.Fast, which allows another
        /// order of its rows; Min and Max give the same result in any order.
        /// </summary>
        public EquatableArray<string> Chains { get; init; } = new([]);

        /// <summary>
        /// The Lookup parameters whose chases the loop prefetches ahead of each vector (Kernel.Lookups.cs) when the job's
        /// <c>f{i}</c> says the pass is past the last-level cache (<c>Lookup&lt;T&gt;.PrefetchesPastLastLevel</c>, set once per pass).
        /// </summary>
        public EquatableArray<int> Prefetched { get; init; } = new([]);

        /// <summary>
        /// The job columns (1-based) the loop over whole vectors writes without reading (Kernel.Streams.cs): a run past
        /// the last-level cache streams them (<c>StreamingStores.Pays</c>, once per run). Only for a loop that runs every
        /// vector alike: no bail, gather, wide component, marked rows, records, reduction or Maybe&lt;T&gt; (its column is
        /// a ref struct of the run, which a method of the job cannot take with the column refs).
        /// </summary>
        public EquatableArray<int> Streamed { get; init; } = new([]);

        /// <summary>The loop streams a column past the last-level cache: its own, or its flat loop's.</summary>
        public bool Streams => Streamed.Length > 0 || Flat is { Streamed.Length: > 0 };

        /// <summary>
        /// A wide component's AVX-512 form pays (its lines start with <see cref="Mark512"/>): the Vector512 loop runs it
        /// where <c>Avx512F.IsSupported</c>.
        /// </summary>
        public bool Wide512 { get; init; }

        /// <summary>
        /// A wide component's 128-bit form pays (its lines start with <see cref="Mark128"/>): the Vector&lt;T&gt; loop
        /// runs it where <c>Interleaved.IsWideAccelerated</c> (ARM64).
        /// </summary>
        public bool Wide128 { get; init; }

        /// <summary>
        /// The loop of a pair method (Kernel.Neighbors.cs): <see cref="Setup"/> before the clusters, <see cref="Body"/>
        /// the loads of a cluster's rows, <see cref="MaskedBody"/> one neighbor against them (its values broadcast
        /// first), <see cref="MaskedStores"/> the cluster's stores; the lanes are a cluster's (<c>__pass</c>).
        /// </summary>
        public bool Pairs { get; init; }
    }

    /// <summary>
    /// A loop that reads the columns as flat arrays of lanes, <paramref name="Fields"/> lanes per row (a row's fields are
    /// consecutive): what the method does to field i of each component it does to every field i alike, so every lane
    /// runs the same operations and no deinterleaving is needed. The loop variable <c>i</c> counts lanes.
    /// </summary>
    /// <param name="Setup">The uniform values and broadcasts.</param>
    /// <param name="Body">Loads (one vector per component) and the method's statements for one field.</param>
    /// <param name="Stores">The stores of the components the method writes.</param>
    /// <param name="Fields">The fields of every component: lanes per row.</param>
    internal sealed record FlatCode(string Setup, string Body, string Stores, int Fields)
    {
        /// <summary>The columns the flat loop writes without reading (<see cref="Code.Streamed"/>).</summary>
        public EquatableArray<int> Streamed { get; init; } = new([]);
    }

    /// <summary>
    /// The vector type the loop names, as a token: <c>SystemGenerator.VectorLoops</c> writes the Vector512 loop and the
    /// Vector&lt;T&gt; loop from one text by replacing this token alone (and <see cref="SquareRoot"/>), never text that
    /// names user types (a setup line holds <c>ref readonly global::System.Numerics.Vector3 __p1</c> as written). The
    /// prefix U+0001 occurs in no C# token outside a literal, and the loop's literals are formatted here.
    /// </summary>
    internal const string V = "\u0001Vector";

    /// <summary>The token of the lane masks' form: <c>Interleaved.Lanes32</c>, <c>Interleaved.Lanes32In512</c>.</summary>
    internal const string LanesForm = "\u0001In512";

    /// <summary>
    /// The token of the rows a loop visits, as bits (<c>MaybeColumn.StampPresent</c>): every row of a run
    /// (<c>ulong.MaxValue</c>), or a masked word's <c>pass</c>.
    /// </summary>
    internal const string Visited = "\u0001Visited";

    /// <summary>
    /// The token of the statements that run the rows up to <c>__end</c> through the method and go on to the vector after
    /// them: a lookup whose entities live in several tables takes it before the gathers it would waste (Kernel.Lookups.cs).
    /// </summary>
    internal const string RunRows = "\u0001RunRows";

    /// <summary>The reason a method reading other entities keeps its rows one by one unless it asks for the gathers.</summary>
    internal const string GathersOnRequest = "it reads other entities through a Lookup<T>: a vector loop gathers them, faster where a vector's targets share a table and up to 1.1 times as slow where they spread over many; SimdMode = SimdMode.Require asks for it";

    /// <summary>The token of the square root: Vector.SquareRoot, Vector512.Sqrt.</summary>
    internal const string SquareRoot = V + ".SquareRoot(";
    private const string Interleaved = "global::EasyEcs.Interleaved";

    /// <summary>The most fields a component of a vector loop has: the forms of <c>EasyEcs.Interleaved</c>.</summary>
    private const int MaxFields = 4;

    private int _size;      // bytes per lane: 1, 2, 4 or 8 (twice _base while a half of a pair is translated)
    private string _mask;   // the lane type of masks: sbyte, short, int or long (all bits set where true)
    /// <summary>The components: their index (job column k), their lane fields in memory order, and whether they are written.</summary>
    private readonly Dictionary<IParameterSymbol, (int Index, IFieldSymbol[] Fields, bool Ref)> _components = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IParameterSymbol, string> _uniformParams = new(SymbolEqualityComparer.Default);
    /// <summary>The Reduce&lt;T&gt; parameters: T's fields, how T.Combine combines each ("+", "Min", "Max"), the run's partial result.</summary>
    private readonly Dictionary<IParameterSymbol, Reduction> _reductions = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ILocalSymbol, string> _locals = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, string> _broadcasts = new();
    private readonly StringBuilder _setup = new();
    private readonly Body _body = new();
    private readonly HashSet<int> _written = new();
    /// <summary>
    /// The Maybe&lt;T&gt; / ReadOnlyMaybe&lt;T&gt; parameters: their position i (the job's column <c>m{i}</c>) and T's lane
    /// fields. Each is a lane mask <c>__has{i}</c> (the rows that have T) and one vector per field <c>__q{i}_{f}</c>.
    /// </summary>
    private readonly Dictionary<IParameterSymbol, (int Index, IFieldSymbol[] Fields)> _optionals = new(SymbolEqualityComparer.Default);
    /// <summary>The paths from each component (or optional's T) to its lanes, in lane order (<see cref="Paths"/>).</summary>
    private readonly Dictionary<IParameterSymbol, IFieldSymbol[][]> _paths = new(SymbolEqualityComparer.Default);
    private readonly HashSet<int> _writtenOptionals = new();
    /// <summary>
    /// What each mask atom (a mask temp, or one negated: <c>~__m3</c>) proves: the optionals whose component every lane
    /// it holds has. A read of <c>m.Value</c> under a mask that proves m needs no check.
    /// </summary>
    private readonly Dictionary<string, HashSet<int>> _implied = new();
    private readonly HashSet<string> _guarded = new();
    private string? _active;  // the lanes the expression being translated is evaluated for (null: every lane)
    private string? _alive; // lanes that have not returned yet, once a return under a condition cleared some
    private bool _bails;
    private string? _cold; // the reason of the first cold branch: what its rows run the method for
    private bool _fast; // FloatMode.Fast: float sums reordered and the vector approximations of Sin, Exp, Log and kin
    private int _temps;
    private int _flat = -1; // translating the statements of field _flat of a flat loop: that field is lane 0, others refused

    private Kernel(int size)
    {
        _size = _base = size;
        _mask = MaskOf(size);
    }

    /// <summary>
    /// The loop's statements as two texts: for every row of a vector, and for a word of rows of which some pass
    /// (<c>__pass</c>), where a write of what the loop stores (a component's lane, a reduction's accumulator, an optional's
    /// lane) also leaves the lanes that do not pass as they are: <c>__pass</c> joins its mask, so no store or fold
    /// selects them back afterwards (<see cref="Write"/>).
    /// </summary>
    private sealed class Body
    {
        private readonly StringBuilder _all = new(), _masked = new();

        public Body Append(string text)
        {
            _all.Append(text);
            _masked.Append(text);
            return this;
        }

        /// <summary>A statement written one way for whole vectors, another for masked words.</summary>
        public void Append(string all, string masked)
        {
            _all.Append(all);
            _masked.Append(masked);
        }

        public void Insert(int at, string text)
        {
            _all.Insert(at, text);
            _masked.Insert(at, text);
        }

        /// <summary>Both texts' lengths, to undo a branch (<see cref="Save"/>).</summary>
        public (int All, int Masked) Length
        {
            get => (_all.Length, _masked.Length);
            set => (_all.Length, _masked.Length) = value;
        }

        /// <summary>The text for masked words.</summary>
        public string Masked => _masked.ToString();

        public override string ToString() => _all.ToString();
    }

    /// <summary>The text <see cref="Live"/> replaces with the lanes a statement is computed for.</summary>
    private const string LiveLanes = "__lanes";

    /// <summary>
    /// A statement over the lanes it is computed for (<see cref="LiveLanes"/> in <paramref name="statement"/>): the lanes
    /// <paramref name="active"/> holds (null: every lane) of a whole vector, and of them the rows that pass
    /// (<c>__pass</c>) in a masked word. Written in place rather than kept in a local, since every local counts towards
    /// the 32 a runner inlines (<see cref="SystemGenerator.InlinedLocals"/>).
    /// </summary>
    private void Live(string? active, string statement) => _body.Append(
        statement.Replace(LiveLanes, active ?? $"{Vec(_mask)}.AllBitsSet"),
        statement.Replace(LiveLanes, active == null ? "__pass" : $"(__pass & {active})"));

    /// <summary>The lane type of masks of lanes of <paramref name="size"/> bytes.</summary>
    private static string MaskOf(int size) => size switch { 1 => "sbyte", 2 => "short", 4 => "int", _ => "long" };

    /// <param name="Index">The parameter's position: its accumulators are <c>__a{Index}_{field}</c>.</param>
    /// <param name="Type">T, the singleton reduced into.</param>
    /// <param name="Fields">T's fields, in declaration order.</param>
    /// <param name="Ops">How T.Combine combines each field: "+", "Min" or "Max".</param>
    /// <param name="Partial">The run's partial result (a <c>Reduce&lt;T&gt;</c> local of the job).</param>
    private sealed record Reduction(int Index, INamedTypeSymbol Type, IFieldSymbol[] Fields, string[] Ops, string Partial);

    /// <summary>
    /// Why the method runs one row at a time: the first construct the vector loop cannot express. <c>IsFixable</c> when
    /// the user can rewrite it without changing what the method computes, and the reason then names the rewrite;
    /// <c>SplitComponent</c> is the component whose used fields the reason advises to move into a component of their own.
    /// </summary>
    private sealed class Unsupported(string reason, bool isFixable = false, string? splitComponent = null) : System.Exception(reason)
    {
        public string Reason { get; } = reason;
        public bool IsFixable { get; } = isFixable;
        public string? SplitComponent { get; } = splitComponent;

        /// <summary>About the whole method (its structural change, its records), not one branch: no cold branch takes it.</summary>
        public bool IsWholeMethod { get; init; }
    }

    /// <summary>
    /// Why <see cref="Vectorize(IMethodSymbol, MethodDeclarationSyntax, SemanticModel, System.Func{int, Symbols.ParamKind, string}, System.Threading.CancellationToken, out ScalarReason?)"/>
    /// left a method scalar: <paramref name="Text"/>, what EECS055 and EECS056 report; <paramref name="IsFixable"/>,
    /// whether the user can rewrite the method for a vector loop (the text then names the rewrite: EECS055 warns) or the
    /// algorithm needs what stops it (EECS055 suggests); <paramref name="SplitComponent"/>, the fully qualified name of
    /// the component whose used fields the text advises to split out (EECS091 leaves that component to EECS055).
    /// </summary>
    public sealed record ScalarReason(string Text, bool IsFixable, string? SplitComponent);

    /// <summary>
    /// The vector loop of a [ForEach] method, or null with the reason it runs one row at a time
    /// (<paramref name="reason"/>: "component Position has 5 fields"). The one test of what qualifies: the generator
    /// emits the loop it returns, and the analyzer reports the reason (EECS055, EECS056), so a method the generator
    /// leaves scalar is exactly a method reported. <paramref name="fieldOf"/> names the job field holding parameter i
    /// (a singleton's <c>s{i}</c>, a context value's <c>x{i}</c>).
    /// </summary>
    public static Code? Vectorize(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct, out string? reason)
    {
        var code = Vectorize(method, syntax, model, fieldOf, ct, out ScalarReason? why);
        reason = why?.Text;
        return code;
    }

    /// <summary>The same test, with the kind of its reason (<see cref="ScalarReason"/>), for the analyzer.</summary>
    public static Code? Vectorize(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct, out ScalarReason? reason)
    {
        try
        {
            return VectorizeMethod(method, syntax, model, fieldOf, ct, out reason);
        }
        catch (System.Exception e) when (e is not System.OperationCanceledException)
        {
            reason = Failed(e);
            return null;
        }
    }

    /// <summary>
    /// The reason of a method whose translation threw: a bug of the generator, not of the method. The method keeps its
    /// row-by-row job and the analyzer reports this (the same call), so one method's failure never drops the files of
    /// the others (a generator that throws loses all of its output, and the build then fails on every name it writes).
    /// </summary>
    private static ScalarReason Failed(System.Exception e) => new(FailedTo("translate it", e), false, null);

    /// <summary>
    /// The reason of any stage that reads a method's body and threw (<paramref name="what"/>: what it failed to do): the
    /// stage falls back to the method's plain form and the analyzer reports this text through the same call.
    /// </summary>
    internal static string FailedTo(string what, System.Exception e) =>
        $"the generator failed to {what} ({e.GetType().Name}: {e.Message} Please report this as an EasyEcs bug)";

    private static Code? VectorizeMethod(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct, out ScalarReason? reason)
    {
        reason = Symbols.Simd(method) == Symbols.SimdOff ? new ScalarReason("SimdMode = SimdMode.Off", false, null)
            : !method.IsStatic ? new ScalarReason("it is not static: make it static", true, null)
            : Declined(method, syntax, model) is { } declined ? new ScalarReason(declined.Reason, declined.IsFixable, null)
            : null;
        if (reason != null) return null;
        // Whether the job may change structure is the method's as written (the generator's job follows it), helpers or not.
        var declaration = method;
        (syntax, model) = Symbols.WithBody(method, syntax, model); // Declined: it returns nothing and is not generic
        bool stable = Symbols.IsStable(method, syntax, model);
        IOperation root;
        // Views: the method as written without them (Kernel.Views.cs), whose loop is the view method's.
        if (method.Parameters.Any(p => Symbols.Classify(p, Symbols.IsRunner(method)) == Symbols.ParamKind.View))
        {
            if (Desugar(method, syntax, model, ct, out var viewReason) is not var (desugared, desugaredBody, desugaredModel))
            {
                reason = new ScalarReason(viewReason ?? "a view member cannot be inlined", false, null);
                return null;
            }
            (method, root, model) = (desugared, desugaredBody, desugaredModel);
        }
        else
        {
            if (model.GetOperation(syntax, ct) is not IMethodBodyOperation body)
            {
                reason = new ScalarReason("it has no body", false, null);
                return null;
            }
            root = (IOperation?)body.BlockBody ?? body.ExpressionBody!;
            // The user's own calls inlined (Kernel.Inlining.cs): the loop of the body a hand would write without them.
            if (InlineCalls(method, syntax, model, root, ct) is var (inlined, speculative) && speculative.GetOperation(inlined, ct) is { } inlinedBody)
                (root, model) = (inlinedBody, speculative);
        }

        var lane = new Lane(method, Symbols.IsRunner(declaration), Symbols.IsParallel(declaration), Symbols.FastFloats(declaration), model, stable, declaration)
            { Pairs = PairForm.IsPairMethod(declaration) }; // a pair method's loop is the body of its j loop (Kernel.Neighbors.cs)
        var code = Vectorize(lane, root, fieldOf, ct, out reason);
        // Gathers win where a vector's targets share a table and lose a little where they spread over many (D131): where
        // they live is the data's, not the code's, so the loop that gathers is the method's to ask for.
        if (code is { Gathers: true } && Symbols.Simd(declaration) != Symbols.SimdRequire)
        {
            reason = new ScalarReason(GathersOnRequest, false, null);
            return null;
        }
        return code;
    }

    /// <summary>
    /// The vector loop of a lambda pass (<c>world.ForEach(static (ref P p) =&gt; …)</c>, SystemGenerator.Lambdas.cs), or
    /// null with the reason: the test of a [ForEach] method, on the lambda's parameters and body. <paramref name="stable"/>
    /// says whether the body can make structural changes (<see cref="LambdaPass.IsStable"/>), as a method's
    /// declaration would.
    /// </summary>
    public static Code? Vectorize(IMethodSymbol lambda, IAnonymousFunctionOperation function, bool parallel, bool stable,
        SemanticModel model, System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct, out string? reason)
    {
        // A view's members are inlined from a method's declaration (Kernel.Views.cs), which a lambda has not.
        if (lambda.Parameters.Any(p => Symbols.Classify(p, true) == Symbols.ParamKind.View))
        {
            reason = "it takes a view, whose members only a [ForEach] method inlines into its vector loop: write one";
            return null;
        }

        try
        {
            var lane = new Lane(lambda, true, parallel, false, model, stable);
            if (Declined(lane) is { } declined)
            {
                reason = declined.Reason;
                return null;
            }
            var code = Vectorize(lane, function.Body, fieldOf, ct, out ScalarReason? why);
            reason = why?.Text;
            return code;
        }
        catch (System.Exception e) when (e is not System.OperationCanceledException)
        {
            reason = Failed(e).Text; // as a method's (above)
            return null;
        }
    }

    private static Code? Vectorize(Lane lane, IOperation root, System.Func<int, Symbols.ParamKind, string> fieldOf,
        System.Threading.CancellationToken ct, out ScalarReason? reason)
    {
        reason = null;
        try
        {
            return Translate(lane, root, fieldOf, ct);
        }
        catch (Unsupported unsupported)
        {
            reason = new ScalarReason(unsupported.Reason, unsupported.IsFixable, unsupported.SplitComponent);
            return null;
        }
    }

    /// <summary>
    /// The code of one lane: a [ForEach] method or a lambda (its parameters), whether the values it takes are a
    /// caller's (<paramref name="Outside"/> a system: <see cref="Symbols.Classify(IParameterSymbol, bool)"/>), whether
    /// it runs in parallel, whether it allows .NET's vector approximations (FloatMode.Fast), the model of its body and
    /// whether that body can make structural changes.
    /// </summary>
    /// <param name="Declaration">The method as declared, whose attributes the lane follows: <paramref name="Method"/> itself,
    /// or the method a view's desugared local function stands for (Kernel.Views.cs).</param>
    private sealed record Lane(IMethodSymbol Method, bool Outside, bool Parallel, bool Fast, SemanticModel Model, bool Stable, IMethodSymbol? Declaration = null)
    {
        /// <summary>A pair method ([Neighbor] parameters): the loop is the body of one neighbor against a cluster of rows (Kernel.Neighbors.cs).</summary>
        public bool Pairs { get; init; }
    }

    /// <summary>
    /// Whether a method <see cref="Vectorize"/> leaves scalar looks meant for a vector loop, so EECS055 names the reason:
    /// it is static, takes only components (by <c>ref</c> or <c>in</c>), Maybe&lt;T&gt; of them, singletons and values (no
    /// Entity, World, span or Mut), and each component is a struct of lane-type fields only (float, double, int, uint,
    /// long, ulong, byte, sbyte, short, ushort).
    /// The reason is then about its components or its body.
    /// </summary>
    public static bool LooksEligible(IMethodSymbol method) => method.IsStatic && LooksEligible(method, Symbols.IsRunner(method));

    /// <summary><see cref="LooksEligible(IMethodSymbol)"/> for a method or a lambda whose values are a caller's when <paramref name="outside"/>.</summary>
    public static bool LooksEligible(IMethodSymbol method, bool outside)
    {
        bool any = false;
        foreach (var p in method.Parameters)
            switch (Symbols.Classify(p, outside))
            {
                case Symbols.ParamKind.Component:
                    if (!LaneFields(p.Type)) return false;
                    any = true;
                    break;
                case Symbols.ParamKind.Optional when Symbols.MaybeElement(p.Type) is { } element && LaneFields(element):
                    break;
                case Symbols.ParamKind.Lookup when Symbols.LookupElement(p.Type) is { } read && LaneFields(read): // gathered (Kernel.Lookups.cs)
                    break;
                case Symbols.ParamKind.Singleton or Symbols.ParamKind.Context or Symbols.ParamKind.Reduction or Symbols.ParamKind.Entity or Symbols.ParamKind.Emit: // their uses decide (Kernel.Structural.cs)
                    break;
                case Symbols.ParamKind.View when Views.IsValid(p.Type) && Views.Fields((INamedTypeSymbol)p.Type).All(f => !f.Mut && LaneFields(f.Component)):
                    any = true;
                    break;
                default:
                    return false;
            }
        return any;
    }

    /// <summary>
    /// A struct of plain values (numbers, bools, enums, and structs of them): what a vector loop could take, so the reason
    /// it does not (a bool field, a field of another size) is worth a message.
    /// </summary>
    private static bool LaneFields(ITypeSymbol type, int depth = 0) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct } && depth < 4
        && type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray() is { Length: > 0 } fields
        && fields.All(f => f.Type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_Double || f.Type.TypeKind == TypeKind.Enum
            || f.Type.SpecialType == SpecialType.None && LaneFields(f.Type, depth + 1));

    /// <summary>The type argument of a Scratch&lt;T&gt; / Lookup&lt;T&gt; parameter, for a reason.</summary>
    private static string? Element(ITypeSymbol type) => type is INamedTypeSymbol { TypeArguments.Length: 1 } named ? named.TypeArguments[0].Name : null;

    /// <summary>
    /// The lane type a value of <paramref name="type"/> is held as: an enum's underlying integer type (its lanes are its
    /// values' bits, and its comparisons, bitwise operations and conversions those of the integer), any other type itself.
    /// </summary>
    private static ITypeSymbol Under(ITypeSymbol type) => type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying } ? underlying : type;

    private static bool IsEnum(ITypeSymbol? type) => type?.TypeKind == TypeKind.Enum;

    /// <summary>A conversion C# calls an enumeration conversion: an enum to an integer, an integer to an enum, or between enums.</summary>
    private static bool IsEnumConversion(IConversionOperation c) => Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetConversion(c).IsEnumeration;

    private static bool IsLaneType(ITypeSymbol? type) => type != null && Under(type).SpecialType is SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    private static int SizeOf(ITypeSymbol type) => Under(type).SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
        SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
        SpecialType.System_Single or SpecialType.System_Int32 or SpecialType.System_UInt32 => 4,
        _ => 8,
    };

    /// <summary>
    /// The reason <see cref="Translate"/> would give from the method's signature alone, found before its body is bound
    /// and without an exception (a Mut, an Entity, a span: most scalar methods): its first parameter that rules a vector
    /// loop out, unless a component or optional parameter before it may give its own reason. Null to translate.
    /// </summary>
    private static Unsupported? Declined(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        if (!method.ReturnsVoid) return new Unsupported("it returns a value");
        if (method.IsGenericMethod) return new Unsupported("it is generic");
        if (Symbols.WithBody(method, syntax, model).Item1 is { Body: null, ExpressionBody: null }) return null; // Vectorize: "it has no body"
        return Declined(method, Symbols.IsRunner(method), Symbols.IsParallel(method));
    }

    /// <summary><see cref="Declined(IMethodSymbol, MethodDeclarationSyntax, SemanticModel)"/> for a lambda.</summary>
    private static Unsupported? Declined(Lane lane) =>
        lane.Method.IsGenericMethod ? new Unsupported("it is generic") // a lambda's value is dropped: LambdaPass.Discards
            : Declined(lane.Method, lane.Outside, lane.Parallel);

    private static Unsupported? Declined(IMethodSymbol method, bool outside, bool parallel)
    {
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            if (Refused(p, kind, parallel) is { } refused) return new Unsupported(refused);
            if (kind is Symbols.ParamKind.Component or Symbols.ParamKind.Optional) return null; // its lanes decide
        }

        return null;
    }

    /// <summary>
    /// Why parameter <paramref name="p"/> of kind <paramref name="kind"/> rules a vector loop out whatever its type, or null.
    /// None is fixable: each parameter does what the method needs (a value taken by ref may be a running index rather
    /// than a sum, so a Reduce&lt;T&gt; is not always the same method).
    /// </summary>
    private static string? Refused(IParameterSymbol p, Symbols.ParamKind kind, bool parallel) => kind switch
    {
        Symbols.ParamKind.Component or Symbols.ParamKind.Singleton or Symbols.ParamKind.Reduction or Symbols.ParamKind.View => null,
        Symbols.ParamKind.Context => p.RefKind == RefKind.Ref ? $"it takes '{p.Name}' by ref: a vector loop reduces only into a Reduce<T>" : null,
        Symbols.ParamKind.Optional => parallel
            ? $"it takes a Maybe<{Symbols.MaybeElement(p.Type)?.Name}> and runs in parallel: an optional column has a vector loop on one thread"
            : null,
        Symbols.ParamKind.Entity or Symbols.ParamKind.Emit => null, // its uses decide (Kernel.Structural.cs)
        Symbols.ParamKind.Entities => "it takes the EntitySpan",
        Symbols.ParamKind.World => "it takes the World",
        Symbols.ParamKind.ComponentSpan => "it takes spans: a batch method writes its own loop",
        Symbols.ParamKind.Mutable => $"it takes a Mut<{Symbols.MutElement(p.Type)?.Name}>",
        Symbols.ParamKind.Scratch => $"it takes a Scratch<{Element(p.Type)}>: working memory a row at a time, not one per lane",
        Symbols.ParamKind.Lookup => null, // its reads decide (Kernel.Lookups.cs)
        Symbols.ParamKind.Neighbor => NeighborRefused(p), // Kernel.Neighbors.cs
        _ =>$"parameter '{p.Name}' is not a component, a singleton, a Reduce<T> or a value",
    };

    private static Code Translate(Lane lane, IOperation body, System.Func<int, Symbols.ParamKind, string> fieldOf,
        System.Threading.CancellationToken ct)
    {
        var (method, model) = (lane.Method, lane.Model);
        ITypeSymbol? first = null;
        var components = new List<IFieldSymbol[]>();
        var componentTypes = new List<ITypeSymbol>();
        var wides = new List<WideLayout?>();
        var optionals = new List<IFieldSymbol[]>();
        bool outside = lane.Outside;
        HashSet<IParameterSymbol>? whole = null;
        Dictionary<IParameterSymbol, List<(IFieldSymbol[] Path, ISymbol[] Access)>>? touched = null;
        // A component the loop does not split (more than MaxFields lanes, a field that is not a lane, lanes of two sizes)
        // still has a vector loop on AVX-512 when the method uses only 4-byte lanes of it (Kernel.Wide.cs); else the reason,
        // and the advice to split the lanes it uses into a component of their own when they are few.
        WideLayout WideOr(IParameterSymbol p, Unsupported reason)
        {
            whole ??= new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
            touched ??= Touched(body, whole);
            if (WideComponent(p, touched.TryGetValue(p, out var used) ? used : null, whole, model.Compilation) is { } wide) return wide with { Reason = reason.Reason };
            throw Advised(reason.Reason, p.Type, used != null && !whole.Contains(p) ? used.Select(u => u.Path).ToArray() : null, reason.IsFixable);
        }
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            switch (kind)
            {
                case Symbols.ParamKind.Component:
                    IFieldSymbol[] fields;
                    WideLayout? wide = null;
                    try
                    {
                        fields = Lanes(p.Type);
                        if (fields.Length > 1 && fields.FirstOrDefault(f => IsBool(f.Type)) is { } flag)
                            throw new Unsupported($"field {p.Type.Name}.{Symbols.MemberName(flag)} is a bool among {fields.Length} fields: a vector loop takes a bool in a component of its own (one byte per row)"); // inherent until the samples split theirs (owner-review)
                        if (fields.Any(f => SizeOf(f.Type) != SizeOf(fields[0].Type)))
                            throw new Unsupported($"lane types {fields[0].Type.ToDisplayString()} and {fields.First(f => SizeOf(f.Type) != SizeOf(fields[0].Type)).Type.ToDisplayString()} differ in size");
                    }
                    catch (Unsupported unsupported)
                    {
                        wide = WideOr(p, unsupported);
                        fields = wide.Paths.Select(path => path[path.Length - 1]).ToArray();
                    }
                    components.Add(fields);
                    componentTypes.Add(p.Type);
                    wides.Add(wide);
                    break;
                case Symbols.ParamKind.Optional when !lane.Parallel:
                    optionals.Add(Lanes(Symbols.MaybeElement(p.Type)!));
                    break;
                case Symbols.ParamKind.Neighbor when lane.Pairs: // a value per neighbor, broadcast in its j loop
                    if (NeighborRefused(p) is { } refusedNeighbor) throw new Unsupported(refusedNeighbor);
                    break;
                default:
                    if (Refused(p, kind, lane.Parallel) is { } refused) throw new Unsupported(refused);
                    break;
            }
        }
        if (components.Count == 0) throw new Unsupported("it takes no component");
        // The loop's lanes are the narrowest columns'; a column of lanes twice as wide is a pair of vectors per row
        // vector (Kernel.Pairs.cs), but a component the loop gathers (Kernel.Wide.cs) takes lanes of its own size only.
        first = components.Select(f => f[0].Type).OrderBy(SizeOf).First();
        for (int k = 0; k < components.Count; k++)
        {
            var field = components[k][0];
            int size = SizeOf(field.Type);
            if (size == SizeOf(first)) continue;
            if (size != 2 * SizeOf(first) || wides.Any(w => w != null))
                throw new Unsupported($"lane types {first.ToDisplayString()} and {field.Type.ToDisplayString()} differ in size"
                    + (size == 2 * SizeOf(first) ? "" : $": a vector loop takes lanes of one size and of twice that size, not {size / SizeOf(first)} times"));
            if (components[k].Length > 1 && size < 4)
                throw new Unsupported($"component {componentTypes[k].Name} has {components[k].Length} fields of {field.Type.ToDisplayString()}, twice as wide as the {first.ToDisplayString()} lanes: a vector loop splits fields of 4 or 8 bytes");
        }
        foreach (var field in optionals.SelectMany(f => f))
            if (SizeOf(first) != SizeOf(field.Type))
                throw new Unsupported($"lane types {first.ToDisplayString()} and {field.Type.ToDisplayString()} differ in size");
        if (SizeOf(first) < 4 && optionals.Count > 0)
            throw new Unsupported($"it takes a Maybe<{Symbols.MaybeElement(method.Parameters.First(q => Symbols.Classify(q, outside) == Symbols.ParamKind.Optional).Type)?.Name}> of {first.ToDisplayString()} lanes: optional columns have vector loops of 4- and 8-byte lanes");

        // The parameters of a kernel: the main one, and one per field of a flat loop (Flat) with the same setup.
        Kernel Make(bool reductions)
        {
            var made = new Kernel(SizeOf(first)) { _fast = lane.Fast, _compilation = model.Compilation, _model = model, _stable = lane.Stable,
                _lambda = lane.Method.MethodKind == MethodKind.AnonymousFunction };
            made._pairs = components.Any(f => SizeOf(f[0].Type) > SizeOf(first));
            for (int i = 0, k = 0, o = 0; i < method.Parameters.Length; i++)
            {
                var p = method.Parameters[i];
                var kind = Symbols.Classify(p, outside);
                if (kind == Symbols.ParamKind.Component)
                {
                    made._components[p] = (k + 1, components[k], p.RefKind == RefKind.Ref);
                    if (SizeOf(components[k][0].Type) > SizeOf(first)) made._doubled.Add(k + 1);
                    if (wides[k] is { } wide) (made._paths[p], made._wideOf[p]) = (wide.Paths, wide);
                    else made._paths[p] = Paths(p.Type, p.Type.Name);
                    k++;
                    continue;
                }
                if (kind == Symbols.ParamKind.Optional)
                {
                    // The column of the run (or 64 rows of zeros the row mask keeps at row 0, for a table without it).
                    var fields = optionals[o++];
                    made._optionals[p] = (i, fields);
                    made._paths[p] = Paths(Symbols.MaybeElement(p.Type)!, p.Type.Name);
                    made._implied[$"__has{i}"] = [i];
                    string t = Symbols.MaybeElement(p.Type)!.ToDisplayString(Symbols.Fqn);
                    made._setup.Append($"ref {Elem(fields[0].Type)} __r{i} = ref global::System.Runtime.CompilerServices.Unsafe.As<{t}, {Elem(fields[0].Type)}>(ref m{i}.Lanes);\n");
                    made._setup.Append($"var __z{i} = m{i}.RowMask;\n");
                    continue;
                }
                if (kind == Symbols.ParamKind.View) continue; // desugared: its fields are the component parameters at the end
                if (kind == Symbols.ParamKind.Reduction)
                {
                    if (reductions) made._reductions[p] = made.Reduced(p, i, fieldOf(i, kind), lane.Fast, model.Compilation, ct);
                    continue;
                }
                if (kind == Symbols.ParamKind.Entity)
                {
                    made._entity = p;
                    continue;
                }
                if (kind == Symbols.ParamKind.Emit)
                {
                    made._emits[p] = (i, false);
                    continue;
                }
                if (kind == Symbols.ParamKind.Neighbor)
                {
                    made._uniformParams[p] = Neighbor(i); // the job's j loop declares it (Kernel.Neighbors.cs)
                    continue;
                }
                string name = $"__p{i}";
                if (kind == Symbols.ParamKind.Lookup)
                {
                    // The loop's own Lookup<T> over the job's World (Kernel.Lookups.cs); its index is the method's.
                    var element = Symbols.LookupElement(p.Type)!;
                    made._uniformParams[p] = name;
                    made._lookupParams[p] = (name, element);
                    made._lookupOrdinals[p] = i;
                    made._setup.Append($"var {name} = new global::EasyEcs.Lookup<{element.ToDisplayString(Symbols.Fqn)}>(World, null);\n");
                    continue;
                }
                made._uniformParams[p] = name;
                string source = fieldOf(i, kind);
                made._setup.Append(p.RefKind == RefKind.None
                    ? $"var {name} = {source};\n"
                    : $"ref readonly {p.Type.ToDisplayString(Symbols.Fqn)} {name} = ref {source};\n");
            }
            return made;
        }

        var kernel = Make(true);
        // A component of several fields is deinterleaved into one vector per field (Interleaved: shuffles, which move
        // bits, so fields of another type of the same size are reinterpreted after the load), and written back whole:
        // the fields the method does not write keep the values loaded.
        // Each column's loads, with the names that use them: a column the loop never reads nor stores is not loaded
        // (an unused load of a byref stays in the JIT's code, as it may fault).
        var columns = new List<(string[] Names, string Loads)>();
        var stores = new StringBuilder();
        foreach (var (p, (index, fields, _)) in kernel._components.OrderBy(c => c.Value.Index).Select(c => (c.Key, c.Value)))
        {
            string column = Column(fields[0].Type);
            var loads = new StringBuilder();
            columns.Add(([kernel._wideOf.ContainsKey(p) ? "" : $"__c{index}_"], ""));
            if (kernel._wideOf.TryGetValue(p, out var wide)) kernel.WideLoads(index, wide, column, loads);
            else
                foreach (var (h, row) in kernel.Rows(index, column))
                {
                    if (fields.Length == 1 && IsBool(fields[0].Type)) loads.Append($"var __c{index}_0{h} = {BoolLoad($"{V}.LoadUnsafe(ref e{index}, {row})")};\n");
                    else if (fields.Length == 1) loads.Append($"var __c{index}_0{h} = {V}.LoadUnsafe(ref e{index}, {row});\n");
                    else
                    {
                        // Each field is loaded into its name; a field of another type of the lane size is loaded as the
                        // column's lanes and reinterpreted. No copy of the loaded value: the JIT inlines no method of more
                        // than 32 IL locals (MAX_INL_LCLS, even AggressiveInlining), and a copy per field is what pushed
                        // masked jobs (Move3, Move4) past it, so the runner called them per 64-row word (D126).
                        loads.Append($"{Interleaved}.Load{fields.Length}(ref e{index}, {row}, {string.Join(", ", fields.Select((fd, f) => $"out {Vec(column)} {(Elem(fd.Type) == column ? "__c" : "__s")}{index}_{f}{h}"))});\n");
                        for (int f = 0; f < fields.Length; f++)
                            if (Elem(fields[f].Type) != column)
                                loads.Append($"var __c{index}_{f}{h} = {As(column, Elem(fields[f].Type), $"__s{index}_{f}{h}")};\n");
                    }
                }
            columns[columns.Count - 1] = (columns[columns.Count - 1].Names, loads.ToString());
        }
        foreach (var (index, fields) in kernel._optionals.Values.OrderBy(o => o.Index))
        {
            string column = Elem(fields[0].Type), row = $"(nuint)i & __z{index}";
            var loads = new StringBuilder();
            loads.Append($"var __has{index} = {Interleaved}.Lanes{kernel._size * 8}{LanesForm}(m{index}.Present(i), 0);\n");
            if (fields.Length == 1) loads.Append($"var __q{index}_0 = {V}.LoadUnsafe(ref __r{index}, {row});\n");
            else
            {
                loads.Append($"{Interleaved}.Load{fields.Length}(ref __r{index}, {row}, {string.Join(", ", fields.Select((fd, f) => $"out {Vec(column)} {(Elem(fd.Type) == column ? "__q" : "__t")}{index}_{f}"))});\n");
                for (int f = 0; f < fields.Length; f++)
                    if (Elem(fields[f].Type) != column)
                        loads.Append($"var __q{index}_{f} = {As(column, Elem(fields[f].Type), $"__t{index}_{f}")};\n");
            }
            columns.Add(([$"__q{index}_", $"__has{index}"], loads.ToString()));
        }
        var root = body;
        kernel.Statement(root, null);
        kernel.FinishChanges(lane.Declaration ?? method);
        // A masked word keeps the values loaded of what the method writes, and stores them back in the lanes that do not
        // pass: the rows are left as they were (their memory is the table's, whether a component is absent or not).
        var kept = new StringBuilder();
        var masked = new StringBuilder();
        foreach (var (p, (index, fields, _)) in kernel._components.Where(c => kernel._written.Contains(c.Value.Index)).OrderBy(c => c.Value.Index).Select(c => (c.Key, c.Value)))
        {
            string column = Column(fields[0].Type);
            // A wide component's masked words run the method (SystemGenerator): only the loop's stores.
            if (kernel._wideOf.TryGetValue(p, out var wide))
            {
                kernel.WideStores(index, wide, column, stores);
                continue;
            }
            foreach (var (h, row) in kernel.Rows(index, column))
            {
                // A pair's halves are written in every lane: their passing lanes are selected here. The other columns'
                // writes kept the lanes that do not pass (Kernel.Write).
                string Blend(int f) => h.Length == 0 ? $"__c{index}_{f}" : $"{V}.ConditionalSelect({kernel.PassAs(h, Elem(fields[f].Type))}, __c{index}_{f}{h}, __o{index}_{f}{h})";
                if (h.Length > 0) for (int f = 0; f < fields.Length; f++) kept.Append($"var __o{index}_{f}{h} = __c{index}_{f}{h};\n");
                string One(string value) => IsBool(fields[0].Type) ? BoolStore(value) : value; // a bool: its mask as 0 or 1
                stores.Append(fields.Length == 1
                    ? $"{V}.StoreUnsafe({One($"__c{index}_0{h}")}, ref e{index}, {row});\n"
                    : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, $"__c{index}_{f}{h}")))}, ref e{index}, {row});\n");
                masked.Append(fields.Length == 1
                    ? $"{V}.StoreUnsafe({One(Blend(0))}, ref e{index}, {row});\n"
                    : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, Blend(f))))}, ref e{index}, {row});\n");
            }
        }
        // A written optional column is stored whole, the lanes of rows without the component as loaded (a read or a
        // write of m.Value runs only where the row has it, else the vector's rows go to the method); nothing for a table
        // without the column. Its stamps: the rows that have it, as the method stamps them.
        foreach (var (index, fields) in kernel._optionals.Values.Where(o => kernel._writtenOptionals.Contains(o.Index)).OrderBy(o => o.Index))
        {
            string column = Elem(fields[0].Type);
            string Store(System.Func<int, string> value) => fields.Length == 1
                ? $"if (__z{index} != 0) {V}.StoreUnsafe({value(0)}, ref __r{index}, (nuint)i);\n"
                : $"if (__z{index} != 0) {Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, value(f))))}, ref __r{index}, (nuint)i);\n";
            stores.Append(Store(f => $"__q{index}_{f}"));
            masked.Append(Store(f => $"__q{index}_{f}")); // its writes kept the lanes that do not pass
            kernel._setup.Append($"m{index}.StampPresent((int)rows, {Visited});\n");
        }
        // The reductions' lane accumulators: kept before the body (a bail sets them back, a masked word keeps the lanes
        // that do not pass), folded after the loop, lane by lane, into the run's partial result.
        var held = new StringBuilder();
        var restore = new StringBuilder();
        var fold = new StringBuilder();
        string count = $"{Vec(Column(first))}.Count";
        foreach (var r in kernel._reductions.Values.OrderBy(r => r.Index))
        {
            string t = r.Type.ToDisplayString(Symbols.Fqn);
            fold.Append($"for (int __j = 0; __j < {count}; __j++)\n{{\n    var __v = default({t});\n");
            for (int f = 0; f < r.Fields.Length; f++)
            {
                string acc = $"__a{r.Index}_{f}", type = Elem(r.Fields[f].Type);
                bool pair = kernel.IsDoubled(r.Fields[f].Type);
                foreach (var h in Suffixes(pair))
                {
                    held.Append($"var __h{r.Index}_{f}{h} = {acc}{h};\n");
                    restore.Append($"{acc}{h} = __h{r.Index}_{f}{h};\n");
                    if (h.Length > 0) masked.Insert(0, $"{acc}{h} = {V}.ConditionalSelect({kernel.PassAs(h, type)}, {acc}{h}, __h{r.Index}_{f}{h});\n");
                }
                // lane j of the loop's vectors: lane j of a pair's lower half, or lane j - Count of its upper one
                fold.Append(pair
                    ? $"    __v.{r.Fields[f].Name} = __j < {Vec(type)}.Count ? {acc}_0[__j] : {acc}_1[__j - {Vec(type)}.Count];\n"
                    : $"    __v.{r.Fields[f].Name} = {acc}[__j];\n");
            }
            fold.Append($"    {r.Partial}.Combine(__v);\n}}\n");
        }
        string prologue = kernel._bails ? "bool __bail = false;\n" : "";
        string hold = kernel._bails || kernel._lookups.Count > 0 ? held.ToString() : ""; // a lookup sends unresolved vectors to the method too (Kernel.RunRows)
        // A masked word holds the accumulators for a bail and for a pair's fold.
        string maskedHold = kernel._bails || kernel._lookups.Count > 0 || kernel._reductions.Values.Any(r => r.Fields.Any(f => kernel.IsDoubled(f.Type))) ? held.ToString() : "";
        if (kernel._wideOf.Count > 0) kernel.Gate();
        string uses = kernel._body.ToString() + stores + masked + kernel._setup;
        var read = new StringBuilder();
        foreach (var (names, text) in columns)
            if (names.Any(n => n.Length == 0 || uses.Contains(n))) read.Append(text);
        if (kernel._wideOf.Count > 0) kernel.FitLocals(kernel._setup.ToString() + prologue + read + hold + kernel._body + stores);
        var flat = Flat(kernel, root, () => Make(false));
        // Narrow lanes have no shuffles: a component of several fields takes the flat loop or none.
        if (kernel._narrow && flat == null && kernel._components.FirstOrDefault(c => c.Value.Fields.Length > 1 && !kernel._doubled.Contains(c.Value.Index)) is { Key: { } narrow, Value.Fields: { } several })
        {
            whole ??= new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
            touched ??= Touched(body, whole);
            throw Advised($"component {several[0].ContainingType.Name} has {several.Length} fields of {several[0].Type.ToDisplayString()}: a vector loop splits fields of 4 or 8 bytes, and takes narrower ones only when the method does the same to each field",
                narrow.Type, !whole.Contains(narrow) && touched.TryGetValue(narrow, out var used) ? used.Select(u => u.Path).ToArray() : null, false);
        }
        // The passing rows of a masked word as masks of a pair's halves, for the stores and the lanes of what it writes.
        if (kernel._halvesPass) kept.Insert(0, $"var __pw_0 = {V}.WidenLower(__pass);\nvar __pw_1 = {V}.WidenUpper(__pass);\n");
        // Each lookup's backoff (Lookup.Resolve): after a miss, the vectors of the run to send to the method without trying.
        for (int g = 0; g < kernel._lookups.Count; g++) kernel._setup.Append($"int __b{g} = 0;\n");
        if (lane.Pairs) return kernel.ForPairs(read.ToString() + kept, masked.ToString(), Elem(first)); // Kernel.Neighbors.cs
        return new Code(new EquatableArray<string>(kernel._components.Values.OrderBy(c => c.Index).Select(c => Column(c.Fields[0].Type)).ToArray()),
            kernel._setup.ToString(), prologue + read + hold + kernel._body, stores.ToString(), prologue + read + kept + maskedHold + kernel._body.Masked, masked.ToString(),
            restore.ToString(), fold.ToString(), kernel._size, kernel._mask, Column(first), kernel._components.Any(c => c.Value.Fields.Length > 1 && !kernel._wideOf.ContainsKey(c.Key)),
            kernel._bails, flat, kernel._cold, kernel._wideOf.Count == 0 ? null : string.Join(" && ", kernel._components.Where(c => kernel._wideOf.ContainsKey(c.Key)).Select(c => $"__layout{c.Value.Index}")),
            kernel.WideStatics())
        {
            Wide512 = kernel._wide512,
            Wide128 = kernel._wide128,
            StructuralChanges = kernel.StructuralChanges(),
            VectorTail = kernel._change != null || kernel._emits.Values.Any(e => e.Added),
            Written = new EquatableArray<int>(kernel._written.OrderBy(k => k).ToArray()),
            Gathers = kernel._lookups.Count > 0,
            Prefetched = new EquatableArray<int>(kernel._prefetched.OrderBy(i => i).ToArray()),
            Streamed = new(kernel._bails || kernel._lookups.Count > 0 || kernel._wideOf.Count > 0 || kernel._change != null || kernel._emits.Count > 0 || kernel._reductions.Count > 0 || kernel._optionals.Count > 0
                ? [] : WriteOnly(prologue + read + hold + kernel._body, kernel._written.Where(k => kernel._components.Values.First(c => c.Index == k).Fields.Length == 1))),
            Chains = new EquatableArray<string>(kernel._reductions.Values.OrderBy(r => r.Index)
                .SelectMany(r => Enumerable.Range(0, r.Fields.Length).Where(f => IsFloat(r.Fields[f].Type))
                    .SelectMany(f => Suffixes(kernel.IsDoubled(r.Fields[f].Type)).Select(h => $"__a{r.Index}_{f}{h}|{r.Ops[f]}")))
                .ToArray()),
        };
    }

    /// <summary>
    /// The flat loop of a method that treats each field alike, or null. Every component has the same 2 to
    /// <see cref="MaxFields"/> fields of one type; the method declares no local, returns nowhere, reduces into nothing
    /// and has no call that bails; each of its statements reads and writes field i of the components for one i; and
    /// the statements of each field, translated with that field as the only lane, are one text (the same operations
    /// on the same uniform values, in the same order). Then field i of every row runs those operations, as in the
    /// method: lane by lane, the results are the method's bit for bit.
    /// </summary>
    private static FlatCode? Flat(Kernel main, IOperation root, System.Func<Kernel> make)
    {
        if (main._change != null || main._emits.Count > 0 || main._bails || main._lookups.Count > 0 || main._alive != null || main._reductions.Count > 0 || main._locals.Count > 0 || main._wides.Count > 0 || main._structs.Count > 0 || main._optionals.Count > 0 || main._wideOf.Count > 0) return null;
        var components = main._components.Values.ToArray();
        var type = components[0].Fields[0].Type;
        int fields = components[0].Fields.Length;
        if (fields < 2 || components.Any(c => c.Fields.Length != fields || c.Fields.Any(f => !SymbolEqualityComparer.Default.Equals(f.Type, type))))
            return null;

        var statements = new List<IOperation>();
        void Flatten(IOperation op)
        {
            if (op is IBlockOperation block) foreach (var inner in block.Operations) Flatten(inner);
            else if (op is not (IEmptyOperation or IReturnOperation { IsImplicit: true, ReturnedValue: null })) statements.Add(op); // a lambda's own end
        }
        Flatten(root);
        var groups = new List<IOperation>[fields];
        for (int f = 0; f < fields; f++) groups[f] = new List<IOperation>();
        foreach (var statement in statements)
        {
            int field = -1;
            bool every = false, mixed = false; // position = new Position(position.Value + velocity.Value * dt): each lane alike
            foreach (var op in statement.DescendantsAndSelf())
            {
                if (main.Covers(op) is > 0 and var covered)
                {
                    if (covered != fields) return null;
                    every = true;
                    continue;
                }
                if (main.LaneOf(op) is not { } lane) continue;
                mixed |= field >= 0 && lane.Field != field;
                field = lane.Field;
            }
            // A statement of one field goes to its group; one that writes every lane of a struct (position = new
            // Position(position.X + velocity.X, position.Y + velocity.Y)) to every group, where each field's
            // translation reads only that field (a read of another fails it: new Position(position.Y, position.X)).
            if (!every && (field < 0 || mixed)) return null;
            if (every) foreach (var group in groups) group.Add(statement);
            else groups[field].Add(statement);
        }

        string? setup = null, body = null;
        Kernel? first = null;
        for (int f = 0; f < fields; f++)
        {
            if (groups[f].Count == 0) return null; // a field the method leaves alone: a flat loop would not
            var kernel = make();
            kernel._flat = f;
            try
            {
                foreach (var statement in groups[f]) kernel.Statement(statement, null);
            }
            catch (Unsupported)
            {
                return null;
            }
            if (kernel._bails) return null; // a branch of one field cold: the loop with bails
            if (f == 0)
            {
                (setup, body, first) = (kernel._setup.ToString(), kernel._body.ToString(), kernel);
                continue;
            }
            if (kernel._setup.ToString() != setup || kernel._body.ToString() != body) return null;
        }

        var loads = new StringBuilder();
        var stores = new StringBuilder();
        foreach (var (index, _, _) in first!._components.Values.OrderBy(c => c.Index))
        {
            loads.Append($"var __c{index}_0 = {V}.LoadUnsafe(ref e{index}, (nuint)i);\n");
            if (first._written.Contains(index)) stores.Append($"{V}.StoreUnsafe(__c{index}_0, ref e{index}, (nuint)i);\n");
        }
        return new FlatCode(setup!, loads + body, stores.ToString(), fields) { Streamed = new(WriteOnly(loads + body, first._written)) };
    }

    /// <summary>
    /// The lanes of a component, in memory order: 1 to <see cref="MaxFields"/> primitive lane values with C# arithmetic
    /// of their own type (no promotion to int), laid out without padding (sequential, declared in one place); a field
    /// that is a struct of lanes (<c>System.Numerics.Vector3</c>, another struct of floats) gives its own lanes in its
    /// place. Otherwise the reason it has no lanes.
    /// </summary>
    private static IFieldSymbol[] Lanes(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) throw new Unsupported($"component {type.Name} is not a struct");
        if (named.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute"))
            throw new Unsupported($"component {type.Name} sets its layout ([StructLayout])");
        var paths = Paths(type, $"component {type.Name}");
        if (paths.Length > MaxFields) throw new Unsupported($"component {type.Name} has {paths.Length} fields (a vector loop splits up to {MaxFields})");
        return paths.Select(p => p[p.Length - 1]).ToArray();
    }

    /// <summary>
    /// The paths from <paramref name="type"/> to each of its lanes (a primitive lane field, or one inside a nested struct
    /// of them), in memory order; otherwise the reason (<paramref name="what"/> names the type in it).
    /// </summary>
    private static IFieldSymbol[][] Paths(ITypeSymbol type, string what)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) throw new Unsupported($"{what} is not a struct");
        if (named.IsGenericType) throw new Unsupported($"{what} is generic");
        if (Symbols.FieldsInSeveralParts(named))
            throw new Unsupported($"{what} declares fields in several parts, so the order of its fields is not defined: declare them in one part", true);
        var fields = named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        if (fields.Length == 0) throw new Unsupported($"{what} has no fields");
        var paths = new List<IFieldSymbol[]>();
        foreach (var field in fields)
        {
            string name = $"{type.Name}.{Symbols.MemberName(field)}";
            if (!field.IsFixedSizeBuffer && (IsLaneType(field.Type) || IsBool(field.Type))) paths.Add([field]);
            else if (!field.IsFixedSizeBuffer && field.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } inner
                     && SequentialLayout(inner) && paths.Count < MaxFields * 4)
                foreach (var path in Paths(inner, $"field {name}"))
                    paths.Add([field, .. path]);
            else throw new Unsupported($"field {name} is a {field.Type.ToDisplayString()}, not a lane type (float, double, int, uint, long, ulong, byte, sbyte, short, ushort or an enum of them)");
        }
        return paths.ToArray();
    }

    /// <summary>A nested struct's fields lie in declaration order: no [StructLayout], or a sequential one without Pack or Size.</summary>
    private static bool SequentialLayout(INamedTypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "StructLayoutAttribute") is not { } layout
        || layout.ConstructorArguments.Length == 1 && layout.ConstructorArguments[0].Value is 0 && layout.NamedArguments.All(n => n.Key is not ("Pack" or "Size") || n.Value.Value is 0);

    /// <summary>Whether a struct type flattens into lanes (<see cref="Paths"/>), for a test that does not throw.</summary>
    private static IFieldSymbol[][]? TryPaths(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } || IsLaneType(type)) return null;
        try
        {
            return Paths(type, type.Name);
        }
        catch (Unsupported)
        {
            return null;
        }
    }

    // ---------------- types ----------------

    /// <summary>A lane type of this kernel: byte, sbyte (1 byte), short, ushort (2), float, int, uint (4) or double, long, ulong (8), its size.</summary>
    private bool IsLane(ITypeSymbol? type) => IsLaneType(type) && SizeOf(type!) == _size;

    private static bool IsBool(ITypeSymbol? type) => type?.SpecialType == SpecialType.System_Boolean;
    private static bool IsFloat(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Single or SpecialType.System_Double;
    private static bool IsSigned(ITypeSymbol? t) => t != null && Under(t).SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
        or SpecialType.System_SByte or SpecialType.System_Int16 || IsFloat(t);
    private static string Elem(ITypeSymbol type) => Under(type).ToDisplayString(Symbols.Fqn);

    /// <summary>
    /// The lane type of a component's column: its first field's, a bool's as byte (0 or 1). A bool field's vector in the
    /// loop is a mask (<see cref="BoolLoad"/>, <see cref="BoolStore"/>), as every bool value is.
    /// </summary>
    private static string Column(ITypeSymbol type) => IsBool(type) ? "byte" : Elem(type);

    /// <summary>A bool column's bytes as the 1-byte loop's mask: all ones where the byte is not 0.</summary>
    private static string BoolLoad(string bytes) => $"{V}.As<byte, sbyte>({V}.GreaterThan({bytes}, {Vec("byte")}.Zero))";

    /// <summary>A mask stored as a bool column's bytes: exactly 1 (true) or 0, as C# writes a bool.</summary>
    private static string BoolStore(string mask) => $"{V}.As<sbyte, byte>(-{mask})";
    private static string Vec(string element) => $"{V}<{element}>";

    /// <summary>The vector type of a value of <paramref name="type"/>: its lanes, or the mask type for a bool.</summary>
    private string VecOf(ITypeSymbol? type) => Vec(IsBool(type) ? _mask : Elem(type!));

    /// <summary><paramref name="value"/>, a vector of <paramref name="from"/>, as one of <paramref name="to"/> (the same bits).</summary>
    private static string As(string from, string to, string value) => from == to ? value : $"{V}.As<{from}, {to}>({value})";

    /// <summary>A mask (vector of the mask type) as a vector of <paramref name="type"/>'s lanes, for a ConditionalSelect.</summary>
    private string MaskAs(string mask, ITypeSymbol type) => As(_mask, IsBool(type) ? _mask : Elem(type), mask);

    private string Temp(string prefix) => $"__{prefix}{_temps++}";
    private static string? And(string? a, string? b) => a == null ? b : b == null ? a : $"({a} & {b})";

    // ---------------- statements ----------------

    /// <summary>Translates <paramref name="op"/> for the lanes of <paramref name="mask"/> (null: every lane).</summary>
    /// <returns>False after a return every remaining lane takes: what follows is not reached.</returns>
    private bool Statement(IOperation op, string? mask)
    {
        _held.Clear(); // the values a pair's halves share are a statement's, read before it writes
        switch (op)
        {
            case IBlockOperation block:
                foreach (var s in block.Operations)
                    if (!Statement(s, mask)) return false;
                return true;
            case IEmptyOperation:
                return true;
            case IVariableDeclarationGroupOperation group:
                foreach (var declaration in group.Declarations)
                foreach (var declarator in declaration.Declarators)
                {
                    var local = declarator.Symbol;
                    var init = declarator.GetVariableInitializer()?.Value ?? declaration.Initializer?.Value;
                    if (local.IsRef) throw new Unsupported($"ref local '{local.Name}' is not supported: name the parameter's fields instead", true);
                    if (init == null) throw new Unsupported($"local '{local.Name}' has no initializer: give it its value where it is declared", true);
                    if (DeclareLookup(local, init)) continue; // var at = lookup[id]: names the lookup's value
                    if (PairLocal(local, init))
                    {
                        _active = And(mask, _alive);
                        var (lower, upper) = Halves(() => Expr(init));
                        string pair = Temp("l");
                        _body.Append($"var {pair}_0 = {lower};\nvar {pair}_1 = {upper};\n");
                        _locals[local] = pair;
                        _pairLocals.Add(local);
                        continue;
                    }
                    if (_narrow && IsInt(local.Type))
                    {
                        _active = And(mask, _alive);
                        DeclareWide(local, WideOf(init));
                        continue;
                    }
                    if (StructPaths(local.Type) != null)
                    {
                        _active = And(mask, _alive);
                        DeclareStruct(local, init);
                        continue;
                    }
                    if (!IsLane(local.Type) && !IsBool(local.Type))
                        throw new Unsupported($"local '{local.Name}' is a {local.Type.ToDisplayString()}, not a lane type of {LaneSizes} bytes");
                    string name = Temp("l");
                    _active = And(mask, _alive);
                    string value = Expr(init);
                    _locals[local] = name;
                    _body.Append($"var {name} = {value};\n"); // a fresh local: its value in other lanes is never read
                    Alias(name, value);
                }
                return true;
            case IExpressionStatementOperation or IReturnOperation { IsImplicit: true, ReturnedValue: not null }: // an expression lambda's value, dropped (LambdaPass.Discards)
            {
                var e = op is IExpressionStatementOperation statement ? statement.Operation : ((IReturnOperation)op).ReturnedValue!;
                _active = And(mask, _alive);
                if (!Deferred(e)) Assignment(e, mask);
                return true;
            }
            case IConditionalOperation { WhenTrue: var whenTrue, WhenFalse: var whenFalse } branch when branch.Type == null:
            {
                string condition = Temp("m");
                _active = And(mask, _alive);
                string tested = Expr(branch.Condition);
                _body.Append($"var {condition} = {tested};\n"); // before either branch writes what it reads
                Alias(condition, tested);
                Prove(condition, branch.Condition);
                Branch(whenTrue, And(mask, condition)!);
                if (whenFalse != null) Branch(whenFalse, And(mask, $"~{condition}")!);
                return true;
            }
            case IReturnOperation { ReturnedValue: null }:
                if (mask == null) return false;
                if (_alive == null)
                {
                    _alive = Temp("alive");
                    _body.Append($"var {_alive} = ~{mask};\n");
                    _implied[_alive] = ImpliedNot(mask);
                }
                else
                {
                    _body.Append($"{_alive} &= ~{mask};\n");
                    _implied[_alive].UnionWith(ImpliedNot(mask)); // the lanes left are fewer: what they have, they keep
                }
                return true;
            default:
                throw Not(op);
        }
    }

    /// <summary>
    /// A branch under a condition the loop computes: translated, or, when it holds what the loop cannot express (a call,
    /// a loop, logging), a cold branch: a vector with a lane that takes it sends its rows to the method itself before any
    /// store, as a Math.Clamp that would throw does. The rows that never take it stay in the vector loop.
    /// </summary>
    private void Branch(IOperation op, string mask)
    {
        var saved = Save();
        try
        {
            Statement(op, mask);
        }
        catch (Unsupported unsupported) when (!unsupported.IsWholeMethod)
        {
            // A method that changes structure cannot run its own rows during the pass (it is frozen): no cold branch.
            if (!_stable)
                throw FixableReason($"{unsupported.Reason}, in a branch of a method that changes structure: a vector loop sends such a branch's rows to the method, "
                    + "which cannot change structure during the pass; move that branch into a [ForEach] method of its own");
            Restore(saved);
            _cold ??= unsupported.Reason;
            _bails = true;
            Live(And(mask, _alive), $"__bail |= {LiveLanes} != {Vec(_mask)}.Zero; // a cold branch: the method runs these rows\n");
        }
    }

    /// <summary>What a translation changes, kept to undo a branch that turns out cold.</summary>
    private sealed record State((int, int) Body, int Setup, int Temps, Dictionary<ILocalSymbol, string> Locals, HashSet<ILocalSymbol> PairLocals, Dictionary<ILocalSymbol, Wide> Wides,
        Dictionary<ILocalSymbol, string?[]> Structs, HashSet<int> Written, HashSet<int> WrittenOptionals, Dictionary<string, HashSet<int>> Implied,
        HashSet<string> Guarded, Dictionary<string, string> Broadcasts, Dictionary<string, (string, string)> MaskHalves, string? Alive, bool Bails,
        Dictionary<string, int> Resolved, Dictionary<ILocalSymbol, int> LookupLocals, Dictionary<string, string> Gathered, int Lookups);

    private State Save() => new(_body.Length, _setup.Length, _temps, new(_locals, SymbolEqualityComparer.Default),
        new(_pairLocals, SymbolEqualityComparer.Default), new(_wides, SymbolEqualityComparer.Default),
        new(_structs, SymbolEqualityComparer.Default), [.. _written], [.. _writtenOptionals],
        _implied.ToDictionary(e => e.Key, e => new HashSet<int>(e.Value)), [.. _guarded], new(_broadcasts), new(_maskHalves), _alive, _bails,
        new(_resolved), new(_lookupLocals, SymbolEqualityComparer.Default), new(_gathered), _lookups.Count);

    private void Restore(State state)
    {
        _body.Length = state.Body;
        _setup.Length = state.Setup;
        _temps = state.Temps;
        static void Reset<TKey, TValue>(Dictionary<TKey, TValue> into, Dictionary<TKey, TValue> from)
        {
            into.Clear();
            foreach (var e in from) into[e.Key] = e.Value;
        }
        Reset(_locals, state.Locals);
        _pairLocals.Clear();
        _pairLocals.UnionWith(state.PairLocals);
        Reset(_wides, state.Wides);
        Reset(_structs, state.Structs);
        Reset(_implied, state.Implied);
        Reset(_broadcasts, state.Broadcasts);
        Reset(_maskHalves, state.MaskHalves);
        _written.Clear();
        _written.UnionWith(state.Written);
        _writtenOptionals.Clear();
        _writtenOptionals.UnionWith(state.WrittenOptionals);
        _guarded.Clear();
        _guarded.UnionWith(state.Guarded);
        (_alive, _bails) = (state.Alive, state.Bails);
        Reset(_resolved, state.Resolved);
        Reset(_lookupLocals, state.LookupLocals);
        Reset(_gathered, state.Gathered);
        _lookups.RemoveRange(state.Lookups, _lookups.Count - state.Lookups);
    }

    private void Assignment(IOperation op, string? mask)
    {
        // A write of a value twice as wide as the lanes: each half of the pair, the lower lanes first (each reads only
        // its own half of what the other writes, and the values of the lanes' width both read are held before).
        bool pair = PairTarget(op);
        if (pair && _half < 0)
        {
            EachHalf(() => Assignment(op, mask));
            return;
        }
        if (!pair && _half >= 0) throw new Unsupported($"'{Text(op)}' writes lanes of two sizes in one statement");
        if (Reduces(op, mask)) return;
        IOperation target;
        string value;
        switch (op)
        {
            case ISimpleAssignmentOperation { IsRef: false } a when StructPaths(a.Target.Type) != null:
                WriteStruct(a.Target, StructOf(a.Value), mask);
                return;
            case ICompoundAssignmentOperation { OperatorMethod: { } method, IsChecked: false } a when KnownVector(method.ContainingType) && StructPaths(a.Target.Type) != null:
                WriteStruct(a.Target, VectorBinary(a.OperatorKind, a.Target, a.Value, a.Target), mask); // position.Value += v * dt
                return;
            case ISimpleAssignmentOperation { IsRef: false } a when _narrow && IsInt(a.Target.Type):
                WriteWide(a.Target, WideOf(a.Value), mask);
                return;
            case ISimpleAssignmentOperation { IsRef: false } a:
                Writable(a.Target);
                if (a.Value is ISimpleAssignmentOperation inner) // a = b = value: b first, then a from it
                {
                    Assignment(inner, mask);
                    value = Expr(inner.Target);
                }
                else value = Expr(a.Value);
                target = a.Target;
                break;
            case ICompoundAssignmentOperation a when _narrow && !a.IsChecked && a.OperatorMethod == null && (IsLane(a.Target.Type) || IsInt(a.Target.Type)):
            {
                // b += x is b = (byte)(b + x): the operation on ints, truncated back to the field's type
                var computed = WideBinary(a.OperatorKind, WideOf(a.Target), WideOf(a.Value), a);
                if (IsInt(a.Target.Type))
                {
                    WriteWide(a.Target, computed, mask);
                    return;
                }
                target = a.Target;
                value = Truncate(computed, a.Target.Type!, a);
                break;
            }
            case ICompoundAssignmentOperation a when !a.IsChecked && a.OperatorMethod == null && a.InConversion.IsIdentity && a.OutConversion.IsIdentity:
                Writable(a.Target);
                target = a.Target;
                value = Binary(a.OperatorKind, a.Target, a.Value, a.Target.Type);
                break;
            case IIncrementOrDecrementOperation { IsChecked: false, OperatorMethod: null } a when _narrow && IsInt(a.Type):
                WriteWide(a.Target, WideBinary(a.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract,
                    WideOf(a.Target), Constant(1), a), mask);
                return;
            case IIncrementOrDecrementOperation { IsChecked: false, OperatorMethod: null } a when IsLane(a.Type):
                target = a.Target;
                value = $"({Expr(a.Target)} {(a.Kind == OperationKind.Increment ? "+" : "-")} {VecOf(a.Type)}.One)";
                break;
            default:
                throw Not(op);
        }

        string name = Target(target);
        Write(name, value, target.Type!, mask);
    }

    /// <summary>
    /// <paramref name="name"/> = <paramref name="value"/> in the lanes of <paramref name="mask"/> that have not returned;
    /// in a masked word, a value the loop stores (<see cref="Kept"/>) in the passing lanes only.
    /// </summary>
    private void Write(string name, string value, ITypeSymbol type, string? mask)
    {
        string? active = And(mask, _alive);
        _maskHalves.Remove(name); // a bool local written: the halves it held are not its value any more
        string Select(string? lanes) => lanes == null ? $"{name} = {value};\n"
            : $"{name} = {V}.ConditionalSelect({(_half < 0 ? MaskAs(lanes, type) : As(_mask, IsBool(type) ? _mask : Elem(type), WidenMask(lanes)))}, {value}, {name});\n";
        _body.Append(Select(active), _half < 0 && Kept(name) ? Select(And(active, "__pass")) : Select(active));
    }

    /// <summary>
    /// A vector the loop stores or folds after the body: a component's lane (<c>__c1_0</c>), a reduction's accumulator
    /// (<c>__a2_0</c>) or an optional's lane (<c>__q1_0</c>), not a pair's half (<c>__c1_0_1</c>). Its writes in a
    /// masked word keep the lanes that do not pass, so it is stored or folded as it is.
    /// </summary>
    private static bool Kept(string name) =>
        name.Length > 3 && name.StartsWith("__", System.StringComparison.Ordinal) && name[2] is 'c' or 'a' or 'q' && char.IsDigit(name[3])
        && name.Count(c => c == '_') == 3;

    /// <summary>
    /// Refuses a write to a singleton or a value before its value is translated, so the reason names the write (a sum
    /// into a singleton: Reduce&lt;T&gt;), not an operation of the value the loop could not have used anyway.
    /// </summary>
    private void Writable(IOperation target)
    {
        var root = target;
        while (root is IMemberReferenceOperation { Instance: { } instance }) root = instance;
        if (root is IParameterReferenceOperation { Parameter: var p } && _uniformParams.ContainsKey(p))
            throw new Unsupported($"it writes '{Text(target)}', which is neither a local nor the lane of a component taken by ref");
    }

    /// <summary>The vector a write goes to: a kernel local, or the lane field of a component taken by ref.</summary>
    private string Target(IOperation target)
    {
        if (target is ILocalReferenceOperation { Local: var local } && _locals.TryGetValue(local, out var name)) return Local(local, name, target);
        if (StructLocalLane(target) is { } lane) return lane;
        if (LaneOf(target) is { Ref: true } c)
        {
            _written.Add(c.Index);
            return ComponentLane(c.Index, c.Field, target);
        }
        if (OptionalLane(target) is { } q)
        {
            Guard(q.Index);
            _writtenOptionals.Add(q.Index);
            return $"__q{q.Index}_{q.Field}";
        }
        throw new Unsupported($"it writes '{Text(target)}', which is neither a local nor the lane of a component taken by ref");
    }

    // ---------------- reductions ----------------

    /// <summary>
    /// A <c>Reduce&lt;T&gt;</c> parameter the vector loop can carry in lanes: T's fields are lane fields of the kernel's
    /// size, and T.Combine combines each field on its own as a sum, a Min or a Max of both sides' field, operations that
    /// do not depend on the order of the rows (integer sums wrap; Min / Max of floats follow IEEE 754:2019, NaN and -0
    /// included). A float sum does, by rounding: only with FloatMode.Fast. Each lane starts at T.Identity.
    /// </summary>
    private Reduction Reduced(IParameterSymbol p, int index, string partial, bool fast, Compilation compilation, System.Threading.CancellationToken ct)
    {
        if (p.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } reduce || reduce.TypeArguments[0] is not INamedTypeSymbol type) throw new Unsupported($"parameter '{p.Name}' is not a Reduce<T>");
        string name = type.Name;
        var fields = type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        if (fields.Length == 0) throw new Unsupported($"reduction {name} has no fields");
        foreach (var field in fields)
        {
            if (Symbols.Associated(field) is { } property) throw new Unsupported($"reduction {name} holds {property.Name} in a property: the lanes fold into fields, so make it a field", true);
            if (field.IsReadOnly) throw new Unsupported($"field {name}.{field.Name} of the reduction is readonly: remove readonly", true);
            if (!IsLane(field.Type) && !IsDoubled(field.Type)) throw new Unsupported($"field {name}.{field.Name} of the reduction is a {field.Type.ToDisplayString()}, not a lane type of {LaneSizes} bytes");
            if (IsDoubled(field.Type)) _pairs = true;
        }

        var ops = AttributeOps(type, fields) ?? CombineOps(type, fields, compilation, ct);
        for (int f = 0; f < fields.Length; f++)
            if (ops[f] == "+" && IsFloat(fields[f].Type) && !fast)
                throw new Unsupported($"the float sum {name}.{fields[f].Name} would add its rows in another order (one sum per lane): set FloatMode = FloatMode.Fast to accept a sum that is not bit for bit the method's");

        string t = type.ToDisplayString(Symbols.Fqn);
        _setup.Append($"var __id{index} = {t}.Identity;\n");
        for (int f = 0; f < fields.Length; f++)
            foreach (var h in Suffixes(IsDoubled(fields[f].Type)))
                _setup.Append($"var __a{index}_{f}{h} = {V}.Create<{Elem(fields[f].Type)}>(__id{index}.{fields[f].Name});\n");
        return new Reduction(index, type, fields, ops, partial);
    }

    /// <summary>
    /// How the Combine that PartGenerator writes from T's <c>[Sum]</c> / <c>[Min]</c> / <c>[Max]</c> fields combines each
    /// field of T, or null when T declares no reduction by attributes (its Combine is written by hand). The generators of
    /// a compilation do not see that part, so its declaration is read from the attributes (<see cref="Parts"/>), the same
    /// way for the generator and the analyzer.
    /// </summary>
    private static string[]? AttributeOps(INamedTypeSymbol type, IFieldSymbol[] fields)
    {
        if (!Parts.Declares(type, Parts.Sum) && !Parts.Declares(type, Parts.Product) && !Parts.Declares(type, Parts.Min)
            && !Parts.Declares(type, Parts.Max) && !Parts.Declares(type, Parts.All) && !Parts.Declares(type, Parts.Any)) return null;
        string name = type.Name;
        if (Parts.Of(type) is not { WritesReduction: true } parts)
            throw new Unsupported($"the generator writes no Combine for {name}: its reduction attributes are reported");
        var ops = new string[fields.Length];
        for (int f = 0; f < fields.Length; f++)
        {
            string field = global::EasyEcs.Generator.Code.Escape(fields[f].Name);
            int at = parts.Fields.FindIndex(x => x.Name == field);
            if (at < 0) throw new Unsupported($"field {name}.{fields[f].Name} carries no reduction attribute");
            ops[f] = parts.Fields[at].Op switch
            {
                Parts.Op.Sum => "+",
                Parts.Op.Min => "Min",
                Parts.Op.Max => "Max",
                var op => throw new Unsupported($"{name}.{fields[f].Name} combines by [{op}]: a vector loop reduces sums, Min and Max"),
            };
        }

        return ops;
    }

    /// <summary>
    /// How T.Combine combines each field of T: its body returns <c>new T { F = a.F + b.F, G = Math.Min(a.G, b.G) }</c>
    /// (a sum, a Min or a Max of both sides' field, every field set once), otherwise the reason it does not.
    /// </summary>
    private static string[] CombineOps(INamedTypeSymbol type, IFieldSymbol[] fields, Compilation compilation, System.Threading.CancellationToken ct)
    {
        string name = type.Name;
        var combine = type.GetMembers("Combine").OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 2 && m.Parameters.All(q => SymbolEqualityComparer.Default.Equals(q.Type, type)));
        if (combine?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not MethodDeclarationSyntax syntax)
            throw new Unsupported($"{name}.Combine is not declared in this compilation, so its lanes cannot be combined field by field");
        if (compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax, ct) is not IMethodBodyOperation body)
            throw new Unsupported($"{name}.Combine has no body");
        var block = body.ExpressionBody ?? body.BlockBody;
        IOperation? returned = block is { Operations.Length: 1 } && block.Operations[0] is IReturnOperation { ReturnedValue: var e } ? e : null;
        // new() { ... } is a target-typed creation: an implicit conversion of kind ObjectCreation around it.
        while (returned is IConversionOperation { IsImplicit: true } c && (c.Conversion.IsIdentity || c.Operand is IObjectCreationOperation)) returned = c.Operand;
        if (returned is not IObjectCreationOperation { Arguments.Length: 0, Initializer: { } initializer } created
            || !SymbolEqualityComparer.Default.Equals(created.Type, type))
            throw new Unsupported($"{name}.Combine does not return new {name} {{ ... }} with each field set from both sides");

        var ops = new string?[fields.Length];
        foreach (var init in initializer.Initializers)
        {
            if (init is not ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Member: var member } } a
                || System.Array.FindIndex(fields, f => SymbolEqualityComparer.Default.Equals(f, member)) is not (>= 0 and var f) || ops[f] != null)
                throw new Unsupported($"{name}.Combine sets '{Text(init)}', which is not one of {name}'s fields set once");
            bool Sides(IOperation x, IOperation y) => Side(x, f, out int i) && Side(y, f, out int j) && i != j;
            bool Side(IOperation x, int field, out int parameter)
            {
                parameter = -1;
                while (x is IConversionOperation { Conversion.IsIdentity: true } c) x = c.Operand;
                if (x is not IFieldReferenceOperation { Instance: IParameterReferenceOperation { Parameter: var q } } r
                    || !SymbolEqualityComparer.Default.Equals(r.Field, fields[field])) return false;
                parameter = q.Ordinal;
                return true;
            }
            ops[f] = a.Value switch
            {
                IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, OperatorMethod: null, IsChecked: false } b when Sides(b.LeftOperand, b.RightOperand) => "+",
                IInvocationOperation { Instance: null, TargetMethod.Name: "Min" or "Max", Arguments.Length: 2 } m when IsMath(m.TargetMethod)
                    && Sides(m.Arguments[0].Value, m.Arguments[1].Value) => m.TargetMethod.Name,
                _ => throw new Unsupported($"{name}.Combine sets {fields[f].Name} to '{Text(a.Value)}', not the sum, Min or Max of both sides' {fields[f].Name}"),
            };
        }
        for (int f = 0; f < fields.Length; f++)
            if (ops[f] == null) throw new Unsupported($"{name}.Combine does not set {fields[f].Name}");
        return ops!;
    }

    /// <summary>The reduction and field <paramref name="op"/> names: <c>r.Value.F</c> of a Reduce&lt;T&gt; parameter r.</summary>
    private (Reduction Reduction, int Field)? Accumulator(IOperation op)
    {
        if (op is not IFieldReferenceOperation { Field: var field, Instance: IFieldReferenceOperation { Field.Name: "Value", Instance: IParameterReferenceOperation { Parameter: var p } } }
            || !_reductions.TryGetValue(p, out var r)) return null;
        int f = System.Array.FindIndex(r.Fields, x => SymbolEqualityComparer.Default.Equals(x, field));
        return f < 0 ? null : (r, f);
    }

    /// <summary>
    /// A write of a partial result, translated into its lanes: <c>r.Value.F += e</c> / <c>-=</c> / <c>++</c> /
    /// <c>--</c> where T.Combine sums F, <c>r.Value.F = Math.Min(r.Value.F, e)</c> (or Max) where it takes that, or
    /// <c>r.Combine(new T { ... })</c> with every field. False when <paramref name="op"/> writes no partial result.
    /// </summary>
    private bool Reduces(IOperation op, string? mask)
    {
        (Reduction Reduction, int Field)? target = op switch
        {
            ICompoundAssignmentOperation a => Accumulator(a.Target),
            IIncrementOrDecrementOperation a => Accumulator(a.Target),
            ISimpleAssignmentOperation a => Accumulator(a.Target),
            _ => null,
        };
        if (op is IInvocationOperation { Instance: IParameterReferenceOperation { Parameter: var p }, TargetMethod.Name: "Combine", Arguments.Length: 1 } call
            && _reductions.TryGetValue(p, out var reduction))
        {
            var value = call.Arguments[0].Value;
            while (value is IConversionOperation { Conversion.IsIdentity: true } c) value = c.Operand;
            if (value is not IObjectCreationOperation { Arguments.Length: 0, Initializer: { } initializer })
                throw new Unsupported($"it combines '{Text(value)}', not new {reduction.Type.Name} {{ ... }} with every field set");
            var values = new IOperation?[reduction.Fields.Length];
            foreach (var init in initializer.Initializers)
                if (init is ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Member: var member } } a
                    && System.Array.FindIndex(reduction.Fields, f => SymbolEqualityComparer.Default.Equals(f, member)) is >= 0 and var f)
                    values[f] = a.Value;
                else throw new Unsupported($"it combines '{Text(init)}', which sets no field of {reduction.Type.Name}");
            for (int f = 0; f < values.Length; f++)
            {
                if (values[f] == null) throw new Unsupported($"it combines a {reduction.Type.Name} without {reduction.Fields[f].Name}, which then is not its identity");
                var given = values[f]!;
                if (IsDoubled(reduction.Fields[f].Type)) EachHalf(() => Fold(reduction, f, Expr(given), mask));
                else Fold(reduction, f, Expr(given), mask);
            }
            return true;
        }
        if (target is not var (r, field)) return false;

        string acc = Acc(r, field), op2 = r.Ops[field];
        var type = r.Fields[field].Type;
        string NotCombined(string what) => $"it {what} '{Text(op)}', where {r.Type.Name}.Combine takes the {(op2 == "+" ? "sum" : op2)} of {r.Fields[field].Name}";
        switch (op)
        {
            case ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, IsChecked: false, OperatorMethod: null } a
                when op2 == "+" && a.InConversion.IsIdentity && a.OutConversion.IsIdentity:
                Write(acc, $"({acc} {(a.OperatorKind == BinaryOperatorKind.Add ? "+" : "-")} {Expr(a.Value)})", type, mask);
                return true;
            case ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, IsChecked: false, OperatorMethod: null } a
                when op2 == "+" && _narrow && IsLane(type):
                // b += x is b = (byte)(b + x): a sum of lanes that wrap, the low bits of x added
                Write(acc, $"({acc} {(a.OperatorKind == BinaryOperatorKind.Add ? "+" : "-")} {Truncate(WideOf(a.Value), type, a)})", type, mask);
                return true;
            case IIncrementOrDecrementOperation { IsChecked: false, OperatorMethod: null } a when op2 == "+":
                Write(acc, $"({acc} {(a.Kind == OperationKind.Increment ? "+" : "-")} {Vec(Elem(type))}.One)", type, mask);
                return true;
            case ISimpleAssignmentOperation { Value: IInvocationOperation { Instance: null, Arguments.Length: 2 } m }
                when IsMath(m.TargetMethod) && m.TargetMethod.Name == op2:
                var (x, y) = (m.Arguments[0].Value, m.Arguments[1].Value);
                IOperation? other = Accumulator(x) is { Field: var fx } ax && ax.Reduction == r && fx == field ? y
                    : Accumulator(y) is { Field: var fy } ay && ay.Reduction == r && fy == field ? x : null;
                if (other == null) throw new Unsupported(NotCombined("writes"));
                Fold(r, field, Expr(other), mask);
                return true;
            default:
                throw new Unsupported(NotCombined("writes"));
        }
    }

    /// <summary>Field <paramref name="field"/> of <paramref name="r"/>'s lanes combined with <paramref name="value"/> as T.Combine does.</summary>
    private void Fold(Reduction r, int field, string value, string? mask)
    {
        string acc = Acc(r, field);
        Write(acc, r.Ops[field] == "+" ? $"({acc} + {value})" : $"{V}.{r.Ops[field]}({acc}, {value})", r.Fields[field].Type, mask);
    }

    /// <summary>
    /// The component and field whose lane <paramref name="op"/> is: its field, or the auto-property backed by it (a
    /// record struct's positional member), through the fields of nested structs (<c>position.Value.X</c>).
    /// </summary>
    private (int Index, int Field, bool Ref)? LaneOf(IOperation op)
    {
        if (Chain(op) is not ({ } root, { Length: > 0 } members) || root is not IParameterReferenceOperation { Parameter: var parameter }
            || !_components.TryGetValue(parameter, out var c))
            return null;
        int f = Leaf(_paths[parameter], members);
        if (f < 0) return null;
        if (_flat < 0) return (c.Index, f, c.Ref);
        if (f != _flat) throw new Unsupported("a statement mixes fields"); // Flat checked this; never reported
        return (c.Index, 0, c.Ref);
    }

    /// <summary>The root of a chain of field and property reads (<c>p.Value.X</c>: p) and the members read, root first.</summary>
    private static (IOperation? Root, ISymbol[] Members) Chain(IOperation op)
    {
        var members = new List<ISymbol>();
        while (true)
        {
            switch (op)
            {
                case IFieldReferenceOperation { Field.IsStatic: false, Instance: { } instance } f:
                    members.Add(f.Field);
                    op = instance;
                    continue;
                case IPropertyReferenceOperation { Property.IsStatic: false, Arguments.Length: 0, Instance: { } instance } p:
                    members.Add(p.Property);
                    op = instance;
                    continue;
            }
            members.Reverse();
            return (op, members.ToArray());
        }
    }

    /// <summary>Whether a member read is <paramref name="field"/>: the field, or the auto-property backed by it.</summary>
    private static bool Is(ISymbol member, IFieldSymbol field) =>
        SymbolEqualityComparer.Default.Equals(member, field) || SymbolEqualityComparer.Default.Equals(member, Symbols.Associated(field));

    /// <summary>The lane whose path <paramref name="members"/> is, or -1.</summary>
    private static int Leaf(IFieldSymbol[][] paths, ISymbol[] members)
    {
        for (int f = 0; f < paths.Length; f++)
            if (paths[f].Length == members.Length && Enumerable.Range(0, members.Length).All(k => Is(members[k], paths[f][k]))) return f;
        return -1;
    }

    // ---------------- optional components ----------------

    /// <summary>The optional and field whose lane <paramref name="op"/> is: <c>m.Value.X</c> of a Maybe&lt;T&gt; m.</summary>
    private (int Index, int Field)? OptionalLane(IOperation op)
    {
        if (Chain(op) is not ({ } root, { Length: > 1 } members) || members[0].Name != "Value" || root is not IParameterReferenceOperation { Parameter: var parameter }
            || !_optionals.TryGetValue(parameter, out var o))
            return null;
        int f = Leaf(_paths[parameter], members.Skip(1).ToArray());
        return f < 0 ? null : (o.Index, f);
    }

    /// <summary>
    /// A read or write of optional <paramref name="index"/>'s Value for the lanes of <see cref="_active"/>: Value throws
    /// for a row without the component, so where the lanes are not proven to have it, a lane without it sends the
    /// vector's rows to the method itself (which throws as written).
    /// </summary>
    private void Guard(int index)
    {
        if (Implied(_active).Contains(index) || !_guarded.Add($"{index} {_active}")) return;
        _bails = true;
        Live(_active, $"__bail |= ({LiveLanes} & ~__has{index}) != {Vec(_mask)}.Zero;\n");
    }

    /// <summary>Record what mask <paramref name="name"/>, the value of <paramref name="condition"/>, and its negation prove.</summary>
    private void Prove(string name, IOperation condition)
    {
        if (!TracksPresence || !name.StartsWith("__", System.StringComparison.Ordinal) || _implied.ContainsKey(name)) return;
        _implied[name] = Has(condition, true);
        _implied["~" + name] = Has(condition, false);
    }

    /// <summary>The optionals whose component every lane has where <paramref name="condition"/> is <paramref name="holds"/>.</summary>
    private HashSet<int> Has(IOperation condition, bool holds)
    {
        while (condition is IConversionOperation { Conversion.IsIdentity: true } c) condition = c.Operand;
        switch (condition)
        {
            case IPropertyReferenceOperation { Property.Name: "HasValue", Instance: IParameterReferenceOperation { Parameter: var p } } when holds && _optionals.TryGetValue(p, out var o):
                return [o.Index];
            case IPropertyReferenceOperation or IInvocationOperation when holds && LookupHas(condition, false) is { } has: // resolved by the condition itself
                return [int.Parse(has.Substring("__has".Length), System.Globalization.CultureInfo.InvariantCulture)];
            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, Operand: var operand }:
                return Has(operand, !holds);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And or BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or } b
                when IsBool(b.Type):
                var (left, right) = (Has(b.LeftOperand, holds), Has(b.RightOperand, holds));
                // a && b holds: both do; a || b fails: both do. Otherwise either may: what both prove.
                if ((b.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And) == holds) left.UnionWith(right);
                else left.IntersectWith(right);
                return left;
            default:
                return [];
        }
    }

    /// <summary>What a mask (atoms joined by <c>&amp;</c>, as <see cref="And"/> joins them) proves: what any of its atoms does.</summary>
    private HashSet<int> Implied(string? mask)
    {
        var set = new HashSet<int>();
        if (mask == null) return set;
        foreach (var atom in Atoms(mask))
            if (_implied.TryGetValue(atom, out var proven)) set.UnionWith(proven);
        return set;
    }

    /// <summary>What the lanes outside a mask prove: what the negation of every one of its atoms does.</summary>
    private HashSet<int> ImpliedNot(string mask)
    {
        HashSet<int>? set = null;
        foreach (var atom in Atoms(mask))
        {
            var proven = _implied.TryGetValue(atom.StartsWith("~", System.StringComparison.Ordinal) ? atom.Substring(1) : "~" + atom, out var p) ? p : [];
            if (set == null) set = new HashSet<int>(proven);
            else set.IntersectWith(proven);
        }
        return set ?? [];
    }

    private static string[] Atoms(string mask) => mask.Replace("(", "").Replace(")", "").Split([" & "], System.StringSplitOptions.RemoveEmptyEntries);

    // ---------------- expressions ----------------

    /// <summary>
    /// <paramref name="op"/> as a vector of its type's lanes, or as a mask (the mask type, all bits set where true) for
    /// a bool.
    /// </summary>
    private string Expr(IOperation op)
    {
        if (IsUniform(op))
        {
            string scalar = Scalar(op);
            if (IsBool(op.Type)) return $"({scalar} ? {Vec(_mask)}.AllBitsSet : {Vec(_mask)}.Zero)";
            if (!IsLane(op.Type)) throw new Unsupported($"'{Text(op)}' is a {op.Type?.ToDisplayString()}, not a lane type of {LaneSizes} bytes");
            return Broadcast(scalar, op.Type!);
        }
        if (PairExpr(op) is { } pair) return pair;
        if (_entity != null && (IsEntity(Chain(op).Root ?? op) || op is IInvocationOperation { Instance: { } receiver } && IsEntity(receiver))) throw EntityUse(op);

        switch (op)
        {
            case IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c when _narrow && IsLane(c.Type) && IsInt(c.Operand.Type):
                return Truncate(WideOf(c.Operand), c.Type!, c); // (byte)(a + b): the low bits, as C# truncates
            case IConversionOperation { IsChecked: false, OperatorMethod: null } c when _narrow && IsEnumConversion(c) && IsLane(c.Type) && IsInt(c.Operand.Type):
                return Truncate(WideOf(c.Operand), Under(c.Type!), c); // (E)(a + b) of a narrow enum: the same low bits
            case IConversionOperation { Conversion.IsIdentity: true } c when IsLane(c.Type) || IsBool(c.Type):
                return Expr(c.Operand);
            case IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c when IsLane(c.Type) && IsLane(c.Operand.Type):
                return Convert(Expr(c.Operand), c.Operand.Type!, c.Type!) ?? throw Not(c);
            // An enum to its integer or back (or another enum, or another integer of its size): the bits, as C# converts them unchecked.
            case IConversionOperation { IsChecked: false, OperatorMethod: null } c when IsEnumConversion(c) && IsLane(c.Type) && IsLane(c.Operand.Type):
                return Convert(Expr(c.Operand), Under(c.Operand.Type!), Under(c.Type!)) ?? throw Not(c);
            case IFieldReferenceOperation or IPropertyReferenceOperation when LaneOf(op) is { } c:
                return ComponentLane(c.Index, c.Field, op);
            case IPropertyReferenceOperation { Property.Name: "HasValue", Instance: IParameterReferenceOperation { Parameter: var p } } when _optionals.TryGetValue(p, out var o):
                return $"__has{o.Index}";
            case IFieldReferenceOperation or IPropertyReferenceOperation when OptionalLane(op) is { } q:
                Guard(q.Index);
                return $"__q{q.Index}_{q.Field}";
            case IPropertyReferenceOperation or IInvocationOperation when LookupHas(op) is { } has: // x.HasValue, lookup.Has(id)
                return has;
            case IFieldReferenceOperation or IPropertyReferenceOperation when LookupLane(op) is { } gathered: // x.Value.F, lookup.Read(id).F
                return gathered;
            case IFieldReferenceOperation or IPropertyReferenceOperation when IsLane(op.Type) && ((IMemberReferenceOperation)op).Instance is { } instance
                && StructPaths(instance.Type) != null && Accumulator(op) == null:
                return Member((IMemberReferenceOperation)op) is { Length: 1 } one && one[0] is { } lane ? lane : throw new Unsupported("a statement mixes fields"); // p.X of a struct value
            case IFieldReferenceOperation when Accumulator(op) != null:
                throw new Unsupported($"it reads '{Text(op)}', a partial result the lanes of a vector loop hold in parts");
            case ILocalReferenceOperation { Local: var local } when _locals.TryGetValue(local, out var name):
                return Local(local, name, op);
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } b:
                return Binary(b.OperatorKind, b.LeftOperand, b.RightOperand, b.Type);
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } u:
                string operand = Expr(u.Operand);
                return u.OperatorKind switch
                {
                    UnaryOperatorKind.Plus when IsLane(u.Type) => operand,
                    UnaryOperatorKind.Minus when IsLane(u.Type) && IsSigned(u.Type) => $"(-{operand})",
                    UnaryOperatorKind.BitwiseNegation when IsLane(u.Type) && !IsFloat(u.Type) => $"(~{operand})",
                    UnaryOperatorKind.Not when IsBool(u.Type) => $"(~{operand})",
                    _ => throw Not(u),
                };
            case IConditionalOperation { IsRef: false, WhenFalse: { } whenFalse } t when IsLane(t.Type) || IsBool(t.Type):
            {
                string condition = Expr(t.Condition);
                if (!TracksPresence || _half >= 0) // a half's masks are not the lanes' (an optional's reads are, Narrowed)
                    return $"{V}.ConditionalSelect({MaskAs(condition, t.Type!)}, {Expr(t.WhenTrue)}, {Expr(whenFalse)})";
                // Each arm reads under its lanes (m.HasValue ? m.Value.X : 0 reads only rows that have it).
                condition = Hold(condition, t.Condition.Type!);
                Prove(condition, t.Condition);
                string? active = _active;
                _active = And(active, condition);
                string whenTrue = Expr(t.WhenTrue);
                _active = And(active, $"~{condition}");
                string otherwise = Expr(whenFalse);
                _active = active;
                return $"{V}.ConditionalSelect({MaskAs(condition, t.Type!)}, {whenTrue}, {otherwise})";
            }
            case IIsPatternOperation { Value: var value } pattern when IsLane(value.Type):
                return Pattern(Expr(value), value.Type!, pattern.Pattern);
            case IIsPatternOperation { Value: var value } pattern when _narrow && IsInt(value.Type):
                return WidePattern(WideOf(value), pattern.Pattern, pattern);
            case IInvocationOperation { Instance: null } call when IsMath(call.TargetMethod):
                return Call(call);
            default:
                throw Not(op);
        }
    }

    /// <summary>A uniform scalar of <paramref name="type"/>, broadcast once per run (before the loop); an enum's as its integer.</summary>
    private string Broadcast(string scalar, ITypeSymbol type) => Broadcast(IsEnum(type) ? $"(({Elem(type)})({scalar}))" : scalar, Elem(type));

    /// <summary>A uniform scalar broadcast to lanes of <paramref name="element"/> (a fully qualified C# type).</summary>
    private string Broadcast(string scalar, string element)
    {
        string key = element + " " + scalar;
        if (!_broadcasts.TryGetValue(key, out var name))
        {
            name = Temp("u");
            _broadcasts[key] = name;
            _setup.Append($"var {name} = {V}.Create<{element}>({scalar});\n"); // once per run, not per vector
        }
        return name;
    }

    /// <summary>
    /// The conversion of a vector between lane types of one size, as C# converts each lane, or null: between signed and
    /// unsigned integers the bits stay (unchecked), an integer to a float rounds to nearest, and a float to an integer
    /// truncates and saturates (NaN to 0), what <c>Vector.ConvertToInt32</c> and its kin do since .NET 9.
    /// </summary>
    private static string? Convert(string value, ITypeSymbol from, ITypeSymbol to)
    {
        if (!IsFloat(from) && !IsFloat(to)) return As(Elem(from), Elem(to), value);
        if (IsFloat(from) && IsFloat(to)) return null; // float and double: not one size
        string? convert = to.SpecialType switch
        {
            SpecialType.System_Single => "ConvertToSingle",
            SpecialType.System_Double => "ConvertToDouble",
            SpecialType.System_Int32 => "ConvertToInt32",
            SpecialType.System_UInt32 => "ConvertToUInt32",
            SpecialType.System_Int64 => "ConvertToInt64",
            SpecialType.System_UInt64 => "ConvertToUInt64",
            _ => null,
        };
        return convert == null ? null : $"{V}.{convert}({value})";
    }

    /// <summary>A comparison of two vectors of <paramref name="type"/> as a mask.</summary>
    private string Compare(string compare, string left, string right, ITypeSymbol type) =>
        As(Elem(type), _mask, $"{V}.{compare}<{Elem(type)}>({left}, {right})");

    private string Binary(BinaryOperatorKind kind, IOperation leftOp, IOperation right, ITypeSymbol? type)
    {
        type ??= leftOp.Type;
        var leftType = leftOp.Type;
        if (kind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift)
        {
            if (!IsLane(type) || IsFloat(type)) throw new Unsupported($"shifts of a {type?.ToDisplayString()} are not supported");
            if (!IsUniform(right)) throw new Unsupported("a shift by a count that differs per row is not supported");
            string count = $"(int)({Scalar(right)})"; // C# masks the count (& 31 / & 63), and so do the vector shifts
            string shifted = Expr(leftOp);
            return kind == BinaryOperatorKind.LeftShift ? $"{V}.ShiftLeft({shifted}, {count})"
                : $"{V}.{(kind == BinaryOperatorKind.RightShift && IsSigned(type) ? "ShiftRightArithmetic" : "ShiftRightLogical")}({shifted}, {count})";
        }

        if (_narrow && (IsInt(leftType) || IsInt(right.Type)))
            return WideCompare(kind, WideOf(leftOp), WideOf(right), leftOp, right);
        bool lanes = IsLane(leftType) && IsLane(right.Type) && SymbolEqualityComparer.Default.Equals(leftType, right.Type);
        bool masks = IsBool(leftType) && IsBool(right.Type);
        if (masks && (TracksPresence || MayThrow(right)) && _half < 0 && kind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
        {
            // The right side reads under the lanes the left lets through (m.HasValue && m.Value.X > 0, d != 0 && n / d > 1).
            string left = Hold(Expr(leftOp), leftType!);
            Prove(left, leftOp);
            string? active = _active;
            _active = And(active, kind == BinaryOperatorKind.ConditionalAnd ? left : $"~{left}");
            string other = Expr(right);
            _active = active;
            return $"({left} {(kind == BinaryOperatorKind.ConditionalAnd ? "&" : "|")} {other})";
        }
        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && lanes && !IsFloat(type))
            return IntegerDivision(kind == BinaryOperatorKind.Remainder, leftOp, right, type!);
        if (kind == BinaryOperatorKind.Remainder) throw new Unsupported("'%' of floats is not supported (no vector remainder)");
        if (kind == BinaryOperatorKind.Divide && lanes && IsFloat(type) && Reciprocal(right, type!) is { } reciprocal)
            return $"({Expr(leftOp)} * {reciprocal})";
        if (!lanes && !masks) throw new Unsupported($"operator {Operator(kind)} on {leftType?.ToDisplayString()} and {right.Type?.ToDisplayString()} is not supported");
        string l = Expr(leftOp), r = Expr(right);
        return kind switch
        {
            BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply when lanes && IsLane(type)
                => $"({l} {Operator(kind)} {r})",
            BinaryOperatorKind.Divide when lanes && IsFloat(type) => $"({l} / {r})",
            BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr when lanes && !IsFloat(type) || masks
                => $"({l} {Operator(kind)} {r})",
            BinaryOperatorKind.ConditionalAnd when masks => $"({l} & {r})",  // no side effects: both sides are evaluated
            BinaryOperatorKind.ConditionalOr when masks => $"({l} | {r})",
            BinaryOperatorKind.Equals when masks => $"(~({l} ^ {r}))",
            BinaryOperatorKind.NotEquals when masks => $"({l} ^ {r})",
            BinaryOperatorKind.Equals when lanes => Compare("Equals", l, r, leftType!),
            BinaryOperatorKind.NotEquals when lanes => $"(~{Compare("Equals", l, r, leftType!)})", // NaN != NaN: true, as in C#
            BinaryOperatorKind.LessThan when lanes => Compare("LessThan", l, r, leftType!),
            BinaryOperatorKind.LessThanOrEqual when lanes => Compare("LessThanOrEqual", l, r, leftType!),
            BinaryOperatorKind.GreaterThan when lanes => Compare("GreaterThan", l, r, leftType!),
            BinaryOperatorKind.GreaterThanOrEqual when lanes => Compare("GreaterThanOrEqual", l, r, leftType!),
            _ => throw new Unsupported($"operator {Operator(kind)} on {leftType?.ToDisplayString()} is not supported"),
        };
    }

    /// <summary>
    /// x / y or x % y of integer lanes, as C# computes them: the quotient truncated toward zero, the remainder with the
    /// sign of x. By a constant power of two, shifts: a negative x first adds 2^k - 1, so the shift rounds toward zero,
    /// and the remainder is x less its rounded-down multiple of 2^k. By any other divisor, lanes of 4 bytes divide as
    /// doubles, exactly (<c>EasyEcs.LaneMath</c>'s remarks); a lane that would throw (a divisor of 0, int.MinValue / -1)
    /// sends its vector's rows to the method itself (which throws as written) and divides by 1 in the meantime.
    /// </summary>
    private string IntegerDivision(bool remainder, IOperation leftOp, IOperation right, ITypeSymbol type)
    {
        string what = remainder ? "'%'" : "integer division";
        if (_half >= 0) throw new Unsupported($"{what} of lanes of two sizes is not supported");
        int bits = SizeOf(type) * 8;
        bool signed = IsSigned(type);
        string zero = $"{VecOf(type)}.Zero";
        string MinValue() => Broadcast(bits == 32 ? "int.MinValue" : "long.MinValue", type);
        string Quotient(string x, string y) => $"global::EasyEcs.LaneMath.Quotient({x}, {y})";
        string x = Hold(Expr(leftOp), type);
        if (right.ConstantValue is { HasValue: true, Value: { } constant })
        {
            long d = signed ? System.Convert.ToInt64(constant, CultureInfo.InvariantCulture) : unchecked((long)System.Convert.ToUInt64(constant, CultureInfo.InvariantCulture));
            ulong magnitude = signed && d < 0 ? unchecked((ulong)-(d + 1) + 1) : unchecked((ulong)d);
            if ((magnitude & (magnitude - 1)) == 0)
            {
                int k = 0;
                while (magnitude >> k != 1) k++;
                if (k == 0 && d == 1) return remainder ? zero : x;
                if (k == 0) // -1: int.MinValue / -1 throws, and so does int.MinValue % -1
                {
                    Bail(Compare("Equals", x, MinValue(), type));
                    return remainder ? zero : $"({zero} - {x})";
                }
                if (!signed)
                    return remainder ? $"({x} & {Broadcast(Literal(System.Convert.ChangeType(magnitude - 1, ClrType(type), CultureInfo.InvariantCulture), type), type)})"
                        : $"{V}.ShiftRightLogical({x}, {k})";
                // x + (2^k - 1) where x is negative: the sign spread over the low k bits
                string rounded = Hold($"({x} + {V}.ShiftRightLogical({V}.ShiftRightArithmetic({x}, {bits - 1}), {bits - k}))", type);
                if (remainder)
                {
                    object multiples = bits == 32 ? unchecked((int)-(1L << k)) : (object)unchecked(-(1L << k));
                    return $"({x} - ({rounded} & {Broadcast(Literal(multiples, type), type)}))";
                }
                string quotient = $"{V}.ShiftRightArithmetic({rounded}, {k})";
                return d < 0 ? $"({zero} - {quotient})" : quotient;
            }
            if (bits != 32) throw new Unsupported($"{what} of {type.ToDisplayString()} lanes by {Text(right)}, not a power of two, is not supported (no exact vector form for lanes of 8 bytes)");
            string divisor = Expr(right);
            string q = remainder ? Hold(Quotient(x, divisor), type) : Quotient(x, divisor);
            return remainder ? $"({x} - ({q} * {divisor}))" : q;
        }
        if (bits != 32) throw new Unsupported($"{what} of {type.ToDisplayString()} lanes by a value that is not a constant power of two is not supported (no exact vector form for lanes of 8 bytes)");
        string y = Hold(Expr(right), type);
        string bad = Compare("Equals", y, zero, type);
        if (signed) bad = $"({bad} | ({Compare("Equals", x, MinValue(), type)} & {Compare("Equals", y, Broadcast("-1", type), type)}))";
        bad = Hold(bad, type);
        Bail(bad);
        string safe = Hold($"{V}.ConditionalSelect({MaskAs(bad, type)}, {VecOf(type)}.One, {y})", type);
        if (!remainder) return Quotient(x, safe);
        string quotientOfSafe = Hold(Quotient(x, safe), type);
        return $"({x} - ({quotientOfSafe} * {safe}))";
    }

    /// <summary>The lanes of <paramref name="mask"/> that are evaluated (<see cref="_active"/>) send their vector's rows to the method itself.</summary>
    private void Bail(string mask)
    {
        _bails = true;
        Live(_active, $"__bail |= ({LiveLanes} & {mask}) != {Vec(_mask)}.Zero;\n");
    }

    /// <summary>
    /// The reciprocal of a constant float divisor, broadcast, where x * (1 / d) is x / d in every lane: d a power of two
    /// whose reciprocal is a float (double) too, so both are the one rounding of the same real number. Under
    /// FloatMode.Fast, any finite constant other than 0 (within an ulp). Otherwise null: the lanes divide.
    /// </summary>
    private string? Reciprocal(IOperation divisor, ITypeSymbol type)
    {
        if (divisor.ConstantValue is not { HasValue: true, Value: { } constant }) return null;
        double d = System.Convert.ToDouble(constant, CultureInfo.InvariantCulture);
        if (double.IsNaN(d) || double.IsInfinity(d) || d == 0) return null;
        bool single = type.SpecialType == SpecialType.System_Single;
        long bits = System.BitConverter.DoubleToInt64Bits(d);
        double r = 1 / d;
        bool power = (bits & 0xFFFFFFFFFFFFFL) == 0 && ((bits >> 52) & 0x7FF) != 0; // a normal double with no fraction bits
        bool exact = power && !double.IsInfinity(r) && r != 0 && (!single || (double)(float)r == r);
        if (!exact && !_fast) return null;
        return Broadcast(Literal(single ? (float)r : (object)r, type), type);
    }

    /// <summary>
    /// <c>value is pattern</c> as a mask: relational (<c>&gt; 0</c>), constant (<c>0</c>, <c>float.NaN</c>), <c>and</c>,
    /// <c>or</c>, <c>not</c>, as C# tests each (a constant NaN matches NaN).
    /// </summary>
    private string Pattern(string value, ITypeSymbol type, IPatternOperation pattern)
    {
        switch (pattern)
        {
            case IRelationalPatternOperation relational:
                string compare = relational.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => "LessThan", BinaryOperatorKind.LessThanOrEqual => "LessThanOrEqual",
                    BinaryOperatorKind.GreaterThan => "GreaterThan", BinaryOperatorKind.GreaterThanOrEqual => "GreaterThanOrEqual",
                    _ => throw Not(pattern),
                };
                return Compare(compare, value, Operand(relational.Value, type), type);
            case IConstantPatternOperation { Value: var constant }:
                if (constant.ConstantValue is { HasValue: true, Value: float.NaN or double.NaN })
                    return $"(~{Compare("Equals", value, value, type)})";
                return Compare("Equals", value, Operand(constant, type), type);
            case IBinaryPatternOperation { OperatorKind: var kind, LeftPattern: var left, RightPattern: var right }:
                return $"({Pattern(value, type, left)} {(kind == BinaryOperatorKind.And ? "&" : "|")} {Pattern(value, type, right)})";
            case INegatedPatternOperation { Pattern: var negated }:
                return $"(~{Pattern(value, type, negated)})";
            default:
                throw Not(pattern);
        }
    }

    /// <summary>A pattern's operand as a vector of the tested value's type (a constant converted to it).</summary>
    private string Operand(IOperation op, ITypeSymbol type) =>
        op.ConstantValue is { HasValue: true, Value: { } constant } && !SymbolEqualityComparer.Default.Equals(op.Type, type)
            ? Broadcast(Literal(System.Convert.ChangeType(constant, ClrType(type), CultureInfo.InvariantCulture), type), type)
            : Expr(op);

    private static System.Type ClrType(ITypeSymbol type) => Under(type).SpecialType switch
    {
        SpecialType.System_Single => typeof(float), SpecialType.System_Double => typeof(double), SpecialType.System_Int32 => typeof(int),
        SpecialType.System_UInt32 => typeof(uint), SpecialType.System_Int64 => typeof(long),
        SpecialType.System_Byte => typeof(byte), SpecialType.System_SByte => typeof(sbyte), SpecialType.System_Int16 => typeof(short),
        SpecialType.System_UInt16 => typeof(ushort), _ => typeof(ulong),
    };

    /// <summary>
    /// A call of <c>Math</c>, <c>MathF</c> or a lane type's own static (<c>float.Clamp</c>) with an exact vector form.
    /// Each form computes what the scalar function returns for every lane, NaN and -0 included.
    /// </summary>
    private string Call(IInvocationOperation call)
    {
        var m = call.TargetMethod;
        var type = m.ReturnType;
        if (m.Name == "IsNaN" && IsBool(type) && m.Parameters.Length == 1 && IsFloat(m.Parameters[0].Type) && IsLane(m.Parameters[0].Type))
        {
            string x = Hold(Expr(call.Arguments[0].Value), m.Parameters[0].Type);
            return $"(~{Compare("Equals", x, x, m.Parameters[0].Type)})"; // NaN is the one value unequal to itself
        }
        if (!IsLane(type) || !m.Parameters.All(p => IsLane(p.Type))) throw Not(call);
        string Arg(int i) => Expr(call.Arguments.First(a => a.Parameter?.Ordinal == i).Value);
        bool single = m.Parameters.Length == 1, isFloat = IsFloat(type);
        switch (m.Name)
        {
            case "Min" or "Max" when m.Parameters.Length == 2:     // IEEE 754:2019 like Math.Min / Max (NaN, -0)
                return $"{V}.{m.Name}({Arg(0)}, {Arg(1)})";
            case "Abs" when single && isFloat:
                return $"{V}.Abs({Arg(0)})";
            case "Abs" when single && type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64:
            {
                // Math.Abs(int.MinValue) throws: that lane sends its rows to the method
                string x = Hold(Arg(0), type);
                Bail(Compare("Equals", x, Broadcast(type.SpecialType == SpecialType.System_Int32 ? "int.MinValue" : "long.MinValue", type), type));
                return $"{V}.Abs({x})";
            }
            case "CopySign" when m.Parameters.Length == 2 && type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64:
            {
                // |value| with sign's sign; MinValue made positive throws (its own magnitude with a negative sign is itself)
                string x = Hold(Arg(0), type), negative = Hold(Compare("LessThan", Arg(1), $"{VecOf(type)}.Zero", type), m.Parameters[0].Type);
                Bail($"({Compare("Equals", x, Broadcast(type.SpecialType == SpecialType.System_Int32 ? "int.MinValue" : "long.MinValue", type), type)} & ~{negative})");
                string magnitude = Hold($"{V}.Abs({x})", type);
                return $"{V}.ConditionalSelect({MaskAs(negative, type)}, {VecOf(type)}.Zero - {magnitude}, {magnitude})";
            }
            case "Sign" when single && type.SpecialType == SpecialType.System_Int32 && m.Parameters[0].Type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Single:
            {
                // -1, 0 or 1: the masks are all bits set (-1) where true, so (x < 0) - (x > 0) as integers; Sign(NaN) throws
                var of = m.Parameters[0].Type;
                string x = Hold(Arg(0), of), zero = $"{VecOf(of)}.Zero";
                if (IsFloat(of)) Bail($"(~{Compare("Equals", x, x, of)})");
                return $"({As(_mask, Elem(type), Compare("LessThan", x, zero, of))} - {As(_mask, Elem(type), Compare("GreaterThan", x, zero, of))})";
            }
            case "Pow" when m.Parameters.Length == 2 && isFloat:
                return Pow(call, type);
            case "Log10" when single && type.SpecialType == SpecialType.System_Single && _fast:
                return $"global::EasyEcs.LaneMath.Log10({Arg(0)})"; // ln x * log10(e) in doubles: within an ulp
            case "Log10" when single && isFloat && _fast:                // ln x * log10(e): within a few ulps
                return $"({V}.Log({Arg(0)}) * {Broadcast("0.43429448190325182765d", type)})";
            case "Tan" when single && isFloat && _fast:
            {
                string x = Hold(Arg(0), type);
                return $"({V}.Sin({x}) / {V}.Cos({x}))";
            }
            case "Sinh" or "Cosh" or "Tanh" when single && type.SpecialType == SpecialType.System_Single && _fast:
                return $"global::EasyEcs.LaneMath.{m.Name}({Arg(0)})"; // through double Exp
            case "Tan" or "Log10" or "Sinh" or "Cosh" or "Tanh" when single && isFloat && !_fast && (m.Name is "Tan" or "Log10" || type.SpecialType == SpecialType.System_Single):
                throw new Unsupported($"{m.ContainingType.Name}.{m.Name} is not bit-exact in vector form: set FloatMode = FloatMode.Fast");
            case "Sqrt" when single && isFloat:
                return $"{SquareRoot}{Arg(0)})";
            case "Floor" or "Ceiling" or "Truncate" when single && isFloat:
                return $"{V}.{m.Name}({Arg(0)})";
            case "Round" when single && isFloat:                   // to even, like Math.Round(x)
                return $"{V}.Round({Arg(0)})";
            case "CopySign" when m.Parameters.Length == 2 && isFloat: // of integers it throws for MinValue
                return $"{V}.CopySign({Arg(0)}, {Arg(1)})";
            case "FusedMultiplyAdd" when m.Parameters.Length == 3 && isFloat:
                return $"{V}.FusedMultiplyAdd({Arg(0)}, {Arg(1)}, {Arg(2)})";
            case "Lerp" when m.Parameters.Length == 3 && isFloat:
            {
                // float.Lerp is MultiplyAddEstimate(value1, 1 - amount, value2 * amount): fused exactly where the vector
                // form fuses (FMA), so the lanes round as the scalar call does
                string amount = Hold(Arg(2), type);
                return $"{V}.MultiplyAddEstimate({Arg(0)}, {VecOf(type)}.One - {amount}, {Arg(1)} * {amount})";
            }
            case "Clamp" when m.Parameters.Length == 3:
                return Clamp(call, type);
            case "DegreesToRadians" or "RadiansToDegrees" when single && isFloat:
            {
                // float.DegreesToRadians is (x * Pi) / 180, and RadiansToDegrees (x * 180) / Pi: the same two IEEE
                // operations per lane
                bool toRadians = m.Name == "DegreesToRadians";
                string pi = Broadcast(type.SpecialType == SpecialType.System_Single ? "float.Pi" : "double.Pi", type);
                string half = Broadcast(Literal(System.Convert.ChangeType(180, ClrType(type), CultureInfo.InvariantCulture), type), type);
                return toRadians ? $"(({Arg(0)} * {pi}) / {half})" : $"(({Arg(0)} * {half}) / {pi})";
            }
            case "Sin" or "Cos" or "Exp" or "Log" or "Log2" when single && isFloat:
            case "Hypot" when m.Parameters.Length == 2 && isFloat:
                // .NET's vector forms approximate within a few ulps (Vector.Exp(0) is 1 + 1 ulp): not the scalar
                // function's bits, so only where the method accepts approximate floats
                if (!_fast) throw new Unsupported($"{m.ContainingType.Name}.{m.Name} is not bit-exact in vector form: set FloatMode = FloatMode.Fast");
                return m.Parameters.Length == 1 ? $"{V}.{m.Name}({Arg(0)})" : $"{V}.{m.Name}({Arg(0)}, {Arg(1)})";
            default:
                throw Not(call);
        }
    }

    /// <summary>
    /// Math.Pow by a constant exponent: Pow(x, 1) is x and Pow(x, 0) is 1 for every number (a NaN runs the method);
    /// under FloatMode.Fast, 2, 3 and 4 are products, -1 and -2 a division, and 0.5 a square
    /// root with pow's two exceptions (Pow(-0, 0.5) is +0, Pow(-Infinity, 0.5) is +Infinity).
    /// </summary>
    private string Pow(IInvocationOperation call, ITypeSymbol type)
    {
        var m = call.TargetMethod;
        string X() => Expr(call.Arguments.First(a => a.Parameter?.Ordinal == 0).Value);
        var exponent = call.Arguments.First(a => a.Parameter?.Ordinal == 1).Value;
        double? e = exponent.ConstantValue is { HasValue: true, Value: { } c } ? System.Convert.ToDouble(c, CultureInfo.InvariantCulture) : null;
        string one = $"{VecOf(type)}.One";
        if (e is 0 or 1)
        {
            // pow(x, 1) is x and pow(x, 0) is 1 for every number; a NaN's result (its sign, its payload, a signaling
            // NaN's) is the C library's, so a NaN lane sends its rows to the method
            string x0 = Hold(X(), type);
            Bail($"(~{Compare("Equals", x0, x0, type)})");
            return e == 1 ? x0 : one;
        }
        if (!_fast)
            throw new Unsupported(e is 2 or 3 or 4 or -1 or -2 or 0.5
                ? $"{m.ContainingType.Name}.{m.Name} is not bit-exact in vector form except by 0 and 1: set FloatMode = FloatMode.Fast"
                : $"{m.ContainingType.Name}.{m.Name} has no vector form in .NET 10 but by a constant 0, 1 (and under FloatMode.Fast 2, 3, 4, -1, -2 or 0.5)");
        if (e is not (2 or 3 or 4 or -1 or -2 or 0.5))
            throw new Unsupported($"{m.ContainingType.Name}.{m.Name} has no vector form in .NET 10 but by a constant 0, 1, 2, 3, 4, -1, -2 or 0.5");
        string x = Hold(X(), type);
        switch (e)
        {
            case 2: return $"({x} * {x})";
            case 3: return $"(({x} * {x}) * {x})";
            case 4:
            {
                string square = Hold($"({x} * {x})", type);
                return $"({square} * {square})";
            }
            case -1: return $"({one} / {x})";
            case -2:
            {
                // (1 / x)^2, not 1 / x^2: x^2 of a small x is subnormal (bits lost) where 1 / x is not
                string reciprocal = Hold($"({one} / {x})", type);
                return $"({reciprocal} * {reciprocal})";
            }
            default:
                string infinity = Broadcast(type.SpecialType == SpecialType.System_Single ? "float.PositiveInfinity" : "double.PositiveInfinity", type);
                return $"{V}.ConditionalSelect({MaskAs(Compare("Equals", x, $"(-{infinity})", type), type)}, {infinity}, {SquareRoot}{x} + {VecOf(type)}.Zero))";
        }
    }

    /// <summary>
    /// Math.Clamp: the min when the value is below it, else the max when above it, else the value (NaN stays NaN). It
    /// throws when min is above max: bounds known to be in order need nothing, bounds that may not be make a lane where
    /// they are not send its vector's rows to the method itself (which throws as written).
    /// </summary>
    private string Clamp(IInvocationOperation call, ITypeSymbol type)
    {
        IOperation Arg(int i) => call.Arguments.First(a => a.Parameter?.Ordinal == i).Value;
        IOperation min = Arg(1), max = Arg(2);
        string value = Hold(Expr(Arg(0)), type), lo = Hold(Expr(min), type), hi = Hold(Expr(max), type);
        if (min.ConstantValue is { HasValue: true, Value: { } a } && max.ConstantValue is { HasValue: true, Value: { } b })
        {
            if (System.Convert.ToDouble(a, CultureInfo.InvariantCulture) > System.Convert.ToDouble(b, CultureInfo.InvariantCulture)
                && !(IsFloat(type) && (double.IsNaN(System.Convert.ToDouble(a, CultureInfo.InvariantCulture)) || double.IsNaN(System.Convert.ToDouble(b, CultureInfo.InvariantCulture)))))
                throw new Unsupported($"{call.TargetMethod.ContainingType.Name}.Clamp's min is above its max, so the call throws");
        }
        else
        {
            _bails = true;
            _body.Append($"__bail |= {V}.GreaterThanAny({lo}, {hi});\n");
        }
        string below = MaskAs(Compare("LessThan", value, lo, type), type), above = MaskAs(Compare("GreaterThan", value, hi, type), type);
        return $"{V}.ConditionalSelect({below}, {lo}, {V}.ConditionalSelect({above}, {hi}, {value}))";
    }

    /// <summary>A value used twice, in a local of the loop body (a uniform or a lane is already one).</summary>
    private string Hold(string value, ITypeSymbol type)
    {
        if (value.StartsWith("__", System.StringComparison.Ordinal) && value.All(c => char.IsLetterOrDigit(c) || c == '_')) return value;
        string name = Temp("t");
        _body.Append($"var {name} = {value};\n");
        Alias(name, value);
        return name;
    }

    /// <summary>Whether <paramref name="op"/> holds an operation that throws for some lanes (they send their rows to the method).</summary>
    private static bool MayThrow(IOperation op) => op.DescendantsAndSelf().Any(o =>
        o is IBinaryOperation { OperatorKind: BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder } b && !IsFloat(b.Type)
        || o is IInvocationOperation { TargetMethod: { Name: "Clamp" or "Abs" or "CopySign" or "Sign" } m } && IsMath(m));

    private static bool IsMath(IMethodSymbol m) =>
        m.IsStatic && m.ContainingType is { ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } t
        && (t.Name is "Math" or "MathF" || IsLaneType(t));

    // ---------------- uniform values ----------------

    /// <summary>
    /// The same in every lane: it reads no component and no kernel local (constants, statics, singletons, context). What
    /// may throw is not (Math.Clamp, integer division, Math.Abs / CopySign / Sign of an integer or a float's sign): it
    /// runs per lane, where a bad lane sends its rows to the method, so a run without rows throws nothing.
    /// </summary>
    private bool IsUniform(IOperation op) => op switch
    {
        ILocalReferenceOperation or IInstanceReferenceOperation => false,
        IParameterReferenceOperation { Parameter: var p } => _uniformParams.ContainsKey(p),
        IInvocationOperation { TargetMethod.Name: "Clamp" } => false,
        IBinaryOperation { OperatorKind: BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder } b when !IsFloat(b.Type) => false,
        IInvocationOperation { TargetMethod: { Name: "Abs" or "CopySign" or "Sign" } m } when !IsFloat(m.ReturnType) => false,
        _ => op.ChildOperations.All(IsUniform),
    };

    /// <summary>A uniform value as scalar C# the generated file can compile (fully qualified, constants folded).</summary>
    private string Scalar(IOperation op)
    {
        if (op.ConstantValue is { HasValue: true, Value: var constant }) return Literal(constant, op.Type);
        switch (op)
        {
            case IParameterReferenceOperation { Parameter: var p }:
                return _uniformParams[p];
            case IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } f }:
                return $"{f.ContainingType.ToDisplayString(Symbols.Fqn)}.{f.Name}";
            case IFieldReferenceOperation { Field: { IsStatic: false } f, Instance: { } instance }:
                return $"{Scalar(instance)}.{f.Name}";
            case IPropertyReferenceOperation { Property: { IsStatic: false, Parameters.Length: 0 } p, Instance: { } instance }
                when Symbols.BackingField(p) != null:
                return $"{Scalar(instance)}.{p.Name}"; // an auto-property: a field read, no code of its own
            case IConversionOperation { OperatorMethod: null, IsTryCast: false, Conversion.IsNumeric: true } or IConversionOperation { Conversion.IsIdentity: true }
                or IConversionOperation { OperatorMethod: null, IsTryCast: false } when IsEnumConversion((IConversionOperation)op):
                var c = (IConversionOperation)op;
                return $"(({c.Type!.ToDisplayString(Symbols.Fqn)})({Scalar(c.Operand)}))";
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } b when b.OperatorKind is not (BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) || IsFloat(b.Type):
                return $"({Scalar(b.LeftOperand)} {BinaryText(b.OperatorKind)} {Scalar(b.RightOperand)})";
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } u:
                return $"({UnaryText(u.OperatorKind)}{Scalar(u.Operand)})";
            case IInvocationOperation { Instance: null, TargetMethod: var m } call when IsMath(m)
                && (m.Name is "Min" or "Max" or "Sqrt" or "Floor" or "Ceiling" or "Truncate" or "Round" or "FusedMultiplyAdd" or "Lerp"
                    || m.Name is "Abs" or "CopySign" && IsFloat(m.ReturnType)
                    // pure functions of floats: the scalar call once per run gives every lane its bits
                    || IsFloat(m.ReturnType) && m.Parameters.All(q => IsFloat(q.Type)) && m.Name is "Sin" or "Cos" or "Tan" or "Asin" or "Acos"
                        or "Atan" or "Atan2" or "Sinh" or "Cosh" or "Tanh" or "Exp" or "Log" or "Log2" or "Log10" or "Pow" or "Cbrt"
                        or "Hypot" or "DegreesToRadians" or "RadiansToDegrees"):
                return $"{m.ContainingType.ToDisplayString(Symbols.Fqn)}.{m.Name}({string.Join(", ", call.Arguments.Select(a => Scalar(a.Value)))})";
            default:
                // a call or a property could do anything: not hoisted out of the rows
                throw new Unsupported($"'{Text(op)}' is not a value the loop can compute once per run (a call or a property could do anything)");
        }
    }

    /// <summary>The C# operator of <paramref name="kind"/> in a uniform value (string concatenation and the like are not).</summary>
    private static string BinaryText(BinaryOperatorKind kind) =>
        Operator(kind) is var text && !char.IsLetter(text[0]) ? text : throw new Unsupported($"operator {text} is not supported");

    private static string UnaryText(UnaryOperatorKind kind) => kind switch
    {
        UnaryOperatorKind.Plus => "+", UnaryOperatorKind.Minus => "-", UnaryOperatorKind.BitwiseNegation => "~", UnaryOperatorKind.Not => "!",
        _ => throw new Unsupported($"operator {kind} is not supported"),
    };

    private static string Literal(object? value, ITypeSymbol? type) => value switch
    {
        float f when float.IsNaN(f) => "float.NaN",
        float f when float.IsPositiveInfinity(f) => "float.PositiveInfinity",
        float f when float.IsNegativeInfinity(f) => "float.NegativeInfinity",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
        double d when double.IsNaN(d) => "double.NaN",
        double d when double.IsPositiveInfinity(d) => "double.PositiveInfinity",
        double d when double.IsNegativeInfinity(d) => "double.NegativeInfinity",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
        bool b => b ? "true" : "false",
        int i => i == int.MinValue ? "int.MinValue" : $"({i.ToString(CultureInfo.InvariantCulture)})",
        uint u => u.ToString(CultureInfo.InvariantCulture) + "u",
        long l => l == long.MinValue ? "long.MinValue" : $"({l.ToString(CultureInfo.InvariantCulture)}L)",
        ulong u => u.ToString(CultureInfo.InvariantCulture) + "UL",
        _ when type != null && Under(type) is { SpecialType: not SpecialType.None } lane => $"(({lane.ToDisplayString(Symbols.Fqn)})({System.Convert.ToString(value, CultureInfo.InvariantCulture)}))",
        _ => throw new Unsupported($"the constant {value} is not supported"),
    };

    // ---------------- reasons ----------------

    /// <summary>The reason <paramref name="op"/> keeps the method scalar, named the way the method writes it.</summary>
    private Unsupported Not(IOperation op)
    {
        // A call of the user's own code the inliner left (Kernel.Inlining.cs), or one it cannot see into.
        if (op is IInvocationOperation { TargetMethod: var target } && !IsMath(target) && !KnownVector(target.ContainingType)
            || op is IPropertyReferenceOperation { Property.GetMethod: not null } && op.Syntax.GetAnnotations(CallReason).Any())
        {
            var (text, fixable) = ReasonForCall(op, op is IInvocationOperation invocation ? invocation.TargetMethod : ((IPropertyReferenceOperation)op).Property.GetMethod!);
            return new Unsupported(text, fixable);
        }

        return NotSupported(op);
    }

    private Unsupported NotSupported(IOperation op) => op is ISwitchExpressionOperation select ? NotSupported(select) : new(op switch
    {
        IInvocationOperation { TargetMethod: var m } when IsMath(m) => IsFloat(m.ReturnType) && m.Name is "Asin" or "Acos" or "Atan" or "Atan2" or "Cbrt"
            || m.ReturnType.SpecialType == SpecialType.System_Double && m.Name is "Sinh" or "Cosh" or "Tanh"
            ? $"{m.ContainingType.Name}.{m.Name} has no vector form in .NET 10"
            : $"{m.ContainingType.Name}.{m.Name}{(m.Parameters.Length > 0 && !m.Parameters.All(p => IsLane(p.Type)) ? $" of {string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString()))}" : "")} is not supported",
        IInvocationOperation { TargetMethod: var m } when KnownVector(m.ContainingType) && m.Name is "Dot" or "Length" or "LengthSquared" or "Distance"
            or "DistanceSquared" or "Normalize" or "Normalized" or "Cross" or "Sum"
            => $"{m.ContainingType.Name}.{m.Name} adds the fields of a vector together, in an order not proven to be the scalar code's",
        IInvocationOperation { TargetMethod: var m } => $"it calls {m.ContainingType.Name}.{m.Name}",
        IConversionOperation c => $"the conversion of '{Text(c.Operand)}' from {c.Operand.Type?.ToDisplayString()} to {c.Type?.ToDisplayString()} is not supported",
        IBinaryOperation { IsChecked: true } or IUnaryOperation { IsChecked: true } or ICompoundAssignmentOperation { IsChecked: true }
            => $"checked arithmetic ('{Text(op)}') is not supported",
        IBinaryOperation { OperatorMethod: not null } or IUnaryOperation { OperatorMethod: not null } or ICompoundAssignmentOperation { OperatorMethod: not null }
            => $"it calls a user-defined operator ('{Text(op)}')",
        _ => $"'{Text(op)}' is not supported",
    });

    /// <summary>
    /// A switch expression: a vector loop has none, and the conditional expressions it is made of are what the user writes
    /// instead. Over a call on a struct (a helper struct walking other entities' data, VoxelServer's
    /// <c>new Nearby(...).Trail(x, z) switch</c>) that call runs row by row, so the split names it: the call in a pass of
    /// its own storing its result in a component, the switch over that field here.
    /// </summary>
    private static Unsupported NotSupported(ISwitchExpressionOperation select)
    {
        var value = select.Value;
        while (value is IConversionOperation { IsImplicit: true } conversion) value = conversion.Operand;
        const string conditionals = "conditional expressions ('v == A ? a : v == B ? b : c')";
        return value is IInvocationOperation { Instance.Type: { TypeKind: TypeKind.Struct } type, TargetMethod: var m } && !IsMath(m) && !KnownVector(type)
            ? new($"it switches over a call on struct {type.Name} ('{Text(value)}'), which runs row by row, and a vector loop has no switch: call "
                  + $"{type.Name}.{m.Name} in a [ForEach] method of its own that stores the result in a component, and switch over that field here with {conditionals}", true)
            : new($"it has a switch expression ('{Text(select)}'), which a vector loop has no form of: write it as {conditionals}", true);
    }

    /// <summary>The source of <paramref name="op"/> on one line, cut short.</summary>
    private static string Text(IOperation op)
    {
        string text = string.Join(" ", op.Syntax.ToString().Split(['\r', '\n', '\t', ' '], System.StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
    }

    private static string Operator(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*", BinaryOperatorKind.Divide => "/",
        BinaryOperatorKind.Remainder => "%", BinaryOperatorKind.And => "&", BinaryOperatorKind.Or => "|", BinaryOperatorKind.ExclusiveOr => "^",
        BinaryOperatorKind.ConditionalAnd => "&&", BinaryOperatorKind.ConditionalOr => "||",
        BinaryOperatorKind.LeftShift => "<<", BinaryOperatorKind.RightShift => ">>", BinaryOperatorKind.UnsignedRightShift => ">>>",
        BinaryOperatorKind.Equals => "==", BinaryOperatorKind.NotEquals => "!=", BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=", BinaryOperatorKind.GreaterThan => ">", BinaryOperatorKind.GreaterThanOrEqual => ">=",
        _ => kind.ToString(),
    };
}
