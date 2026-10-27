using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Reports misuse of EasyEcs at compile time. Diagnostics live here rather than in the generators (the recommended
/// split: generators stay pure and cacheable, analyzers run on the symbols they need). The rules are reported by one
/// analyzer per area file (<see cref="All"/>); this class holds their descriptors and their code.
/// </summary>
public static partial class EasyEcsAnalyzer
{
    /// <summary>A new instance of every analyzer of EasyEcs, one per area (what a host loading the package runs).</summary>
    public static ImmutableArray<DiagnosticAnalyzer> All => ImmutableArray.Create<DiagnosticAnalyzer>(
        new DeclarationAnalyzer(), new SystemAnalyzer(), new PerformanceAnalyzer(), new UsageAnalyzer(), new ParallelAnalyzer(),
        new PartAnalyzer(), new FormAnalyzer(), new LambdaPassAnalyzer(), new ViewAnalyzer(), new RecordAnalyzer(), new FusionAnalyzer(), new PairAnalyzer());

    /// <summary>The analyzer of one area: the settings every area shares, then its own registrations.</summary>
    public abstract class Area : DiagnosticAnalyzer
    {
        public sealed override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            Register(context);
        }

        protected abstract void Register(AnalysisContext context);
    }

    private const string Category = "EasyEcs";

    public static readonly DiagnosticDescriptor BadParameter = new("EECS002", "Invalid [ForEach] parameter",
        "Parameter '{0}' of {1} must be a 'ref'/'in' IComponent or ISingletonComponent struct, a Span/ReadOnlySpan of an IComponent struct, a Maybe, Mut or Lookup of one, a 'ref Reduce' of a singleton, a Scratch, an Emit of a struct, an Entity or the World (outside a system, also a value the caller passes, of any other type)",
        Category, DiagnosticSeverity.Error, true, helpLinkUri: Help("EECS002"));

    public static readonly DiagnosticDescriptor ComponentCount = new("EECS003", "Invalid component count",
        "{0} must take between 1 and 9 components, or none with a [With<T>] or [Changed<T>] filter", Category, DiagnosticSeverity.Error, true, helpLinkUri: Help("EECS003"));

    public static readonly DiagnosticDescriptor Generic = new("EECS004", "Generic [ForEach] method in a system",
        "[ForEach] method '{0}' of a system is generic, and nothing gives it type arguments: make the system generic instead ([EcsSystem] ref struct Regen<T>)",
        Category, DiagnosticSeverity.Error, true,
        "A system runs its [ForEach] methods itself, once per update, so a generic method would have no type arguments. " +
        "Generic systems are fine (each closed type is a system of its own), and so are generic [ForEach] methods outside " +
        "systems, whose entry points take the type arguments.", helpLinkUri: Help("EECS004"));

    public static readonly DiagnosticDescriptor NotASystem = new("EECS005", "Instance [ForEach] method outside a system",
        "[ForEach] method '{0}' is an instance method of '{1}', which is no system: make it static (the generator writes an entry point that takes the world) or move it into an [EcsSystem]",
        Category, DiagnosticSeverity.Error, true, helpLinkUri: Help("EECS005"));

    public static readonly DiagnosticDescriptor NotStruct = new("EECS006", "ECS data must be a struct",
        "'{0}' implements {1} and must be a struct: components and singletons are stored by value",
        Category, DiagnosticSeverity.Error, true, helpLinkUri: Help("EECS006"));

    public static readonly DiagnosticDescriptor MixedBatch = new("EECS009", "Batch [ForEach] mixes spans and per-entity parameters",
        "[ForEach] method '{0}' mixes batch and per-entity parameters: a batch method takes component spans, an EntitySpan and singletons ('ref'/'in'); an EntitySpan needs component spans",
        Category, DiagnosticSeverity.Error, true,
        "A batch method receives runs of rows as spans, and their entities as an EntitySpan. A per-entity parameter " +
        "(ref/in component, Entity) has no single value for a run, and the World could change rows while the spans point " +
        "at them.", helpLinkUri: Help("EECS009"));

    public static readonly DiagnosticDescriptor NotParallel = new("EECS010", "[ForEach(Parallel = true)] cannot run on several threads",
        "[ForEach] method '{0}' cannot run in parallel: {1}",
        Category, DiagnosticSeverity.Error, true,
        "A parallel [ForEach] runs chunks of rows on several threads at once. It must make no structural change, run per " +
        "entity, and write nothing that another row's run reads, such as a singleton.", helpLinkUri: Help("EECS010"));

    public static readonly DiagnosticDescriptor WriteThroughReadOnly = new("EECS011", "Write through a read-only parameter",
        "{0} changes data that '{1}' holds by reference, but takes '{1}' as read-only; take it by 'ref'",
        Category, DiagnosticSeverity.Error, true,
        "A read-only component or singleton ('in', ReadOnlySpan) is scheduled as a read and never stamped as changed. A " +
        "collection or class it holds is still writable: changing it would race with parallel systems that read it, " +
        "and [Changed<T>] systems would not see the change. Take it by 'ref' so the write is scheduled and stamped.", helpLinkUri: Help("EECS011"));

    public static readonly DiagnosticDescriptor BufferCopy = new("EECS012", "DynamicBuffer<T> held by value",
        "'{0}' copies a DynamicBuffer<{1}>; reach the buffer through its entity ('ref' or 'in')",
        Category, DiagnosticSeverity.Error, true,
        "A DynamicBuffer<T> is a component: its writes are tracked through the 'ref' that reaches it. A copy refers to " +
        "the same list, so writes through the copy are never stamped as changed, and a snapshot or another entity " +
        "would share the list. Use 'ref' (write) or 'in' (read) parameters, ref locals and Entity.Get / Read; a new " +
        "component value is 'new DynamicBuffer<T>()' (no list until its first write).", helpLinkUri: Help("EECS012"));

    public static readonly DiagnosticDescriptor HiddenStructuralChange = new("EECS013", "Structural change in a [ForEach] method that promises none",
        "{0} calls '{1}', a structural change, but {2}",
        Category, DiagnosticSeverity.Error, true,
        "A [ForEach] method that is static and takes no Entity or World (or uses its Entity only to read) cannot change " +
        "structure, so the generator walks its rows without re-checking storage, and the system shares a parallel stage " +
        "with others. A change made through a handle a component holds (a relation) or a world kept elsewhere breaks " +
        "both: refs into moved rows, and a change while other systems run (which throws). Taking the Entity as a " +
        "parameter makes the system run alone with a pass that notices the change. A batch method holds spans into the " +
        "tables, so it changes no structure at all (the runtime throws).", helpLinkUri: Help("EECS013"));

    public static readonly DiagnosticDescriptor OffThreadStructuralChange = new("EECS014", "Structural change on another thread",
        "'{0}' is a structural change, made in a delegate that '{1}' runs on another thread; structural changes belong to the world's owner thread",
        Category, DiagnosticSeverity.Error, true,
        "A world's structure (create/destroy, Set/Remove, singletons, systems, Capture/Restore, Update) is changed by " +
        "one owner thread. A change from another thread races with the owner's and can tear the tables. Hand the " +
        "change to the owner as data (a queue the owner drains in a system), or give that thread its own world.", helpLinkUri: Help("EECS014"));

    public static readonly DiagnosticDescriptor IndexedByRef = new("EECS015", "Indexed component written through a ref",
        "'{0}' is indexed: {1}; write it with Set (or the generated Set{0}) so its index follows",
        Category, DiagnosticSeverity.Error, true,
        "The index of an IIndexed<TKey> component follows the values written with Set and CreateEntity as they are " +
        "written; a write through a ref (Get, GetOrAdd, TryGetRef, a 'ref' or Span [ForEach] parameter, a " +
        "query item, a job's 'ref' parameter, a Maybe) would leave it stale. Read it with Read, 'in', ReadOnlySpan or " +
        "ReadOnlyMaybe; write it " +
        "with Set or the generated SetX.", helpLinkUri: Help("EECS015"));

    public static readonly DiagnosticDescriptor RemovedRequired = new("EECS016", "A [Removed<T>] method requires T",
        "[ForEach] method '{0}' visits entities that lost '{1}' but {2}; no row passes both",
        Category, DiagnosticSeverity.Error, true,
        "[Removed<T>] visits entities that no longer have T. Taking T as a parameter, or requiring it with [With<T>] or " +
        "[Changed<T>], asks for entities that have it: the method would never run. Read the entity's other components.", helpLinkUri: Help("EECS016"));

    public static readonly DiagnosticDescriptor EntityInData = new("EECS017", "A component stores an Entity handle",
        "'{0}' stores an Entity in '{1}': store its EntityId instead",
        Category, DiagnosticSeverity.Warning, true,
        "An Entity is a handle: it holds a reference to its world. A component (or singleton) holding one is managed, " +
        "so its column is scanned by the GC, every store pays a write barrier, rows are not copied as blocks, and it " +
        "cannot be serialized. Store the EntityId (8 bytes, unmanaged; a handle converts to it implicitly) and turn it " +
        "back into a handle with world.TryGetEntity(id, out var entity).", helpLinkUri: Help("EECS017"));

    public static readonly DiagnosticDescriptor OrderCycle = new("EECS019", "Systems are ordered in a cycle",
        "[UpdateBefore] / [UpdateAfter] order '{0}' in a cycle: {1}",
        Category, DiagnosticSeverity.Error, true,
        "Each ordering says which of two systems runs first; a cycle cannot hold, and AddSystem throws when the last " +
        "system of the cycle is added. Remove one of the orderings.", helpLinkUri: Help("EECS019"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor OrderAcrossGroups = new("EECS020", "A system is ordered against another group",
        "'{0}' is ordered against '{1}', which runs in another group: the ordering never applies",
        Category, DiagnosticSeverity.Warning, true,
        "Orderings apply within one group: World.Update() and World.Update<TGroup>() run their groups separately, in the " +
        "order the caller calls them. Order the systems of one group, or move the system into the other's group.", helpLinkUri: Help("EECS020"));

    public static readonly DiagnosticDescriptor EntryPointCollision = new("EECS021", "A generated entry point has a method's signature",
        "The entry point of {2} (the {1}the caller's values) would have the signature of a method '{0}' of its type, or of another generated entry point: rename one of them",
        Category, DiagnosticSeverity.Error, true,
        "Outside a system, the generator writes an overload of a [ForEach] method that takes the World (and the change tick, for [Changed<T>] / [Removed<T>]) followed by the method's context parameters, and runs the method over every matching entity; for a [ForEntity] method, one that takes the entity and runs the method on it.", helpLinkUri: Help("EECS021"));

    public static readonly DiagnosticDescriptor Unnameable = new("EECS022", "Type out of reach of the generated code",
        "{0} uses '{1}', which code outside its declaring type cannot name: make it internal or public",
        Category, DiagnosticSeverity.Error, true,
        "The generator writes the jobs of [ForEach] methods outside the user's type (no partial needed) and calls the method through UnsafeAccessor, so private methods and systems are fine; the types of its parameters, and the type of a [ForEach] method outside a system, must be internal or public.", helpLinkUri: Help("EECS022"));

    public static readonly DiagnosticDescriptor FilterOutsideGroup = new("EECS023", "Filter on a struct that is no system group",
        "'{0}' carries [{1}], which filters only on [ForEach] methods and on system groups (ISystemGroup structs)",
        Category, DiagnosticSeverity.Error, true,
        "[With<T>], [Without<T>] and [WithAny<T1, T2>] on an ISystemGroup struct join every [ForEach] method of the group's " +
        "systems. On any other struct (a component) they do nothing.", helpLinkUri: Help("EECS023"));

    public static readonly DiagnosticDescriptor LocalForEach = new("EECS024", "[ForEach] on a local function",
        "Local function '{0}' carries [ForEach], which the generator cannot reach: make it a method of a system or a static method of a type",
        Category, DiagnosticSeverity.Error, true,
        "The generator writes a job for each [ForEach] method and calls it from outside its type; a local function has no name outside its method.", helpLinkUri: Help("EECS024"));

    public static readonly DiagnosticDescriptor BadSlices = new("EECS035", "Invalid [ForEach(Slices = N)]",
        "[ForEach] method '{0}' cannot be sliced: {1}",
        Category, DiagnosticSeverity.Error, true,
        "A sliced method visits one slice of its rows per run of its system, so every row once per N runs. Slices is 1 or " +
        "more. A [Changed<T>] or [Removed<T>] method selects the rows changed since its system last ran, and each run moves " +
        "that tick: the rows of the slices not visited would lose their changes. Drop Slices, or read the changes " +
        "every run and spread the work they cause.", helpLinkUri: Help("EECS035"));

    public static readonly DiagnosticDescriptor BadChunkSize = new("EECS054", "Invalid [ForEach(ChunkSize = N)]",
        "[ForEach] method '{0}' cannot set ChunkSize: {1}",
        Category, DiagnosticSeverity.Error, true,
        "ChunkSize is the rows a thread of a parallel pass takes at a time: 0 lets the runtime choose from the rows' measured cost, " +
        "more sets it (rounded up to 64). A method without Parallel = true runs on one thread and has no chunks: set " +
        "Parallel = true or drop ChunkSize.", helpLinkUri: Help("EECS054"));

    public static readonly DiagnosticDescriptor ReductionOverwritten = new("EECS057", "A reduction whose result another write replaces",
        "Parameter '{0}' of [ForEach] method '{1}' reduces into {2}, but {3}",
        Category, DiagnosticSeverity.Error, true,
        "A Reduce<T> parameter gives each chunk of rows a partial result of singleton T and replaces T with the combined " +
        "result when the pass ends. A second Reduce<T> of the same method or system, or a 'ref T' parameter of the same " +
        "method, writes T in the same pass: one result would silently replace the other. Reduce each result into a " +
        "singleton of its own, and read the last result with 'in T'.", helpLinkUri: Help("EECS057"));
    public static readonly DiagnosticDescriptor NoVectorLoop = new("EECS056", "A required vector loop that the method does not qualify for",
        "[ForEach(SimdMode = SimdMode.Require)] method '{0}' gets no vector loop: {1}",
        Category, DiagnosticSeverity.Error, true,
        "SimdMode.Require asks for the method's vector loop. A method qualifies when it is static, " +
        "takes no Entity and makes no structural change, takes components of up to 4 lanes of one size (byte and sbyte; " +
        "short and ushort; float, int and uint; double, long and ulong; Vector2/3/4 fields give their lanes; on AVX-512 " +
        "also 4-byte lanes of a row of up to 16 words, where the permutes that gather them pay), Maybe<T> of them, " +
        "singletons, values and Reduce<T> results (sums, Min and Max that T.Combine takes field by field; a float sum only " +
        "with FloatMode.Fast), and computes with arithmetic, comparisons, branches, conversions and Math functions that " +
        "have an exact vector counterpart (Sin, Cos, Exp and Log only with FloatMode.Fast). " +
        "Change what the message names, or set SimdMode.Auto to accept the loop of one row at a time.", helpLinkUri: Help("EECS056"));

    public static readonly DiagnosticDescriptor ScratchEscape = new("EECS058", "Scratch memory stored into the world",
        "[ForEach] method '{0}' stores '{1}', which its Scratch<{2}> '{3}' holds, into '{4}'",
        Category, DiagnosticSeverity.Error, true,
        "A Scratch<T> value belongs to the thread running the method and is reused by the next row that thread runs, " +
        "of any pass. A reference into it (a list, an array, a span) stored in a component or a singleton is changed " +
        "under the entity by later rows, on another thread in a parallel pass. Copy what the row produced into the " +
        "component's own storage (a DynamicBuffer, a value), or keep the data in a component.", helpLinkUri: Help("EECS058"));

    public static readonly DiagnosticDescriptor LookupOfWritten = new("EECS059", "A lookup of a component the pass writes",
        "Parameter '{0}' of {1} looks up {2}, which {3}",
        Category, DiagnosticSeverity.Error, true,
        "A Lookup<T> reads T of any entity, as it was before the pass: nothing may write T while the pass runs. The " +
        "method writes T itself, so a lookup would read rows before or after their write in this pass, and a parallel " +
        "pass would read them while another thread writes them. Read the entity's own T through its parameter, write " +
        "the result into another component (double buffering: read Position, write NextPosition), or split the work " +
        "into two systems.", helpLinkUri: Help("EECS059"));

    public static readonly DiagnosticDescriptor BadEmit = new("EECS070", "Records read or reordered by the pass that emits them",
        "Parameter '{0}' of {1} emits {2}, but {3}",
        Category, DiagnosticSeverity.Error, true,
        "An Emit<T> parameter gives each chunk of rows a list of records and appends the lists to singleton Emitted<T> " +
        "in chunk order when the pass ends, so the records come out in row order whatever the threads. The same method " +
        "taking Emitted<T> would read the records while they are added: none of the pass's in parallel, some on one " +
        "thread. A second Emit<T> of the same method would order its records differently on one thread (interleaved row " +
        "by row) and in parallel (each chunk's lists one after the other). Read the records in a system after the pass, " +
        "and emit each kind of record through one parameter.", helpLinkUri: Help("EECS070"));

    public static readonly DiagnosticDescriptor BadTileShape = new("EECS072", "An invalid tile shape",
        "Tile shape '{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "A tile (ITileShape) is Width x Depth x Height cells, Depth 1 to 64 (a layer's active rows are one 64-bit " +
        "word), with a halo of 1 or more cells. A halo reads the cells of the tile beside, so it is at most the tile's " +
        "extent along every axis with neighbors (its Faces), and along every axis when the boundary mirrors: like an " +
        "MPI ghost width, never wider than the subdomain. Tile.Pad throws for such a shape; widen the tile or narrow " +
        "the halo.", helpLinkUri: Help("EECS072"));

    public static readonly DiagnosticDescriptor SharedTile = new("EECS073", "Two tiles of the same cells and grid",
        "[ForEach] method '{0}' takes '{1}' and '{2}', both Tile<{3}>: they load into one texture and read one buffer",
        Category, DiagnosticSeverity.Error, true,
        "A Tile<TCell, TTile> loads into the running thread's texture for TCell and TTile, and both parameters read the " +
        "same DynamicBuffer<TCell> of the same entity: the second load replaces the first while it is still sampled. " +
        "Take one tile per field: one cell type (one struct, one buffer) per field the stencil reads, each its own " +
        "Tile<TCell, TTile>.", helpLinkUri: Help("EECS073"));

    public static readonly DiagnosticDescriptor BadIndexedKey = new("EECS036", "A component that cannot be indexed by its key",
        "'{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "A world indexes a component once, by one key: World.Index throws for a second key (another IIndexed<TKey> or " +
        "IUniqueIndexed<TKey>), so that key can never be indexed. A unique component is indexed by its unique key from " +
        "its first table, and every write is checked against that index. An EntityId key cannot be unique: loads and " +
        "moves rewrite such keys after they copy the rows, too late to check them first; index the relation with " +
        "IIndexed<EntityId> (or [LookupKey]). A key declared with [LookupKey] / [UniqueKey] is IEquatable of itself, the only key of " +
        "its component, of the type of any index interface the struct declares, and not written by hand as well; a " +
        "[Group] member is an int or an enum of int, the only one.", helpLinkUri: Help("EECS036"));

    // EECS002's property for its code fix (EasyEcs.CodeFixes): a component or singleton taken by value.
    public const string ByValue = "ByValue";

    // EECS012's property for its code fix: the parameter's method shares its signature (override, interface).
    public const string SharedSignature = "SharedSignature";


    internal static void InitializeDeclarations(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        context.RegisterSyntaxNodeAction(AnalyzeTileShape, SyntaxKind.StructDeclaration, SyntaxKind.RecordStructDeclaration);
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(AnalyzeParallel, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
        context.RegisterSyntaxNodeAction(AnalyzeBufferOfEntities, SyntaxKind.GenericName);
        context.RegisterSymbolAction(AnalyzeBufferHolder, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeBufferCopy, OperationKind.VariableDeclarator, OperationKind.SimpleAssignment,
            OperationKind.Argument);
        context.RegisterOperationAction(AnalyzeWrite, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment,
            OperationKind.CoalesceAssignment, OperationKind.Increment, OperationKind.Decrement, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeStructuralCall, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeIndexedRef, OperationKind.Invocation, OperationKind.VariableDeclarator);
        context.RegisterCompilationStartAction(AnalyzeOrderings);
    }

    /// <summary>
    /// EECS020 for each ordering that names a system of another group, and EECS019 at the end of the compilation for
    /// the orderings of its systems that form a cycle (edges from the system that runs first).
    /// </summary>
    private static void AnalyzeOrderings(CompilationStartAnalysisContext start)
    {
        var edges = new System.Collections.Concurrent.ConcurrentBag<(INamedTypeSymbol First, INamedTypeSymbol Second, Location Location)>();
        start.RegisterSymbolAction(context =>
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (!Symbols.IsValidSystem(type)) return;
            foreach (var attribute in type.GetAttributes())
            {
                bool before = Symbols.Is(attribute.AttributeClass, Symbols.UpdateBeforeAttribute);
                if (!before && !Symbols.Is(attribute.AttributeClass, Symbols.UpdateAfterAttribute)) continue;
                // Generic systems by definition: G2<int> and G2<T> are one node of the order.
                if (attribute.AttributeClass!.TypeArguments[0] is not INamedTypeSymbol { TypeKind: not TypeKind.Error } argument) continue;
                var other = argument.OriginalDefinition;
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0];
                if (!SymbolEqualityComparer.Default.Equals(Symbols.GroupOf(type), Symbols.GroupOf(other)))
                    context.ReportDiagnostic(Diagnostic.Create(OrderAcrossGroups, location, type.Name, other.Name));
                edges.Add(before ? (type.OriginalDefinition, other, location) : (other, type.OriginalDefinition, location));
            }
        }, SymbolKind.NamedType);

        start.RegisterCompilationEndAction(context =>
        {
            var next = new System.Collections.Generic.Dictionary<INamedTypeSymbol, System.Collections.Generic.List<(INamedTypeSymbol, Location)>>(SymbolEqualityComparer.Default);
            foreach (var (first, second, location) in edges)
            {
                if (!next.TryGetValue(first, out var list)) next[first] = list = [];
                list.Add((second, location));
            }

            // Depth-first: a system met again on the current path closes a cycle; each cycle is reported on its edges.
            var state = new System.Collections.Generic.Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default); // 1 on path, 2 done
            var path = new System.Collections.Generic.List<(INamedTypeSymbol Type, Location Location)>();
            void Visit(INamedTypeSymbol type)
            {
                state[type] = 1;
                foreach (var (other, location) in next.TryGetValue(type, out var list) ? list : [])
                {
                    path.Add((type, location));
                    if (state.TryGetValue(other, out int seen) && seen == 1)
                    {
                        int from = path.FindIndex(step => SymbolEqualityComparer.Default.Equals(step.Type, other));
                        var names = string.Join(" -> ", path.Skip(from).Select(step => step.Type.Name).Concat([other.Name]));
                        foreach (var step in path.Skip(from))
                            context.ReportDiagnostic(Diagnostic.Create(OrderCycle, step.Location, step.Type.Name, names));
                    }
                    else if (!state.ContainsKey(other)) Visit(other);
                    path.RemoveAt(path.Count - 1);
                }

                state[type] = 2;
            }

            foreach (var type in next.Keys.ToArray())
                if (!state.ContainsKey(type)) Visit(type);
        });
    }

    /// <summary>
    /// EECS072: a tile shape whose static members are constants (expression-bodied, or left to their defaults) and
    /// that Tile.Pad would reject: the checks of TileShapeCheck&lt;TTile&gt;.Validate, read at build time.
    /// </summary>
    private static void AnalyzeTileShape(SyntaxNodeAnalysisContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)context.Node, context.CancellationToken) is not { } type) return;
        var shape = type.AllInterfaces.FirstOrDefault(i => Symbols.Named(i, Symbols.TileShape));
        if (shape == null) return;
        int? Read(string name, int fallback)
        {
            if (shape.GetMembers(name).FirstOrDefault() is not IPropertySymbol member) return null;
            if (type.FindImplementationForInterfaceMember(member) is not IPropertySymbol property
                || SymbolEqualityComparer.Default.Equals(property.ContainingType, shape)) return fallback;
            if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not PropertyDeclarationSyntax declaration
                || declaration.SyntaxTree != context.Node.SyntaxTree || Symbols.Returned(declaration) is not { } body) return null;
            return context.SemanticModel.GetConstantValue(body, context.CancellationToken) is { HasValue: true, Value: { } value }
                ? System.Convert.ToInt32(value) : null;
        }
        // The defaults of ITileShape: Height 1, Halo 1, Faces Sides (15), Boundary Fill (0); Mirror is 2.
        if (Read("Width", 0) is not { } w || Read("Depth", 0) is not { } d || Read("Height", 1) is not { } h || Read("Halo", 1) is not { } r
            || Read("Faces", 15) is not { } faces || Read("Boundary", 0) is not { } boundary) return;
        bool mirror = boundary == 2;
        string? problem = w < 1 || d is < 1 or > 64 || h < 1 ? $"is {w} x {d} x {h} cells: Width and Height must be 1 or more, Depth 1 to 64"
            : r < 1 ? $"has a halo of {r}: 1 or more"
            : r > w && (mirror || (faces & 12) != 0) ? $"has a halo of {r}, wider than its rows of {w} cells"
            : r > d && (mirror || (faces & 3) != 0) ? $"has a halo of {r}, deeper than its {d} rows"
            : h > 1 && r > h && (mirror || (faces & 48) != 0) ? $"has a halo of {r}, taller than its {h} layers"
            : null;
        if (problem != null)
            context.ReportDiagnostic(Diagnostic.Create(BadTileShape, ((TypeDeclarationSyntax)context.Node).Identifier.GetLocation(), type.Name, problem));
    }

    /// <summary>
    /// EECS015: a ref to an indexed component handed out by Entity (Get, GetOrAdd, TryGetRef), or a ref
    /// local bound to a query item's or a job parameter's indexed component. Writes through those (and through
    /// [ForEach] parameters, see <see cref="AnalyzeMethod"/>) are reported by <see cref="AnalyzeWrite"/>.
    /// </summary>
    private static void AnalyzeIndexedRef(OperationAnalysisContext context)
    {
        switch (context.Operation)
        {
            // A copy changed and dropped is EECS038's (its advice, Set, is the one that keeps the index).
            case IInvocationOperation { TargetMethod: { ReturnsByRef: true, TypeArguments.Length: 1 } method } call
                when Symbols.Named(method.ContainingType, Symbols.Entity) && Symbols.IsIndexed(method.TypeArguments[0]) && !IsDroppedCopy(call):
                // Get<T>() that only reads (or binds a 'ref readonly' local): Read<T>() is the same read.
                bool reads = method.Name == "Get" && (!Uses.MayWrite(call)
                    || call.Parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: RefKind.RefReadOnly } });
                var fix = reads && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax name } }
                    ? Fix(call.Syntax.ReplaceToken(name.Identifier, SyntaxFactory.Identifier("Read")).ToString(), "Read it with Read<T>()") : null;
                context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, call.Syntax.GetLocation(), fix, method.TypeArguments[0].Name,
                    $"Entity.{method.Name} hands out a ref (read it with Read)"));
                break;
            case IVariableDeclaratorOperation { Symbol: { RefKind: RefKind.Ref } local, Initializer.Value: var value } declarator
                when Symbols.IndexedRoot(value) is { } root:
                context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, context.Operation.Syntax.GetLocation(),
                    LocalOnlyRead(local, declarator) ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "RefReadOnly") : null, root.Name,
                    "a ref local can write it (use 'ref readonly')"));
                break;
        }
    }

    /// <summary>Whether every use of ref local <paramref name="local"/> only reads it: 'ref readonly' then binds it the same way.</summary>
    private static bool LocalOnlyRead(ILocalSymbol local, IOperation declaration)
    {
        IOperation root = declaration;
        while (root.Parent != null) root = root.Parent;
        foreach (var operation in root.Descendants())
            if (operation is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, local) && Uses.MayWrite(reference))
                return false;
        return true;
    }

    /// <summary>
    /// EECS013: a structural call in a [ForEach] method the generator treats as stable. EECS014: a structural call in
    /// a lambda or anonymous method handed to a thread-starting call. Both see the call itself only: a change made in
    /// a helper method the delegate or [ForEach] method calls is not followed (the runtime still throws for one made
    /// while the world runs work on several threads).
    /// </summary>
    private static void AnalyzeStructuralCall(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (!Symbols.IsStructural(call.TargetMethod)) return;
        string name = call.TargetMethod.ContainingType.Name + "." + call.TargetMethod.Name;

        bool inFunction = false;
        for (var node = call.Parent; node != null; node = node.Parent)
        {
            if (node is ILocalFunctionOperation) inFunction = true;
            if (node is not IAnonymousFunctionOperation function) continue;
            inFunction = true;
            var start = node.Parent is IDelegateCreationOperation creation ? creation.Parent : node.Parent;
            IOperation handoff;
            IMethodSymbol starter;
            string by;
            if (start is IEventAssignmentOperation { Adds: true, EventReference: IEventReferenceOperation { Event: { Name: "Elapsed", AddMethod: { } add } timer } } subscription
                && Symbols.Named(timer.ContainingType, "System.Timers.Timer"))
                (handoff, starter, by) = (subscription, add, "Timer.Elapsed"); // raised on the thread pool
            else if (start is IArgumentOperation { Parent: { } argumentOf } && Starter(argumentOf) is { } started && Symbols.StartsThread(started))
                (handoff, starter, by) = (argumentOf, started, Started(started));
            else continue;
            if (!Threads.Concurrent(call, function.Syntax, handoff, starter, context.CancellationToken)) return;
            context.ReportDiagnostic(Diagnostic.Create(OffThreadStructuralChange, call.Syntax.GetLocation(), name, by));
            return;
        }

        // A method handed off as a method group (Task.Run(Work), ThreadPool.QueueUserWorkItem(Work)) in its own type, in
        // this file: one level, the method's own calls.
        if (!inFunction && context.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } worker
            && worker.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is MethodDeclarationSyntax declaration
            && declaration.SyntaxTree == call.Syntax.SyntaxTree && declaration.Parent is TypeDeclarationSyntax owner
            && ArgumentNames(owner).Contains(worker.Name)) // one walk of the type per analysis, not one per structural call
            foreach (var group in owner.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (ArgumentName(group.Expression) != worker.Name
                    || call.SemanticModel!.GetOperation(group, context.CancellationToken) is not IArgumentOperation
                    {
                        Value: IDelegateCreationOperation { Target: IMethodReferenceOperation reference }, Parent: { } handoff,
                    }
                    || !SymbolEqualityComparer.Default.Equals(reference.Method.OriginalDefinition, worker.OriginalDefinition)
                    || Starter(handoff) is not { } starter || !Symbols.StartsThread(starter)
                    || !Threads.Concurrent(call, declaration, handoff, starter, context.CancellationToken)) continue;
                context.ReportDiagnostic(Diagnostic.Create(OffThreadStructuralChange, call.Syntax.GetLocation(), name, Started(starter)));
                return;
            }

        if (context.ContainingSymbol is not IMethodSymbol method || !Symbols.IsGeneratedJob(method) || OwnWorld(call)) return;
        if (Symbols.IsBatch(method))
            context.ReportDiagnostic(Diagnostic.Create(HiddenStructuralChange, call.Syntax.GetLocation(), Symbols.Describe(method), name,
                "it is a batch method, whose spans point into the tables; record the change as data for a method that runs per entity"));
        else if (BodyFacts.IsStable(method, context.Operation.SemanticModel!))
            context.ReportDiagnostic(Diagnostic.Create(HiddenStructuralChange, call.Syntax.GetLocation(), Symbols.Describe(method), name,
                "takes no Entity or World to make it with; take the Entity as a parameter, or record the change as data"));
    }

    /// <summary>
    /// The names the arguments of <paramref name="type"/>'s code pass as plain or member-access names (the method groups
    /// it may hand to another thread), computed once per declaration: EECS014's method-group check asks it for every
    /// structural call of the type, and a walk of the type per call made it the costliest rule of large files.
    /// </summary>
    private static System.Collections.Generic.HashSet<string> ArgumentNames(TypeDeclarationSyntax type) =>
        ArgumentNamesOf.GetValue(type, static type =>
        {
            var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (var node in type.DescendantNodes())
                if (node is ArgumentSyntax argument && ArgumentName(argument.Expression) is { Length: > 0 } name) names.Add(name);
            return names;
        });

    // Keyed by the declaration (immutable syntax): an unchanged file keeps its entry across compilations, an edited one
    // gets new nodes, and the table holds no node alive.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TypeDeclarationSyntax, System.Collections.Generic.HashSet<string>> ArgumentNamesOf = new();

    private static string? ArgumentName(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax id ? id.Identifier.ValueText : expression is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : null;

    /// <summary>EECS011: a write or a mutating call that reaches a read-only [ForEach] parameter's reference.</summary>
    private static void AnalyzeWrite(OperationAnalysisContext context)
    {
        var target = context.Operation switch
        {
            IAssignmentOperation assignment => assignment.Target,
            IIncrementOrDecrementOperation step => step.Target,
            IInvocationOperation call => Symbols.MutatedReceiver(call),
            _ => null,
        };
        if (Symbols.ReadOnlyRoot(target) is { } parameter)
            context.ReportDiagnostic(Diagnostic.Create(WriteThroughReadOnly, context.Operation.Syntax.GetLocation(),
                // the parameter: its code fix, unless 'ref' would write a singleton from several threads (EECS010)
                Symbols.IsParallel((IMethodSymbol)parameter.ContainingSymbol) && Symbols.Classify(parameter) == Symbols.ParamKind.Singleton
                    ? [] : parameter.Locations,
                Symbols.Describe((IMethodSymbol)parameter.ContainingSymbol), parameter.Name));
        if (Symbols.IndexedRoot(target) is { } indexed)
            context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, context.Operation.Syntax.GetLocation(), indexed.Name,
                "this writes it through a ref"));
    }

    /// <summary>EECS012: a field, property, by-value parameter or by-value return of a dynamic buffer.</summary>
    private static void AnalyzeBufferHolder(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared) return;
        switch (symbol)
        {
            case IFieldSymbol field when field.RefKind == RefKind.None: // a ref field reaches the buffer itself
                Report(field.Type, field, field.Locations.FirstOrDefault());
                break;
            case IPropertySymbol property when property.RefKind == RefKind.None:
                Report(property.Type, property, property.Locations.FirstOrDefault());
                break;
            case IMethodSymbol method when method.AssociatedSymbol == null:
                if (method.RefKind == RefKind.None) Report(method.ReturnType, method, method.Locations.FirstOrDefault());
                // A [ForEach] method's buffer by value is an invalid parameter: EECS002 says it, with the same fixes.
                if (Symbols.IsGeneratedJob(method)) break;
                // an override or an interface's method shares its signature: changing one parameter breaks the others
                var properties = Symbols.SharesSignature(method) ? ImmutableDictionary<string, string?>.Empty.Add(SharedSignature, "") : null;
                foreach (var p in method.Parameters)
                    if (p.RefKind == RefKind.None) Report(p.Type, p, p.Locations.FirstOrDefault(), properties);
                break;
        }

        void Report(ITypeSymbol type, ISymbol holder, Location? location, ImmutableDictionary<string, string?>? properties = null)
        {
            if (Symbols.BufferElement(type) is { } element)
                context.ReportDiagnostic(Diagnostic.Create(BufferCopy, location, properties, holder.Name, element.ToDisplayString()));
        }
    }

    /// <summary>
    /// EECS012: a DynamicBuffer&lt;T&gt; copied by a local (ref locals are fine), an assignment or an argument (not 'ref' or
    /// 'out' or 'ref readonly') of a generic method (Set, CreateEntity: the value is stored), unless it is a new, empty buffer.
    /// </summary>
    private static void AnalyzeBufferCopy(OperationAnalysisContext context)
    {
        var (name, type, value) = context.Operation switch
        {
            IVariableDeclaratorOperation { Symbol.RefKind: RefKind.None } declarator =>
                (declarator.Symbol.Name, declarator.Symbol.Type, declarator.Initializer?.Value),
            ISimpleAssignmentOperation { IsRef: false } assignment => (Describe(assignment.Target), assignment.Target.Type, assignment.Value),
            // 'in' still stores the value (Set); 'ref', 'out' and 'ref readonly' (Unsafe.IsNullRef) pass the buffer itself
            IArgumentOperation { Parameter: { RefKind: not (RefKind.Ref or RefKind.Out or RefKind.RefReadOnlyParameter), OriginalDefinition.Type: ITypeParameterSymbol } } argument =>
                (argument.Parameter!.Name, argument.Value.Type, argument.Value),
            _ => (null, null, null),
        };
        if (type == null || Symbols.BufferElement(type) is not { } element) return;
        if (context.Operation is not IVariableDeclaratorOperation && Symbols.IsNewBuffer(value)) return; // locals never hold one
        context.ReportDiagnostic(Diagnostic.Create(BufferCopy, context.Operation.Syntax.GetLocation(), name,
            element.ToDisplayString()));

        static string Describe(IOperation target) => target switch
        {
            ILocalReferenceOperation local => local.Local.Name,
            IParameterReferenceOperation parameter => parameter.Parameter.Name,
            IFieldReferenceOperation field => field.Field.Name,
            _ => target.Syntax.ToString(),
        };
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var location = type.Locations.FirstOrDefault();

        // Classes may not implement a marker; interfaces may extend it (e.g. a project-wide IStateComponent).
        if (type.TypeKind == TypeKind.Class)
            foreach (var name in new[] { Symbols.Component, Symbols.Singleton })
                if (Symbols.Implements(type, name))
                    context.ReportDiagnostic(Diagnostic.Create(NotStruct, location, type.Name, name.Substring("EasyEcs.".Length)));

        if (type.TypeKind == TypeKind.Struct && (Symbols.Implements(type, Symbols.Component) || Symbols.Implements(type, Symbols.Singleton)))
            AnalyzeDataFields(context, type);
        if (type.TypeKind == TypeKind.Struct) AnalyzeUniqueKey(context, type, location);

        if (type.TypeKind == TypeKind.Struct && !Symbols.Implements(type, "EasyEcs.ISystemGroup"))
            foreach (var attribute in type.GetAttributes())
                if (attribute.AttributeClass is { IsGenericType: true } cls
                    && (Symbols.Named(cls.ConstructedFrom, Symbols.WithAttribute) || Symbols.Named(cls.ConstructedFrom, Symbols.WithoutAttribute)
                        || Symbols.AnyFilter(cls) == Symbols.FilterKind.WithAny))
                    context.ReportDiagnostic(Diagnostic.Create(FilterOutsideGroup,
                        attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? location,
                        type.Name, cls.Name.Replace("Attribute", "") + "<" + string.Join(", ", cls.TypeArguments.Select(t => t.Name)) + ">"));
    }

    /// <summary>EECS036: a unique component with an EntityId key, or a component with a second index key.</summary>
    private static void AnalyzeUniqueKey(SymbolAnalysisContext context, INamedTypeSymbol type, Location? location)
    {
        string Name(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var unique = type.AllInterfaces.FirstOrDefault(i => Symbols.Named(i.OriginalDefinition, Symbols.UniqueIndexed));
        if (unique == null)
        {
            // A plain component with two keys: the first World.Index of it decides, the other throws.
            var keys = type.AllInterfaces.Where(i => Symbols.Named(i.OriginalDefinition, Symbols.Indexed))
                .Select(i => i.TypeArguments[0]).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToArray();
            if (keys.Length > 1)
                context.ReportDiagnostic(Diagnostic.Create(BadIndexedKey, location, type.Name,
                    $"is indexed by {string.Join(" and ", keys.Select(k => $"IIndexed<{Name(k)}>"))}, but a component has one index: World.Index throws for every key but the first it is asked for"));
            return;
        }

        var key = unique.TypeArguments[0];
        string keyName = Name(key);
        if (Symbols.Named(key, Symbols.EntityId))
            context.ReportDiagnostic(Diagnostic.Create(BadIndexedKey, location, type.Name,
                $"is IUniqueIndexed<{keyName}>, but an EntityId key cannot be unique: index the relation with IIndexed<EntityId>"));
        foreach (var other in type.AllInterfaces)
            if ((Symbols.Named(other.OriginalDefinition, Symbols.Indexed) || Symbols.Named(other.OriginalDefinition, Symbols.UniqueIndexed))
                && !SymbolEqualityComparer.Default.Equals(other.TypeArguments[0], key))
                context.ReportDiagnostic(Diagnostic.Create(BadIndexedKey, location, type.Name,
                    $"is IUniqueIndexed<{keyName}>, so its key {Name(other.TypeArguments[0])} can never be indexed: one index per component"));
    }

    /// <summary>EECS017: an instance field (or a record's positional property) of a component or singleton holding an Entity.</summary>
    private static void AnalyzeDataFields(SymbolAnalysisContext context, INamedTypeSymbol data)
    {
        foreach (var field in data.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || !HoldsEntity(field.Type, 0)) continue;
            var member = Symbols.Associated(field) ?? field;
            var location = member.Locations.FirstOrDefault(l => l.IsInSource) ?? data.Locations.FirstOrDefault();
            // A plain Entity field, declared alone: its code fix makes it an EntityId (when every use still compiles).
            bool plain = Symbols.Associated(field) == null && Symbols.Named(field.Type, Symbols.Entity)
                         && field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1 } };
            context.ReportDiagnostic(Diagnostic.Create(EntityInData, location,
                plain ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "EntityId") : null, data.Name, member.Name));
        }
    }

    /// <summary>
    /// EECS017: a <c>DynamicBuffer&lt;T&gt;</c> named in source whose element holds an Entity (a buffer is a component),
    /// unless it is the type of a component's field, which <see cref="AnalyzeDataFields"/> reports.
    /// </summary>
    private static void AnalyzeBufferOfEntities(SyntaxNodeAnalysisContext context)
    {
        var name = (GenericNameSyntax)context.Node;
        if (name.Identifier.ValueText != "DynamicBuffer" || name.TypeArgumentList.Arguments.Count != 1
            || context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol is not INamedTypeSymbol type
            || Symbols.BufferElement(type) is not { } element || !HoldsEntity(element, 0) || InDataField(context, name)) return;
        context.ReportDiagnostic(Diagnostic.Create(EntityInData, name.GetLocation(),
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), "its elements"));
    }

    /// <summary>Whether <paramref name="name"/> is in the type of a field (an auto-property, a record's parameter) of a component or singleton.</summary>
    private static bool InDataField(SyntaxNodeAnalysisContext context, SyntaxNode name)
    {
        SyntaxNode? member = name.Ancestors().FirstOrDefault(n => n is FieldDeclarationSyntax or PropertyDeclarationSyntax or ParameterSyntax);
        bool field = member switch
        {
            FieldDeclarationSyntax f => !f.Modifiers.Any(SyntaxKind.StaticKeyword) && !f.Modifiers.Any(SyntaxKind.ConstKeyword),
            PropertyDeclarationSyntax p => !p.Modifiers.Any(SyntaxKind.StaticKeyword) && p.ExpressionBody == null
                                           && p.AccessorList?.Accessors.All(a => a.Body == null && a.ExpressionBody == null) == true,
            ParameterSyntax { Parent.Parent: RecordDeclarationSyntax } => true,
            _ => false,
        };
        return field && member!.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } owner
            && context.SemanticModel.GetDeclaredSymbol(owner, context.CancellationToken) is { TypeKind: TypeKind.Struct } data
            && (Symbols.Implements(data, Symbols.Component) || Symbols.Implements(data, Symbols.Singleton));
    }

    /// <summary>Whether <paramref name="type"/> is an Entity or holds one (an array, a type argument, a struct's field).</summary>
    private static bool HoldsEntity(ITypeSymbol type, int depth)
    {
        if (depth > 4) return false;
        if (Symbols.Named(type, Symbols.Entity)) return true;
        if (type is IArrayTypeSymbol array) return HoldsEntity(array.ElementType, depth + 1);
        if (type is not INamedTypeSymbol named) return false;
        foreach (var argument in named.TypeArguments)
            if (HoldsEntity(argument, depth + 1)) return true;
        // Structs of referenced assemblies too: their visible fields are what a row holds (private ones the compiler
        // does not import are left out).
        if (named.TypeKind != TypeKind.Struct || named.SpecialType != SpecialType.None) return false;
        foreach (var field in named.GetMembers().OfType<IFieldSymbol>())
            if (!field.IsStatic && HoldsEntity(field.Type, depth + 1)) return true;
        return false;
    }

    /// <summary>
    /// EECS007's fix: a private field, declared alone, of a type a readonly field keeps the same (a class, a readonly
    /// struct, a number or enum: no mutating call on a copy), written only by its initializer and the constructors (the
    /// static ones for a static field), not in a lambda there. Syntax only: any other write of its name blocks the fix.
    /// </summary>
    private static bool AssignedOnlyAtConstruction(IFieldSymbol field, System.Threading.CancellationToken token)
    {
        if (field.DeclaredAccessibility != Accessibility.Private || field.IsReadOnly || field.IsVolatile
            || !(field.Type.IsReferenceType || field.Type.IsReadOnly || field.Type.SpecialType != SpecialType.None || field.Type.TypeKind == TypeKind.Enum)
            || field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(token) is not VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1 } })
            return false;
        foreach (var part in field.ContainingType.DeclaringSyntaxReferences)
            foreach (var name in part.GetSyntax(token).DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (name.Identifier.ValueText != field.Name) continue;
                SyntaxNode use = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
                while (use.Parent is ParenthesizedExpressionSyntax) use = use.Parent;
                bool written = use.Parent switch
                {
                    AssignmentExpressionSyntax assignment => assignment.Left == use,
                    PrefixUnaryExpressionSyntax prefix => prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression)
                                                          || prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression)
                                                          || prefix.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddressOfExpression),
                    PostfixUnaryExpressionSyntax => true,
                    ArgumentSyntax argument => argument.RefKindKeyword.ValueText is "ref" or "out",
                    RefExpressionSyntax => true,
                    TupleExpressionSyntax or ArgumentSyntax { Parent: TupleExpressionSyntax } => true, // a deconstruction: conservative
                    _ => false,
                };
                if (!written) continue;
                // A write in a constructor of the field's kind (static or instance), outside any lambda, is allowed.
                bool construction = false;
                for (var node = use.Parent; node != null; node = node.Parent)
                {
                    if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) break;
                    if (node is ConstructorDeclarationSyntax ctor)
                    {
                        construction = ctor.Modifiers.Any(m => m.ValueText == "static") == field.IsStatic;
                        break;
                    }

                    if (node is MemberDeclarationSyntax) break;
                }

                if (!construction) return false;
            }

        return true;
    }

    /// <summary>An auto-property has a compiler-generated backing field (computed properties hold no state).</summary>
    private static bool IsAutoProperty(IPropertySymbol property) => Symbols.BackingField(property) != null;

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!Symbols.IsGeneratedJob(method)) return;
        var location = method.Locations.FirstOrDefault();

        bool outside = Symbols.IsRunner(method), one = Symbols.IsForEntity(method);
        var excluded = Excluded(method, context.CancellationToken);
        string? problem = one ? Symbols.ForEntityProblem(method) : null;
        if (problem != null)
            context.ReportDiagnostic(Diagnostic.Create(BadForEntity, location,
                !method.IsStatic && problem.StartsWith("is an instance") ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Static") : null,
                method.Name, problem));
        else if (outside && !method.IsStatic && !Symbols.IsSystem(method.ContainingType)) // in a system: EECS075
            context.ReportDiagnostic(Diagnostic.Create(NotASystem, location, method.Name, method.ContainingType.Name));
        else if (outside && Symbols.EntryPointCollides(method))
            context.ReportDiagnostic(Diagnostic.Create(EntryPointCollision, location, method.Name,
                (one ? "entity, " : "World, ") + (Symbols.TracksSince(method) ? "the tick, " : Symbols.Slices(method) > 1 ? "the slice, " : ""),
                Symbols.Describe(method)));
        if (method.IsGenericMethod && !outside)
            context.ReportDiagnostic(Diagnostic.Create(Generic, location, method.Name));
        if (Symbols.SlicesProblem(method) is { } slices)
            context.ReportDiagnostic(Diagnostic.Create(BadSlices, location, method.Name, slices));
        if (Symbols.ChunkSizeProblem(method) is { } chunks)
            context.ReportDiagnostic(Diagnostic.Create(BadChunkSize, location, // dropped, the method runs as without it
                ImmutableDictionary<string, string?>.Empty.Add(FixKind, "ChunkSize"), method.Name, chunks));
        if (Symbols.Unnameable(method) is { } unnameable)
            context.ReportDiagnostic(Diagnostic.Create(Unnameable, location, // the type its code fix makes internal, with its containers
                Symbols.Hidden(unnameable)?.Locations.Where(l => l.IsInSource) ?? [],
                Symbols.Describe(method), unnameable.ToDisplayString()));

        int components = 0;
        bool valid = true;
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            if (kind == Symbols.ParamKind.Invalid && p.Type.TypeKind == TypeKind.Error) valid = false; // the compiler reports it
            else if (kind == Symbols.ParamKind.Invalid)
            {
                valid = false;
                context.ReportDiagnostic(Diagnostic.Create(BadParameter, p.Locations.FirstOrDefault(),
                    Symbols.ByValueData(p) ? ImmutableDictionary<string, string?>.Empty.Add(ByValue, "") : null, p.Name, Symbols.Describe(method)));
            }
            else if (kind == Symbols.ParamKind.Tile && method.Parameters.FirstOrDefault(other => other.Ordinal < p.Ordinal
                         && SymbolEqualityComparer.Default.Equals(other.Type, p.Type)) is { } first)
                context.ReportDiagnostic(Diagnostic.Create(SharedTile, p.Locations.FirstOrDefault(), method.Name, first.Name, p.Name,
                    string.Join(", ", ((INamedTypeSymbol)p.Type).TypeArguments.Select(t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))));
            else if (kind is Symbols.ParamKind.Lookup or Symbols.ParamKind.Tile
                     && Symbols.LookedUp(p).Select(t => (t, Symbols.LookupProblem(method, p, t))).FirstOrDefault(c => c.Item2 != null) is ({ } read, { } written))
                context.ReportDiagnostic(Diagnostic.Create(LookupOfWritten, p.Locations.FirstOrDefault(), p.Name, Symbols.Describe(method),
                    read.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), written));
            else if (kind == Symbols.ParamKind.Emit && Symbols.EmitProblem(method, p) is { } emit)
                context.ReportDiagnostic(Diagnostic.Create(BadEmit, p.Locations.FirstOrDefault(), p.Name, Symbols.Describe(method),
                    Symbols.EmitElement(p.Type)!.Name, emit));
            else if (kind == Symbols.ParamKind.Reduction && Symbols.ReductionProblem(method, p) is { } reduction)
                context.ReportDiagnostic(Diagnostic.Create(ReductionOverwritten, p.Locations.FirstOrDefault(), p.Name, method.Name,
                    Symbols.ReduceElement(p.Type)!.Name, reduction));
            else if (kind == Symbols.ParamKind.Optional && !Symbols.IsReadOnlyMaybe(p.Type)
                     && Symbols.MaybeElement(p.Type) is { } optional && Symbols.IsIndexed(optional) && !HasBody(method))
                context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, p.Locations.FirstOrDefault(), optional.Name,
                    $"parameter '{p.Name}' hands out a ref to it (take a ReadOnlyMaybe<{optional.Name}> to read it)"));
            else if (kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable)
            {
                components++;
                var component = Symbols.QueriedComponent(p);
                bool writable = kind switch
                {
                    Symbols.ParamKind.Component => p.RefKind == RefKind.Ref,
                    Symbols.ParamKind.Mutable => true,
                    _ => Symbols.Named(p.Type.OriginalDefinition, "System.Span<T>"),
                };
                // A ref or Span parameter of a method with a body is reported with the body (AnalyzeParameters), which
                // knows whether the method only reads it (then 'in' / ReadOnlySpan is the fix). A component the method
                // also excludes is EECS016's or EECS049's: the method never runs.
                if (writable && component != null && Symbols.IsIndexed(component) && (kind == Symbols.ParamKind.Mutable || !HasBody(method))
                    && !excluded.Contains(component))
                    context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, p.Locations.FirstOrDefault(), component.Name,
                        $"parameter '{p.Name}' can write it (take it as 'in' or ReadOnlySpan)"));
            }
        }

        if (valid && !Symbols.ComponentCountValid(method, components + Views.ComponentCount(method)))
            context.ReportDiagnostic(Diagnostic.Create(ComponentCount, location, Symbols.Describe(method)));
        if (valid && problem == null && Symbols.MixesBatch(method))
            context.ReportDiagnostic(Diagnostic.Create(MixedBatch, location, method.Name));
    }

    /// <summary>A method declared once, with its body: its operation block is analyzed (no partial parts).</summary>
    internal static bool HasBody(IMethodSymbol method) =>
        method.PartialDefinitionPart == null && method.PartialImplementationPart == null
        && method.DeclaringSyntaxReferences.Length == 1
        && method.DeclaringSyntaxReferences[0].GetSyntax() is MethodDeclarationSyntax { Body: not null } or MethodDeclarationSyntax { ExpressionBody: not null };

    /// <summary>A change of a world the method creates itself (<c>using var w = new World()</c>): not the visited one's.</summary>
    private static bool OwnWorld(IInvocationOperation call) =>
        Symbols.Receiver(call) is ILocalReferenceOperation { Local: var local }
        && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: var value }
        && value is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
        && Symbols.Named(local.Type, Symbols.World);

    /// <summary>
    /// The components a [ForEach] method's entities must lack ([Without&lt;T&gt;], [Removed&lt;T&gt;], its group's):
    /// a parameter of one of them is EECS016's or EECS049's (the method never runs), so the rules about what the
    /// parameter costs or writes leave it out.
    /// </summary>
    internal static System.Collections.Generic.HashSet<ITypeSymbol> Excluded(IMethodSymbol method, System.Threading.CancellationToken token) =>
        new(Symbols.Requirements(method, token).Where(r => !r.Has).Select(r => r.Type), SymbolEqualityComparer.Default);

    /// <summary>EECS024: [ForEach] on a local function (a symbol action never sees one).</summary>
    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context)
    {
        var function = (LocalFunctionStatementSyntax)context.Node;
        if (function.AttributeLists.Count == 0
            || context.SemanticModel.GetDeclaredSymbol(function, context.CancellationToken) is not IMethodSymbol method
            || !Symbols.IsGeneratedJob(method)) return;
        context.ReportDiagnostic(Diagnostic.Create(LocalForEach, function.Identifier.GetLocation(), method.Name));
    }

    /// <summary>EECS010: a [ForEach(Parallel = true)] method that cannot run on several threads.</summary>
    private static void AnalyzeParallel(SyntaxNodeAnalysisContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } method
            || !Symbols.IsForEach(method) || !Symbols.IsParallel(method)
            || method.PartialDefinitionPart != null) return; // a partial method: reported once, on its definition
        if (BodyFacts.ParallelProblem(method, syntax, context.SemanticModel) is not { } problem) return;
        // An instance method that 'static' alone makes stable: the fix offers 'static'.
        bool makeStatic = !method.IsStatic && !Symbols.SharesSignature(method) && method.PartialImplementationPart == null
                          && Symbols.StableIfStatic(method, syntax, context.SemanticModel);
        context.ReportDiagnostic(Diagnostic.Create(NotParallel, method.Locations.FirstOrDefault(),
            makeStatic ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Static") : null, method.Name, problem));
    }

    private static string Started(IMethodSymbol starter) =>
        starter.ContainingType.Name + (starter.MethodKind == MethodKind.Constructor ? "" : "." + starter.Name);

    private static IMethodSymbol? Starter(IOperation handoff) => handoff switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation construction => construction.Constructor,
        _ => null,
    };

}

/// <summary>The declaration and [ForEach] rules that are not performance rules (EasyEcsAnalyzer.cs), as one analyzer: Roslyn skips an analyzer only when every rule it reports is suppressed, so a
/// NoWarn of these rules saves their cost.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeclarationAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        EasyEcsAnalyzer.BadParameter, EasyEcsAnalyzer.ComponentCount, EasyEcsAnalyzer.Generic, EasyEcsAnalyzer.NotASystem, EasyEcsAnalyzer.NotStruct, EasyEcsAnalyzer.MixedBatch,
        EasyEcsAnalyzer.NotParallel, EasyEcsAnalyzer.WriteThroughReadOnly, EasyEcsAnalyzer.BufferCopy, EasyEcsAnalyzer.HiddenStructuralChange, EasyEcsAnalyzer.OffThreadStructuralChange, EasyEcsAnalyzer.EntityInData,
        EasyEcsAnalyzer.OrderCycle, EasyEcsAnalyzer.OrderAcrossGroups, EasyEcsAnalyzer.EntryPointCollision, EasyEcsAnalyzer.FilterOutsideGroup, EasyEcsAnalyzer.LocalForEach, EasyEcsAnalyzer.BadSlices,
        EasyEcsAnalyzer.BadChunkSize, EasyEcsAnalyzer.ReductionOverwritten, EasyEcsAnalyzer.LookupOfWritten, EasyEcsAnalyzer.BadEmit, EasyEcsAnalyzer.BadForEntity, EasyEcsAnalyzer.BadTileShape,
        EasyEcsAnalyzer.IndexedByRef, EasyEcsAnalyzer.BadIndexedKey, EasyEcsAnalyzer.Unnameable,
        EasyEcsAnalyzer.SharedTile);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeDeclarations(context);
}
