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
    /// <param name="Interleaves">A component has several fields: the loop needs <c>EasyEcs.Interleaved.IsAccelerated</c>.</param>
    /// <param name="Bails">
    /// The body sets <c>__bail</c> when a lane would throw: the rows of that vector then run the method itself, before any
    /// store.
    /// </param>
    /// <param name="Flat">
    /// The loop over flat lanes when the method treats each field alike (<see cref="FlatCode"/>), else null: words of rows
    /// of which only some pass still take the loop above.
    /// </param>
    internal sealed record Code(EquatableArray<string> Columns, string Setup, string Body, string Stores, string MaskedBody,
        string MaskedStores, string Restore, string Fold, int Size, string Mask, bool Interleaves, bool Bails, FlatCode? Flat);

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

    private readonly int _size;      // bytes per lane: 1, 2, 4 or 8
    private readonly string _mask;   // the lane type of masks: sbyte, short, int or long (all bits set where true)
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
    private bool _fast; // FloatMode.Fast: float sums reordered and the vector approximations of Sin, Exp, Log and kin
    private int _temps;
    private int _flat = -1; // translating the statements of field _flat of a flat loop: that field is lane 0, others refused

    private Kernel(int size)
    {
        _size = size;
        _mask = size switch { 1 => "sbyte", 2 => "short", 4 => "int", _ => "long" };
    }

    /// <param name="Index">The parameter's position: its accumulators are <c>__a{Index}_{field}</c>.</param>
    /// <param name="Type">T, the singleton reduced into.</param>
    /// <param name="Fields">T's fields, in declaration order.</param>
    /// <param name="Ops">How T.Combine combines each field: "+", "Min" or "Max".</param>
    /// <param name="Partial">The run's partial result (a <c>Reduce&lt;T&gt;</c> local of the job).</param>
    private sealed record Reduction(int Index, INamedTypeSymbol Type, IFieldSymbol[] Fields, string[] Ops, string Partial);

    /// <summary>Why the method runs one row at a time: the first construct the vector loop cannot express.</summary>
    private sealed class Unsupported(string reason) : System.Exception(reason)
    {
        public string Reason { get; } = reason;
    }

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
        reason = Symbols.Simd(method) == Symbols.SimdOff ? "Simd = SimdMode.Off"
            : !method.IsStatic ? "it is not static"
            : !Symbols.IsStable(method, syntax, model) ? "it can make structural changes"
            : null;
        if (reason != null) return null;
        try
        {
            return Translate(method, syntax, model, fieldOf, ct);
        }
        catch (Unsupported unsupported)
        {
            reason = unsupported.Reason;
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

    private static bool LaneFields(ITypeSymbol type) =>
        type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray() is { Length: > 0 } fields && fields.All(f => IsLaneType(f.Type));

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

    private static Code Translate(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        System.Func<int, Symbols.ParamKind, string> fieldOf, System.Threading.CancellationToken ct)
    {
        if (!method.ReturnsVoid) throw new Unsupported("it returns a value");
        if (method.IsGenericMethod) throw new Unsupported("it is generic");
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        ITypeSymbol? first = null;
        var components = new List<IFieldSymbol[]>();
        var optionals = new List<IFieldSymbol[]>();
        bool outside = Symbols.IsRunner(method);
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            switch (kind)
            {
                case Symbols.ParamKind.Component:
                    var fields = Lanes(p.Type);
                    foreach (var field in fields)
                    {
                        if (first != null && SizeOf(first) != SizeOf(field.Type))
                            throw new Unsupported($"lane types {first.ToDisplayString()} and {field.Type.ToDisplayString()} differ in size");
                        first ??= field.Type;
                    }
                    components.Add(fields);
                    break;
                case Symbols.ParamKind.Singleton:
                    break;
                case Symbols.ParamKind.Context when p.RefKind == RefKind.Ref:
                    throw new Unsupported($"it takes '{p.Name}' by ref: a vector loop reduces only into a Reduce<T>");
                case Symbols.ParamKind.Reduction:
                    break;
                case Symbols.ParamKind.Context:
                    break;
                case Symbols.ParamKind.Optional when Symbols.IsParallel(method):
                    throw new Unsupported($"it takes a Maybe<{Symbols.MaybeElement(p.Type)?.Name}> and runs in parallel: an optional column has a vector loop on one thread");
                case Symbols.ParamKind.Optional:
                    optionals.Add(Lanes(Symbols.MaybeElement(p.Type)!));
                    break;
                default:
                    throw new Unsupported(kind switch
                    {
                        Symbols.ParamKind.Entity => "it takes the Entity",
                        Symbols.ParamKind.Entities => "it takes the EntitySpan",
                        Symbols.ParamKind.World => "it takes the World",
                        Symbols.ParamKind.ComponentSpan => "it takes spans: a batch method writes its own loop",
                        Symbols.ParamKind.Mutable => $"it takes a Mut<{Symbols.MutElement(p.Type)?.Name}>",
                        Symbols.ParamKind.Scratch => $"it takes a Scratch<{Element(p.Type)}>: working memory a row at a time, not one per lane",
                        Symbols.ParamKind.Lookup => $"it takes a Lookup<{Element(p.Type)}>: a lookup of another entity per row has no vector form",
                        _ => $"parameter '{p.Name}' is not a component, a singleton, a Reduce<T> or a value",
                    });
            }
        }
        if (first == null) throw new Unsupported("it takes no component");
        foreach (var field in optionals.SelectMany(f => f))
            if (SizeOf(first) != SizeOf(field.Type))
                throw new Unsupported($"lane types {first.ToDisplayString()} and {field.Type.ToDisplayString()} differ in size");
        if (SizeOf(first) < 4 && optionals.Count > 0)
            throw new Unsupported($"it takes a Maybe<{Symbols.MaybeElement(method.Parameters.First(q => Symbols.Classify(q, outside) == Symbols.ParamKind.Optional).Type)?.Name}> of {first.ToDisplayString()} lanes: optional columns have vector loops of 4- and 8-byte lanes");
        if (model.GetOperation(syntax, ct) is not IMethodBodyOperation body) throw new Unsupported("it has no body");

        // The parameters of a kernel: the main one, and one per field of a flat loop (Flat) with the same setup.
        Kernel Make(bool reductions)
        {
            var made = new Kernel(SizeOf(first)) { _fast = Symbols.FastFloats(method) };
            for (int i = 0, k = 0, o = 0; i < method.Parameters.Length; i++)
            {
                var p = method.Parameters[i];
                var kind = Symbols.Classify(p, outside);
                if (kind == Symbols.ParamKind.Component)
                {
                    made._components[p] = (k + 1, components[k], p.RefKind == RefKind.Ref);
                    k++;
                    continue;
                }
                if (kind == Symbols.ParamKind.Optional)
                {
                    // The column of the run (or 64 rows of zeros the row mask keeps at row 0, for a table without it).
                    var fields = optionals[o++];
                    made._optionals[p] = (i, fields);
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
        var loads = new StringBuilder();
        var stores = new StringBuilder();
        foreach (var (index, fields, _) in kernel._components.Values.OrderBy(c => c.Index))
        {
            string column = Elem(fields[0].Type);
            if (fields.Length == 1) loads.Append($"var __c{index}_0 = {V}.LoadUnsafe(ref e{index}, (nuint)i);\n");
            else
            {
                loads.Append($"{Interleaved}.Load{fields.Length}(ref e{index}, (nuint)i, {string.Join(", ", fields.Select((_, f) => $"out {Vec(column)} __s{index}_{f}"))});\n");
                for (int f = 0; f < fields.Length; f++)
                    loads.Append($"var __c{index}_{f} = {As(column, Elem(fields[f].Type), $"__s{index}_{f}")};\n");
            }
        }
        foreach (var (index, fields) in kernel._optionals.Values.OrderBy(o => o.Index))
        {
            string column = Elem(fields[0].Type), row = $"(nuint)i & __z{index}";
            loads.Append($"var __has{index} = {Interleaved}.Lanes{kernel._size * 8}{LanesForm}(m{index}.Present(i), 0);\n");
            if (fields.Length == 1) loads.Append($"var __q{index}_0 = {V}.LoadUnsafe(ref __r{index}, {row});\n");
            else
            {
                loads.Append($"{Interleaved}.Load{fields.Length}(ref __r{index}, {row}, {string.Join(", ", fields.Select((_, f) => $"out {Vec(column)} __t{index}_{f}"))});\n");
                for (int f = 0; f < fields.Length; f++)
                    loads.Append($"var __q{index}_{f} = {As(column, Elem(fields[f].Type), $"__t{index}_{f}")};\n");
            }
        }
        var root = (IOperation?)body.BlockBody ?? body.ExpressionBody!;
        kernel.Statement(root, null);
        // A masked word keeps the values loaded of what the method writes, and stores them back in the lanes that do not
        // pass: the rows are left as they were (their memory is the table's, whether a component is absent or not).
        var kept = new StringBuilder();
        var masked = new StringBuilder();
        foreach (var (index, fields, _) in kernel._components.Values.Where(c => kernel._written.Contains(c.Index)).OrderBy(c => c.Index))
        {
            string column = Elem(fields[0].Type);
            string Blend(int f) => $"{V}.ConditionalSelect({As(kernel._mask, Elem(fields[f].Type), "__pass")}, __c{index}_{f}, __o{index}_{f})";
            for (int f = 0; f < fields.Length; f++) kept.Append($"var __o{index}_{f} = __c{index}_{f};\n");
            stores.Append(fields.Length == 1
                ? $"{V}.StoreUnsafe(__c{index}_0, ref e{index}, (nuint)i);\n"
                : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, $"__c{index}_{f}")))}, ref e{index}, (nuint)i);\n");
            masked.Append(fields.Length == 1
                ? $"{V}.StoreUnsafe({Blend(0)}, ref e{index}, (nuint)i);\n"
                : $"{Interleaved}.Store{fields.Length}({string.Join(", ", fields.Select((field, f) => As(Elem(field.Type), column, Blend(f))))}, ref e{index}, (nuint)i);\n");
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
                string acc = $"__a{r.Index}_{f}";
                held.Append($"var __h{r.Index}_{f} = {acc};\n");
                restore.Append($"{acc} = __h{r.Index}_{f};\n");
                masked.Insert(0, $"{acc} = {V}.ConditionalSelect({As(kernel._mask, Elem(r.Fields[f].Type), "__pass")}, {acc}, __h{r.Index}_{f});\n");
                fold.Append($"    __v.{r.Fields[f].Name} = {acc}[__j];\n");
            }
            fold.Append($"    {r.Partial}.Combine(__v);\n}}\n");
        }
        string prologue = kernel._bails ? "bool __bail = false;\n" : "";
        string live = kernel._live ? $"var __live = {Vec(kernel._mask)}.AllBitsSet;\n" : "", maskedLive = kernel._live ? "var __live = __pass;\n" : "";
        string hold = kernel._bails ? held.ToString() : "";
        var flat = Flat(kernel, root, () => Make(false));
        // Narrow lanes have no shuffles: a component of several fields takes the flat loop or none.
        if (kernel._narrow && flat == null && kernel._components.Values.FirstOrDefault(c => c.Fields.Length > 1) is { Fields: { } several })
            throw new Unsupported($"component {several[0].ContainingType.Name} has {several.Length} fields of {several[0].Type.ToDisplayString()}: a vector loop splits fields of 4 or 8 bytes, and takes narrower ones only when the method does the same to each field");
        return new Code(new EquatableArray<string>(kernel._components.Values.OrderBy(c => c.Index).Select(c => Elem(c.Fields[0].Type)).ToArray()),
            kernel._setup.ToString(), prologue + live + loads + hold + kernel._body, stores.ToString(), prologue + maskedLive + loads + kept + held + kernel._body, masked.ToString(),
            restore.ToString(), fold.ToString(), kernel._size, kernel._mask, kernel._components.Values.Any(c => c.Fields.Length > 1), kernel._bails, flat);
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
        if (main._bails || main._alive != null || main._reductions.Count > 0 || main._locals.Count > 0 || main._wides.Count > 0 || main._optionals.Count > 0) return null;
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
            foreach (var op in statement.DescendantsAndSelf())
            {
                if (main.LaneOf(op) is not { } lane) continue;
                if (field >= 0 && lane.Field != field) return null; // a statement that mixes fields
                field = lane.Field;
            }
            if (field < 0) return null;
            groups[field].Add(statement);
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
    /// The fields of a component, in memory order: 1 to <see cref="MaxFields"/> primitive lane values with C# arithmetic
    /// of their own type (no promotion to int), laid out without padding (sequential, declared in one place); otherwise
    /// the reason it has no lanes.
    /// </summary>
    private static IFieldSymbol[] Lanes(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) throw new Unsupported($"component {type.Name} is not a struct");
        if (named.IsGenericType) throw new Unsupported($"component {type.Name} is generic");
        if (named.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute"))
            throw new Unsupported($"component {type.Name} sets its layout ([StructLayout])");
        if (named.DeclaringSyntaxReferences.Length > 1)
            throw new Unsupported($"component {type.Name} is declared in several parts, so the order of its fields is not defined");
        var fields = named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        if (fields.Length == 0) throw new Unsupported($"component {type.Name} has no fields");
        if (fields.Length > MaxFields) throw new Unsupported($"component {type.Name} has {fields.Length} fields (a vector loop splits up to {MaxFields})");
        foreach (var field in fields)
            if (field.IsFixedSizeBuffer || !IsLaneType(field.Type))
                throw new Unsupported($"field {type.Name}.{field.AssociatedSymbol?.Name ?? field.Name} is a {field.Type.ToDisplayString()}, not a lane type (float, double, int, uint, long, ulong, byte, sbyte, short or ushort)");
        return fields;
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
                    if (local.IsRef) throw new Unsupported($"ref local '{local.Name}' is not supported");
                    if (init == null) throw new Unsupported($"local '{local.Name}' has no initializer");
                    if (_narrow && IsInt(local.Type))
                    {
                        _active = And(mask, _alive);
                        DeclareWide(local, WideOf(init));
                        continue;
                    }
                    if (!IsLane(local.Type) && !IsBool(local.Type))
                        throw new Unsupported($"local '{local.Name}' is a {local.Type.ToDisplayString()}, not a lane type of {_size} bytes");
                    string name = Temp("l");
                    _active = And(mask, _alive);
                    string value = Expr(init);
                    _locals[local] = name;
                    _body.Append($"var {name} = {value};\n"); // a fresh local: its value in other lanes is never read
                }
                return true;
            case IExpressionStatementOperation { Operation: var e }:
                _active = And(mask, _alive);
                Assignment(e, mask);
                return true;
            case IConditionalOperation { WhenTrue: var whenTrue, WhenFalse: var whenFalse } branch when branch.Type == null:
            {
                string condition = Temp("m");
                _active = And(mask, _alive);
                _body.Append($"var {condition} = {Expr(branch.Condition)};\n"); // before either branch writes what it reads
                Prove(condition, branch.Condition);
                Statement(whenTrue, And(mask, condition));
                if (whenFalse != null) Statement(whenFalse, And(mask, $"~{condition}"));
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

    private void Assignment(IOperation op, string? mask)
    {
        if (Reduces(op, mask)) return;
        IOperation target;
        string value;
        switch (op)
        {
            case ISimpleAssignmentOperation { IsRef: false } a when _narrow && IsInt(a.Target.Type):
                WriteWide(a.Target, WideOf(a.Value), mask);
                return;
            case ISimpleAssignmentOperation { IsRef: false } a:
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
        _body.Append(active == null ? $"{name} = {value};\n"
            : $"{name} = {V}.ConditionalSelect({MaskAs(active, type)}, {value}, {name});\n");
    }

    /// <summary>The vector a write goes to: a kernel local, or the lane field of a component taken by ref.</summary>
    private string Target(IOperation target)
    {
        if (target is ILocalReferenceOperation { Local: var local } && _locals.TryGetValue(local, out var name)) return name;
        if (LaneOf(target) is { Ref: true } c)
        {
            _written.Add(c.Index);
            return $"__c{c.Index}_{c.Field}";
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
            if (field.AssociatedSymbol != null) throw new Unsupported($"reduction {name} holds {field.AssociatedSymbol.Name} in a property: the lanes fold into fields");
            if (field.IsReadOnly) throw new Unsupported($"field {name}.{field.Name} of the reduction is readonly");
            if (!IsLane(field.Type)) throw new Unsupported($"field {name}.{field.Name} of the reduction is a {field.Type.ToDisplayString()}, not a lane type of {_size} bytes");
        }

        var ops = CombineOps(type, fields, compilation, ct);
        for (int f = 0; f < fields.Length; f++)
            if (ops[f] == "+" && IsFloat(fields[f].Type) && !fast)
                throw new Unsupported($"the float sum {name}.{fields[f].Name} would add its rows in another order (one sum per lane): set FloatMode = FloatMode.Fast to accept a sum that is not bit for bit the method's");

        string t = type.ToDisplayString(Symbols.Fqn);
        _setup.Append($"var __id{index} = {t}.Identity;\n");
        for (int f = 0; f < fields.Length; f++)
            _setup.Append($"var __a{index}_{f} = {V}.Create<{Elem(fields[f].Type)}>(__id{index}.{fields[f].Name});\n");
        return new Reduction(index, type, fields, ops, partial);
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
                Fold(reduction, f, Expr(values[f]!), mask);
            }
            return true;
        }
        if (target is not var (r, field)) return false;

        string acc = $"__a{r.Index}_{field}", op2 = r.Ops[field];
        var type = r.Fields[field].Type;
        string NotCombined(string what) => $"it {what} '{Text(op)}', where {r.Type.Name}.Combine takes the {(op2 == "+" ? "sum" : op2)} of {r.Fields[field].Name}";
        switch (op)
        {
            case ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, IsChecked: false, OperatorMethod: null } a
                when op2 == "+" && a.InConversion.IsIdentity && a.OutConversion.IsIdentity:
                Write(acc, $"({acc} {(a.OperatorKind == BinaryOperatorKind.Add ? "+" : "-")} {Expr(a.Value)})", type, mask);
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
        string acc = $"__a{r.Index}_{field}";
        Write(acc, r.Ops[field] == "+" ? $"({acc} + {value})" : $"{V}.{r.Ops[field]}({acc}, {value})", r.Fields[field].Type, mask);
    }

    /// <summary>
    /// The component and field whose lane <paramref name="op"/> is: its field, or the auto-property backed by it (a
    /// record struct's positional member).
    /// </summary>
    private (int Index, int Field, bool Ref)? LaneOf(IOperation op)
    {
        var (instance, member) = op switch
        {
            IFieldReferenceOperation f => (f.Instance, (ISymbol)f.Field),
            IPropertyReferenceOperation { Arguments.Length: 0 } p => (p.Instance, p.Property),
            _ => (null, null),
        };
        if (instance is not IParameterReferenceOperation { Parameter: var parameter } || !_components.TryGetValue(parameter, out var c))
            return null;
        for (int f = 0; f < c.Fields.Length; f++)
            if (SymbolEqualityComparer.Default.Equals(member, c.Fields[f]) || SymbolEqualityComparer.Default.Equals(member, c.Fields[f].AssociatedSymbol))
            {
                if (_flat < 0) return (c.Index, f, c.Ref);
                if (f != _flat) throw new Unsupported("a statement mixes fields"); // Flat checked this; never reported
                return (c.Index, 0, c.Ref);
            }
        return null;
    }

    // ---------------- optional components ----------------

    /// <summary>The optional and field whose lane <paramref name="op"/> is: <c>m.Value.X</c> of a Maybe&lt;T&gt; m.</summary>
    private (int Index, int Field)? OptionalLane(IOperation op)
    {
        var (instance, member) = op switch
        {
            IFieldReferenceOperation f => (f.Instance, (ISymbol)f.Field),
            IPropertyReferenceOperation { Arguments.Length: 0 } p => (p.Instance, p.Property),
            _ => (null, null),
        };
        if (instance is not IPropertyReferenceOperation { Property.Name: "Value", Instance: IParameterReferenceOperation { Parameter: var parameter } }
            || !_optionals.TryGetValue(parameter, out var o))
            return null;
        for (int f = 0; f < o.Fields.Length; f++)
            if (SymbolEqualityComparer.Default.Equals(member, o.Fields[f]) || SymbolEqualityComparer.Default.Equals(member, o.Fields[f].AssociatedSymbol))
                return (o.Index, f);
        return null;
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
            if (!IsLane(op.Type)) throw new Unsupported($"'{Text(op)}' is a {op.Type?.ToDisplayString()}, not a lane type of {_size} bytes");
            return Broadcast(scalar, op.Type!);
        }

        switch (op)
        {
            case IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c when _narrow && IsLane(c.Type) && IsInt(c.Operand.Type):
                return Truncate(WideOf(c.Operand), c.Type!, c); // (byte)(a + b): the low bits, as C# truncates
            case IConversionOperation { Conversion.IsIdentity: true } c when IsLane(c.Type) || IsBool(c.Type):
                return Expr(c.Operand);
            case IConversionOperation { Conversion.IsNumeric: true, IsChecked: false, OperatorMethod: null } c when IsLane(c.Type) && IsLane(c.Operand.Type):
                return Convert(Expr(c.Operand), c.Operand.Type!, c.Type!) ?? throw Not(c);
            case IFieldReferenceOperation or IPropertyReferenceOperation when LaneOf(op) is { } c:
                return $"__c{c.Index}_{c.Field}";
            case IPropertyReferenceOperation { Property.Name: "HasValue", Instance: IParameterReferenceOperation { Parameter: var p } } when _optionals.TryGetValue(p, out var o):
                return $"__has{o.Index}";
            case IFieldReferenceOperation or IPropertyReferenceOperation when OptionalLane(op) is { } q:
                Guard(q.Index);
                return $"__q{q.Index}_{q.Field}";
            case IFieldReferenceOperation when Accumulator(op) != null:
                throw new Unsupported($"it reads '{Text(op)}', a partial result the lanes of a vector loop hold in parts");
            case ILocalReferenceOperation { Local: var local } when _locals.TryGetValue(local, out var name):
                return name;
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
                if (_optionals.Count == 0)
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
        if (masks && _optionals.Count > 0 && kind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
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
