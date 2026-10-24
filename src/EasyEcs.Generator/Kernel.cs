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
/// conversions saturate like C#'s. A call that would throw for some lane (<c>Math.Clamp</c> with its min above its max)
/// sends the rows of its vector to the method itself. Anything else (other calls, integer division, checked arithmetic,
/// <c>Math.Abs</c> of an integer) leaves the method scalar, as written. Lanes of 1 and 2 bytes (byte, sbyte, short,
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
    /// on AVX-512 alone (no Vector&lt;T&gt; loop, masked words run the method); else null.
    /// </param>
    /// <param name="Statics">The job's static members: the permutes' index vectors and the layout checks.</param>
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
    internal sealed record FlatCode(string Setup, string Body, string Stores, int Fields);

    /// <summary>
    /// The vector type the loop names, as a token: <c>SystemGenerator.EmitVectorLoop</c> writes the Vector512 loop and the
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
    private readonly StringBuilder _setup = new(), _body = new();
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
    private bool _live;       // a guard reads __live: the lanes of the vector the loop computes for
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
        reason = Symbols.Simd(method) == Symbols.SimdOff ? new ScalarReason("SimdMode = SimdMode.Off", false, null)
            : !method.IsStatic ? new ScalarReason("it is not static: make it static", true, null)
            : Declined(method, syntax, model) is { } declined ? new ScalarReason(declined.Reason, declined.IsFixable, null)
            : null;
        if (reason != null) return null;
        try
        {
            return Translate(method, syntax, model, fieldOf, ct);
        }
        catch (Unsupported unsupported)
        {
            reason = new ScalarReason(unsupported.Reason, unsupported.IsFixable, unsupported.SplitComponent);
            return null;
        }
    }

    /// <summary>
    /// Whether a method <see cref="Vectorize"/> leaves scalar looks meant for a vector loop, so EECS055 names the reason:
    /// it is static, takes only components (by <c>ref</c> or <c>in</c>), Maybe&lt;T&gt; of them, singletons and values (no
    /// Entity, World, span or Mut), and each component is a struct of lane-type fields only (float, double, int, uint,
    /// long, ulong, byte, sbyte, short, ushort).
    /// The reason is then about its components or its body.
    /// </summary>
    public static bool LooksEligible(IMethodSymbol method)
    {
        if (!method.IsStatic) return false;
        bool outside = Symbols.IsRunner(method), any = false;
        foreach (var p in method.Parameters)
            switch (Symbols.Classify(p, outside))
            {
                case Symbols.ParamKind.Component:
                    if (!LaneFields(p.Type)) return false;
                    any = true;
                    break;
                case Symbols.ParamKind.Optional when Symbols.MaybeElement(p.Type) is { } element && LaneFields(element):
                    break;
                case Symbols.ParamKind.Singleton or Symbols.ParamKind.Context or Symbols.ParamKind.Reduction:
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

    private static bool IsLaneType(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    private static int SizeOf(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => 1,
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
        if (Symbols.WithBody(method, syntax, model).Item1 is { Body: null, ExpressionBody: null }) return null; // Translate: "it has no body"
        bool outside = Symbols.IsRunner(method);
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            if (Refused(p, kind, method) is { } refused) return new Unsupported(refused);
            if (kind is Symbols.ParamKind.Component or Symbols.ParamKind.Optional) return null; // its lanes decide
        }

        return null;
    }

    /// <summary>
    /// Why parameter <paramref name="p"/> of kind <paramref name="kind"/> rules a vector loop out whatever its type, or null.
    /// None is fixable: each parameter does what the method needs (a value taken by ref may be a running index rather
    /// than a sum, so a Reduce&lt;T&gt; is not always the same method).
    /// </summary>
    private static string? Refused(IParameterSymbol p, Symbols.ParamKind kind, IMethodSymbol method) => kind switch
    {
        Symbols.ParamKind.Component or Symbols.ParamKind.Singleton or Symbols.ParamKind.Reduction => null,
        Symbols.ParamKind.Context => p.RefKind == RefKind.Ref ? $"it takes '{p.Name}' by ref: a vector loop reduces only into a Reduce<T>" : null,
        Symbols.ParamKind.Optional => Symbols.IsParallel(method)
            ? $"it takes a Maybe<{Symbols.MaybeElement(p.Type)?.Name}> and runs in parallel: an optional column has a vector loop on one thread"
            : null,
        Symbols.ParamKind.Entity or Symbols.ParamKind.Emit => null, // its uses decide (Kernel.Structural.cs)
        Symbols.ParamKind.Entities => "it takes the EntitySpan",
        Symbols.ParamKind.World => "it takes the World",
        Symbols.ParamKind.ComponentSpan => "it takes spans: a batch method writes its own loop",
        Symbols.ParamKind.Mutable => $"it takes a Mut<{Symbols.MutElement(p.Type)?.Name}>",
        Symbols.ParamKind.Scratch => $"it takes a Scratch<{Element(p.Type)}>: working memory a row at a time, not one per lane",
        Symbols.ParamKind.Lookup => $"it takes a Lookup<{Element(p.Type)}>: a lookup of another entity per row has no vector form",
        _ => $"parameter '{p.Name}' is not a component, a singleton, a Reduce<T> or a value",
    };

    private static Code Translate(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct)
    {
        (syntax, model) = Symbols.WithBody(method, syntax, model); // Declined: it returns nothing and is not generic
        if (model.GetOperation(syntax, ct) is not IMethodBodyOperation body) throw new Unsupported("it has no body");
        ITypeSymbol? first = null;
        var components = new List<IFieldSymbol[]>();
        var componentTypes = new List<ITypeSymbol>();
        var wides = new List<WideLayout?>();
        var optionals = new List<IFieldSymbol[]>();
        bool outside = Symbols.IsRunner(method);
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
                case Symbols.ParamKind.Optional when !Symbols.IsParallel(method):
                    optionals.Add(Lanes(Symbols.MaybeElement(p.Type)!));
                    break;
                default:
                    if (Refused(p, kind, method) is { } refused) throw new Unsupported(refused);
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
            var made = new Kernel(SizeOf(first)) { _fast = Symbols.FastFloats(method), _compilation = model.Compilation, _model = model, _stable = Symbols.IsStable(method, syntax, model) };
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
                if (kind == Symbols.ParamKind.Reduction)
                {
                    if (reductions) made._reductions[p] = made.Reduced(p, i, fieldOf(i, kind), Symbols.FastFloats(method), model.Compilation, ct);
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
                string name = $"__p{i}";
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
            string column = Elem(fields[0].Type);
            var loads = new StringBuilder();
            columns.Add(([kernel._wideOf.ContainsKey(p) ? "" : $"__c{index}_"], ""));
            if (kernel._wideOf.TryGetValue(p, out var wide)) kernel.WideLoads(index, wide, column, loads);
            else
                foreach (var (h, row) in kernel.Rows(index, column))
                {
                    if (fields.Length == 1) loads.Append($"var __c{index}_0{h} = {V}.LoadUnsafe(ref e{index}, {row});\n");
                    else
                    {
                        loads.Append($"{Interleaved}.Load{fields.Length}(ref e{index}, {row}, {string.Join(", ", fields.Select((_, f) => $"out {Vec(column)} __s{index}_{f}{h}"))});\n");
                        for (int f = 0; f < fields.Length; f++)
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
                loads.Append($"{Interleaved}.Load{fields.Length}(ref __r{index}, {row}, {string.Join(", ", fields.Select((_, f) => $"out {Vec(column)} __t{index}_{f}"))});\n");
                for (int f = 0; f < fields.Length; f++)
                    loads.Append($"var __q{index}_{f} = {As(column, Elem(fields[f].Type), $"__t{index}_{f}")};\n");
            }
            columns.Add(([$"__q{index}_", $"__has{index}"], loads.ToString()));
        }
        var root = (IOperation?)body.BlockBody ?? body.ExpressionBody!;
        kernel.Statement(root, null);
        kernel.FinishChanges(method);
        // A masked word keeps the values loaded of what the method writes, and stores them back in the lanes that do not
        // pass: the rows are left as they were (their memory is the table's, whether a component is absent or not).
        var kept = new StringBuilder();
        var masked = new StringBuilder();
        foreach (var (p, (index, fields, _)) in kernel._components.Where(c => kernel._written.Contains(c.Value.Index)).OrderBy(c => c.Value.Index).Select(c => (c.Key, c.Value)))
        {
            string column = Elem(fields[0].Type);
            // A wide component's masked words run the method (SystemGenerator): only the loop's stores.
            if (kernel._wideOf.TryGetValue(p, out var wide))
            {
                kernel.WideStores(index, wide, column, stores);
                continue;
            }
            foreach (var (h, row) in kernel.Rows(index, column))
            {
                string Blend(int f) => $"{V}.ConditionalSelect({kernel.PassAs(h, Elem(fields[f].Type))}, __c{index}_{f}{h}, __o{index}_{f}{h})";
                for (int f = 0; f < fields.Length; f++) kept.Append($"var __o{index}_{f}{h} = __c{index}_{f}{h};\n");
                stores.Append(fields.Length == 1
                    ? $"{V}.StoreUnsafe(__c{index}_0{h}, ref e{index}, {row});\n"
                    : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, $"__c{index}_{f}{h}")))}, ref e{index}, {row});\n");
                masked.Append(fields.Length == 1
                    ? $"{V}.StoreUnsafe({Blend(0)}, ref e{index}, {row});\n"
                    : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, Blend(f))))}, ref e{index}, {row});\n");
            }
        }
        // A written optional column is stored whole, the lanes of rows without the component as loaded (a read or a
        // write of m.Value runs only where the row has it, else the vector's rows go to the method); nothing for a table
        // without the column. Its stamps: the rows that have it, as the method stamps them.
        foreach (var (index, fields) in kernel._optionals.Values.Where(o => kernel._writtenOptionals.Contains(o.Index)).OrderBy(o => o.Index))
        {
            string column = Elem(fields[0].Type);
            string Blend(int f) => $"{V}.ConditionalSelect({As(kernel._mask, Elem(fields[f].Type), "__pass")}, __q{index}_{f}, __k{index}_{f})";
            for (int f = 0; f < fields.Length; f++) kept.Append($"var __k{index}_{f} = __q{index}_{f};\n");
            string Store(System.Func<int, string> value) => fields.Length == 1
                ? $"if (__z{index} != 0) {V}.StoreUnsafe({value(0)}, ref __r{index}, (nuint)i);\n"
                : $"if (__z{index} != 0) {Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, value(f))))}, ref __r{index}, (nuint)i);\n";
            stores.Append(Store(f => $"__q{index}_{f}"));
            masked.Append(Store(Blend));
            kernel._setup.Append($"m{index}.StampPresent((int)rows, {Visited});\n");
        }
        // The reductions' lane accumulators: kept before the body (a bail sets them back, a masked word keeps the lanes
        // that do not pass), folded after the loop, lane by lane, into the run's partial result.
        var held = new StringBuilder();
        var restore = new StringBuilder();
        var fold = new StringBuilder();
        string count = $"{Vec(Elem(first))}.Count";
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
                    masked.Insert(0, $"{acc}{h} = {V}.ConditionalSelect({kernel.PassAs(h, type)}, {acc}{h}, __h{r.Index}_{f}{h});\n");
                }
                // lane j of the loop's vectors: lane j of a pair's lower half, or lane j - Count of its upper one
                fold.Append(pair
                    ? $"    __v.{r.Fields[f].Name} = __j < {Vec(type)}.Count ? {acc}_0[__j] : {acc}_1[__j - {Vec(type)}.Count];\n"
                    : $"    __v.{r.Fields[f].Name} = {acc}[__j];\n");
            }
            fold.Append($"    {r.Partial}.Combine(__v);\n}}\n");
        }
        string prologue = kernel._bails ? "bool __bail = false;\n" : "";
        string live = kernel._live ? $"var __live = {Vec(kernel._mask)}.AllBitsSet;\n" : "", maskedLive = kernel._live ? "var __live = __pass;\n" : "";
        string hold = kernel._bails ? held.ToString() : "";
        if (kernel._wideOf.Count > 0) kernel.Gate();
        string uses = kernel._body.ToString() + stores + masked + kernel._setup;
        var read = new StringBuilder();
        foreach (var (names, text) in columns)
            if (names.Any(n => n.Length == 0 || uses.Contains(n))) read.Append(text);
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
        return new Code(new EquatableArray<string>(kernel._components.Values.OrderBy(c => c.Index).Select(c => Elem(c.Fields[0].Type)).ToArray()),
            kernel._setup.ToString(), prologue + live + read + hold + kernel._body, stores.ToString(), prologue + maskedLive + read + kept + held + kernel._body, masked.ToString(),
            restore.ToString(), fold.ToString(), kernel._size, kernel._mask, Elem(first), kernel._components.Any(c => c.Value.Fields.Length > 1 && !kernel._wideOf.ContainsKey(c.Key)),
            kernel._bails, flat, kernel._cold, kernel._wideOf.Count == 0 ? null : string.Join(" && ", kernel._components.Where(c => kernel._wideOf.ContainsKey(c.Key)).Select(c => $"__layout{c.Value.Index}")),
            kernel._statics.ToString())
        {
            StructuralChanges = kernel.StructuralChanges(),
            VectorTail = kernel._change != null || kernel._emits.Values.Any(e => e.Added),
            Written = new EquatableArray<int>(kernel._written.OrderBy(k => k).ToArray()),
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
        if (main._change != null || main._emits.Count > 0 || main._bails || main._alive != null || main._reductions.Count > 0 || main._locals.Count > 0 || main._wides.Count > 0 || main._structs.Count > 0 || main._optionals.Count > 0 || main._wideOf.Count > 0) return null;
        var components = main._components.Values.ToArray();
        var type = components[0].Fields[0].Type;
        int fields = components[0].Fields.Length;
        if (fields < 2 || components.Any(c => c.Fields.Length != fields || c.Fields.Any(f => !SymbolEqualityComparer.Default.Equals(f.Type, type))))
            return null;

        var statements = new List<IOperation>();
        void Flatten(IOperation op)
        {
            if (op is IBlockOperation block) foreach (var inner in block.Operations) Flatten(inner);
            else if (op is not IEmptyOperation) statements.Add(op);
        }
        Flatten(root);
        var groups = new List<IOperation>[fields];
        for (int f = 0; f < fields; f++) groups[f] = new List<IOperation>();
        foreach (var statement in statements)
        {
            int field = -1;
            bool every = false; // position = new Position(position.Value + velocity.Value * dt): each lane alike
            foreach (var op in statement.DescendantsAndSelf())
            {
                if (main.Covers(op) is > 0 and var covered)
                {
                    if (covered != fields) return null;
                    every = true;
                    continue;
                }
                if (main.LaneOf(op) is not { } lane) continue;
                if (field >= 0 && lane.Field != field) return null; // a statement that mixes fields
                field = lane.Field;
            }
            if (every && field >= 0 || !every && field < 0) return null;
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
        return new FlatCode(setup!, loads + body, stores.ToString(), fields);
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
            string name = $"{type.Name}.{field.AssociatedSymbol?.Name ?? field.Name}";
            if (!field.IsFixedSizeBuffer && IsLaneType(field.Type)) paths.Add([field]);
            else if (!field.IsFixedSizeBuffer && field.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } inner
                     && SequentialLayout(inner) && paths.Count < MaxFields * 4)
                foreach (var path in Paths(inner, $"field {name}"))
                    paths.Add([field, .. path]);
            else throw new Unsupported($"field {name} is a {field.Type.ToDisplayString()}, not a lane type (float, double, int, uint, long, ulong, byte, sbyte, short or ushort)");
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
    private static bool IsSigned(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
        or SpecialType.System_SByte or SpecialType.System_Int16 || IsFloat(t);
    private static string Elem(ITypeSymbol type) => type.ToDisplayString(Symbols.Fqn);
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
            case IExpressionStatementOperation { Operation: var e }:
                _active = And(mask, _alive);
                if (!Deferred(e)) Assignment(e, mask);
                return true;
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
            _bails = _live = true;
            string lanes = And(mask, _alive)!;
            _body.Append($"__bail |= (__live & {lanes}) != {Vec(_mask)}.Zero; // a cold branch: the method runs these rows\n");
        }
    }

    /// <summary>What a translation changes, kept to undo a branch that turns out cold.</summary>
    private sealed record State(int Body, int Setup, int Temps, Dictionary<ILocalSymbol, string> Locals, HashSet<ILocalSymbol> PairLocals, Dictionary<ILocalSymbol, Wide> Wides,
        Dictionary<ILocalSymbol, string?[]> Structs, HashSet<int> Written, HashSet<int> WrittenOptionals, Dictionary<string, HashSet<int>> Implied,
        HashSet<string> Guarded, Dictionary<string, string> Broadcasts, Dictionary<string, (string, string)> MaskHalves, string? Alive, bool Bails, bool Live);

    private State Save() => new(_body.Length, _setup.Length, _temps, new(_locals, SymbolEqualityComparer.Default),
        new(_pairLocals, SymbolEqualityComparer.Default), new(_wides, SymbolEqualityComparer.Default),
        new(_structs, SymbolEqualityComparer.Default), [.. _written], [.. _writtenOptionals],
        _implied.ToDictionary(e => e.Key, e => new HashSet<int>(e.Value)), [.. _guarded], new(_broadcasts), new(_maskHalves), _alive, _bails, _live);

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
        (_alive, _bails, _live) = (state.Alive, state.Bails, state.Live);
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

    /// <summary><paramref name="name"/> = <paramref name="value"/> in the lanes of <paramref name="mask"/> that have not returned.</summary>
    private void Write(string name, string value, ITypeSymbol type, string? mask)
    {
        string? active = And(mask, _alive);
        _maskHalves.Remove(name); // a bool local written: the halves it held are not its value any more
        _body.Append(active == null ? $"{name} = {value};\n"
            : $"{name} = {V}.ConditionalSelect({(_half < 0 ? MaskAs(active, type) : As(_mask, IsBool(type) ? _mask : Elem(type), WidenMask(active)))}, {value}, {name});\n");
    }

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
            if (field.AssociatedSymbol != null) throw new Unsupported($"reduction {name} holds {field.AssociatedSymbol.Name} in a property: the lanes fold into fields, so make it a field", true);
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
        SymbolEqualityComparer.Default.Equals(member, field) || SymbolEqualityComparer.Default.Equals(member, field.AssociatedSymbol);

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
        _bails = _live = true;
        string lanes = _active == null ? "__live" : $"(__live & {_active})";
        _body.Append($"__bail |= ({lanes} & ~__has{index}) != {Vec(_mask)}.Zero;\n");
    }

    /// <summary>Record what mask <paramref name="name"/>, the value of <paramref name="condition"/>, and its negation prove.</summary>
    private void Prove(string name, IOperation condition)
    {
        if (_optionals.Count == 0 || !name.StartsWith("__", System.StringComparison.Ordinal) || _implied.ContainsKey(name)) return;
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
            case IConversionOperation { Conversion.IsIdentity: true } c when IsLane(c.Type) || IsBool(c.Type):
                return Expr(c.Operand);
            case IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c when IsLane(c.Type) && IsLane(c.Operand.Type):
                return Convert(Expr(c.Operand), c.Operand.Type!, c.Type!) ?? throw Not(c);
            case IFieldReferenceOperation or IPropertyReferenceOperation when LaneOf(op) is { } c:
                return ComponentLane(c.Index, c.Field, op);
            case IPropertyReferenceOperation { Property.Name: "HasValue", Instance: IParameterReferenceOperation { Parameter: var p } } when _optionals.TryGetValue(p, out var o):
                return $"__has{o.Index}";
            case IFieldReferenceOperation or IPropertyReferenceOperation when OptionalLane(op) is { } q:
                Guard(q.Index);
                return $"__q{q.Index}_{q.Field}";
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
                if (_optionals.Count == 0 || _half >= 0) // a half's masks are not the lanes' (an optional's reads are, Narrowed)
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

    /// <summary>A uniform scalar of <paramref name="type"/>, broadcast once per run (before the loop).</summary>
    private string Broadcast(string scalar, ITypeSymbol type) => Broadcast(scalar, Elem(type));

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
        if (masks && _optionals.Count > 0 && _half < 0 && kind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
        {
            // The right side reads under the lanes the left lets through (m.HasValue && m.Value.X > 0).
            string left = Hold(Expr(leftOp), leftType!);
            Prove(left, leftOp);
            string? active = _active;
            _active = And(active, kind == BinaryOperatorKind.ConditionalAnd ? left : $"~{left}");
            string other = Expr(right);
            _active = active;
            return $"({left} {(kind == BinaryOperatorKind.ConditionalAnd ? "&" : "|")} {other})";
        }
        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && lanes && !IsFloat(type))
            throw new Unsupported("integer division is not supported (it throws on 0)");
        if (kind == BinaryOperatorKind.Remainder) throw new Unsupported("'%' is not supported (no vector remainder)");
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

    private static System.Type ClrType(ITypeSymbol type) => type.SpecialType switch
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
            case "Abs" when single && isFloat:                     // Math.Abs of int.MinValue throws: no integers
                return $"{V}.Abs({Arg(0)})";
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

    private static bool IsMath(IMethodSymbol m) =>
        m.IsStatic && m.ContainingType is { ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } t
        && (t.Name is "Math" or "MathF" || IsLaneType(t));

    // ---------------- uniform values ----------------

    /// <summary>
    /// The same in every lane: it reads no component and no kernel local (constants, statics, singletons, context). A
    /// Math.Clamp is not: it may throw, so it runs per lane, where a bad lane sends its rows to the method.
    /// </summary>
    private bool IsUniform(IOperation op) => op switch
    {
        ILocalReferenceOperation or IInstanceReferenceOperation => false,
        IParameterReferenceOperation { Parameter: var p } => _uniformParams.ContainsKey(p),
        IInvocationOperation { TargetMethod.Name: "Clamp" } => false,
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
                when p.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p)):
                return $"{Scalar(instance)}.{p.Name}"; // an auto-property: a field read, no code of its own
            case IConversionOperation { OperatorMethod: null, IsTryCast: false, Conversion.IsNumeric: true } or IConversionOperation { Conversion.IsIdentity: true }:
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
        _ when type is { SpecialType: not SpecialType.None } => $"(({type.ToDisplayString(Symbols.Fqn)})({System.Convert.ToString(value, CultureInfo.InvariantCulture)}))",
        _ => throw new Unsupported($"the constant {value} is not supported"),
    };

    // ---------------- reasons ----------------

    /// <summary>The reason <paramref name="op"/> keeps the method scalar, named the way the method writes it.</summary>
    private Unsupported Not(IOperation op) => new(op switch
    {
        IInvocationOperation { TargetMethod: var m } when IsMath(m) => m.Name is "Abs" or "CopySign" && !IsFloat(m.ReturnType)
            ? $"{m.ContainingType.Name}.{m.Name} of an integer is not supported (it throws on MinValue)"
            : IsFloat(m.ReturnType) && m.Name is "Tan" or "Asin" or "Acos" or "Atan" or "Atan2" or "Sinh" or "Cosh" or "Tanh" or "Log10" or "Pow" or "Cbrt"
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
