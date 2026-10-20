using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules for code that works but does more than it needs (EECS025-EECS033) and EECS034: what a [ForEach] method
/// declares and does per row, allocations in code that runs every update, structural changes and queries one call could
/// make, and the generated members. A rule whose code costs is a warning (EECS025-EECS032), the generated member is a
/// suggestion (the same call at the same cost, shorter) and an update inside a system an error. Each is reported only where the cheaper form does the
/// same thing; the code fixes (EasyEcs.CodeFixes) apply the replacement the analyzer computed, so the analyzer stays
/// the only place that decides.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor ReadOnlyWriteAccess = new("EECS025", "A write access that only reads",
        "{0} is a write access, but the code only reads it: {1}; {2}",
        Category, DiagnosticSeverity.Warning, true,
        "A 'ref' component or singleton parameter (a Span<T> in a batch method) puts the type in the system's write set, " +
        "so no parallel stage runs it beside a system that reads the type, and every row it visits of a tracked " +
        "component (ITrackChanges) is stamped as changed, so [Changed<T>] systems visit rows nobody changed. " +
        "Entity.Get<T>() of a tracked component stamps the row too. Read with 'in', ReadOnlySpan<T> or Read<T>().",
        helpLinkUri: Help("EECS025"));

    public static readonly DiagnosticDescriptor UnusedParameter = new("EECS026", "A [ForEach] parameter the method does not use",
        "[ForEach] method '{0}' does not use parameter '{1}': {2}",
        Category, DiagnosticSeverity.Warning, true,
        "Every parameter costs per row or per pass: a component loads and advances its column (and a 'ref' one is a " +
        "write), a singleton is resolved, an Entity builds a handle for every row and, like the World, makes the system " +
        "run alone. A component the method only requires is a [With<T>] filter.",
        helpLinkUri: Help("EECS026"));

    public static readonly DiagnosticDescriptor RowLookup = new("EECS027", "A lookup per row that a parameter makes once",
        "[ForEach] method '{0}' looks up '{1}' with '{2}' for every entity it visits; {3}",
        Category, DiagnosticSeverity.Warning, true,
        "A [ForEach] method's parameters are resolved once per table or per pass: a component is a column the loop walks, " +
        "a singleton a ref taken before the loop. Looking the same data up through the visited Entity or the World " +
        "repeats the search for every row (the entity's record, its table's column, the singleton's slot) and, through " +
        "the World, makes the system run alone.",
        helpLinkUri: Help("EECS027"));

    public static readonly DiagnosticDescriptor Allocation = new("EECS028", "An allocation in code that runs every update",
        "'{0}' allocates {3} in '{1}', which runs {2}",
        Category, DiagnosticSeverity.Warning, true,
        "A [ForEach] method runs for every entity it visits and OnUpdate once per update, so garbage there is garbage " +
        "every tick, and the collections it causes pause the world. Objects, arrays, capturing lambdas, LINQ, strings " +
        "built at run time, boxing and enumerators of interfaces allocate; code inside a throw, and calls the compiler " +
        "removes ([Conditional]), are left out.",
        helpLinkUri: Help("EECS028"));

    public static readonly DiagnosticDescriptor InstanceForEach = new("EECS029", "An instance [ForEach] method that uses no instance member",
        "[ForEach] method '{0}' uses nothing of its system; make it static",
        Category, DiagnosticSeverity.Warning, true,
        "An instance method could reach the World through its system, so the generator treats it as one that may change " +
        "structure: its system runs alone in a parallel stage, and its rows are walked with the checks a structural " +
        "change needs. A static method that takes no Entity or World is proven not to change structure.",
        helpLinkUri: Help("EECS029"));

    public static readonly DiagnosticDescriptor PerEntityChange = new("EECS030", "A structural change entity by entity over a query",
        "This loop {0} each entity of the query one by one; '{1}' does it for all of them in one call",
        Category, DiagnosticSeverity.Warning, true,
        "A structural change through each entity moves (or flips) one row per call, through the structural gate each " +
        "time. SetAll, RemoveAll, DisableAll, EnableAll and DestroyEntities change every matching row of a table " +
        "together, whole words of rows at once.",
        helpLinkUri: Help("EECS030"));

    public static readonly DiagnosticDescriptor CreateInLoop = new("EECS031", "Entities created one by one in a counted loop",
        "This loop creates {0} entities one by one; {1}",
        Category, DiagnosticSeverity.Warning, true,
        "Creating entities one by one grows the table and the entity storage by doubling as they fill, and in a world " +
        "that has never held them the discarded arrays can set off a full garbage collection. CreateEntities and " +
        "Instantiate(prefab, span) create them in one pass; EnsureCapacity reserves room first when each entity gets " +
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
        Category, DiagnosticSeverity.Info, true,
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
        public INamedTypeSymbol Entity = null!, World = null!, EntitySpan = null!, EcsSystem = null!, Track = null!, Prefab = null!;

        public static Core? Create(Compilation compilation)
        {
            var entity = compilation.GetTypeByMetadataName(Symbols.Entity);
            var world = compilation.GetTypeByMetadataName(Symbols.World);
            var span = compilation.GetTypeByMetadataName(Symbols.EntitySpan);
            var system = compilation.GetTypeByMetadataName(Symbols.EcsSystem);
            var track = compilation.GetTypeByMetadataName(Symbols.TrackChanges);
            var prefab = compilation.GetTypeByMetadataName("EasyEcs.Prefab");
            return entity == null || world == null || span == null || system == null || track == null || prefab == null ? null
                : new Core { Entity = entity, World = world, EntitySpan = span, EcsSystem = system, Track = track, Prefab = prefab };
        }

        public bool Is(ITypeSymbol? type, INamedTypeSymbol core) => SymbolEqualityComparer.Default.Equals(type, core);

        public bool Tracked(ITypeSymbol type) => type.AllInterfaces.Contains(Track, SymbolEqualityComparer.Default);

        public bool IsSystem(INamedTypeSymbol? type)
        {
            for (var t = type?.BaseType; t != null; t = t.BaseType)
                if (Is(t, EcsSystem)) return true;
            return false;
        }

        /// <summary>The system whose code this is (the containing type, or one it is nested in), or null.</summary>
        public INamedTypeSymbol? SystemOf(ISymbol? symbol)
        {
            for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType; type != null; type = type.ContainingType)
                if (IsSystem(type)) return type;
            return null;
        }
    }

    private static void InitializePerformance(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (Core.Create(start.Compilation) is not { } core) return;
            start.RegisterOperationBlockAction(c => AnalyzeBody(c, core));
            start.RegisterOperationAction(c => AnalyzeCall(c, core), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeItem(c), OperationKind.PropertyReference);
            start.RegisterOperationAction(c => AnalyzeComparison(c, core), OperationKind.Binary);
            start.RegisterOperationAction(c => AnalyzeLoop(c, core), OperationKind.Loop);
        });

    // ---------------- [ForEach] methods and OnUpdate: parameters, lookups, allocations ----------------

    private static void AnalyzeBody(OperationBlockAnalysisContext context, Core core)
    {
        if (context.OwningSymbol is not IMethodSymbol method) return;
        var body = context.OperationBlocks.FirstOrDefault(b => b is IMethodBodyOperation or IBlockOperation);
        if (body == null) return;
        bool forEach = Symbols.IsForEach(method);
        if (forEach)
        {
            var uses = new Dictionary<IParameterSymbol, List<IOperation>>(SymbolEqualityComparer.Default);
            bool usesInstance = false;
            foreach (var operation in body.Descendants())
            {
                if (operation is IParameterReferenceOperation reference)
                {
                    if (!uses.TryGetValue(reference.Parameter, out var list)) uses[reference.Parameter] = list = [];
                    list.Add(reference);
                }
                else if (operation is IInstanceReferenceOperation) usesInstance = true;
            }

            AnalyzeParameters(context, method, body, uses);
            AnalyzeLookups(context, core, method, body);
            AnalyzeAllocations(context, method, body, "for every entity it visits, each update");
            if (!method.IsStatic && !usesInstance && core.IsSystem(method.ContainingType) && !Symbols.SharesSignature(method)
                && method.PartialDefinitionPart == null && method.PartialImplementationPart == null)
                context.ReportDiagnostic(Diagnostic.Create(InstanceForEach, method.Locations.FirstOrDefault(), method.Name));
        }
        else if (method is { Name: "OnUpdate", IsOverride: true, Parameters.Length: 0 } && core.IsSystem(method.ContainingType)
                 && !HasCadence(method.ContainingType, core))
            AnalyzeAllocations(context, method, body, "every update");
    }

    /// <summary>
    /// Whether the system (or a base below EcsSystem) overrides UpdateInterval: its OnUpdate runs on the cadence its
    /// author chose, so what it allocates there is a choice, not garbage of every update.
    /// </summary>
    private static bool HasCadence(INamedTypeSymbol? type, Core core)
    {
        for (; type != null && !core.Is(type, core.EcsSystem); type = type.BaseType)
            if (type.GetMembers("UpdateInterval").Any(m => m.IsOverride)) return true;
        return false;
    }

    /// <summary>EECS025 for 'ref' / Span parameters that only read, EECS026 for parameters not used at all.</summary>
    private static void AnalyzeParameters(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body,
        Dictionary<IParameterSymbol, List<IOperation>> uses)
    {
        bool empty = body is IMethodBodyOperation { BlockBody.Operations.Length: 0, ExpressionBody: null } or IBlockOperation { Operations.Length: 0 };
        bool outside = Symbols.IsRunner(method);
        var removed = Filtered(method, Symbols.RemovedAttribute);
        var required = Filtered(method, Symbols.WithAttribute).Concat(Filtered(method, Symbols.ChangedAttribute)).ToArray();
        int spans = method.Parameters.Count(p => Symbols.Classify(p) == Symbols.ParamKind.ComponentSpan);
        bool entitySpan = method.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.Entities);

        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p, outside);
            if (kind is Symbols.ParamKind.Invalid or Symbols.ParamKind.Context) continue;
            var location = p.Locations.FirstOrDefault();
            var component = kind switch
            {
                Symbols.ParamKind.Component => p.Type,
                Symbols.ParamKind.ComponentSpan => Symbols.SpanElement(p.Type),
                _ => null,
            };

            if (!uses.TryGetValue(p, out var references))
            {
                if (empty) continue; // a method being written: nothing to say yet
                if (component != null)
                {
                    string shown = component.ToMinimalDisplayString(body.SemanticModel!, location?.SourceSpan.Start ?? 0);
                    // [With<T>] cannot name a type parameter (CS8968); [Removed<T>] with T is EECS016; an EntitySpan needs a span.
                    if (component is ITypeParameterSymbol || removed.Contains(component, SymbolEqualityComparer.Default)
                        || kind == Symbols.ParamKind.ComponentSpan && entitySpan && spans == 1) continue;
                    // A write access of a tracked component stamps every row it visits: [With<T>] would not, so the
                    // fix is offered only where it changes nothing but the cost.
                    bool stamps = (p.RefKind == RefKind.Ref || kind == Symbols.ParamKind.ComponentSpan
                                   && p.Type.OriginalDefinition.ToDisplayString() == "System.Span<T>")
                                  && Symbols.Implements(component, Symbols.TrackChanges);
                    if (!stamps && required.Contains(component, SymbolEqualityComparer.Default))
                    {
                        // [With<T>] or [Changed<T>] already requires it: the parameter only adds the column's walk.
                        context.ReportDiagnostic(Diagnostic.Create(UnusedParameter, location, ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Remove"),
                            method.Name, p.Name, $"its column is still loaded for every row, and a filter already requires {shown}; remove it"));
                        continue;
                    }

                    var with = stamps ? null : ImmutableDictionary<string, string?>.Empty.Add(FixKind, "With")
                        .Add(WithType, shown);
                    context.ReportDiagnostic(Diagnostic.Create(UnusedParameter, location, with, method.Name, p.Name, stamps
                        ? $"it stamps every row it visits as changed without writing it; require the component with [With<{shown}>] unless the stamp is the point"
                        : $"its column is still loaded for every row; require the component with [With<{shown}>] instead"));
                }
                else
                    context.ReportDiagnostic(Diagnostic.Create(UnusedParameter, location,
                        ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Remove"), method.Name, p.Name, kind switch
                        {
                            Symbols.ParamKind.Entity => "it builds a handle for every row and makes the system run alone; remove it",
                            Symbols.ParamKind.World => "it makes the system run alone; remove it",
                            Symbols.ParamKind.Entities => "remove it",
                            Symbols.ParamKind.Singleton => "it is resolved every pass and scheduled as an access; remove it",
                            _ => "it is looked up for every row; remove it",
                        }));
                continue;
            }

            bool span = kind == Symbols.ParamKind.ComponentSpan && p.Type.OriginalDefinition.ToDisplayString() == "System.Span<T>";
            bool byRef = kind is Symbols.ParamKind.Component or Symbols.ParamKind.Singleton && p.RefKind == RefKind.Ref;
            if (!span && !byRef) continue;
            var type = component ?? p.Type;
            if (Symbols.IsIndexed(type)) continue; // EECS015 says it
            if (references.Any(Uses.MayWrite)) continue;
            string cost = kind == Symbols.ParamKind.Singleton
                ? "the system is scheduled as the singleton's writer, so no parallel stage runs it beside a reader"
                : Symbols.Implements(type, Symbols.TrackChanges)
                    ? "every row it visits is stamped as changed, so [Changed<" + type.Name + ">] systems visit rows nobody changed"
                    : "the system is scheduled as the component's writer, so no parallel stage runs it beside a reader";
            context.ReportDiagnostic(Diagnostic.Create(ReadOnlyWriteAccess, location,
                ImmutableDictionary<string, string?>.Empty.Add(FixKind, span ? "ReadOnlySpan" : "In"),
                $"Parameter '{p.Name}' of [ForEach] method '{method.Name}' ({(span ? "Span<" + type.Name + ">" : "ref")})", cost,
                span ? "take it as a ReadOnlySpan<" + type.Name + ">" : "take it by 'in'"));
        }
    }

    /// <summary>The type arguments of a method's filter attributes of one kind (<c>EasyEcs.WithAttribute&lt;T&gt;</c>, ...).</summary>
    private static ITypeSymbol[] Filtered(IMethodSymbol method, string attribute) => method.GetAttributes()
        .Where(a => a.AttributeClass is { IsGenericType: true } c && c.ConstructedFrom.ToDisplayString() == attribute)
        .Select(a => a.AttributeClass!.TypeArguments[0]).ToArray();

    /// <summary>
    /// EECS027: the visited entity's components looked up through its Entity, and singletons through the World, inside
    /// a [ForEach] method. Components the method itself adds or removes through the entity are left out.
    /// </summary>
    private static void AnalyzeLookups(OperationBlockAnalysisContext context, Core core, IMethodSymbol method, IOperation body)
    {
        var entities = method.Parameters.Where(p => Symbols.Classify(p) is Symbols.ParamKind.Entity or Symbols.ParamKind.Entities).ToArray();
        var worlds = method.Parameters.Where(p => Symbols.Classify(p) == Symbols.ParamKind.World).ToArray();
        if (entities.Length == 0 && worlds.Length == 0 && method.IsStatic) return;

        var changed = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var lookups = new List<(IOperation Operation, ITypeSymbol Type, string Member, bool Singleton, bool Presence)>();
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

            if (Lookup(member, onEntity, core) is not { } found) continue;
            lookups.Add((operation, found.Type, found.Member, !onEntity, found.Presence));
        }

        foreach (var (operation, type, member, singleton, presence) in lookups)
        {
            // Changed through the entity in the same method, indexed (a Maybe of it would be a write: EECS015), or
            // looked up only on some rows: a branch, a loop, after an early return.
            if (changed.Contains(type) || Symbols.IsIndexed(type) || !EveryRow(operation, body)) continue;
            string name = type.ToMinimalDisplayString(operation.SemanticModel!, operation.Syntax.SpanStart);
            string advice = singleton ? $"take it as a parameter ('in {name}' or 'ref {name}'), resolved once per pass"
                : presence ? $"take a Maybe<{name}> parameter, or split the method with [With<{name}>] / [Without<{name}>]"
                : $"take it as a parameter ('in {name}' or 'ref {name}'), or a Maybe<{name}> when some entities lack it";
            context.ReportDiagnostic(Diagnostic.Create(RowLookup, operation.Syntax.GetLocation(), method.Name, name,
                Shorten(operation.Syntax), advice));
        }
    }

    /// <summary>
    /// Whether <paramref name="operation"/> runs on every call of the method: in no branch (an if's or a switch's arms,
    /// the right of &amp;&amp; / || / ??, a loop's body, a catch), and after no explicit return, continue or throw.
    /// </summary>
    private static bool EveryRow(IOperation operation, IOperation body)
    {
        for (IOperation current = operation, parent = operation.Parent!; parent != null && current != body; current = parent, parent = parent.Parent!)
            switch (parent)
            {
                case IConditionalOperation conditional when conditional.Condition != current:
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } binary when binary.RightOperand == current:
                case ICoalesceOperation coalesce when coalesce.WhenNull == current:
                case ILoopOperation loop when loop.Body == current:
                case ISwitchCaseOperation or ISwitchExpressionArmOperation or ICatchClauseOperation or IConditionalAccessOperation
                    or IAnonymousFunctionOperation or ILocalFunctionOperation:
                    return false;
            }

        int at = operation.Syntax.SpanStart;
        foreach (var exit in body.Descendants())
            if (exit is IReturnOperation { IsImplicit: false } or IThrowOperation or IBranchOperation { BranchKind: BranchKind.Continue or BranchKind.Break }
                && exit.Syntax.SpanStart < at && !exit.Syntax.Span.Contains(at))
                return false;
        return true;
    }

    private static bool IsVisited(IOperation? receiver, IParameterSymbol[] entities, Core core)
    {
        while (receiver is IConversionOperation conversion) receiver = conversion.Operand;
        return receiver switch
        {
            IParameterReferenceOperation reference => core.Is(reference.Type, core.Entity) && entities.Contains(reference.Parameter, SymbolEqualityComparer.Default),
            IPropertyReferenceOperation { Property.IsIndexer: true, Instance: IParameterReferenceOperation span } => // entities[i]
                core.Is(span.Type, core.EntitySpan) && entities.Contains(span.Parameter, SymbolEqualityComparer.Default),
            _ => false,
        };
    }

    private static bool IsWorld(IOperation? receiver, IParameterSymbol[] worlds, Core core, IMethodSymbol method)
    {
        while (receiver is IConversionOperation conversion) receiver = conversion.Operand;
        return receiver switch
        {
            IParameterReferenceOperation reference => worlds.Contains(reference.Parameter, SymbolEqualityComparer.Default),
            IPropertyReferenceOperation { Property.Name: "World", Instance: IInstanceReferenceOperation } property =>
                !method.IsStatic && core.Is(property.Type, core.World),
            _ => false,
        };
    }

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
    /// prefix (Has, Is, TryGet, Write, GetOrAdd, Set, Remove, Disable, Enable), in the generated class's namespace.
    /// </summary>
    private static ITypeSymbol? Named(ISymbol member, Core core)
    {
        var holder = member.ContainingType;
        while (holder?.ContainingType != null) holder = holder.ContainingType;
        if (holder?.ContainingNamespace is not { } ns) return null;
        string name = member.Name;
        if (name.StartsWith("Is") && name.EndsWith("Disabled") && name.Length > "IsDisabled".Length) name = name.Substring(2, name.Length - 10);
        foreach (var prefix in new[] { "", "Has", "Is", "TryGet", "Write", "GetOrAdd", "Set", "Remove", "Disable", "Enable", "Add", "Links" })
        {
            if (!name.StartsWith(prefix) || name.Length == prefix.Length) continue;
            string type = name.Substring(prefix.Length);
            // The class is named after the type's flat name (Outer__Inner): its type is the one whose flat name it carries.
            foreach (var candidate in TypesNamed(ns, type))
            {
                if (holder.Name != Symbols.FlatName(candidate) + "EcsExtensions" && holder.Name != Symbols.FlatName(candidate) + "BufferExtensions") continue;
                if (holder.Name.EndsWith("BufferExtensions"))
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

    private static void AnalyzeAllocations(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body, string runs)
    {
        var pending = new Stack<IOperation>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            var operation = pending.Pop();
            // A throw ends the tick; a [Conditional] call is not compiled in release; console output is the cost
            // itself (the program prints on purpose, and the write costs more than the string it formats).
            if (operation is IThrowOperation
                || operation is IInvocationOperation call && (Conditional(call.TargetMethod) || call.TargetMethod.ContainingType?.ToDisplayString() == "System.Console"))
                continue;
            if (Allocates(operation) is { } what)
            {
                var where = operation is IForEachLoopOperation loop ? loop.Collection.Syntax : operation.Syntax;
                context.ReportDiagnostic(Diagnostic.Create(Allocation, where.GetLocation(), Shorten(where), method.Name, runs, what));
                if (operation is not ILoopOperation) continue; // one report per expression: its parts allocate as part of it
            }

            foreach (var child in operation.ChildOperations) pending.Push(child);
        }
    }

    private static string Shorten(SyntaxNode syntax)
    {
        var text = syntax.ToString().Replace("\r", "").Replace('\n', ' ');
        return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
    }

    /// <summary>A method the compiler drops outside the symbol it names (Debug.Assert, Debug.WriteLine, ...).</summary>
    private static bool Conditional(IMethodSymbol method) =>
        method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute");

    /// <summary>What <paramref name="operation"/> allocates, or null: the kinds EECS028 names.</summary>
    private static string? Allocates(IOperation operation)
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
            case IInvocationOperation call when StringCall(call.TargetMethod):
                return "a string built at run time";
            case IInvocationOperation call when call.TargetMethod.ContainingType?.ToDisplayString() is "System.Linq.Enumerable"
                                                     or "System.Linq.ParallelEnumerable" or "System.Linq.Queryable":
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

        if (core.Is(owner, core.World) && method.Name == "Update" && core.SystemOf(context.ContainingSymbol) is { } system
            && !StableForEach(context.ContainingSymbol, call.SemanticModel!))
        {
            context.ReportDiagnostic(Diagnostic.Create(UpdateInSystem, call.Syntax.GetLocation(), system.Name,
                "World.Update" + (method.TypeArguments.Length == 1 ? "<" + method.TypeArguments[0].Name + ">" : "")));
            return;
        }

        var model = call.SemanticModel!;
        // EECS025: a tracked component reached for writing (Get<T>(), the generated WriteX()) and only read.
        if (core.Is(owner, core.Entity) && method is { Name: "Get", TypeArguments.Length: 1 } && core.Tracked(method.TypeArguments[0])
            && !Symbols.IsIndexed(method.TypeArguments[0]) && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            && !Uses.MayWrite(call))
        {
            string read = access.Expression + "." + access.Name.ToString().Replace("Get", "Read") + "()";
            context.ReportDiagnostic(Diagnostic.Create(ReadOnlyWriteAccess, call.Syntax.GetLocation(), Fix(read, "Read it with Read<T>()"),
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
                context.ReportDiagnostic(Diagnostic.Create(ReadOnlyWriteAccess, call.Syntax.GetLocation(), Fix(read, "Read it with ." + name),
                    method.Name + "()", "it stamps the row as changed, so [Changed<" + name + ">] systems visit it", "read it with ." + name));
            return;
        }

        if (!generated) GeneratedEquivalent(context, call, core);
    }

    /// <summary>A stable [ForEach] method: EECS013 already reports a structural call (Update is one) in it.</summary>
    private static bool StableForEach(ISymbol symbol, SemanticModel model) =>
        symbol is IMethodSymbol method && Symbols.IsForEach(method)
        && method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax syntax
        && syntax.SyntaxTree == model.SyntaxTree && Symbols.IsStable(method, syntax, model);

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
                && g.OriginalDefinition.ToDisplayString() != Symbols.DynamicBuffer)) return;
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
                (true, "GetOrAdd") => ("GetOrAdd" + name, false),
                (true, "Remove") => ("Remove" + name, false),
                (true, "Disable") => ("Disable" + name, false),
                (true, "Enable") => ("Enable" + name, false),
                (true, "IsDisabled") => ("Is" + name + "Disabled", true),
                (true, "Set") => SetCandidate(call, name, tag, out args),
                (false, "GetSingleton") => (name, true),
                (false, "HasSingleton") => ("Has" + name, true),
                (false, "GetOrAddSingleton") => ("GetOrAdd" + name, false),
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
        if (!property.Name.StartsWith("Component") || property.ContainingType is not { Name: "QueryItem" } item
            || item.ContainingNamespace?.ToDisplayString() != "EasyEcs" || reference.Syntax is not MemberAccessExpressionSyntax access
            || property.Type is not INamedTypeSymbol { IsGenericType: false } type) return;
        string replacement = access.Expression + "." + type.Name;
        if (!Binds(reference.SemanticModel!, access, replacement, out var bound) || !Symbols.IsGenerated(bound!)
            || bound is IPropertySymbol { RefKind: not RefKind.Ref } && Uses.MayWrite(reference)) return;
        context.ReportDiagnostic(Diagnostic.Create(GeneratedMember, access.GetLocation(), Fix(replacement, "Use ." + type.Name),
            access.ToString(), replacement));
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
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

    /// <summary>The <c>world.Query&lt;…&gt;(filter)</c> a foreach walks, or null.</summary>
    private static IInvocationOperation? Query(IOperation collection, Core core) =>
        Unwrap(collection) is IInvocationOperation { TargetMethod: { Name: "Query", TypeArguments.Length: > 0 } method } call
        && core.Is(method.ContainingType, core.World) && call.Instance != null ? call : null;

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
            return;
        }

        // EECS030: the same structural change through each entity.
        if (statement is not IExpressionStatementOperation { Operation: IInvocationOperation call } || item == null
            || !IsItemEntity(Symbols.Receiver(call), item) || !Invariant(call.Arguments.Select(a => a.Value), loop.Body, item)) return;
        var method = call.TargetMethod;
        bool generated = Symbols.IsGenerated(method);
        var component = method.TypeArguments.Length == 1 ? method.TypeArguments[0] : generated ? Named(method, core) : null;
        string with = (filter != null ? "(" + filter + ")" : "QueryFilter.Create()") + string.Concat(types.Select(t => $".With<{t.ToMinimalDisplayString(model, at)}>()"));
        string? bulk = null, call2 = null, verb = null;
        string? name = generated ? Generated(method.Name) : method.Name;
        string T() => component!.ToMinimalDisplayString(model, at);
        switch (name)
        {
            case "Destroy" when !generated:
                (bulk, call2, verb) = ("DestroyEntities", $"DestroyEntities<{typeList}>({filter})", "destroys");
                break;
            // A Set of a component the query requires overwrites a value: no row moves, nothing for SetAll to batch.
            // A grouped component's new value moves each row to its group's table, one by one under SetAll too.
            case "Set" when component != null && !types.Contains(component, SymbolEqualityComparer.Default)
                            && !Symbols.Implements(component, Symbols.Group)
                            && SetValue(call, component, generated, model, at) is { } value:
                (bulk, call2, verb) = ("SetAll", $"SetAll({value}, {with})", "adds a component to");
                break;
            case "Remove" when component != null && call.Arguments.All(a => a.IsImplicit):
                (bulk, call2, verb) = ("RemoveAll", $"RemoveAll<{T()}>({with})", "removes a component from");
                break;
            case "Disable" when component != null:
                (bulk, call2, verb) = ("DisableAll", $"DisableAll<{T()}>({with})", "disables a component of");
                break;
            case "Enable" when component != null:
                (bulk, call2, verb) = ("EnableAll", $"EnableAll<{T()}>({with})", "enables a component of");
                break;
        }

        if (bulk == null) return;
        string text = world + "." + call2;
        context.ReportDiagnostic(Diagnostic.Create(PerEntityChange, loop.Syntax.GetLocation(),
            Binds(model, loop.Syntax, text, out _) ? Fix(text + ";", "Make it one bulk call") : null, verb, bulk));

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
        Unwrap(receiver) is IPropertyReferenceOperation { Property.Name: "Entity", Instance: ILocalReferenceOperation local }
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
        for (var parent = create.Parent; parent != null && parent != loop; parent = parent.Parent)
            if (parent is ILoopOperation or IConditionalOperation or ISwitchOperation or ICoalesceOperation or IConditionalAccessOperation)
                return;
        string world = create.Instance!.Syntax.ToString();
        // A system creates entities in a world that is running: rows freed by earlier destroys are reused, and a
        // table that grew keeps its size, so the cost is a start-up's, paid once.
        if (Reserved(loop, world, core) || core.SystemOf(context.ContainingSymbol) != null) return;
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
            && Unwrap(assigned) == create && SymbolEqualityComparer.Default.Equals(i.Local, index)
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

        // Each entity its own values: reserve the table first (EnsureCapacity of a prefab reserves the table of its
        // components alone, not the one an instance with given components goes to).
        if (instantiate && create.Arguments.Length > 1) return;
        string reserve = instantiate
            ? Invariant([create.Arguments[0].Value], loop.Body, index) ? $"{world}.EnsureCapacity({create.Arguments[0].Value.Syntax}, {count})" : ""
            : $"{world}.EnsureCapacity<{string.Join(", ", create.TargetMethod.TypeArguments.Select(t => t.ToMinimalDisplayString(model, at)))}>({count})";
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
        if (Unwrap(bound) is not IPropertyReferenceOperation { Property.Name: "Length", Instance: ILocalReferenceOperation { Local: { Type: IArrayTypeSymbol } local } } length)
            return null;
        IOperation root = length;
        while (root.Parent != null) root = root.Parent;
        int? size = null;
        foreach (var operation in root.Descendants())
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local):
                    if (Unwrap(declarator.Initializer?.Value) is not IArrayCreationOperation { DimensionSizes: { Length: 1 } sizes }
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
    private static bool Simple(IOperation operation) => Unwrap(operation) switch
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
                    && call.TargetMethod.Name is "EnsureCapacity" or "EnsureEntityCapacity" or "CreateEntities")
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
        var count = (IInvocationOperation)Unwrap(countLeft ? binary.LeftOperand : binary.RightOperand)!;
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
        Unwrap(operation) is IInvocationOperation { TargetMethod: { Name: "Count", TypeArguments.Length: > 0 } method, Instance: not null }
        && core.Is(method.ContainingType, core.World);
}
