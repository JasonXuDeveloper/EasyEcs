using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules for code that works but does more than it needs (EECS025-EECS033, EECS039, EECS046, EECS052, EECS053), EECS034
/// and EECS055-EECS056: what a [ForEach] method declares and does per row, allocations in code that runs every update, structural changes
/// and queries one call could make, a component added right after its entity is created, the generated members, and
/// the methods left without a vector loop. A rule whose code costs is a warning (EECS025-EECS032), the generated member is a
/// suggestion (the same call at the same cost, shorter), a scalar loop a message (EECS055; an error when the method
/// requires a vector loop, EECS056) and an update inside a system an error. Each is reported only where the cheaper form does the
/// same thing; the code fixes (EasyEcs.CodeFixes) apply the replacement the analyzer computed, so the analyzer stays
/// the only place that decides.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor WriteAccessOnlyReads = new("EECS025", "A write access that only reads",
        "{0} is a write access, but the code only reads it: {1}; {2}",
        Category, DiagnosticSeverity.Warning, true,
        "A 'ref' component or singleton parameter (a Span<T> in a batch method) puts the type in the system's write set, " +
        "so no parallel stage runs it beside a system that reads the type, and every row it visits of a tracked " +
        "component (ITracked) is stamped as changed, so [Changed<T>] systems visit rows nobody changed. " +
        "Entity.Get<T>() of a tracked component stamps the row too. Read with 'in', ReadOnlySpan<T> or Read<T>().",
        helpLinkUri: Help("EECS025"));

    public static readonly DiagnosticDescriptor UnusedParameter = new("EECS026", "A [ForEach] or [ForEntity] parameter the method does not use",
        "{0} does not use parameter '{1}': {2}",
        Category, DiagnosticSeverity.Warning, true,
        "Every parameter costs per row or per pass: a component loads and advances its column (and a 'ref' one is a " +
        "write), a singleton is resolved, an Entity builds a handle for every row and, like the World, makes the system " +
        "run alone. A component the method only requires is a [With<T>] filter.",
        helpLinkUri: Help("EECS026"));

    public static readonly DiagnosticDescriptor RowLookup = new("EECS027", "A lookup per row that a parameter makes once",
        "{0} looks up '{1}' with '{2}' {4}; {3}",
        Category, DiagnosticSeverity.Warning, true,
        "A [ForEach] method's parameters are resolved once per table or per pass: a component is a column the loop walks, " +
        "a singleton a ref taken before the loop. Looking the same data up through the visited Entity or the World " +
        "repeats the search for every row (the entity's record, its table's column, the singleton's slot) and, through " +
        "the World, makes the system run alone.",
        helpLinkUri: Help("EECS027"));

    public static readonly DiagnosticDescriptor Allocation = new("EECS028", "An allocation in code that runs every update",
        "'{0}' allocates {3} in '{1}', which runs {2}{4}",
        Category, DiagnosticSeverity.Warning, true,
        "A [ForEach] method runs for every entity it visits and an [OnUpdate] method once per update, so garbage there is garbage " +
        "every tick, and the collections it causes pause the world. Objects, arrays, capturing lambdas, LINQ, strings " +
        "built at run time, boxing, enumerators of interfaces and the cold calls of tools (TrimExcess, Capture(), " +
        "Describe, DescribeTables, DescribeSchedule, new Prefab) allocate; code inside a throw, and calls the compiler " +
        "removes ([Conditional]), are left out. A [ForEach] method keeps its working collections in a Scratch<T> " +
        "parameter: one per thread, reused by the next rows.",
        helpLinkUri: Help("EECS028"));

    public static readonly DiagnosticDescriptor PerEntityChange = new("EECS030", "A structural change entity by entity over a query",
        "This {0} {1} each entity {2} one by one; {3} does it for all of them in one call",
        Category, DiagnosticSeverity.Warning, true,
        "A structural change through each entity moves (or flips) one row per call, through the structural gate each " +
        "time. SetAll, RemoveAll, DisableAll, EnableAll and DestroyAll change every matching row of a table " +
        "together, whole words of rows at once. A [ForEach] method whose whole body is such a change is the same loop, " +
        "written by the generator: make the bulk call in the system's [OnUpdate] method instead.",
        helpLinkUri: Help("EECS030"));

    public static readonly DiagnosticDescriptor CreateInLoop = new("EECS031", "Entities created one by one in a counted loop",
        "This loop creates {0} entities one by one; {1}",
        Category, DiagnosticSeverity.Warning, true,
        "Creating entities one by one grows the table and the entity storage by doubling as they fill, and in a world " +
        "that has never held them the discarded arrays can set off a full garbage collection. CreateEntities and " +
        "Instantiate(prefab, span) create them in one pass; Reserve reserves room first when each entity gets " +
        "its own values.",
        helpLinkUri: Help("EECS031"));

    public static readonly DiagnosticDescriptor QueryByHand = new("EECS032", "Query work the world does in one call",
        "{0}; {1}",
        Category, DiagnosticSeverity.Warning, true,
        "Count sums the row counts of the matching tables and Any stops at the first table with a row, without visiting " +
        "rows. A filter skips whole tables and words of rows that a test in the loop would visit one by one.",
        helpLinkUri: Help("EECS032"));

    public static readonly DiagnosticDescriptor GeneratedMember = new("EECS033", "A generated member says it shorter",
        "'{0}' can be written '{1}'",
        Category, DiagnosticSeverity.Warning, true,
        "The generator writes named members for every component and singleton (entity.Health, HasHealth, SetHealth(…), " +
        "IsDead, world.Clock, world.ZoneIndex, item.Position) that call the generic API: the same call, without " +
        "repeating the type.",
        helpLinkUri: Help("EECS033"));

    public static readonly DiagnosticDescriptor UpdateInSystem = new("EECS034", "A system runs a world update",
        "System '{0}' calls '{1}', an update of the world it runs in",
        Category, DiagnosticSeverity.Error, true,
        "World.Update() and World.Update<TGroup>() run systems. Called from a system, Update() runs the system again " +
        "inside its own run (until the stack overflows), and a group's update runs other systems in the middle of a " +
        "stage, under its change tick. Run groups from the code that hosts the world, in the order they belong.",
        helpLinkUri: Help("EECS034"));

    public static readonly DiagnosticDescriptor UntrackedMut = new("EECS039", "A Mut<T> of a component that is not tracked",
        "Parameter '{0}' of {1} is a Mut<{2}>, but {2} is not tracked (ITracked): nothing is stamped either way; take it by 'ref'",
        Category, DiagnosticSeverity.Warning, true,
        "Mut<T> stamps the rows a method writes, one by one, so [Changed<T>] systems visit only those. A component that " +
        "is not tracked has no stamps: a Mut of it is a 'ref' parameter behind a wrapper, Value and Write() where the " +
        "ref would do.", helpLinkUri: Help("EECS039"));

    public static readonly DiagnosticDescriptor ReadOnlyQuery = new("EECS046", "A query loop that only reads tracked components",
        "This loop only reads what '{0}' hands out, and the query stamps every row it visits of {1}; '{2}' reads them without stamping",
        Category, DiagnosticSeverity.Warning, true,
        "Query items hand out refs, so every row a query visits of a tracked component (ITracked, DynamicBuffer<T>) " +
        "counts as written, whether or not the loop writes through it: [Changed<T>] systems then visit rows nobody " +
        "changed. Read<T1..Tn> walks the same rows with ref readonly items and stamps nothing.",
        helpLinkUri: Help("EECS046"));

    public static readonly DiagnosticDescriptor CreateThenSet = new("EECS052", "A component added right after the entity is created",
        "'{0}' adds {1} to the entity created just before, so it is placed twice; '{2}' creates it with the component",
        Category, DiagnosticSeverity.Warning, true,
        "CreateEntity and Instantiate place the entity in the table of the components they are given. A Set of another " +
        "component on the next line moves it again: a second row copy, and a table on the way that nothing else may " +
        "use. Given to the creation, the component lands in the final table with the others.",
        helpLinkUri: Help("EECS052"));

    public static readonly DiagnosticDescriptor SomeRowsWritten = new("EECS053", "A tracked component written by ref on only some rows",
        "Parameter '{0}' of {1} takes {2} by 'ref', but {3}; take it as a Mut<{2}> and write through Write() or SetIfChanged",
        Category, DiagnosticSeverity.Warning, true,
        "A 'ref' parameter of a tracked component (ITracked, DynamicBuffer<T>) stamps every row the method visits, whether " +
        "the method writes it or returns first, so [Changed<T>] systems visit rows whose value never changed (and send, " +
        "save or recompute them again). A Mut<T> stamps only the rows the method writes, through Write() or a " +
        "SetIfChanged that changed the value; reading Value stamps nothing. Batch methods (spans) stamp whole runs and " +
        "are left out.",
        helpLinkUri: Help("EECS053"));

    public static readonly DiagnosticDescriptor ScalarLoop = new("EECS055", "A [ForEach] method that runs one row at a time",
        "[ForEach] method '{0}' runs one row at a time, without a vector loop: {1}",
        Category, DiagnosticSeverity.Info, true,
        "The generator turns a [ForEach] method into a Vector<T> loop (several rows per instruction, the same results bit " +
        "for bit, except under FloatMode.Fast: reordered float sums, approximate Sin, Cos, Exp and Log) when it is static, " +
        "takes no Entity and makes no structural change, takes components of up to 4 lanes of one size (numbers of 1 to " +
        "8 bytes, Vector2/3/4 fields; on AVX-512 also 4-byte lanes of a wider row of up to 16 words, where the permutes " +
        "that gather them pay), Maybe<T> of them, singletons, values and Reduce<T> results, and computes with operations " +
        "that have an exact vector counterpart (a call it cannot express under a condition runs the method for the rows " +
        "of the vectors that take it). This method takes components of plain values but misses one of those: the message " +
        "names the first, and how to split a wide component when the method uses few of its fields. Change it to get the " +
        "vector loop, set SimdMode = SimdMode.Off to keep the method scalar without this message, or SimdMode.Require to " +
        "make it an error.", helpLinkUri: Help("EECS055"));

    /// <summary>
    /// EECS055 and EECS056: a [ForEach] method the generator leaves without a vector loop, with the reason
    /// <see cref="Kernel.Vectorize"/> gives, the same call the generator makes, so a method reported is exactly a
    /// method left scalar. EECS055 only for a method that looks meant for one (<see cref="Kernel.LooksEligible"/>),
    /// EECS056 for any method that requires one.
    /// </summary>
    private static void AnalyzeVectorLoop(SyntaxNodeAnalysisContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } method
            || !Symbols.IsValidForEach(method) || method.PartialDefinitionPart != null // a partial method: once, on its definition
            || Symbols.IsForEntity(method)) return; // one entity per call: no loop to vectorize (the generator asks for none)
        int mode = Symbols.Simd(method);
        if (mode == Symbols.SimdOff || mode == Symbols.SimdAuto && !Kernel.LooksEligible(method)) return;
        if (Kernel.Vectorize(method, syntax, context.SemanticModel, static (i, _) => "", context.CancellationToken, out var reason) != null
            || mode == Symbols.SimdAuto && AccumulatesByHand(method, syntax, context.SemanticModel, context.CancellationToken)) return; // EECS086's

        context.ReportDiagnostic(Diagnostic.Create(mode == Symbols.SimdRequire ? NoVectorLoop : ScalarLoop,
            method.Locations.FirstOrDefault(), method.Name, reason));
    }

    /// <summary>Property keys of the diagnostics' code fixes: what the analyzer decided, the fix only applies it.</summary>
    public const string FixKind = "Fix", FixTitle = "Title", Replace = "Replace", Insert = "Insert", RemoveOthers = "Remove", WithType = "With";

    /// <summary>
    /// EECS031 leaves out loops of a constant count below this (or over an array created with one): 64 presence words,
    /// entity records of 64 KB.
    /// </summary>
    private const int SmallCount = 4096;

    private static string Help(string id) => "https://ecs.xgamedev.net/en/docs/guide/analyzer#" + id.ToLowerInvariant();

    /// <summary>EasyEcs's types in the compilation (null without a reference to it).</summary>
    private sealed class Core
    {
        public INamedTypeSymbol Entity = null!, World = null!, EntitySpan = null!, Track = null!, Prefab = null!;

        public static Core? Create(Compilation compilation)
        {
            var entity = compilation.GetTypeByMetadataName(Symbols.Entity);
            var world = compilation.GetTypeByMetadataName(Symbols.World);
            var span = compilation.GetTypeByMetadataName(Symbols.EntitySpan);
            var track = compilation.GetTypeByMetadataName(Symbols.TrackChanges);
            var prefab = compilation.GetTypeByMetadataName("EasyEcs.Prefab");
            return entity == null || world == null || span == null || track == null || prefab == null ? null
                : new Core { Entity = entity, World = world, EntitySpan = span, Track = track, Prefab = prefab };
        }

        public bool Is(ITypeSymbol? type, INamedTypeSymbol core) => SymbolEqualityComparer.Default.Equals(type, core);

        public bool Tracked(ITypeSymbol type) => type.AllInterfaces.Contains(Track, SymbolEqualityComparer.Default);
    }

    private static void InitializePerformance(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (Core.Create(start.Compilation) is not { } core) return;
            var resets = new Resets();
            resets.Register(start);
            start.RegisterOperationBlockAction(c => AnalyzeBody(c, core, resets));
            start.RegisterOperationAction(c => AnalyzeCall(c, core), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeItem(c), OperationKind.PropertyReference);
            start.RegisterOperationAction(c => AnalyzeComparison(c, core), OperationKind.Binary);
            start.RegisterOperationAction(c => AnalyzeLoop(c, core), OperationKind.Loop);
            start.RegisterOperationAction(c => AnalyzeCreation(c, core), OperationKind.VariableDeclarator);
            start.RegisterOperationAction(c => AnalyzeGuardedSet(c, core), OperationKind.Conditional);
        });

    // ---------------- [ForEach] methods and OnUpdate: parameters, lookups, allocations ----------------

    private static void AnalyzeBody(OperationBlockAnalysisContext context, Core core, Resets resets)
    {
        if (context.OwningSymbol is not IMethodSymbol method) return;
        var body = context.OperationBlocks.FirstOrDefault(b => b is IMethodBodyOperation or IBlockOperation);
        if (body == null) return;
        bool forEach = Symbols.IsGeneratedJob(method);
        if (forEach)
        {
            if (ForEachBulk(context, method, body, core)) return; // the method goes: what it costs per row is moot
            var uses = new Dictionary<IParameterSymbol, List<IOperation>>(SymbolEqualityComparer.Default);
            foreach (var operation in body.Descendants())
                if (operation is IParameterReferenceOperation reference)
                {
                    if (!uses.TryGetValue(reference.Parameter, out var list)) uses[reference.Parameter] = list = [];
                    list.Add(reference);
                }

            AnalyzeParameters(context, method, body, uses);
            AnalyzeHandWritten(context, method, body, core, resets);
            AnalyzeScratch(context, method, body);
            AnalyzeLookups(context, core, method, body);
            AnalyzeAllocations(context, method, body, Symbols.IsForEntity(method) ? "on every call" : "for every entity it visits, each update", true);
        }
        else if (Symbols.JobInterface(method) != null)
        {
            // A hand-written job: its signature is the interface's (EECS037 says what its WriteMask should be), what it
            // looks up and allocates per row costs as in a [ForEach] method.
            AnalyzeLookups(context, core, method, body);
            AnalyzeAllocations(context, method, body, "for every entity its loop visits", true);
        }
        // An [OnUpdate] method of a system with [UpdateInterval(n)], n above 1, runs on the cadence its author chose: what
        // it allocates there is a choice, not garbage of every update.
        else if (Symbols.Hook(method) == Symbols.HookKind.Update && Symbols.IsSystem(method.ContainingType)
                 && Symbols.Schedule(method.ContainingType) is not { Interval: > 1 })
            AnalyzeAllocations(context, method, body, "every update", false);
    }

    /// <summary>EECS025 for 'ref' / Span parameters that only read, EECS026 for parameters not used at all.</summary>
    private static void AnalyzeParameters(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body,
        Dictionary<IParameterSymbol, List<IOperation>> uses)
    {
        bool empty = body is IMethodBodyOperation { BlockBody.Operations.Length: 0, ExpressionBody: null } or IBlockOperation { Operations.Length: 0 };
        bool outside = Symbols.IsRunner(method), one = Symbols.IsForEntity(method);
        string described = Symbols.Describe(method), each = one ? "on every call" : "for every row";
        var requirements = Symbols.Requirements(method, context.CancellationToken);
        var excluded = Excluded(method, context.CancellationToken);
        int spans = method.Parameters.Count(p => Symbols.Classify(p) == Symbols.ParamKind.ComponentSpan);
        bool entitySpan = method.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.Entities);

        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            if (kind is Symbols.ParamKind.Invalid or Symbols.ParamKind.Context) continue;
            var location = p.Locations.FirstOrDefault();
            var component = Symbols.QueriedComponent(p);
            // Excluded by the method's filters too: EECS016 or EECS049 says the method never runs, the cost is moot.
            if (component != null && excluded.Contains(component)) continue;
            uses.TryGetValue(p, out var used);
            bool span = kind == Symbols.ParamKind.ComponentSpan && Symbols.Named(p.Type.OriginalDefinition, "System.Span<T>");
            bool byRef = kind is Symbols.ParamKind.Component or Symbols.ParamKind.Singleton && p.RefKind == RefKind.Ref;

            // EECS015 for a ref or Span parameter of an indexed component (AnalyzeMethod leaves it to the body): the fix
            // is the read-only form when the method only reads it.
            if ((span || byRef && kind == Symbols.ParamKind.Component) && component != null && Symbols.IsIndexed(component) && EasyEcsAnalyzer.HasBody(method))
                context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, location,
                    used == null || !used.Any(Uses.MayWrite) ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, span ? "ReadOnlySpan" : "In") : null,
                    component.Name, $"parameter '{p.Name}' can write it (take it as 'in' or ReadOnlySpan)"));

            if (used is not { } references)
            {
                if (empty) continue; // a method being written: nothing to say yet
                if (component != null)
                {
                    string shown = component.ToMinimalDisplayString(body.SemanticModel!, location?.SourceSpan.Start ?? 0);
                    // [With<T>] cannot name a type parameter (CS8968); an EntitySpan needs a span.
                    if (component is ITypeParameterSymbol || kind == Symbols.ParamKind.ComponentSpan && entitySpan && spans == 1) continue;
                    // A write access of a tracked component stamps every row it visits: [With<T>] would not, so the
                    // fix is offered only where it changes nothing but the cost.
                    bool stamps = (p.RefKind == RefKind.Ref || kind == Symbols.ParamKind.ComponentSpan
                                   && Symbols.Named(p.Type.OriginalDefinition, "System.Span<T>"))
                                  && Symbols.Implements(component, Symbols.TrackChanges);
                    if (!stamps && requirements.Any(r => r is { Has: true, Parameter: false } && SymbolEqualityComparer.Default.Equals(r.Type, component)))
                    {
                        // [With<T>] or [Changed<T>] (the method's or its group's) already requires it: the parameter only adds the column's walk.
                        context.ReportDiagnostic(Diagnostic.Create(UnusedParameter, location, ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Remove"),
                            described, p.Name, $"its column is still loaded {each}, and a filter already requires {shown}; remove it"));
                        continue;
                    }

                    var with = stamps ? null : ImmutableDictionary<string, string?>.Empty.Add(FixKind, "With")
                        .Add(WithType, shown);
                    context.ReportDiagnostic(Diagnostic.Create(UnusedParameter, location, with, described, p.Name, stamps
                        ? $"it stamps {(one ? "the entity's row" : "every row it visits")} as changed without writing it; require the component with [With<{shown}>] unless the stamp is the point"
                        : $"its column is still loaded {each}; require the component with [With<{shown}>] instead"));
                }
                else
                    context.ReportDiagnostic(Diagnostic.Create(UnusedParameter, location,
                        ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Remove"), described, p.Name, kind switch
                        {
                            // One entity per call: no rows to build handles for, no stage to run alone in.
                            Symbols.ParamKind.Entity or Symbols.ParamKind.World when one => "remove it",
                            Symbols.ParamKind.Singleton when one => "it is resolved on every call; remove it",
                            Symbols.ParamKind.Entity => "it builds a handle for every row and makes the system run alone; remove it",
                            Symbols.ParamKind.World => "it makes the system run alone; remove it",
                            Symbols.ParamKind.Entities => "remove it",
                            Symbols.ParamKind.Singleton => "it is resolved every pass and scheduled as an access; remove it",
                            Symbols.ParamKind.Reduction => "it replaces its singleton every pass and is scheduled as its writer; remove it",
                            Symbols.ParamKind.Scratch => "remove it",
                            Symbols.ParamKind.Emit => "it adds its Emitted singleton and is scheduled as its writer; remove it",
                            Symbols.ParamKind.Lookup => "it is scheduled as a reader of the component; remove it",
                            _ => "it is looked up for every row; remove it",
                        }));
                continue;
            }

            if (kind == Symbols.ParamKind.Mutable)
            {
                MutParameter(context, method, p, component, references);
                continue;
            }

            if (!span && !byRef) continue;
            var type = component ?? p.Type;
            if (Symbols.IsIndexed(type)) continue; // EECS015 says it
            if (references.Any(Uses.MayWrite))
            {
                // EECS053: a tracked component a per-entity method writes on some rows only.
                if (byRef && kind == Symbols.ParamKind.Component && spans == 0 && !entitySpan && type is not ITypeParameterSymbol
                    && Symbols.Implements(type, Symbols.TrackChanges))
                    WrittenOnSomeRows(context, method, body, p, type, references);
                continue;
            }
            // A 'ref' singleton of a parallel method is EECS010's ("it writes a singleton ... take it as 'in'").
            if (kind == Symbols.ParamKind.Singleton && Symbols.IsParallel(method)) continue;
            string cost = kind == Symbols.ParamKind.Singleton
                ? "the system is scheduled as the singleton's writer, so no parallel stage runs it beside a reader"
                : Symbols.Implements(type, Symbols.TrackChanges)
                    ? (one ? "the entity's row is" : "every row it visits is") + " stamped as changed, so [Changed<" + type.Name + ">] systems visit rows nobody changed"
                    : "the system is scheduled as the component's writer, so no parallel stage runs it beside a reader";
            context.ReportDiagnostic(Diagnostic.Create(WriteAccessOnlyReads, location,
                ImmutableDictionary<string, string?>.Empty.Add(FixKind, span ? "ReadOnlySpan" : "In"),
                $"Parameter '{p.Name}' of {described} ({(span ? "Span<" + type.Name + ">" : "ref")})", cost,
                span ? "take it as a ReadOnlySpan<" + type.Name + ">" : "take it by 'in'"));
        }
    }

    /// <summary>
    /// EECS053: a 'ref' parameter of a tracked component that some path through the method leaves unwritten (it reaches
    /// the end or a return without a write; a path that throws does not count). Found on the method's control flow graph:
    /// a walk from the entry that stops at every block holding a write of the parameter (a block runs whole, so a path
    /// through it writes) and reaches the exit; a constant condition takes its one branch, a catch is reached only by a
    /// throw (not followed). Conservative: a method that writes the parameter in a finally is left out (the walk does not
    /// follow a finally on the way out), and anything <see cref="Uses.MayWrite"/> cannot prove a read counts as
    /// a write, so a missed report is possible, a wrong one is not. The fix (to <c>Mut&lt;T&gt;</c>) is offered when every
    /// use maps to <c>.Write()</c> or <c>.Value</c>: the writes come first in the additional locations, then the reads.
    /// </summary>
    private static void WrittenOnSomeRows(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body, IParameterSymbol p,
        ITypeSymbol component, List<IOperation> references)
    {
        var writes = new HashSet<SyntaxNode>();
        var reads = new List<IOperation>();
        bool mechanical = !Symbols.SharesSignature(method) && method.PartialDefinitionPart == null && method.PartialImplementationPart == null;
        foreach (var reference in references)
        {
            if (Uses.MayWrite(reference)) writes.Add(reference.Syntax);
            else reads.Add(reference);
            // 'p = ref other' rebinds the parameter, nameof(p) names it: no Mut<T> form of either.
            if (reference.Parent is ISimpleAssignmentOperation { IsRef: true } rebind && rebind.Target == reference
                || reference.Parent is INameOfOperation || reference.Syntax is not IdentifierNameSyntax)
                mechanical = false;
        }

        var graph = context.GetControlFlowGraph(body); // body is one of the context's operation blocks
        var blocks = graph.Blocks;
        var written = new bool[blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            written[i] = Writes(blocks[i], writes);
            // A finally runs on the way out of every path through its try, which the walk does not follow: a write there is left out.
            if (written[i] && InFinally(blocks[i].EnclosingRegion)) return;
        }

        var seen = new bool[blocks.Length];
        var pending = new Stack<BasicBlock>();
        pending.Push(blocks[0]);
        seen[0] = true;
        bool unwritten = false;
        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (block.Kind == BasicBlockKind.Exit)
            {
                unwritten = true;
                break;
            }

            if (written[block.Ordinal]) continue;
            // A constant condition ('while (true)') takes one branch only.
            bool conditional = true, fallThrough = true;
            if (block.BranchValue?.ConstantValue is { HasValue: true, Value: bool constant } && block.ConditionKind != ControlFlowConditionKind.None)
            {
                conditional = constant == (block.ConditionKind == ControlFlowConditionKind.WhenTrue);
                fallThrough = !conditional;
            }

            if (fallThrough) Follow(block.FallThroughSuccessor, seen, pending);
            if (conditional) Follow(block.ConditionalSuccessor, seen, pending);
        }

        if (!unwritten) return;
        var locations = references.Where(r => writes.Contains(r.Syntax)).Concat(reads).Select(r => r.Syntax.GetLocation());
        var properties = mechanical
            ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Mut").Add("Writes", writes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : ImmutableDictionary<string, string?>.Empty;
        context.ReportDiagnostic(Diagnostic.Create(SomeRowsWritten, p.Locations.FirstOrDefault(), locations, properties,
            p.Name, Symbols.Describe(method), component.ToMinimalDisplayString(body.SemanticModel!, p.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0),
            Symbols.IsForEntity(method) ? "some calls return without a write, and every call stamps the row as changed"
                : "some rows pass through without a write, and every row it visits is stamped as changed"));
    }

    /// <summary>Whether <paramref name="region"/> is a finally or lies inside one.</summary>
    private static bool InFinally(ControlFlowRegion? region)
    {
        for (; region != null; region = region.EnclosingRegion)
            if (region.Kind == ControlFlowRegionKind.Finally) return true;
        return false;
    }

    /// <summary>Queues the destination of <paramref name="branch"/> unless it throws or was queued before.</summary>
    private static void Follow(ControlFlowBranch? branch, bool[] seen, Stack<BasicBlock> pending)
    {
        if (branch is not { Destination: { } next } || branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow
            || seen[next.Ordinal])
            return;
        seen[next.Ordinal] = true;
        pending.Push(next);
    }

    /// <summary>Whether a reference in <paramref name="block"/> (its operations or its branch value) is one of <paramref name="writes"/>.</summary>
    private static bool Writes(BasicBlock block, HashSet<SyntaxNode> writes)
    {
        foreach (var operation in block.Operations)
            if (Holds(operation, writes)) return true;
        return block.BranchValue != null && Holds(block.BranchValue, writes);
    }

    private static bool Holds(IOperation root, HashSet<SyntaxNode> writes)
    {
        if (root is IParameterReferenceOperation self && writes.Contains(self.Syntax)) return true;
        foreach (var operation in root.Descendants())
            if (operation is IParameterReferenceOperation reference && writes.Contains(reference.Syntax)) return true;
        return false;
    }

    /// <summary>
    /// EECS025 for a <c>Mut&lt;T&gt;</c> parameter the method never writes (no Write, no SetIfChanged, not passed on: only
    /// <c>.Value</c> read), EECS039 for one of a component that is not tracked, used through <c>.Value</c> and
    /// <c>.Write()</c> only.
    /// </summary>
    private static void MutParameter(OperationBlockAnalysisContext context, IMethodSymbol method, IParameterSymbol p, ITypeSymbol? component,
        List<IOperation> references)
    {
        if (component is null or ITypeParameterSymbol || Symbols.IsIndexed(component)) return; // EECS015 says it
        bool reads = true, plain = true;
        foreach (var reference in references)
        {
            switch (reference.Parent)
            {
                case IPropertyReferenceOperation { Property.Name: "Value" } value when value.Instance == reference:
                    if (Uses.MayWrite(value)) reads = plain = false;  // a collection the value holds is changed
                    break;
                case IInvocationOperation { TargetMethod.Name: "Write", Arguments.Length: 0 } write when write.Instance == reference:
                    reads = false;
                    break;
                default:
                    reads = plain = false;
                    break;
            }
        }

        var location = p.Locations.FirstOrDefault();
        if (reads)
            context.ReportDiagnostic(Diagnostic.Create(WriteAccessOnlyReads, location, ImmutableDictionary<string, string?>.Empty.Add(FixKind, "MutIn"),
                $"Parameter '{p.Name}' of {Symbols.Describe(method)} (Mut<{component.Name}>)",
                "the system is scheduled as the component's writer, so no parallel stage runs it beside a reader", "take it by 'in'"));
        else if (plain && !Symbols.Implements(component, Symbols.TrackChanges))
            context.ReportDiagnostic(Diagnostic.Create(UntrackedMut, location, ImmutableDictionary<string, string?>.Empty.Add(FixKind, "MutRef"),
                p.Name, Symbols.Describe(method), component.Name));
    }

    /// <summary>
    /// EECS027: the visited entity's components looked up through its Entity, and singletons through the World, inside
    /// a [ForEach] method. Components the method itself adds or removes through the entity are left out.
    /// </summary>
    private static void AnalyzeLookups(OperationBlockAnalysisContext context, Core core, IMethodSymbol method, IOperation body)
    {
        var all = new List<(IOperation Operation, ITypeSymbol Type, bool Singleton, bool Presence)>();
        var fixedTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var (operation, type, singleton, presence) in RowLookups(core, method, body, all))
        {
            string name = type.ToMinimalDisplayString(operation.SemanticModel!, operation.Syntax.SpanStart);
            bool job = !Symbols.IsGeneratedJob(method), one = !job && Symbols.IsForEntity(method);
            string advice = job ? (singleton ? $"keep it in a field of the job, set before the loop" : $"add {name} to the job's components")
                : singleton ? $"take it as a parameter ('in {name}' or 'ref {name}'), resolved once per {(one ? "call" : "pass")}"
                : presence ? $"take a {(Symbols.IsIndexed(type) ? "ReadOnlyMaybe" : "Maybe")}<{name}> parameter, or split the method with [With<{name}>] / [Without<{name}>]"
                : Symbols.IsIndexed(type) ? $"take it as an 'in {name}' parameter, or a ReadOnlyMaybe<{name}> when some entities lack it"
                : $"take it as a parameter ('in {name}' or 'ref {name}'), or a Maybe<{name}> when some entities lack it";
            // The fix goes on the first lookup of each type: it replaces every lookup of the type at once.
            var fix = !job && !presence && fixedTypes.Add(type) ? ParameterFix(method, body, type, singleton, all, context.CancellationToken) : null;
            context.ReportDiagnostic(Diagnostic.Create(RowLookup, operation.Syntax.GetLocation(), fix?.Uses ?? [], fix?.Properties,
                job ? $"Job '{method.ContainingType.Name}'" : Symbols.Describe(method), name,
                Shorten(operation.Syntax), advice, one ? "beside the lookup its entry point makes" : "for every entity it visits"));
        }
    }

    /// <summary>
    /// EECS027's code fix: the parameter that resolves <paramref name="type"/> once per pass (<c>in T t</c>, or
    /// <c>ref T t</c> when a lookup may write it; an existing <c>ref</c> / <c>in</c> parameter of the type when the
    /// method takes one), and the lookups it replaces (every one of the type: Get, Read, GetSingleton, WriteX and the
    /// generated properties). None when a lookup of the type is a TryGet, a presence test or passes through a batch
    /// method's entities, when the method takes the type otherwise (Maybe, Mut, a span) or excludes it, when a write
    /// would need a 'ref' that cannot be (an indexed component, a singleton of a parallel method), when the method
    /// already takes 9 components, or when the parameter's name is taken.
    /// </summary>
    private static (ImmutableDictionary<string, string?> Properties, Location[] Uses)? ParameterFix(IMethodSymbol method, IOperation body,
        ITypeSymbol type, bool singleton, List<(IOperation Operation, ITypeSymbol Type, bool Singleton, bool Presence)> all,
        System.Threading.CancellationToken token)
    {
        if (Symbols.IsBatch(method) || method.PartialDefinitionPart != null || method.PartialImplementationPart != null
            || body.Syntax.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } declaration || body.SemanticModel is not { } model
            || Excluded(method, token).Contains(type)) return null;
        var uses = new List<IOperation>();
        bool writes = false;
        foreach (var (operation, lookupType, _, presence) in all)
        {
            if (!SymbolEqualityComparer.Default.Equals(lookupType, type)) continue;
            bool replaceable = !presence && operation switch
            {
                IInvocationOperation { TargetMethod: var called } => called.Name is "Get" or "Read" or "GetSingleton" || called.Name.StartsWith("Write"),
                IPropertyReferenceOperation => true,
                _ => false,
            };
            if (!replaceable || operation.Syntax is not ExpressionSyntax) return null;
            uses.Add(operation);
            writes |= Uses.MayWrite(operation);
        }

        if (uses.Count == 0 || writes && (Symbols.IsIndexed(type) || singleton && Symbols.IsParallel(method))) return null;
        string? name = null, parameter = null;
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p);
            if (!SymbolEqualityComparer.Default.Equals(kind is Symbols.ParamKind.Singleton ? p.Type : Symbols.QueriedComponent(p) ?? Symbols.MaybeElement(p.Type), type)) continue;
            if (kind is not (Symbols.ParamKind.Component or Symbols.ParamKind.Singleton) || writes && p.RefKind != RefKind.Ref) return null;
            name = p.Name;
        }

        if (name == null)
        {
            int components = method.Parameters.Count(p => Symbols.Classify(p) is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan
                or Symbols.ParamKind.Mutable or Symbols.ParamKind.Optional);
            if (!singleton && components >= Symbols.MaxComponents) return null;
            name = char.ToLowerInvariant(type.Name[0]) + type.Name.Substring(1);
            // Taken anywhere in the method (a parameter, a local, a lambda's), or by a member or type it sees: no fix.
            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
                || declaration.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == name)
                || !model.LookupSymbols(declaration.SpanStart, name: name).IsEmpty) return null;
            parameter = (writes ? "ref " : "in ") + type.ToMinimalDisplayString(model, declaration.SpanStart) + " " + name;
        }

        var properties = ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Parameter").Add("Name", name);
        if (parameter != null) properties = properties.Add("Parameter", parameter);
        return (properties, uses.Select(u => u.Syntax.GetLocation()).ToArray());
    }

    /// <summary>
    /// The lookups EECS027 reports in a [ForEach] method's or a job's <paramref name="body"/>: the visited entity's
    /// components through its Entity, singletons through the World, on every row, of a type the method neither changes
    /// through the entity nor indexes.
    /// </summary>
    /// <param name="all">Receives every lookup of a type the method does not change through the entity, on every row or not.</param>
    private static List<(IOperation Operation, ITypeSymbol Type, bool Singleton, bool Presence)> RowLookups(Core core, IMethodSymbol method, IOperation body,
        List<(IOperation Operation, ITypeSymbol Type, bool Singleton, bool Presence)>? all = null)
    {
        var found = new List<(IOperation, ITypeSymbol, bool, bool)>();
        var entities = method.Parameters.Where(p => Symbols.Classify(p) is Symbols.ParamKind.Entity or Symbols.ParamKind.Entities).ToArray();
        var worlds = method.Parameters.Where(p => Symbols.Classify(p) == Symbols.ParamKind.World).ToArray();
        if (entities.Length == 0 && worlds.Length == 0 && method.IsStatic) return found;

        var changed = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var lookups = new List<(IOperation Operation, ITypeSymbol Type, bool Singleton, bool Presence)>();
        foreach (var operation in body.Descendants())
        {
            ISymbol? member;
            IOperation? receiver;
            switch (operation)
            {
                case IInvocationOperation call:
                    member = call.TargetMethod;
                    receiver = Symbols.Receiver(call);
                    break;
                case IPropertyReferenceOperation { Property.IsIndexer: false } property:
                    member = property.Property;
                    receiver = property.Instance;
                    break;
                default: continue;
            }

            bool onEntity = IsVisited(receiver, entities, core);
            bool onWorld = !onEntity && IsWorld(receiver, worlds, core, method);
            if (!onEntity && !onWorld) continue;
            if (member is IMethodSymbol m && Symbols.IsStructural(m))
            {
                if (onEntity && ComponentOf(m, core) is { } touched) changed.Add(touched);
                continue;
            }

            if (Lookup(member, onEntity, core) is not { } lookup) continue;
            lookups.Add((operation, lookup.Type, !onEntity, lookup.Presence));
        }

        // Changed through the entity in the same method, or looked up only on some rows: a branch, a loop, after an early
        // return. An indexed component is read through 'in' or a ReadOnlyMaybe (a Maybe of it would be a write: EECS015).
        foreach (var lookup in lookups)
        {
            if (changed.Contains(lookup.Type)) continue;
            all?.Add(lookup);
            if (EveryRow(lookup.Operation, body)) found.Add(lookup);
        }

        return found;
    }

    /// <summary>
    /// Whether <paramref name="operation"/> runs on every call of the method: in no branch (an if's or a switch's arms,
    /// the right of &amp;&amp; / || / ??, a loop's body, a catch), and after no explicit return, continue or throw.
    /// </summary>
    private static bool EveryRow(IOperation operation, IOperation body)
    {
        if (!Symbols.AlwaysRuns(operation, body)) return false;
        int at = operation.Syntax.SpanStart;
        foreach (var exit in body.Descendants())
            if (exit is IReturnOperation { IsImplicit: false } or IThrowOperation or IBranchOperation { BranchKind: BranchKind.Continue or BranchKind.Break }
                && exit.Syntax.SpanStart < at && !exit.Syntax.Span.Contains(at))
                return false;
        return true;
    }

    private static bool IsVisited(IOperation? receiver, IParameterSymbol[] entities, Core core) =>
        Symbols.Unwrap(receiver) switch
        {
            IParameterReferenceOperation reference => core.Is(reference.Type, core.Entity) && entities.Contains(reference.Parameter, SymbolEqualityComparer.Default),
            IPropertyReferenceOperation { Property.IsIndexer: true, Instance: IParameterReferenceOperation span } => // entities[i]
                core.Is(span.Type, core.EntitySpan) && entities.Contains(span.Parameter, SymbolEqualityComparer.Default),
            _ => false,
        };

    private static bool IsWorld(IOperation? receiver, IParameterSymbol[] worlds, Core core, IMethodSymbol method) =>
        Symbols.Unwrap(receiver) switch
        {
            IParameterReferenceOperation reference => worlds.Contains(reference.Parameter, SymbolEqualityComparer.Default),
            IPropertyReferenceOperation { Property.Name: "World", Instance: IInstanceReferenceOperation } property =>
                !method.IsStatic && core.Is(property.Type, core.World),
            _ => false,
        };

    /// <summary>The component a structural member changes (Set&lt;T&gt;, Remove&lt;T&gt;, generated SetX / RemoveX ...), or null.</summary>
    private static ITypeSymbol? ComponentOf(IMethodSymbol method, Core core) =>
        method.TypeArguments.Length == 1 ? method.TypeArguments[0]
        : Symbols.IsGenerated(method) ? Named(method, core) : null;

    /// <summary>
    /// A read of the visited entity's component (Get, Read, Has, TryGet, TryGetRef and the generated reads), or of a
    /// singleton through the World (GetSingleton, HasSingleton, TryGetSingleton, the generated reads); null otherwise.
    /// </summary>
    private static (ITypeSymbol Type, string Member, bool Presence)? Lookup(ISymbol member, bool onEntity, Core core)
    {
        if (member is IMethodSymbol { TypeArguments.Length: 1 } generic && (core.Is(generic.ContainingType, core.Entity) || core.Is(generic.ContainingType, core.World)))
            return (onEntity, generic.Name) switch
            {
                (true, "Get" or "Read" or "TryGet" or "TryGetRef") => (generic.TypeArguments[0], generic.Name, false),
                (true, "Has") => (generic.TypeArguments[0], generic.Name, true),
                (false, "GetSingleton" or "TryGetSingleton") => (generic.TypeArguments[0], generic.Name, false),
                (false, "HasSingleton") => (generic.TypeArguments[0], generic.Name, true),
                _ => null,
            };
        if (!Symbols.IsGenerated(member) || Symbols.IsGeneratedWriter(member) || member.Name.StartsWith("Links")
            || member.Name.EndsWith("Index") || member.Name.EndsWith("Disabled") && member.Name.StartsWith("Is")
            || Named(member, core) is not { } type) return null;
        bool singleton = Symbols.Implements(type, Symbols.Singleton);
        if (singleton == onEntity) return null;
        bool presence = member.Name.StartsWith("Has") || member is IPropertySymbol { Type.SpecialType: SpecialType.System_Boolean } && member.Name.StartsWith("Is");
        return (type, member.Name, presence);
    }

    /// <summary>
    /// The component, singleton or buffer a generated member is for: the type named by the member's name without its
    /// prefix (Has, Is, TryGet, Write, GetOrAdd, Set, Remove, Disable, Enable, Link, Unlink, Links, In), in the generated class's namespace.
    /// </summary>
    private static ITypeSymbol? Named(ISymbol member, Core core)
    {
        var holder = member.ContainingType;
        while (holder?.ContainingType != null) holder = holder.ContainingType;
        if (holder?.ContainingNamespace is not { } ns) return null;
        string name = member.Name;
        if (name.StartsWith("Is") && name.EndsWith("Disabled") && name.Length > "IsDisabled".Length) name = name.Substring(2, name.Length - 10);
        if (name.StartsWith("Set") && name.EndsWith("IfChanged") && name.Length > "SetIfChanged".Length) name = name.Substring(0, name.Length - 9);
        foreach (var prefix in new[] { "", "Has", "Is", "TryGet", "Write", "GetOrAdd", "Set", "Remove", "Disable", "Enable", "Link", "Unlink", "Links", "In" })
        {
            if (!name.StartsWith(prefix) || name.Length == prefix.Length) continue;
            string type = name.Substring(prefix.Length);
            // The class is named after the type's flat name (Outer__Inner): its type is the one whose flat name it carries.
            foreach (var candidate in TypesNamed(ns, type))
            {
                bool buffer = holder.Name == Symbols.FlatName(candidate) + ComponentGenerator.Classes.Buffer;
                if (!buffer && holder.Name != Symbols.FlatName(candidate) + ComponentGenerator.Classes.Members) continue;
                if (buffer)
                    return core.Entity.ContainingNamespace.GetTypeMembers("DynamicBuffer", 1).FirstOrDefault()?.Construct(candidate);
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> TypesNamed(INamespaceSymbol ns, string name)
    {
        var pending = new Stack<INamedTypeSymbol>(ns.GetTypeMembers());
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (type.Name == name && type.TypeKind == TypeKind.Struct) yield return type;
            foreach (var nested in type.GetTypeMembers()) pending.Push(nested);
        }
    }

    // ---------------- EECS028: allocations ----------------

    /// <param name="pass">The code runs during a pass (a [ForEach] method, a job's Execute): a cold call the pass refuses
    /// (TrimExcess, Capture) throws there, which EECS041 or EECS013 says, so its allocation is not reported.</param>
    private static void AnalyzeAllocations(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body, string runs, bool pass)
    {
        var pending = new Stack<IOperation>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            var operation = pending.Pop();
            // A throw ends the tick; a [Conditional] call is not compiled in release; console output is the cost
            // itself (the program prints on purpose, and the write costs more than the string it formats).
            if (operation is IThrowOperation
                || operation is IInvocationOperation call && (Conditional(call.TargetMethod) || Symbols.Named(call.TargetMethod.ContainingType, "System.Console")))
                continue;
            if (FillsOnce(operation)) continue; // allocated once (per thread): the next rows find it there
            // EECS071's: its fix draws from EntityRandom, which allocates nothing.
            if (operation is IObjectCreationOperation { Type: var random } && Symbols.Named(random, "System.Random") && Symbols.IsForEach(method)) continue;
            if (Allocates(operation, pass) is { } what)
            {
                var where = operation is IForEachLoopOperation loop ? loop.Collection.Syntax : operation.Syntax;
                context.ReportDiagnostic(Diagnostic.Create(Allocation, where.GetLocation(), Shorten(where), method.Name, runs, what,
                    Symbols.IsForEach(method) && ScratchType(operation) is { } kept
                        ? $"; a Scratch<{kept.ToMinimalDisplayString(operation.SemanticModel!, where.SpanStart)}> parameter keeps one per thread for the next rows (clear it before use)"
                        : ""));
                if (operation is not ILoopOperation) continue; // one report per expression: its parts allocate as part of it
            }

            foreach (var child in operation.ChildOperations) pending.Push(child);
        }
    }

    /// <summary>
    /// An assignment that creates a value the next rows reuse: what a <c>Scratch&lt;T&gt;</c> parameter holds
    /// (<c>s.Value ??= new()</c>, or an assignment under a condition: <c>if (s.Value.Ids == null) s.Value.Ids = new int[64];</c>),
    /// or a static field filled on first use (<c>Cache.List ??= new()</c>, a <c>[ThreadStatic]</c> one per thread).
    /// </summary>
    private static bool FillsOnce(IOperation operation) =>
        operation is ICoalesceAssignmentOperation coalesce
            && (IsScratch(Uses.Root(coalesce.Target)) || coalesce.Target is IFieldReferenceOperation { Field.IsStatic: true })
        || operation is ISimpleAssignmentOperation assignment && IsScratch(Uses.Root(assignment.Target))
        && Ancestors(assignment).Any(a => a is IConditionalOperation);

    /// <summary>
    /// The type a <c>Scratch&lt;T&gt;</c> parameter could keep instead of <paramref name="operation"/> creating it per row:
    /// an object or an array the code creates (not a params array, a closure or a string).
    /// </summary>
    private static ITypeSymbol? ScratchType(IOperation operation) => operation switch
    {
        IObjectCreationOperation { Type: { IsReferenceType: true } type } => type,
        IArrayCreationOperation { IsImplicit: false, Type: { } type } => type,
        ICollectionExpressionOperation { Type: { IsReferenceType: true } type } => type,
        _ => null,
    };

    private static bool IsScratch(IParameterSymbol? parameter) => parameter != null && Symbols.Classify(parameter) == Symbols.ParamKind.Scratch;

    private static IEnumerable<IOperation> Ancestors(IOperation operation)
    {
        for (var parent = operation.Parent; parent != null; parent = parent.Parent) yield return parent;
    }

    /// <summary>
    /// EECS058: an assignment storing what a <c>Scratch&lt;T&gt;</c> parameter holds by reference (a class, an array, a
    /// span, a struct holding one) into data of the world: a component, singleton, Maybe, Mut or Reduce parameter.
    /// </summary>
    private static void AnalyzeScratch(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body)
    {
        if (!method.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.Scratch)) return;
        foreach (var operation in body.Descendants())
        {
            if (operation is not ISimpleAssignmentOperation assignment || Uses.Root(assignment.Value) is not { } source
                || !IsScratch(source) || !Uses.Mutable(assignment.Value.Type, 0)) continue;
            if (Uses.Root(assignment.Target) is not { } target || Symbols.Classify(target) is not (Symbols.ParamKind.Component
                    or Symbols.ParamKind.Singleton or Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable or Symbols.ParamKind.Reduction))
                continue;
            context.ReportDiagnostic(Diagnostic.Create(ScratchEscape, assignment.Syntax.GetLocation(), method.Name,
                Shorten(assignment.Value.Syntax), Symbols.ScratchElement(source.Type)!.Name, source.Name, Shorten(assignment.Target.Syntax)));
        }
    }

    private static string Shorten(SyntaxNode syntax)
    {
        var text = syntax.ToString().Replace("\r", "").Replace('\n', ' ');
        return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
    }

    /// <summary>A method the compiler drops outside the symbol it names (Debug.Assert, Debug.WriteLine, ...).</summary>
    private static bool Conditional(IMethodSymbol method) =>
        method.GetAttributes().Any(a => Symbols.Named(a.AttributeClass, "System.Diagnostics.ConditionalAttribute"));

    /// <summary>What <paramref name="operation"/> allocates, or null: the kinds EECS028 names.</summary>
    private static string? Allocates(IOperation operation, bool pass)
    {
        switch (operation)
        {
            case IObjectCreationOperation { Type.IsReferenceType: true } creation:
                return $"a new {creation.Type!.Name}";
            case IAnonymousObjectCreationOperation:
                return "an anonymous object";
            case IArrayCreationOperation array:
                if (array.IsImplicit && array.DimensionSizes.All(d => d.ConstantValue is { HasValue: true, Value: 0 })) return null; // params: Array.Empty
                return array.IsImplicit ? "a params array" : "a new array";
            case ICollectionExpressionOperation collection when collection.Type is { IsReferenceType: true } type
                                                              && !(type is IArrayTypeSymbol && collection.Elements.Length == 0):
                return $"a new {(type is IArrayTypeSymbol ? "array" : type.Name)}";
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation function } when Captures(function) is { } captured:
                return $"a closure over '{captured}' and its delegate";
            case IDelegateCreationOperation { Target: IMethodReferenceOperation { Method.IsStatic: false } }:
                return "a delegate of an instance method";
            case IInterpolatedStringOperation interpolated when interpolated.ConstantValue is not { HasValue: true }
                                                               && interpolated.Parent is not IInterpolatedStringHandlerCreationOperation:
                return "a string built at run time";
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } concat
                when concat.ConstantValue is not { HasValue: true }:
                return "a string built at run time";
            case IInvocationOperation { TargetMethod: { ContainingType: { } owner } method } when (Symbols.Named(owner, Symbols.World) || Symbols.Named(owner, Symbols.Entity))
                                                                                                && !(pass && PassRefused.Contains(method.Name))
                                                                                                && Cold(method) is { } cold:
                return cold;
            case IInvocationOperation call when StringCall(call.TargetMethod):
                return "a string built at run time";
            case IInvocationOperation { TargetMethod.ContainingType: { Name: "Enumerable" or "ParallelEnumerable" or "Queryable" } linq } call
                when Symbols.Named(linq.ContainingNamespace, "System.Linq"):
                return $"LINQ '{call.TargetMethod.Name}' (an enumerator and delegates)";
            case IConversionOperation conversion when conversion.Operand.Type is { IsValueType: true } from
                                                      && conversion.Type is { IsReferenceType: true }
                                                      && conversion.Operand.ConstantValue is not { HasValue: true, Value: null }
                                                      && !(conversion.Parent is IInvocationOperation { TargetMethod.Name: "HasFlag" }
                                                           or IArgumentOperation { Parent: IInvocationOperation { TargetMethod.Name: "HasFlag" } }):
                return $"boxing a {from.Name}";
            case IForEachLoopOperation loop when (loop.Collection is IConversionOperation conv ? conv.Operand.Type : loop.Collection.Type) is
                { TypeKind: TypeKind.Interface }:
                return "an enumerator of an interface";
            default:
                return null;
        }
    }

    /// <summary>
    /// The cold calls of World and Entity that allocate (they belong to tools and quiet moments): what each allocates, or null.
    /// </summary>
    private static string? Cold(IMethodSymbol method) => method.Name switch
    {
        "TrimExcess" => "the arrays of the tables it shrinks (and the next update grows them again)",
        "Capture" when method.Parameters.Length == 0 => "a new WorldSnapshot (Capture(into) reuses one)",
        "Describe" => "a description of every component (objects and strings)",
        "DescribeTables" => "a description of every table (objects and strings)",
        "DescribeSchedule" => "a description of the schedule (objects and strings)",
        _ => null,
    };

    /// <summary>string.Format / Concat / Join, a string's methods that return a new string, and ToString().</summary>
    private static bool StringCall(IMethodSymbol method)
    {
        if (method.ContainingType?.SpecialType == SpecialType.System_String)
            return method.IsStatic ? method.Name is "Format" or "Concat" or "Join" or "Create"
                : method.ReturnType.SpecialType == SpecialType.System_String || method.ReturnType is IArrayTypeSymbol;
        return method is { Name: "ToString", IsStatic: false } && method.ReturnType.SpecialType == SpecialType.System_String;
    }

    /// <summary>The first variable of the enclosing code (or <c>this</c>) a lambda reads, or null: a lambda that captures nothing is cached.</summary>
    private static string? Captures(IAnonymousFunctionOperation function)
    {
        foreach (var operation in function.Body.Descendants())
        {
            ISymbol? variable = operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IInstanceReferenceOperation => function.Symbol.ContainingType,
                _ => null,
            };
            if (variable == null) continue;
            if (variable is INamedTypeSymbol) return "this";
            if (!Inside(variable.ContainingSymbol, function.Symbol)) return variable.Name;
        }

        return null;

        static bool Inside(ISymbol? owner, ISymbol function)
        {
            for (var s = owner; s != null; s = s.ContainingSymbol)
                if (SymbolEqualityComparer.Default.Equals(s, function)) return true;
            return false;
        }
    }

    // ---------------- calls: EECS025 (Get of a tracked component), EECS033, EECS034 ----------------

    private static void AnalyzeCall(OperationAnalysisContext context, Core core)
    {
        var call = (IInvocationOperation)context.Operation;
        var method = call.TargetMethod;
        var owner = method.ContainingType;
        bool generated = owner != null && (owner.IsExtension || owner.ContainingType?.IsExtension == true) && Symbols.IsGenerated(method);
        if (owner == null || !core.Is(owner, core.Entity) && !core.Is(owner, core.World) && !generated) return;

        // In a stable [ForEach] method EECS013 already reports it (Update is a structural call).
        if (core.Is(owner, core.World) && method.Name == "Update" && Symbols.SystemOf(context.ContainingSymbol) is { } system
            && !(context.ContainingSymbol is IMethodSymbol caller && Symbols.IsGeneratedJob(caller) && Symbols.IsStable(caller, call.SemanticModel!)))
        {
            context.ReportDiagnostic(Diagnostic.Create(UpdateInSystem, call.Syntax.GetLocation(), system.Name,
                "World.Update" + (method.TypeArguments.Length == 1 ? "<" + method.TypeArguments[0].Name + ">" : "")));
            return;
        }

        var model = call.SemanticModel!;
        // EECS025: a tracked component reached for writing (Get<T>(), the generated WriteX()) and only read; a copy of
        // it changed and dropped is EECS038's (keeping the ref is the fix, not Read).
        if (IsDroppedCopy(call)) return;
        if (core.Is(owner, core.Entity) && method is { Name: "Get", TypeArguments.Length: 1 } && core.Tracked(method.TypeArguments[0])
            && !Symbols.IsIndexed(method.TypeArguments[0]) && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            && !Uses.MayWrite(call))
        {
            string read = access.Expression + "." + access.Name.ToString().Replace("Get", "Read") + "()";
            context.ReportDiagnostic(Diagnostic.Create(WriteAccessOnlyReads, call.Syntax.GetLocation(), Fix(read, "Read it with Read<T>()"),
                $"Get<{method.TypeArguments[0].Name}>()", "it stamps the row as changed, so [Changed<" + method.TypeArguments[0].Name + ">] systems visit it",
                "read it with Read<" + method.TypeArguments[0].Name + ">()"));
            return;
        }

        if (generated && method.Name.StartsWith("Write") && method.Parameters.Length == 0 && method.ReturnsByRef
            && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax write } && !Uses.MayWrite(call))
        {
            string name = method.Name.Substring("Write".Length);
            string read = write.Expression + "." + name;
            if (Binds(model, call.Syntax, read, out var bound) && Symbols.IsGenerated(bound!))
                context.ReportDiagnostic(Diagnostic.Create(WriteAccessOnlyReads, call.Syntax.GetLocation(), Fix(read, "Read it with ." + name),
                    method.Name + "()", "it stamps the row as changed, so [Changed<" + name + ">] systems visit it", "read it with ." + name));
            return;
        }

        if (!generated && !RowLookupOf(call, context.ContainingSymbol, core) && !OtherLookupOf(call, context.ContainingSymbol, core)
            && !SetAfterCreation(call, core)) GeneratedEquivalent(context, call, core);
    }

    /// <summary>
    /// Whether <paramref name="call"/> is a lookup EECS027 reports (in a [ForEach] method or a job): its fix takes the
    /// lookup away, so EECS033's shorter spelling of it would only be undone.
    /// </summary>
    private static bool RowLookupOf(IInvocationOperation call, ISymbol containing, Core core) =>
        containing is IMethodSymbol method && (Symbols.IsGeneratedJob(method) || Symbols.JobInterface(method) != null)
        && RowLookups(core, method, Symbols.Root(call)).Any(l => l.Operation == call);

    /// <summary>Whether <paramref name="call"/> is a read EECS087 hands to a Lookup&lt;T&gt; (which reads by id, no shorter spelling to give).</summary>
    private static bool OtherLookupOf(IInvocationOperation call, ISymbol containing, Core core) =>
        containing is IMethodSymbol method && Symbols.IsForEach(method)
        && method.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.World)
        && OtherEntityReads(method, Symbols.Root(call), core) is { } found && found.Reads.Any(r => r.Operation == call);

    /// <summary>Whether <paramref name="call"/> is the Set EECS052 folds into the creation before it (and so removes).</summary>
    private static bool SetAfterCreation(IInvocationOperation call, Core core) =>
        call.Parent is IExpressionStatementOperation { Parent: IBlockOperation block } statement && block.Operations.IndexOf(statement) is > 0 and var at
        && block.Operations[at - 1] is IVariableDeclarationGroupOperation group
        && group.Declarations.SelectMany(d => d.Declarators).Any(d => CreateThenSetOf(d, core)?.Set == call);

    private static ImmutableDictionary<string, string?> Fix(string replacement, string title) =>
        ImmutableDictionary<string, string?>.Empty.Add(Replace, replacement).Add(FixTitle, title);

    /// <summary>Whether <paramref name="text"/>, in place of <paramref name="node"/>, binds; <paramref name="symbol"/> is what it names.</summary>
    private static bool Binds(SemanticModel model, SyntaxNode node, string text, out ISymbol? symbol)
    {
        var expression = SyntaxFactory.ParseExpression(text);
        var info = model.GetSpeculativeSymbolInfo(node.SpanStart, expression, SpeculativeBindingOption.BindAsExpression);
        symbol = info.Symbol;
        // A speculative member access binds to an extension block's member even when the receiver is not of the
        // block's type (entity.ChildOf finds the member of QueryItem<P, ChildOf>): the receiver must convert to it.
        if (symbol?.ContainingType is { IsExtension: true, ExtensionParameter: { } extended }
            && (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax called } ? called : expression as MemberAccessExpressionSyntax) is { } access
            && model.GetSpeculativeTypeInfo(node.SpanStart, access.Expression, SpeculativeBindingOption.BindAsExpression).Type is { } receiver
            && !model.Compilation.ClassifyConversion(receiver, extended.Type).IsImplicit)
            symbol = null;
        return symbol != null;
    }

    /// <summary>EECS033: a generic call with a generated member of the same meaning.</summary>
    private static void GeneratedEquivalent(OperationAnalysisContext context, IInvocationOperation call, Core core)
    {
        var method = call.TargetMethod;
        if (call.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } syntax) return;
        if (method.TypeArguments.Any(t => t is not INamedTypeSymbol || t is INamedTypeSymbol { IsGenericType: true } g
                && !Symbols.Named(g.OriginalDefinition, Symbols.DynamicBuffer))) return;
        var model = call.SemanticModel!;
        string receiver = access.Expression.ToString();
        string args = syntax.ArgumentList.ToString();
        bool onEntity = core.Is(method.ContainingType, core.Entity);
        string? candidate = null;
        bool property = false;
        if (method.TypeArguments.Length == 1 && method.TypeArguments[0] is INamedTypeSymbol type)
        {
            var element = Symbols.BufferElement(type);
            string name = (element ?? type).Name;
            bool tag = element == null && !type.GetMembers().Any(m => m is IFieldSymbol { IsStatic: false });
            (candidate, property) = (onEntity, method.Name) switch
            {
                (true, "Get") => (name, true),
                (true, "Read") => (name, true),
                (true, "Has") => (tag ? "Is" + name : "Has" + name, true),
                (true, "TryGet") => ("TryGet" + name, false),
                (true, "GetOrAdd") when call.Arguments.Length == 0 => ("GetOrAdd" + name, false),
                (true, "GetOrAdd") when !tag && SetCandidate(call, name, tag, out args) is ({ }, _) => ("GetOrAdd" + name, false), // GetOrAdd(new T(a)) as GetOrAddT(a)
                (true, "Remove") => ("Remove" + name, false),
                (true, "Disable") => ("Disable" + name, false),
                (true, "Enable") => ("Enable" + name, false),
                (true, "IsDisabled") => ("Is" + name + "Disabled", true),
                (true, "Set") => SetCandidate(call, name, tag, out args),
                (true, "SetIfChanged") when !tag && SetCandidate(call, name, tag, out args) is ({ } set, _) => (set + "IfChanged", false),
                (false, "GetSingleton") => (name, true),
                (false, "HasSingleton") => ("Has" + name, true),
                (false, "GetOrAddSingleton") when call.Arguments.Length == 0 => ("GetOrAdd" + name, false),
                (false, "GetOrAddSingleton") when !tag && SetCandidate(call, name, tag, out args) is ({ }, _) => ("GetOrAdd" + name, false),
                (false, "RemoveSingleton") => ("Remove" + name, false),
                (false, "Relations") => (name + "Index", true),
                _ => (null, false),
            };
        }
        else if (!onEntity && method is { Name: "Index", TypeArguments.Length: 2 })
            (candidate, property) = (method.TypeArguments[0].Name + "Index", true);
        if (candidate == null) return;

        string replacement = receiver + "." + candidate + (property ? "" : args);
        if (!Binds(model, syntax, replacement, out var bound) || !Symbols.IsGenerated(bound!)) return;
        // The same access: Get<T>() is a 'ref' (a watched component's property is 'ref readonly'; its writer is WriteX()).
        if (method.Name == "Get" && bound is IPropertySymbol { RefKind: not RefKind.Ref })
        {
            replacement = receiver + ".Write" + candidate + "()";
            if (!Binds(model, syntax, replacement, out bound) || !Symbols.IsGenerated(bound!)) return;
            candidate = "Write" + candidate + "()";
        }

        // A buffer's members exist while some code names DynamicBuffer<E>: the call must not be the only name.
        if (method.TypeArguments.Length == 1 && Symbols.BufferElement(method.TypeArguments[0]) is { } buffered
            && !BufferNamedElsewhere(model.Compilation, buffered.Name, syntax, context.CancellationToken)) return;

        context.ReportDiagnostic(Diagnostic.Create(GeneratedMember, syntax.GetLocation(), Fix(replacement, "Use ." + candidate.Split('(')[0]),
            Shorten(syntax), replacement));
    }

    /// <summary>
    /// EECS033 for a guarded write: <c>if (e.Score.Points != v) e.SetScore(v);</c> (or <c>e.Set(new Score(v))</c>) on a
    /// component of one field is <c>e.SetScoreIfChanged(v);</c>, the generated writer that compares the value itself
    /// (<c>SetIfChanged</c>). The field is read through the generated property, <c>Read&lt;T&gt;()</c> or <c>Get&lt;T&gt;()</c> of
    /// the same receiver; the if has no else and does nothing else.
    /// </summary>
    private static void AnalyzeGuardedSet(OperationAnalysisContext context, Core core)
    {
        var conditional = (IConditionalOperation)context.Operation;
        if (conditional is not { WhenFalse: null, Syntax: IfStatementSyntax syntax, Condition: IBinaryOperation { OperatorKind: BinaryOperatorKind.NotEquals } test }) return;
        var write = conditional.WhenTrue is IBlockOperation { Operations.Length: 1 } block ? block.Operations[0] : conditional.WhenTrue;
        if (write is not IExpressionStatementOperation { Operation: IInvocationOperation set }
            || set.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax setAccess }
            || set.Arguments.Length != 1)
            return;

        // The written value: the generated SetX(v), or Set(new X(v)) with one argument.
        var method = set.TargetMethod;
        INamedTypeSymbol? component = null;
        IOperation? value = null;
        if (Symbols.IsGenerated(method) && method.Name.StartsWith("Set") && method.Parameters.Length == 1)
        {
            // The generated writer names its component: the struct of that name in scope where the if is.
            component = conditional.SemanticModel!.LookupNamespacesAndTypes(syntax.SpanStart, name: method.Name.Substring("Set".Length))
                .OfType<INamedTypeSymbol>().FirstOrDefault(t => t.TypeKind == TypeKind.Struct && Symbols.Implements(t, Symbols.Component));
            value = set.Arguments[0].Value;
        }
        else if (core.Is(method.ContainingType, core.Entity) && method is { Name: "Set", TypeArguments.Length: 1 }
                 && Unwrap(set.Arguments[0].Value) is IObjectCreationOperation { Initializer: null, Arguments.Length: 1 } creation)
        {
            component = method.TypeArguments[0] as INamedTypeSymbol;
            value = creation.Arguments[0].Value;
        }

        if (component == null || value == null) return;
        var fields = component.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
        if (fields.Count != 1) return;
        // The compared field and value, either way round.
        var (read, compared) = Field(Unwrap(test.LeftOperand), fields[0]) is { } left ? (left, test.RightOperand)
            : Field(Unwrap(test.RightOperand), fields[0]) is { } right ? (right, test.LeftOperand) : (null, null);
        if (read == null || !SyntaxFactory.AreEquivalent(Unwrap(compared!).Syntax, Unwrap(value).Syntax)) return;
        string receiver = setAccess.Expression.ToString();
        if (Receiver(read, component) is not { } readFrom || readFrom != receiver) return;

        string replacement = receiver + ".Set" + component.Name + "IfChanged(" + Unwrap(value).Syntax + ")";
        if (!Binds(conditional.SemanticModel!, syntax, replacement, out var bound) || !Symbols.IsGenerated(bound!)) return;
        context.ReportDiagnostic(Diagnostic.Create(GeneratedMember, syntax.GetLocation(), Fix(replacement + ";", "Use .Set" + component.Name + "IfChanged"),
            Shorten(syntax), replacement));

        static IOperation Unwrap(IOperation operation)
        {
            while (operation is IConversionOperation { IsImplicit: true } conversion) operation = conversion.Operand;
            return operation;
        }

        static IOperation? Field(IOperation operation, IFieldSymbol field) =>
            operation is IFieldReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Field, field) ? reference.Instance : null;

        // The receiver whose component the comparison reads: e.X (generated), e.Read<X>(), e.Get<X>().
        static string? Receiver(IOperation instance, INamedTypeSymbol component) => instance switch
        {
            IPropertyReferenceOperation { Syntax: MemberAccessExpressionSyntax access } property when Symbols.IsGenerated(property.Property)
                && SymbolEqualityComparer.Default.Equals(property.Type, component) => access.Expression.ToString(),
            IInvocationOperation { TargetMethod: { Name: "Read" or "Get", TypeArguments.Length: 1 } read, Syntax: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } }
                when SymbolEqualityComparer.Default.Equals(read.TypeArguments[0], component) => access.Expression.ToString(),
            _ => null,
        };
    }

    /// <summary>
    /// Whether code outside <paramref name="replaced"/> names <c>DynamicBuffer&lt;E&gt;</c> for an element called
    /// <paramref name="element"/>: the generator writes a buffer's members only for elements some code names that way
    /// (ComponentGenerator), so replacing the only name with a member would remove the member with it. Syntax only:
    /// the name's last identifier is compared.
    /// </summary>
    private static bool BufferNamedElsewhere(Compilation compilation, string element, SyntaxNode replaced, System.Threading.CancellationToken token)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            // Generated files (the buffer's own members among them) name it because the members exist: not a reason.
            if (tree.FilePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var node in tree.GetRoot(token).DescendantNodes())
                if (node is GenericNameSyntax { Identifier.ValueText: "DynamicBuffer", TypeArgumentList.Arguments.Count: 1 } name
                    && Last(name.TypeArgumentList.Arguments[0]) == element
                    && !(tree == replaced.SyntaxTree && replaced.Span.Contains(name.Span)))
                    return true;
        }

        return false;

        static string? Last(TypeSyntax type) => type switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            SimpleNameSyntax n => n.Identifier.ValueText,
            _ => null,
        };
    }

    /// <summary>Set(new T(args)) as SetT(args), Set(new T()) / Set(default(T)) of a tag as SetT(); else null.</summary>
    private static (string?, bool) SetCandidate(IInvocationOperation call, string name, bool tag, out string args)
    {
        args = "()";
        if (call.Arguments.Length != 1) return (null, false);
        var value = call.Arguments[0].Value;
        while (value is IConversionOperation { IsImplicit: true } c) value = c.Operand;
        switch (value)
        {
            case IObjectCreationOperation { Initializer: null, Syntax: BaseObjectCreationExpressionSyntax creation }:
                args = creation.ArgumentList?.ToString() ?? "()";
                return ("Set" + name, false);
            case IDefaultValueOperation when tag:
                return ("Set" + name, false);
            default:
                return (null, false);
        }
    }

    /// <summary>EECS033 for query items: <c>item.Component1</c> as <c>item.Position</c>.</summary>
    private static void AnalyzeItem(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        var property = reference.Property;
        if (!Symbols.IsItemComponent(property) || reference.Syntax is not MemberAccessExpressionSyntax access
            || property.Type is not INamedTypeSymbol { IsGenericType: false } type) return;
        string replacement = access.Expression + "." + type.Name;
        if (!Binds(reference.SemanticModel!, access, replacement, out var bound) || !Symbols.IsGenerated(bound!)
            || bound is IPropertySymbol { RefKind: not RefKind.Ref } && Uses.MayWrite(reference)) return;
        context.ReportDiagnostic(Diagnostic.Create(GeneratedMember, access.GetLocation(), Fix(replacement, "Use ." + type.Name),
            access.ToString(), replacement));
    }

    // ---------------- loops: EECS030, EECS031, EECS032 ----------------

    private static void AnalyzeLoop(OperationAnalysisContext context, Core core)
    {
        switch (context.Operation)
        {
            case IForEachLoopOperation loop when Query(loop.Collection, core) is { } query:
                QueryLoop(context, loop, query, core);
                break;
            case IForLoopOperation loop:
                CountedLoop(context, loop, core);
                break;
        }
    }

    /// <summary>The <c>world.Query&lt;…&gt;(filter)</c> or <c>world.Read&lt;…&gt;(filter)</c> a foreach walks, or null.</summary>
    private static IInvocationOperation? Query(IOperation collection, Core core) =>
        Symbols.Unwrap(collection) is IInvocationOperation { TargetMethod: { Name: "Query" or "Read", TypeArguments.Length: > 0 } method } call
        && core.Is(method.ContainingType, core.World) && call.Instance != null ? call : null;

    /// <summary>
    /// Whether every use of the query item <paramref name="item"/> in <paramref name="body"/> reads: its entity, or a
    /// component member whose ref the use cannot write through (<see cref="Uses.MayWrite"/>). Any other use of the item
    /// (passed on, stored) counts as a write, so a missed read costs a missed warning, never a wrong one.
    /// </summary>
    private static bool ReadsOnly(IOperation body, ILocalSymbol item)
    {
        foreach (var use in body.Descendants().OfType<ILocalReferenceOperation>())
        {
            if (!SymbolEqualityComparer.Default.Equals(use.Local, item)) continue;
            if (use.Parent is not IPropertyReferenceOperation { Property: var property } member) return false;
            if (property.Name == "Entity" || property.ReturnsByRefReadonly) continue;
            if (!property.ReturnsByRef || Uses.MayWrite(member)) return false;
        }

        return true;
    }

    private static IOperation? Single(IOperation body) => body switch
    {
        IBlockOperation { Operations.Length: 1 } block => block.Operations[0],
        IBlockOperation => null,
        _ => body,
    };

    private static void QueryLoop(OperationAnalysisContext context, IForEachLoopOperation loop, IInvocationOperation query, Core core)
    {
        var model = loop.SemanticModel!;
        int at = loop.Syntax.SpanStart;
        var item = (loop.LoopControlVariable as IVariableDeclaratorOperation)?.Symbol;
        string world = query.Instance!.Syntax.ToString();
        var types = query.TargetMethod.TypeArguments;
        string typeList = string.Join(", ", types.Select(t => t.ToMinimalDisplayString(model, at)));
        string? filter = query.Arguments.FirstOrDefault(a => !a.IsImplicit)?.Value.Syntax.ToString();
        var statement = Single(loop.Body);

        // EECS032: a loop that only counts.
        if (statement is IExpressionStatementOperation { Operation: var step } && Counter(step) is { } counter && item != null && !Mentions(loop.Body, item))
        {
            string counted = $"{counter} += {world}.Count<{typeList}>({filter})";
            context.ReportDiagnostic(Diagnostic.Create(QueryByHand, loop.Syntax.GetLocation(),
                Binds(model, loop.Syntax, counted, out _) ? Fix(counted + ";", "Count them with Count") : null,
                "This loop counts the query's entities one by one", $"'{world}.Count<{typeList}>()' sums the tables' row counts"));
            return;
        }

        // EECS032: a test of each row's entity that a filter would make per table.
        if (loop.Body is IBlockOperation { Operations.Length: > 0 } body && body.Operations[0] is IConditionalOperation
            {
                WhenTrue: var then, WhenFalse: null, Condition: var condition,
            } && (then is IBranchOperation { BranchKind: BranchKind.Continue } || then is IBlockOperation { Operations.Length: 1 } only
                && only.Operations[0] is IBranchOperation { BranchKind: BranchKind.Continue })
            && Presence(condition, item, core) is { } presence)
        {
            string attribute = presence.Has ? "Without" : "With";
            context.ReportDiagnostic(Diagnostic.Create(QueryByHand, condition.Syntax.GetLocation(),
                "This loop skips entities by testing each one", $"filter the query with {attribute}<{presence.Type.Name}>() instead"));
            ReadOnly();
            return;
        }

        if (!Bulk()) ReadOnly();
        return;

        // EECS046: a query that only reads tracked components. The fix renames the call; a loop variable typed by hand
        // (QueryItem<...> item) would need its type changed too, so it gets the fix only when declared with 'var'.
        // Run only when the loop stays: a loop EECS032 makes a Count or EECS030 a bulk call has nothing left to read.
        void ReadOnly()
        {
            if (query.TargetMethod.Name == "Query" && item != null && ReadsOnly(loop.Body, item)
                && types.Where(t => Symbols.Implements(t, Symbols.TrackChanges)).Select(t => t.ToMinimalDisplayString(model, at)).ToArray() is { Length: > 0 } tracked
                && query.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax queried } })
            {
                string read = "Read" + queried.TypeArgumentList;
                bool implicitlyTyped = loop.Syntax is not ForEachStatementSyntax { Type: { IsVar: false } };
                context.ReportDiagnostic(Diagnostic.Create(ReadOnlyQuery, queried.GetLocation(), implicitlyTyped ? Fix(read, "Read without stamping") : null,
                    queried.ToString(), string.Join(", ", tracked), $"{world}.{read}"));
            }
        }

        // EECS030: the same structural change through each entity.
        bool Bulk()
        {
            if (statement is not IExpressionStatementOperation { Operation: IInvocationOperation call } || item == null
                || !IsItemEntity(Symbols.Receiver(call), item) || !Invariant(call.Arguments.Select(a => a.Value), loop.Body, item)) return false;
            string with = (filter != null ? "(" + filter + ")" : "QueryFilter.Create()") + string.Concat(types.Select(t => $".With<{t.ToMinimalDisplayString(model, at)}>()"));
            if (BulkCall(call, typeList, filter, with, types, model, at, core) is not { } bulk) return false;
            string text = world + "." + bulk.Call;
            context.ReportDiagnostic(Diagnostic.Create(PerEntityChange, loop.Syntax.GetLocation(),
                Binds(model, loop.Syntax, text, out _) ? Fix(text + ";", "Make it one bulk call") : null, "loop", bulk.Verb, "of the query", $"'{bulk.Bulk}'"));
            return true;
        }
    }

    /// <summary>
    /// EECS030 for a [ForEach] method whose whole body is one structural change of its entity, the same for every row
    /// (no parameter in its arguments), over filters a QueryFilter holds (no Changed / Removed, no slices): the bulk
    /// call in OnUpdate does what the generated loop does, a table at a time.
    /// </summary>
    private static bool ForEachBulk(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body, Core core)
    {
        var block = body is IMethodBodyOperation { BlockBody: var b, ExpressionBody: var e } ? b ?? e : body;
        if (block == null || Single(block) is not IExpressionStatementOperation { Operation: IInvocationOperation call }
            || Symbols.Unwrap(Symbols.Receiver(call)) is not IParameterReferenceOperation { Parameter: var entity }
            || Symbols.Classify(entity) != Symbols.ParamKind.Entity || Symbols.IsRunner(method) || Symbols.IsParallel(method)
            || Symbols.Slices(method) != 1 || !Symbols.IsSystem(method.ContainingType)
            || call.Arguments.Any(a => a.Value.DescendantsAndSelf().Any(o => o is IParameterReferenceOperation))
            || !Invariant(call.Arguments.Select(a => a.Value), block, null)) return false;

        var queried = new List<ITypeSymbol>();
        string rest = "";
        var model = body.SemanticModel!;
        int at = body.Syntax.SpanStart;
        foreach (var p in method.Parameters)
            if (Symbols.Classify(p) is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable or Symbols.ParamKind.Optional)
            {
                // An optional component is no filter: its rows with and without it would both need the call.
                if (Symbols.Classify(p) == Symbols.ParamKind.Optional || Symbols.QueriedComponent(p) is not { } c) return false;
                queried.Add(c);
            }
            else if (Symbols.Classify(p) is not (Symbols.ParamKind.Entity or Symbols.ParamKind.Singleton)) return false;

        foreach (var (kind, type) in Symbols.AttributeFilters(method).Select(f => (f.Kind, f.Type))
                     .Concat(Symbols.GroupFilters(method).Select(f => (f.Kind, f.Type))))
            switch (kind)
            {
                case Symbols.FilterKind.With: queried.Add(type); break;
                case Symbols.FilterKind.Without or Symbols.FilterKind.WithAny: rest += $".{kind}<{type.ToMinimalDisplayString(model, at)}>()"; break;
                default: return false; // Changed / Removed: the rows since the last run, which no bulk call selects
            }

        if (queried.Count == 0 || queried.Any(t => t is ITypeParameterSymbol or { TypeKind: TypeKind.Error })) return false;
        string typeList = string.Join(", ", queried.Select(t => t.ToMinimalDisplayString(model, at)));
        string? filter = rest.Length > 0 ? "QueryFilter.Create()" + rest : null;
        string with = "QueryFilter.Create()" + string.Concat(queried.Select(t => $".With<{t.ToMinimalDisplayString(model, at)}>()")) + rest;
        if (BulkCall(call, typeList, filter, with, queried, model, at, core) is not { } bulk) return false;
        context.ReportDiagnostic(Diagnostic.Create(PerEntityChange, method.Locations.FirstOrDefault(),
            "[ForEach] method", bulk.Verb, "it visits", $"'World.{bulk.Call}' in OnUpdate"));
        return true;
    }

    /// <summary>
    /// The bulk call that makes a structural change of one entity for every entity a filter matches: (its name, the call
    /// after the world, what it does), or null when none does. <paramref name="types"/> are the queried components
    /// (<paramref name="typeList"/> as text), <paramref name="filter"/> the filter argument's text (null for none) and
    /// <paramref name="with"/> the same filter with the queried components required.
    /// </summary>
    private static (string Bulk, string Call, string Verb)? BulkCall(IInvocationOperation call, string typeList, string? filter, string with,
        IReadOnlyList<ITypeSymbol> types, SemanticModel model, int at, Core core)
    {
        var method = call.TargetMethod;
        bool generated = Symbols.IsGenerated(method);
        var component = ComponentOf(method, core);
        if (component != null && Symbols.IsWorldTag(component)) return null; // the world's own tags are EECS045's, by one call or by all
        string T() => component!.ToMinimalDisplayString(model, at);
        switch (generated ? Generated(method.Name) : method.Name)
        {
            case "Destroy" when !generated && types.Count > 0:
                return ("DestroyAll", $"DestroyAll<{typeList}>({filter})", "destroys");
            // A Set of a component the query requires overwrites a value: no row moves, nothing for SetAll to batch.
            // A grouped component's new value moves each row to its group's table, one by one under SetAll too.
            case "Set" when component != null && !types.Contains(component, SymbolEqualityComparer.Default)
                            && !Symbols.IsGrouped(component)
                            && SetValue(call, component, generated, model, at) is { } value:
                return ("SetAll", $"SetAll({value}, {with})", "adds a component to");
            case "Remove" when component != null && call.Arguments.All(a => a.IsImplicit):
                return ("RemoveAll", $"RemoveAll<{T()}>({with})", "removes a component from");
            // ChildOf and cleanup components cannot be disabled, by one call or by all (EECS048).
            case "Disable" when component != null && Symbols.IsDisableable(component):
                return ("DisableAll", $"DisableAll<{T()}>({with})", "disables a component of");
            case "Enable" when component != null:
                return ("EnableAll", $"EnableAll<{T()}>({with})", "enables a component of");
            default:
                return null;
        }

        static string Generated(string member) =>
            member.StartsWith("Set") ? "Set" : member.StartsWith("Remove") ? "Remove" : member.StartsWith("Disable") ? "Disable" : member.StartsWith("Enable") ? "Enable" : member;
    }

    /// <summary>The value a Set call stores, as source text that compiles where the loop is; null if it cannot.</summary>
    private static string? SetValue(IInvocationOperation call, ITypeSymbol component, bool generated, SemanticModel model, int at)
    {
        if (!generated) return call.Arguments.Length == 1 ? call.Arguments[0].Value.Syntax.ToString() : null;
        string value = "new " + component.ToMinimalDisplayString(model, at) + "("
                       + string.Join(", ", call.Arguments.Where(a => !a.IsImplicit).Select(a => a.Syntax.ToString())) + ")";
        return Binds(model, call.Syntax, value, out var ctor) && ctor is IMethodSymbol { MethodKind: MethodKind.Constructor } ? value : null;
    }

    /// <summary><c>n++</c>, <c>++n</c> or <c>n += 1</c> on an int or long variable: its source text; else null.</summary>
    private static string? Counter(IOperation operation) => operation switch
    {
        IIncrementOrDecrementOperation { Kind: OperationKind.Increment, Target: var target } when Countable(target) => target.Syntax.ToString(),
        ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Value.ConstantValue: { HasValue: true, Value: 1 }, Target: var target }
            when Countable(target) => target.Syntax.ToString(),
        _ => null,
    };

    private static bool Countable(IOperation target) =>
        target is ILocalReferenceOperation or IFieldReferenceOperation or IParameterReferenceOperation
        && target.Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64;

    /// <summary>A test of the item's entity for a component: (whether the loop continues when it has it, the component).</summary>
    private static (bool Has, ITypeSymbol Type)? Presence(IOperation condition, ILocalSymbol? item, Core core)
    {
        bool negated = false;
        while (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } not)
        {
            negated = !negated;
            condition = not.Operand;
        }

        ISymbol? member;
        IOperation? receiver;
        switch (condition)
        {
            case IInvocationOperation call: member = call.TargetMethod; receiver = Symbols.Receiver(call); break;
            case IPropertyReferenceOperation property: member = property.Property; receiver = property.Instance; break;
            default: return null;
        }

        if (item == null || !IsItemEntity(receiver, item)) return null;
        ITypeSymbol? type = member is IMethodSymbol { Name: "Has", TypeArguments.Length: 1 } has && core.Is(has.ContainingType, core.Entity) ? has.TypeArguments[0]
            : Symbols.IsGenerated(member) && (member.Name.StartsWith("Has") || member.Name.StartsWith("Is") && !member.Name.EndsWith("Disabled"))
              && member is IPropertySymbol { Type.SpecialType: SpecialType.System_Boolean } ? Named(member, core) : null;
        return type == null || type is ITypeParameterSymbol ? null : (!negated, type);
    }

    private static bool IsItemEntity(IOperation? receiver, ISymbol item) =>
        Symbols.Unwrap(receiver) is IPropertyReferenceOperation { Property.Name: "Entity", Instance: ILocalReferenceOperation local }
        && SymbolEqualityComparer.Default.Equals(local.Local, item);

    private static bool Mentions(IOperation body, ISymbol variable) =>
        body.Descendants().Any(o => o is ILocalReferenceOperation local && SymbolEqualityComparer.Default.Equals(local.Local, variable));

    /// <summary>
    /// Values that are the same for every iteration: constants, and variables, fields and properties declared outside
    /// the loop, combined by operators and constructors (no calls, which could return something else each time).
    /// </summary>
    private static bool Invariant(IEnumerable<IOperation> values, IOperation body, ISymbol? item)
    {
        foreach (var value in values)
            foreach (var operation in value.DescendantsAndSelf())
                switch (operation)
                {
                    case IAssignmentOperation { Parent: IObjectOrCollectionInitializerOperation }: // new Q { V = 1 }: a member of the value
                        break;
                    case IInvocationOperation or IIncrementOrDecrementOperation or IAssignmentOperation or IAwaitOperation
                        or IAnonymousFunctionOperation or IDelegateCreationOperation:
                        return false;
                    case ILocalReferenceOperation local when SymbolEqualityComparer.Default.Equals(local.Local, item)
                                                             || body.Syntax.Span.Contains(local.Local.Locations.FirstOrDefault()?.SourceSpan ?? default):
                        return false;
                    case IPropertyReferenceOperation { Property.GetMethod: { } getter } when !getter.IsReadOnly && getter.ContainingType.IsValueType:
                        return false;
                }

        return true;
    }

    /// <summary>EECS031: CreateEntity / Instantiate in a counted for loop with no reservation before it.</summary>
    private static void CountedLoop(OperationAnalysisContext context, IForLoopOperation loop, Core core)
    {
        if (loop.Before.Length != 1 || loop.Before[0] is not IVariableDeclarationGroupOperation group
            || Declarator(group) is not { Symbol: var index, Initializer.Value: var start }
            || loop.Condition is not IBinaryOperation { OperatorKind: BinaryOperatorKind.LessThan, LeftOperand: ILocalReferenceOperation left, RightOperand: var bound }
            || !SymbolEqualityComparer.Default.Equals(left.Local, index)
            || loop.AtLoopBottom.Length != 1 || loop.AtLoopBottom[0] is not IExpressionStatementOperation { Operation: var bottom } || !Steps(bottom, index)
            || !Simple(bound) || !Simple(start)) return;

        var creations = loop.Body.Descendants().OfType<IInvocationOperation>().Where(c => core.Is(c.TargetMethod.ContainingType, core.World)
            && (c.TargetMethod.Name == "CreateEntity" && c.TargetMethod.TypeArguments.Length > 0
                || c.TargetMethod.Name == "Instantiate" && (c.Arguments.Length == 1 || c.TargetMethod.Parameters[1].RefKind == RefKind.In))
            && c.Instance != null).ToArray(); // Instantiate(prefab) or Instantiate(prefab, in values), not a batch
        if (creations.Length != 1 || loop.Body.Descendants().Any(o => o is IAnonymousFunctionOperation)) return;
        var create = creations[0];
        // Only a creation on every pass of this loop creates its count: one under a branch or in an inner loop replaces
        // an entity now and then, or is the inner loop's to report.
        if (!Symbols.AlwaysRuns(create, loop.Body)) return;
        string world = create.Instance!.Syntax.ToString();
        // A system creates entities in a world that is running: rows freed by earlier destroys are reused, and a
        // table that grew keeps its size, so the cost is a start-up's, paid once.
        if (Reserved(loop, world, core) || Symbols.SystemOf(context.ContainingSymbol) != null) return;
        // A count known to be small never sends an array to the large object heap (D28): doubling costs nothing there.
        if ((bound.ConstantValue is { HasValue: true, Value: int end } ? end : FixedLength(bound)) is int known
            && (start.ConstantValue is { HasValue: true, Value: int begin } ? known - begin : known) < SmallCount)
            return;

        var model = loop.SemanticModel!;
        int at = loop.Syntax.SpanStart;
        bool zero = start.ConstantValue is { HasValue: true, Value: 0 };
        string count = zero ? bound.Syntax.ToString() : $"{bound.Syntax} - {start.Syntax}";
        bool instantiate = create.TargetMethod.Name == "Instantiate";

        // The whole loop as one call: `handles[i] = world.CreateEntity(values)` over every slot of `handles`, the values the same each time.
        if (zero && Single(loop.Body) is IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { Target: IArrayElementReferenceOperation { ArrayReference: var array, Indices: { Length: 1 } indices }, Value: var assigned } }
            && indices[0] is ILocalReferenceOperation i
            && Symbols.Unwrap(assigned) == create && SymbolEqualityComparer.Default.Equals(i.Local, index)
            && bound is IPropertyReferenceOperation { Property.Name: "Length", Instance: { } lengthOf } && lengthOf.Syntax.ToString() == array.Syntax.ToString()
            && Invariant(create.Arguments.Select(a => a.Value), loop.Body, index))
        {
            string handles = array.Syntax.ToString();
            string text = instantiate
                ? $"{world}.Instantiate({string.Join(", ", create.Arguments.Select((a, k) => k == 0 ? $"{a.Value.Syntax}, {handles}" : a.Value.Syntax.ToString()))})"
                : $"{world}.CreateEntities({handles}, {string.Join(", ", create.Arguments.Select(a => a.Value.Syntax.ToString()))})";
            context.ReportDiagnostic(Diagnostic.Create(CreateInLoop, loop.Syntax.GetLocation(),
                Binds(model, loop.Syntax, text, out _) ? Fix(text + ";", "Create them in one call") : null,
                count, $"'{text}' creates them in one pass"));
            return;
        }

        // Each entity its own values: reserve the table first (Reserve of a prefab reserves the table of its
        // components alone, not the one an instance with given components goes to).
        if (instantiate && create.Arguments.Length > 1) return;
        string reserve = instantiate
            ? Invariant([create.Arguments[0].Value], loop.Body, index) ? $"{world}.Reserve({create.Arguments[0].Value.Syntax}, {count})" : ""
            : $"{world}.Reserve<{string.Join(", ", create.TargetMethod.TypeArguments.Select(t => t.ToMinimalDisplayString(model, at)))}>({count})";
        if (reserve.Length == 0) return;
        context.ReportDiagnostic(Diagnostic.Create(CreateInLoop, loop.Syntax.GetLocation(),
            Binds(model, loop.Syntax, reserve, out _) ? ImmutableDictionary<string, string?>.Empty.Add(Insert, reserve + ";").Add(FixTitle, "Reserve room first") : null,
            count, $"reserve room first with '{reserve}'"));
    }

    /// <summary>
    /// <c>handles.Length</c> of a local declared as <c>new T[constant]</c> and never assigned or passed by reference
    /// afterwards: that constant; else null.
    /// </summary>
    private static int? FixedLength(IOperation bound)
    {
        if (Symbols.Unwrap(bound) is not IPropertyReferenceOperation { Property.Name: "Length", Instance: ILocalReferenceOperation { Local: { Type: IArrayTypeSymbol } local } } length)
            return null;
        IOperation root = length;
        while (root.Parent != null) root = root.Parent;
        int? size = null;
        foreach (var operation in root.Descendants())
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local):
                    if (Symbols.Unwrap(declarator.Initializer?.Value) is not IArrayCreationOperation { DimensionSizes: { Length: 1 } sizes }
                        || sizes[0].ConstantValue is not { HasValue: true, Value: int n }) return null;
                    size = n;
                    break;
                case IAssignmentOperation { Target: ILocalReferenceOperation target } when SymbolEqualityComparer.Default.Equals(target.Local, local):
                    return null;
                case IArgumentOperation { Parameter.RefKind: not RefKind.None, Value: ILocalReferenceOperation passed }
                    when SymbolEqualityComparer.Default.Equals(passed.Local, local):
                    return null;
            }

        return size;
    }

    /// <summary>The one variable a declaration declares, or null.</summary>
    private static IVariableDeclaratorOperation? Declarator(IVariableDeclarationGroupOperation group) =>
        group.Declarations.Length == 1 && group.Declarations[0].Declarators.Length == 1 ? group.Declarations[0].Declarators[0] : null;

    private static bool Steps(IOperation bottom, ILocalSymbol index) => bottom switch
    {
        IIncrementOrDecrementOperation { Kind: OperationKind.Increment, Target: ILocalReferenceOperation l } => SymbolEqualityComparer.Default.Equals(l.Local, index),
        ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Value.ConstantValue: { HasValue: true, Value: 1 }, Target: ILocalReferenceOperation l } =>
            SymbolEqualityComparer.Default.Equals(l.Local, index),
        _ => false,
    };

    /// <summary>An expression without side effects that can be repeated before the loop: constants, variables, fields, Length / Count.</summary>
    private static bool Simple(IOperation operation) => Symbols.Unwrap(operation) switch
    {
        { ConstantValue.HasValue: true } => true,
        ILocalReferenceOperation or IParameterReferenceOperation => true,
        IFieldReferenceOperation field => field.Instance == null || Simple(field.Instance),
        IInstanceReferenceOperation => true,
        IPropertyReferenceOperation { Property.Name: "Length" or "Count", Property.IsIndexer: false } property => property.Instance == null || Simple(property.Instance),
        IBinaryOperation binary => Simple(binary.LeftOperand) && Simple(binary.RightOperand),
        _ => false,
    };

    /// <summary>Whether a statement before the loop, in its block, reserves room in or bulk-creates into the same world.</summary>
    private static bool Reserved(IForLoopOperation loop, string world, Core core)
    {
        if (loop.Parent is not IBlockOperation block) return false;
        foreach (var statement in block.Operations)
        {
            if (statement == loop) break;
            foreach (var call in statement.DescendantsAndSelf().OfType<IInvocationOperation>())
                if (core.Is(call.TargetMethod.ContainingType, core.World) && call.Instance?.Syntax.ToString() == world
                    && call.TargetMethod.Name is "Reserve" or "EnsureEntityCapacity" or "CreateEntities")
                    return true;
        }

        return false;
    }

    /// <summary>EECS032: <c>world.Count&lt;…&gt;() &gt; 0</c> (and its other spellings) as <c>world.Any&lt;…&gt;()</c>.</summary>
    private static void AnalyzeComparison(OperationAnalysisContext context, Core core)
    {
        var binary = (IBinaryOperation)context.Operation;
        bool countLeft = IsCount(binary.LeftOperand, core);
        if (!countLeft && !IsCount(binary.RightOperand, core)) return;
        var count = (IInvocationOperation)Symbols.Unwrap(countLeft ? binary.LeftOperand : binary.RightOperand)!;
        var other = countLeft ? binary.RightOperand : binary.LeftOperand;
        if (other.ConstantValue is not { HasValue: true, Value: int value }) return;
        var kind = binary.OperatorKind;
        if (!countLeft) kind = kind switch // 0 < n is n > 0
        {
            BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
            BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
            BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
            BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
            _ => kind,
        };
        bool? any = (kind, value) switch
        {
            (BinaryOperatorKind.GreaterThan, 0) or (BinaryOperatorKind.NotEquals, 0) or (BinaryOperatorKind.GreaterThanOrEqual, 1) => true,
            (BinaryOperatorKind.Equals, 0) or (BinaryOperatorKind.LessThanOrEqual, 0) or (BinaryOperatorKind.LessThan, 1) => false,
            _ => null,
        };
        if (any == null || count.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax name } } syntax) return;
        string text = (any.Value ? "" : "!") + syntax.ReplaceToken(name.Identifier, SyntaxFactory.Identifier("Any"));
        context.ReportDiagnostic(Diagnostic.Create(QueryByHand, binary.Syntax.GetLocation(),
            Binds(binary.SemanticModel!, binary.Syntax, text, out _) ? Fix(text, "Use Any") : null,
            "Count is compared with zero", $"'{text}' stops at the first table with a row"));
    }

    private static bool IsCount(IOperation operation, Core core) =>
        Symbols.Unwrap(operation) is IInvocationOperation { TargetMethod: { Name: "Count", TypeArguments.Length: > 0 } method, Instance: not null }
        && core.Is(method.ContainingType, core.World);

    // ---------------- EECS052: a component added right after the creation ----------------

    /// <summary>
    /// EECS052: <c>var e = world.CreateEntity(a);</c> (or <c>Instantiate(prefab, …)</c>) followed by <c>e.Set(b)</c> /
    /// <c>e.SetB(…)</c> of a component the creation does not give. Left out: a grouped component (its value picks
    /// the table), the world's own tags (EECS045), values that call anything or read the entity (they would run before
    /// the creation), and creations of nine components already.
    /// </summary>
    private static void AnalyzeCreation(OperationAnalysisContext context, Core core)
    {
        if (CreateThenSetOf((IVariableDeclaratorOperation)context.Operation, core) is not { } found) return;
        var (create, set, next, component, text) = found;
        var model = create.SemanticModel!;
        int position = create.Syntax.SpanStart;
        var fix = Binds(model, create.Syntax, text, out var bound) && bound is IMethodSymbol { Name: "CreateEntity" or "Instantiate" }
            ? Fix(text, "Create it with the component").Add(RemoveOthers, "") : null;
        context.ReportDiagnostic(Diagnostic.Create(CreateThenSet, create.Syntax.GetLocation(), fix is null ? null : [next.Syntax.GetLocation()], fix,
            Shorten(set.Syntax), component.ToMinimalDisplayString(model, position), Shorten(SyntaxFactory.ParseExpression(text))));
    }

    /// <summary>
    /// The creation <paramref name="declarator"/> declares and the Set right after it that EECS052 folds into it, with
    /// the text of the creation given the component; null when the rule does not apply.
    /// </summary>
    private static (IInvocationOperation Create, IInvocationOperation Set, IOperation Next, ITypeSymbol Component, string Text)?
        CreateThenSetOf(IVariableDeclaratorOperation declarator, Core core)
    {
        if (declarator.Symbol.RefKind != RefKind.None || Symbols.Unwrap(declarator.Initializer?.Value) is not IInvocationOperation create
            || !core.Is(create.TargetMethod.ContainingType, core.World) || create.TargetMethod.Name is not ("CreateEntity" or "Instantiate")
            || !core.Is(create.TargetMethod.ReturnType, core.Entity) || create.Instance == null
            || create.TargetMethod.Name == "Instantiate" && create.Arguments.Skip(1).Any(a => a.Parameter?.RefKind != RefKind.In)
            || create.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax created } creation
            || declarator.Parent?.Parent is not IVariableDeclarationGroupOperation { Parent: IBlockOperation block } statement) return null;
        int at = block.Operations.IndexOf(statement);
        if (at + 1 >= block.Operations.Length || block.Operations[at + 1] is not IExpressionStatementOperation { Operation: IInvocationOperation set } next
            || Symbols.Receiver(set) is not ILocalReferenceOperation target || !SymbolEqualityComparer.Default.Equals(target.Local, declarator.Symbol)) return null;

        var method = set.TargetMethod;
        bool generated = Symbols.IsGenerated(method) && Symbols.IsGeneratedWriter(method) && method.Name.StartsWith("Set") && !method.Name.EndsWith("IfChanged");
        if (!generated && !(core.Is(method.ContainingType, core.Entity) && method is { Name: "Set", TypeArguments.Length: 1 } && set.Arguments.Length == 1)) return null;
        var component = generated ? Named(method, core) : method.TypeArguments[0];
        var given = create.TargetMethod.TypeArguments;
        if (component is not INamedTypeSymbol || given.Length >= Symbols.MaxComponents || given.Contains(component, SymbolEqualityComparer.Default)
            || Symbols.IsGrouped(component) || Symbols.IsWorldTag(component)
            || create.TargetMethod.Name == "Instantiate" && LeavesRoomFor(create.Arguments[0].Value, component)
            || !Invariant(set.Arguments.Where(a => !a.IsImplicit).Select(a => a.Value), next, declarator.Symbol)) return null;

        var model = create.SemanticModel!;
        int position = create.Syntax.SpanStart;
        if (SetValue(set, component, generated, model, position) is not { } value) return null;
        string name = created.Name is GenericNameSyntax typed ? typed.Identifier.ValueText : created.Name.ToString(); // CreateEntity<P>(…): inferred again
        string text = $"{created.Expression}.{name}({string.Join(", ", creation.ArgumentList.Arguments.Select(a => a.ToString()).Append(value))})";
        return (create, set, next, component, text);
    }

    /// <summary>
    /// Whether the prefab <paramref name="prefab"/> (a field, property or local) calls <c>SetAbsent&lt;T&gt;()</c> of
    /// <paramref name="component"/> where it is declared or on itself: its table has the column, so a Set after the
    /// Instantiate flips a presence bit and moves no row.
    /// </summary>
    private static bool LeavesRoomFor(IOperation prefab, ITypeSymbol component)
    {
        var symbol = Symbols.Unwrap(prefab) switch
        {
            IFieldReferenceOperation f => (ISymbol)f.Field,
            IPropertyReferenceOperation p => p.Property,
            ILocalReferenceOperation l => l.Local,
            _ => null,
        };
        if (symbol == null) return false;
        var callerModel = prefab.SemanticModel!;
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax();
            var tree = declaration.SyntaxTree;
            if (!callerModel.Compilation.ContainsSyntaxTree(tree)) continue;
            var model = Symbols.ModelOf(tree, callerModel); // the caller's own when the prefab is declared in its file
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "SetAbsent", TypeArgumentList.Arguments.Count: 1 } name } access
                    || !SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(name.TypeArgumentList.Arguments[0]).Type, component)) continue;
                if (call.Ancestors().Contains(declaration)) return true;          // in the declaration's initializer
                ExpressionSyntax root = access.Expression;                        // prefab.SetX(…).SetAbsent<T>(): its first receiver
                while (root is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner }) root = inner.Expression;
                if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(root).Symbol, symbol)) return true;
            }
        }
        return false;
    }
}
