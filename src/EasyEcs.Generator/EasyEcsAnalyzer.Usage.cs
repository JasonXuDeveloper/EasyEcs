using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules for calls and declarations that compile but do not do what they say (EECS037, EECS038, EECS040-EECS045,
/// EECS047-EECS051):
/// a job's <c>Writes</c> that disagrees with its <c>Execute</c>, a changed copy of a component that is dropped,
/// <c>SetIfChanged</c> on a type it cannot compare, calls a pass refuses made during one, an indexed component changed
/// while its index is walked, a record struct key that prints itself, a sliced filter where every row is needed, and
/// the world's own tags written by hand, a ref used after a structural change, a component that cannot be disabled,
/// filters no entity passes, a second group in one filter and an update during a pass. Each is a call or a declaration
/// the runtime throws for, or a write it loses.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor JobWrites = new("EECS037", "A job's Writes does not match what Execute writes",
        "Job '{0}' {1}; declare 'public static int Writes => {2};'",
        Category, DiagnosticSeverity.Warning, true,
        "A hand-written job (IForEach, IForEachEntity, IForEachBatch) tells the loop which components Execute writes " +
        "through its static Writes (bit k - 1 for component k; -1, all of them, when it is not declared). The loop " +
        "stamps those components of every row it visits as changed and carries their values over when Execute moves " +
        "the entity to another table. A written component left out loses both: [Changed<T>] systems miss the write, and " +
        "a structural change in Execute drops it. A tracked component that Execute only reads, but Writes names, is " +
        "stamped on every row, so [Changed<T>] systems visit rows nobody changed.", helpLinkUri: Help("EECS037"));

    public static readonly DiagnosticDescriptor DroppedCopy = new("EECS038", "A changed copy of a component is dropped",
        "'{0}' is a copy of the value '{1}' returns, and nothing reads it after the change: the world keeps the old value; {2}",
        Category, DiagnosticSeverity.Warning, true,
        "Get, the generated component and singleton properties, query items, Maybe.Value and Mut.Value return a ref into " +
        "the world's storage. 'var x = ...' copies the value it points at, and TryGet hands out a copy: a field written " +
        "on the copy changes the local only. Keep the ref ('ref var x = ref ...'), write through the call directly, or " +
        "write the copy back with Set.", helpLinkUri: Help("EECS038"));

    public static readonly DiagnosticDescriptor NotComparable = new("EECS040", "SetIfChanged on a type it cannot compare",
        "'{0}' compares {1} values, but {1} holds references and does not implement IEquatable<{1}>: the call throws NotSupportedException; make it a record struct or implement IEquatable<{1}>",
        Category, DiagnosticSeverity.Error, true,
        "SetIfChanged writes only a value that differs from the current one. It compares through IEquatable<T>, or the " +
        "bytes of an unmanaged T; a T that holds references and is not IEquatable<T> could only be compared by boxing " +
        "it, so the call throws (Values.Same).", helpLinkUri: Help("EECS040"));

    public static readonly DiagnosticDescriptor RefusedInPass = new("EECS041", "A call a pass refuses",
        "'{0}' throws while {1} iterates its world: {2}",
        Category, DiagnosticSeverity.Error, true,
        "TrimExcess, Capture, Restore, RemoveTypes, GroupBy, Save, Load, SaveEntities, LoadEntities, LoadEntitiesInto and " +
        "MoveEntities rewrite or copy whole tables, so a world refuses them while a pass walks its rows (a foreach over " +
        "Query, a [ForEach] method, a job's Execute): they throw InvalidOperationException. Record what to do as data " +
        "and make the call after the pass.", helpLinkUri: Help("EECS041"));

    public static readonly DiagnosticDescriptor IndexChangedInWalk = new("EECS042", "An indexed component changed while its index is walked",
        "'{0}' adds to the index of '{1}' while this foreach walks it ('{2}'): collect the entities first, then change them",
        Category, DiagnosticSeverity.Error, true,
        "A foreach over an index (Find, Children, Range, Ascending, Descending, Descendants, a relation index's Find) " +
        "reads the index's lists as it goes. Giving an entity the indexed component or a new key (Set, SetIfChanged, " +
        "GetOrAdd, Enable, SetAll, EnableAll, a creation with it, the generated SetX; AddRelation for a relation) adds it " +
        "to a key, and an emptied key hands its list to the keys that grow next: the walk skips entities or visits " +
        "others. Removing the component, dropping links and destroying entities leave holes the walk steps past; a " +
        "change followed by a break, return or throw ends the walk first.", helpLinkUri: Help("EECS042"));

    public static readonly DiagnosticDescriptor KeyPrintsItself = new("EECS043", "A record struct key that prints itself",
        "'{0}.Key' is a public property that returns the record itself: ToString() prints Key, which prints the record, until the stack overflows; implement it explicitly ('{1}')",
        Category, DiagnosticSeverity.Error, true,
        "A record struct prints its public properties in ToString(). A key that is the record itself (IIndexed<TSelf>, " +
        "the actor pattern) as a public property prints the record again, so the first log line or debugger view of " +
        "the value overflows the stack. An explicit implementation belongs to the interface only: the index still " +
        "finds it, ToString() does not print it.", helpLinkUri: Help("EECS043"));

    public static readonly DiagnosticDescriptor SlicedFilter = new("EECS044", "A sliced filter where every row is needed",
        "{0}",
        Category, DiagnosticSeverity.Error, true,
        "QueryFilter.Slice(index, count) cuts each matching table for ForEach passes ([ForEach(Slices = N)], ForEach, " +
        "ForEachEntity, ForEachBatch and their parallel forms). Query, Read, Count, Any, DestroyAll, entity.Run, SetAll, " +
        "RemoveAll, DisableAll and EnableAll need every matching row and throw ArgumentException for a sliced filter; " +
        "Slice itself throws ArgumentOutOfRangeException for a count below 1 or an index outside [0, count).", helpLinkUri: Help("EECS044"));

    public static readonly DiagnosticDescriptor InternalTag = new("EECS045", "A tag the world keeps written by hand",
        "'{0}' writes '{1}', a tag the world keeps itself: {2}",
        Category, DiagnosticSeverity.Error, true,
        "Destroyed marks an entity destroyed while cleanup components remain: Destroy adds it, removing the last cleanup " +
        "component frees the entity. Disabled<T> marks T as disabled: Disable<T> adds it and hides T, Enable<T> " +
        "removes it and gives T back. Written by hand (Set, GetOrAdd, Remove, SetIfChanged, Disable, Enable, " +
        "CreateEntity, CreateEntities, Instantiate, the bulk calls, a prefab's Set and Remove), they leave a retired " +
        "entity never freed, or a component present and tagged disabled at once. Reading them (Has, IsDisabled, " +
        "With / Without filters) is fine.", helpLinkUri: Help("EECS045"));

    public static readonly DiagnosticDescriptor RefAcrossChange = new("EECS047", "A ref used after a structural change",
        "'{0}' {1} before '{2}', a structural change: it can point at a moved row or at another entity's; {3}",
        Category, DiagnosticSeverity.Warning, true,
        "Get, Read, the generated component properties and query items hand out refs into a table's row. A structural " +
        "change (a Set of a component the entity lacks, Destroy, Disable, CreateEntity, Update, ...) moves rows between " +
        "tables, swaps the last row of a table into a hole and grows columns into new arrays: a ref taken before it then " +
        "reads or writes the old place. Removing a component and enabling one flip a bit of the row and move nothing. " +
        "The rule follows one block in order: the ref, a change that always runs, then a use.", helpLinkUri: Help("EECS047"));

    public static readonly DiagnosticDescriptor NotDisableable = new("EECS048", "A component that cannot be disabled",
        "'{0}' disables '{1}', {2}: it throws InvalidOperationException; remove it instead",
        Category, DiagnosticSeverity.Error, true,
        "Disable<T> hides a component and keeps its value. A cleanup component (ICleanupComponent) is what keeps a " +
        "destroyed entity until its resources are released, and ChildOf is what a parent's destruction follows to find " +
        "its children: hidden, both would be forgotten, so Disable<T>, DisableAll<T> and the generated DisableX throw " +
        "for them.", helpLinkUri: Help("EECS048"));

    public static readonly DiagnosticDescriptor NeverMatches = new("EECS049", "Filters no entity passes",
        "[ForEach] method '{0}' requires '{1}' ({2}) and excludes it ({3}): no entity passes both, so the method never runs",
        Category, DiagnosticSeverity.Error, true,
        "A [ForEach] method visits the entities that pass all of its filters and those of its group: its components, " +
        "[With<T>], [Changed<T>], the group's [With<T>] and IGroupWith<T> require a component, [Without<T>] and " +
        "IGroupWithout<T> exclude it. A component both required and excluded leaves no entity to visit. Drop one of " +
        "them, or move the system to another group.", helpLinkUri: Help("EECS049"));

    public static readonly DiagnosticDescriptor SecondGroup = new("EECS050", "A second group in one filter",
        "'{0}' names a second group in a filter that already has '{1}': InGroup throws ArgumentException; a filter names one group",
        Category, DiagnosticSeverity.Error, true,
        "QueryFilter.InGroup<T>(group) (and the generated InX(group)) keeps the one group a filter walks the tables of. " +
        "Naming another one, of another component or another value, throws ArgumentException when the filter is " +
        "built. Build one filter per group.", helpLinkUri: Help("EECS050"));

    public static readonly DiagnosticDescriptor UpdateInPass = new("EECS051", "A world update during a pass",
        "'{0}' runs the world's systems while {1} iterates it: every system runs mid-loop, and closing the pass's holes moves rows the loop has not reached behind it, so the loop misses them; update after the loop",
        Category, DiagnosticSeverity.Error, true,
        "A pass (a foreach over Query or Read, a [ForEach] method, a job's Execute) counts itself on the world, and " +
        "structural changes during it leave holes it closes at its end. An update runs every system in the middle of the loop " +
        "and closes the holes, which moves rows the loop has not reached yet behind it, so the loop misses them (a loop " +
        "that outlives the update ends as no pass). Record what to do, and update after the loop.", helpLinkUri: Help("EECS051"));

    /// <summary>EECS037's property for its code fix: the Writes value Execute needs (<c>0b101</c>).</summary>
    public const string WritesValue = "Writes";

    /// <summary>The World members a pass refuses (they throw while one iterates the world).</summary>
    private static readonly HashSet<string> PassRefused =
    [
        "TrimExcess", "Capture", "Restore", "RemoveTypes", "GroupBy", "Save", "SaveWithout", "Load", "LoadKeeping",
        "SaveEntities", "SaveEntitiesWith", "LoadEntities", "LoadEntitiesInto", "MoveEntities",
    ];

    /// <summary>The calls that need every matching row (they throw for a sliced filter).</summary>
    private static readonly HashSet<string> WholeRowCalls =
        ["Query", "Read", "Count", "Any", "DestroyAll", "SetAll", "RemoveAll", "DisableAll", "EnableAll"];

    private static readonly HashSet<string> EntityTagWriters = ["Set", "GetOrAdd", "Remove", "SetIfChanged", "Disable", "Enable"];

    private static readonly HashSet<string> WorldTagWriters =
        ["CreateEntity", "CreateEntities", "Instantiate", "SetAll", "RemoveAll", "DisableAll", "EnableAll"];

    /// <summary>
    /// The Entity calls that add an entity to a key of an index (Set moves it from its old key to its new one): a key that
    /// grows can take the list of a key emptied during the walk. Removals (Remove, Disable, RemoveRelation, Destroy)
    /// leave holes the walk steps past.
    /// </summary>
    private static readonly HashSet<string> IndexChangers = ["Set", "SetIfChanged", "Enable", "GetOrAdd"];

    /// <summary>The World calls that add entities to an index's keys.</summary>
    private static readonly HashSet<string> WorldIndexChangers = ["SetAll", "EnableAll", "CreateEntity", "CreateEntities", "Instantiate"];

    private static void InitializeUsage(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (Core.Create(start.Compilation) is not { } core) return;
            start.RegisterOperationBlockAction(c => AnalyzeJob(c, core));
            start.RegisterOperationAction(c => AnalyzeUsageCall(c, core), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeCopy(c), OperationKind.VariableDeclarator);
            start.RegisterSymbolAction(AnalyzeRecordKey, SymbolKind.NamedType);
            start.RegisterSymbolAction(AnalyzeFilters, SymbolKind.Method);
            start.RegisterOperationAction(c => AnalyzeRefAcrossChange(c, core), OperationKind.VariableDeclarator);
            start.RegisterOperationAction(c => AnalyzeItemAcrossChange(c, core), OperationKind.Loop);
        });

    // ---------------- EECS037: a hand-written job's Writes ----------------

    private static void AnalyzeJob(OperationBlockAnalysisContext context, Core core)
    {
        if (context.OwningSymbol is not IMethodSymbol method || Symbols.JobInterface(method) is not { } job) return;
        var body = context.OperationBlocks.FirstOrDefault(b => b is IMethodBodyOperation or IBlockOperation);
        if (body == null) return;
        var type = method.ContainingType;
        int n = job.TypeArguments.Length, offset = job.Name == "IForEach" ? 0 : 1, all = (1 << n) - 1;
        if (method.Parameters.Length != n + offset) return;
        if (Declared(job, type, "Writes", -1, out var writesSyntax) is not int declared
            || Declared(job, type, "OwnStamps", 0, out _) is not int own) return;

        int written = 0;
        bool structural = false;
        foreach (var operation in body.Descendants())
        {
            if (operation is IParameterReferenceOperation reference && reference.Parameter.Ordinal >= offset
                && SymbolEqualityComparer.Default.Equals(reference.Parameter.ContainingSymbol, method) && Uses.MayWrite(reference))
                written |= 1 << (reference.Parameter.Ordinal - offset);
            else if (operation is IInvocationOperation call && Symbols.IsStructural(call.TargetMethod)) structural = true;
        }

        var problems = new List<string>();
        for (int k = 0; k < n; k++)
        {
            int bit = 1 << k;
            var component = job.TypeArguments[k];
            if (component is ITypeParameterSymbol) continue;
            bool tracked = core.Tracked(component);
            string name = method.Parameters[k + offset].Name;
            if ((written & bit) != 0 && (declared & bit) == 0 && (tracked || structural))
                problems.Add($"writes '{name}' but its Writes leaves it out, so {(tracked ? $"the write is not stamped and [Changed<{component.Name}>] systems miss it" : "a structural change in Execute does not carry the write over to the entity's new row")}");
            else if ((written & bit) == 0 && (declared & ~own & bit) != 0 && tracked)
                problems.Add($"only reads '{name}' but its Writes names it, so every row it visits is stamped as changed");
        }

        if (problems.Count == 0) return;
        int needed = (written | (own & declared)) & all;
        string value = needed == 0 ? "0" : "0b" + System.Convert.ToString(needed, 2);
        var location = writesSyntax?.GetLocation()
                       ?? type.DeclaringSyntaxReferences.Select(r => r.GetSyntax(context.CancellationToken)).OfType<TypeDeclarationSyntax>()
                           .FirstOrDefault(t => t.SyntaxTree == body.Syntax.SyntaxTree)?.Identifier.GetLocation()
                       ?? method.Locations.FirstOrDefault();
        context.ReportDiagnostic(Diagnostic.Create(JobWrites, location,
            ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Writes").Add(WritesValue, value),
            type.Name, string.Join("; it ", problems), value));
    }

    /// <summary>
    /// The constant a job's static property <paramref name="name"/> (Writes, OwnStamps) returns: <paramref name="fallback"/>
    /// when the job keeps the interface's default, null when it is not a constant. <paramref name="syntax"/> is its declaration.
    /// </summary>
    private static int? Declared(INamedTypeSymbol job, INamedTypeSymbol type, string name, int fallback, out PropertyDeclarationSyntax? syntax)
    {
        syntax = null;
        if (job.GetMembers(name).FirstOrDefault() is not IPropertySymbol member
            || type.FindImplementationForInterfaceMember(member) is not IPropertySymbol implementation
            || implementation.ContainingType.TypeKind == TypeKind.Interface) return fallback;
        if (implementation.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not PropertyDeclarationSyntax declaration) return null;
        syntax = declaration;
        return Symbols.Returned(declaration) switch
        {
            LiteralExpressionSyntax { Token.Value: int value } => value,
            PrefixUnaryExpressionSyntax { OperatorToken.ValueText: "-", Operand: LiteralExpressionSyntax { Token.Value: int value } } => -value,
            _ => null, // computed: a constant the analyzer would have to evaluate, left alone
        };
    }

    // ---------------- EECS038: a changed copy that is dropped ----------------

    /// <summary>EECS038 for <c>var x = &lt;ref accessor&gt;; x.Field = ...;</c> with no read of <c>x</c> after the last write.</summary>
    private static void AnalyzeCopy(OperationAnalysisContext context)
    {
        var declarator = (IVariableDeclaratorOperation)context.Operation;
        if (declarator.Initializer?.Value is not { } value || !IsDroppedCopy(value)) return;
        var accessor = Accessor(value)!.Value;
        string advice = "keep the ref";
        ImmutableDictionary<string, string?>? fix = null;
        if (declarator.Syntax is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Type: var type, Parent: LocalDeclarationStatementSyntax { UsingKeyword.RawKind: 0, IsConst: false } statement } })
        {
            string? writable = accessor.Writable ? value.Syntax.ToString() : accessor.Writer;
            if (writable != null)
            {
                string text = $"ref {type} {declarator.Symbol.Name} = ref {writable};";
                advice = $"keep the ref: '{text}'";
                if (Binds(value.SemanticModel!, value.Syntax, writable, out _))
                    fix = Fix(text, "Keep the ref");
            }
            else advice = accessor.Advice;
            context.ReportDiagnostic(Diagnostic.Create(DroppedCopy, statement.GetLocation(), fix, declarator.Symbol.Name, accessor.Name, advice));
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(DroppedCopy, declarator.Syntax.GetLocation(), declarator.Symbol.Name, accessor.Name,
            accessor.Writer != null || accessor.Writable ? advice : accessor.Advice));
    }

    /// <summary>
    /// Whether <paramref name="value"/> initializes a by-value local that EECS038 reports: the copy of a ref accessor's
    /// value, changed and dropped. The rules about the accessor itself (EECS015's Get of an indexed component, EECS025's
    /// Get or WriteX that only reads) defer to it: the mistake is the copy.
    /// </summary>
    internal static bool IsDroppedCopy(IOperation value) =>
        value.Parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: RefKind.None } declarator }
        && Accessor(value) != null && DeadAfterWrite(declarator.Symbol, declarator);

    /// <summary>
    /// A ref-returning EasyEcs accessor: its name, whether its ref writes (<c>ref</c>, not <c>ref readonly</c>), the
    /// writer to use instead of a read-only one (<c>e.WriteHealth()</c>, <c>m.Write()</c>, <c>e.Get&lt;T&gt;()</c>), and
    /// the advice when there is none.
    /// </summary>
    private static (string Name, bool Writable, string? Writer, string Advice)? Accessor(IOperation value)
    {
        const string set = "write the copy back with Set (the component is indexed or grouped)";
        switch (value)
        {
            case IInvocationOperation { TargetMethod: { } method } call when method.ReturnsByRef || method.ReturnsByRefReadonly:
            {
                string? type = method.ContainingType?.ToDisplayString();
                if (type is not (Symbols.Entity or Symbols.World) && !Symbols.IsGenerated(method)) return null;
                string name = (method.ContainingType!.IsExtension ? "" : method.ContainingType.Name + ".") + method.Name;
                if (method.ReturnsByRef) return (name, !Symbols.IsIndexed(method.ReturnType), null, set);
                // Read<T>() of a component that is not indexed: Get<T>() is its writer.
                string? writer = type == Symbols.Entity && method is { Name: "Read", TypeArguments.Length: 1 } && !Symbols.IsIndexed(method.TypeArguments[0])
                                 && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
                    ? access.Expression + "." + access.Name.ToString().Replace("Read<", "Get<") + "()" : null;
                return (name, false, writer, set);
            }
            case IPropertyReferenceOperation { Property: { IsIndexer: false } property } reference when property.ReturnsByRef || property.ReturnsByRefReadonly:
            {
                bool core = Symbols.IsItemComponent(property)
                            || property.ContainingNamespace?.ToDisplayString() == "EasyEcs" && property.Name == "Value" && property.ContainingType.Name is "Maybe" or "Mut" or "SingletonRef";
                if (!core && !Symbols.IsGenerated(property)) return null;
                string name = (core ? property.ContainingType.Name + "." : "") + property.Name;
                if (property.ReturnsByRef) return (name, !Symbols.IsIndexed(property.Type), null, set);
                string? writer = null;
                if (reference.Syntax is MemberAccessExpressionSyntax access)
                    writer = property.ContainingType.Name == "Mut" ? access.Expression + ".Write()"
                        : Symbols.IsGenerated(property) && !Symbols.IsIndexed(property.Type) ? access.Expression + ".Write" + property.Name + "()" : null;
                return (name, false, writer, set);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="local"/> has a field written (an assignment, <c>++</c> / <c>--</c> through a chain of fields
    /// or settable properties, a call of a void method that is not <c>readonly</c>) and no read after the last such write: not
    /// after it in the code, not in a loop around it, not in a lambda or local function (which could run later). The
    /// code read is the body the local is declared in: a method's, a lambda's or a local function's.
    /// </summary>
    private static bool DeadAfterWrite(ILocalSymbol local, IOperation declaration)
    {
        IOperation root = declaration;
        while (root.Parent != null && root is not (IAnonymousFunctionOperation or ILocalFunctionOperation)) root = root.Parent;
        var writes = new List<IOperation>();
        var reads = new List<IOperation>();
        foreach (var operation in root.Descendants())
        {
            if (operation is not ILocalReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Local, local)) continue;
            for (var p = reference.Parent; p != null && p != root; p = p.Parent)
                if (p is IAnonymousFunctionOperation or ILocalFunctionOperation) return false;
            if (FieldWrite(reference) is { } write) writes.Add(write);
            else reads.Add(reference);
        }

        if (writes.Count == 0) return false;
        var last = writes.OrderBy(w => w.Syntax.SpanStart).Last();
        foreach (var read in reads)
        {
            if (writes.Any(w => w.Syntax.Span.Contains(read.Syntax.Span))) continue; // hp.V = hp.V - 1: part of the write
            if (read.Syntax.SpanStart > last.Syntax.SpanStart) return false;
            for (var p = last.Parent; p != null; p = p.Parent)
                if (p is ILoopOperation loop && loop.Syntax.Span.Contains(read.Syntax.Span) && !loop.Syntax.Span.Contains(declaration.Syntax.Span))
                    return false; // read again on the next turn
        }

        return true;
    }

    /// <summary>The assignment or increment that writes a field of the local <paramref name="reference"/> starts, or null.</summary>
    private static IOperation? FieldWrite(IOperation reference)
    {
        var current = reference;
        while (true)
        {
            switch (current.Parent)
            {
                case IFieldReferenceOperation field when field.Instance == current:
                    current = field;
                    continue;
                case IPropertyReferenceOperation { Property: { IsIndexer: false, SetMethod: { IsInitOnly: false } } } property
                    when property.Instance == current && current.Type is { IsValueType: true }:
                    current = property;
                    continue;
            }

            // A void method that may change its struct (not readonly, of a struct that is not readonly) changes the copy;
            // one that returns a value is taken as a read (ToString of a struct that does not mark it readonly).
            if (current.Parent is IInvocationOperation { TargetMethod: { IsStatic: false, IsReadOnly: false, ReturnsVoid: true } method } call && call.Instance == current
                && current.Type is { IsValueType: true, IsReadOnly: false } && method.ContainingType?.TypeKind == TypeKind.Struct)
                return call;
            if (current == reference) return null; // the local itself: an assignment of a new value is a use
            return current.Parent switch
            {
                IAssignmentOperation assignment when assignment.Target == current => assignment,
                IIncrementOrDecrementOperation step when step.Target == current => step,
                _ => null,
            };
        }
    }

    // ---------------- calls: EECS038 (TryGet), EECS040-EECS042, EECS044, EECS045, EECS048, EECS050, EECS051 ----------------

    private static void AnalyzeUsageCall(OperationAnalysisContext context, Core core)
    {
        var call = (IInvocationOperation)context.Operation;
        var method = call.TargetMethod;
        string? owner = method.ContainingType?.ToDisplayString();

        // EECS040: SetIfChanged of a value it cannot compare.
        if (method.Name == "SetIfChanged" && (owner == Symbols.Entity && method.TypeArguments.Length == 1 ? method.TypeArguments[0]
                : method.ContainingType is { Name: "Mut", TypeArguments.Length: 1 } mut && mut.ContainingNamespace?.ToDisplayString() == "EasyEcs" ? mut.TypeArguments[0]
                : null) is { } compared && !Symbols.IsComparable(compared))
            context.ReportDiagnostic(Diagnostic.Create(NotComparable, call.Syntax.GetLocation(),
                Shorten(call.Syntax), compared.Name));

        // EECS045: the world's own tags.
        if ((owner == Symbols.Entity && EntityTagWriters.Contains(method.Name) || owner == Symbols.World && WorldTagWriters.Contains(method.Name)
             || owner == "EasyEcs.Prefab" && method.Name is "Set" or "Remove")
            && method.TypeArguments.FirstOrDefault(Symbols.IsWorldTag) is { } tag)
            context.ReportDiagnostic(Diagnostic.Create(InternalTag, call.Syntax.GetLocation(),
                method.ContainingType!.Name + "." + method.Name, tag.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                tag.Name == "Destroyed"
                    ? "Destroy adds it while cleanup components remain, and removing the last of them frees the entity; destroy the entity and remove its cleanup components"
                    : $"Disable<{((INamedTypeSymbol)tag).TypeArguments[0].Name}>() adds it and hides the component, Enable<{((INamedTypeSymbol)tag).TypeArguments[0].Name}>() gives it back; call those"));

        // EECS048: a component Disable refuses.
        var hidden = (owner == Symbols.Entity && method.Name == "Disable" || owner == Symbols.World && method.Name == "DisableAll") && method.TypeArguments.Length == 1
            ? method.TypeArguments[0]
            : Symbols.IsGenerated(method) && Symbols.IsGeneratedWriter(method) && method.Name.StartsWith("Disable") ? Named(method, core) : null;
        if (hidden != null && !Symbols.IsDisableable(hidden))
            context.ReportDiagnostic(Diagnostic.Create(NotDisableable, call.Syntax.GetLocation(), Shorten(call.Syntax),
                hidden.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Symbols.Implements(hidden, Symbols.Cleanup)
                    ? "a cleanup component, which keeps a destroyed entity until its resources are released"
                    : "which a parent's destruction follows to find its children"));

        // EECS050: a second group in one filter.
        if (GroupOfCall(call, core) is { } second && Symbols.Receiver(call) is { } chain)
            for (var earlier = Symbols.Unwrap(chain); earlier is IInvocationOperation previous; earlier = Symbols.Unwrap(Symbols.Receiver(previous)))
            {
                if (previous.Type?.ToDisplayString() != "EasyEcs.QueryFilter") break;
                if (GroupOfCall(previous, core) is not { } first) continue;
                bool differs = !SymbolEqualityComparer.Default.Equals(first.Type, second.Type)
                               || first.Value.ConstantValue is { HasValue: true } a && second.Value.ConstantValue is { HasValue: true } b && !Equals(a.Value, b.Value);
                if (differs)
                    context.ReportDiagnostic(Diagnostic.Create(SecondGroup, call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
                            ? Location.Create(call.Syntax.SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(access.Name.SpanStart, call.Syntax.Span.End))
                            : call.Syntax.GetLocation(),
                        GroupText(call), GroupText(previous)));
                break;
            }

        // EECS051: an update during a pass of the same world (from a system, EECS034 says it).
        if (owner == Symbols.World && method.Name == "Update" && Symbols.SystemOf(context.ContainingSymbol) == null
            && DuringPass(call, context.ContainingSymbol, core) is { } running)
            context.ReportDiagnostic(Diagnostic.Create(UpdateInPass, call.Syntax.GetLocation(), Shorten(call.Syntax), running));

        // EECS038: TryGet's copy changed and dropped.
        if (method.Name.StartsWith("TryGet") && method.Name != "TryGetRef" && (owner is Symbols.Entity or Symbols.World || Symbols.IsGenerated(method)))
            foreach (var argument in call.Arguments)
                if (argument.Parameter?.RefKind == RefKind.Out && argument.Value is IDeclarationExpressionOperation { Expression: ILocalReferenceOperation { Local: var local } }
                    && local.Type.IsValueType && local.Type.ToDisplayString() != Symbols.Entity && DeadAfterWrite(local, argument))
                    context.ReportDiagnostic(Diagnostic.Create(DroppedCopy, argument.Syntax.GetLocation(), local.Name,
                        (method.ContainingType!.IsExtension ? "" : method.ContainingType.Name + ".") + method.Name,
                        "write through a ref instead (Get after Has, or TryGetRef), or write the copy back with Set"));

        // EECS044: a sliced filter where every row is needed, and a constant slice out of range.
        if (owner == "EasyEcs.QueryFilter" && method.Name == "Slice" && call.Arguments.Length == 2
            && call.Arguments[0].Value.ConstantValue is { HasValue: true, Value: int index } && call.Arguments[1].Value.ConstantValue is { HasValue: true, Value: int count }
            && (count < 1 || index < 0 || index >= count))
            context.ReportDiagnostic(Diagnostic.Create(SlicedFilter, call.Syntax.GetLocation(),
                $"Slice({index}, {count}) throws ArgumentOutOfRangeException: the count is 1 or more and the index in [0, count)"));
        if (owner == Symbols.World && WholeRowCalls.Contains(method.Name) || owner == Symbols.Entity && method.Name == "Run")
            foreach (var argument in call.Arguments)
                if (argument.Parameter?.Type.ToDisplayString() == "EasyEcs.QueryFilter" && !argument.IsImplicit && Sliced(argument.Value, 0))
                    context.ReportDiagnostic(Diagnostic.Create(SlicedFilter, argument.Syntax.GetLocation(),
                        $"'{method.ContainingType!.Name}.{method.Name}' needs every matching row and throws ArgumentException for a sliced filter (QueryFilter.Slice); pass the filter without its slice"));

        // EECS041: a call a pass refuses, during one.
        if (owner == Symbols.World && PassRefused.Contains(method.Name) && DuringPass(call, context.ContainingSymbol, core) is { } pass)
            context.ReportDiagnostic(Diagnostic.Create(RefusedInPass, call.Syntax.GetLocation(), "World." + method.Name, pass,
                "record what to do as data, and make the call after the pass"));

        // EECS042: the indexed component changed during a walk of its index.
        if (IndexChange(call, core) is { } changed)
            for (var node = call.Parent; node != null; node = node.Parent)
            {
                if (node is IAnonymousFunctionOperation or ILocalFunctionOperation) break;
                if (node is not IForEachLoopOperation loop || !loop.Body.Syntax.Span.Contains(call.Syntax.Span)
                    || Walked(loop.Collection) is not { } walked || !SymbolEqualityComparer.Default.Equals(walked, changed)) continue;
                if (LeavesAfter(call, loop)) break; // the walk ends before it reads another list
                context.ReportDiagnostic(Diagnostic.Create(IndexChangedInWalk, call.Syntax.GetLocation(), Shorten(call.Syntax),
                    changed.Name, Shorten(loop.Collection.Syntax)));
                break;
            }
    }

    /// <summary>
    /// Whether a filter value is sliced: a <c>Slice(i, n)</c> (n not the constant 1) in its chain of QueryFilter calls,
    /// or a local or readonly field whose initializer is one (a local assigned nowhere else).
    /// </summary>
    private static bool Sliced(IOperation? value, int depth)
    {
        while (true)
        {
            if (depth > 8) return false;
            switch (value)
            {
                case IConversionOperation conversion:
                    value = conversion.Operand;
                    continue;
                case IInvocationOperation call when call.TargetMethod.ContainingType?.ToDisplayString() == "EasyEcs.QueryFilter"
                                                    || call.Type?.ToDisplayString() == "EasyEcs.QueryFilter" && Symbols.Receiver(call)?.Type?.ToDisplayString() == "EasyEcs.QueryFilter":
                    if (call.TargetMethod.Name == "Slice" && call.Arguments.Length == 2
                        && call.Arguments[1].Value.ConstantValue is not { HasValue: true, Value: 1 }) return true;
                    value = Symbols.Receiver(call);
                    depth++;
                    continue;
                case ILocalReferenceOperation { Local: var local } reference:
                    IOperation? initial = null;
                    foreach (var operation in Symbols.Root(reference).Descendants())
                        switch (operation)
                        {
                            case IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local):
                                initial = declarator.Initializer?.Value;
                                break;
                            case IAssignmentOperation { Target: ILocalReferenceOperation target } when SymbolEqualityComparer.Default.Equals(target.Local, local):
                            case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out, Value: ILocalReferenceOperation passed }
                                when SymbolEqualityComparer.Default.Equals(passed.Local, local):
                                return false;
                        }

                    value = initial;
                    depth++;
                    continue;
                case IFieldReferenceOperation { Field: { IsReadOnly: true } field } reference:
                    if (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                        || initializer.SyntaxTree != reference.Syntax.SyntaxTree) return false;
                    value = reference.SemanticModel!.GetOperation(initializer);
                    depth++;
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The pass a call to <paramref name="call"/>'s world runs in, or null: a foreach over the same world's Query, a
    /// [ForEach] method (its World, its system's World, its Entity's World), a job's Execute (its Entity's World, a
    /// World field of the job).
    /// </summary>
    private static string? DuringPass(IInvocationOperation call, ISymbol containing, Core core)
    {
        var receiver = Symbols.Unwrap(call.Instance);
        if (receiver == null) return null;
        var worlds = new List<IOperation> { receiver };
        if (call.TargetMethod.Name == "MoveEntities" && call.Arguments.Length > 0) worlds.Add(Symbols.Unwrap(call.Arguments[0].Value)!);

        for (var node = call.Parent; node != null; node = node.Parent)
        {
            if (node is IAnonymousFunctionOperation or ILocalFunctionOperation) return null;
            if (node is IForEachLoopOperation loop && loop.Body.Syntax.Span.Contains(call.Syntax.Span) && Query(loop.Collection, core) is { } query
                && worlds.Any(w => w.Syntax.ToString() == query.Instance!.Syntax.ToString()))
                return $"this foreach over {query.TargetMethod.Name}";
        }

        if (containing is not IMethodSymbol method) return null;
        bool forEach = Symbols.IsForEach(method), job = !forEach && Symbols.JobInterface(method) != null;
        if (!forEach && !job) return null;
        foreach (var world in worlds)
        {
            bool visited = world switch
            {
                IParameterReferenceOperation parameter => forEach && Symbols.Classify(parameter.Parameter) == Symbols.ParamKind.World,
                IPropertyReferenceOperation { Property.Name: "World", Instance: IParameterReferenceOperation entity } =>
                    core.Is(entity.Type, core.Entity) && SymbolEqualityComparer.Default.Equals(entity.Parameter.ContainingSymbol, method),
                IPropertyReferenceOperation { Property.Name: "World", Instance: IInstanceReferenceOperation } => forEach && !method.IsStatic,
                IFieldReferenceOperation { Instance: IInstanceReferenceOperation } or IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } => job,
                _ => false,
            };
            if (visited) return forEach ? $"[ForEach] method '{method.Name}'" : $"job '{method.ContainingType.Name}'";
        }

        return null;
    }

    /// <summary>
    /// The indexed component a call adds to a key (Set, SetIfChanged, GetOrAdd, Enable, the generated SetX / GetOrAddX /
    /// EnableX, SetAll, EnableAll, a creation with the component), or the relation whose index it adds a link to
    /// (AddRelation, a Set of its DynamicBuffer, the generated AddX), or null.
    /// </summary>
    private static ITypeSymbol? IndexChange(IInvocationOperation call, Core core)
    {
        var method = call.TargetMethod;
        if (core.Is(method.ContainingType, core.Entity) && method is { Name: "AddRelation", TypeArguments.Length: 1 })
            return method.TypeArguments[0];
        if (core.Is(method.ContainingType, core.Entity) && IndexChangers.Contains(method.Name)
            || core.Is(method.ContainingType, core.World) && WorldIndexChangers.Contains(method.Name))
            return method.TypeArguments.Select(Indexing).FirstOrDefault(t => t != null);
        if (Symbols.IsGenerated(method) && Symbols.IsGeneratedWriter(method)
            && (method.Name.StartsWith("Set") || method.Name.StartsWith("GetOrAdd") || method.Name.StartsWith("Enable") || method.Name.StartsWith("Add"))
            && Named(method, core) is { } named && Indexing(named) is { } indexed)
            return indexed;
        return null;
    }

    /// <summary>
    /// The type whose index a write of <paramref name="type"/> changes: the component itself when it is indexed, the
    /// element of a <c>DynamicBuffer</c> of relations; else null.
    /// </summary>
    private static ITypeSymbol? Indexing(ITypeSymbol type) =>
        type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == Symbols.Indexed) ? type
        : Symbols.BufferElement(type) is { } element && Symbols.Implements(element, "EasyEcs.IRelation") ? element
        : null;

    /// <summary>
    /// The component whose index a foreach walks, or null: an index's <c>Matches</c> or <c>Ordered</c> (<c>Find</c>,
    /// <c>Children</c>, <c>Range</c>, ...), <c>Descendants</c> (the <c>ChildOf</c> lists), a relation index's
    /// <c>Sources</c> (<c>Find(target)</c>: the relation).
    /// </summary>
    private static ITypeSymbol? Walked(IOperation collection)
    {
        if (Symbols.Unwrap(collection) is not IInvocationOperation { Type: INamedTypeSymbol { ContainingType: { } owner } walk } call) return null;
        string definition = owner.OriginalDefinition.ToDisplayString();
        return walk.Name switch
        {
            "Matches" or "Ordered" when definition == "EasyEcs.ValueIndex<T, TKey>" => owner.TypeArguments[0],
            "Sources" when definition == "EasyEcs.RelationIndex<T>" => owner.TypeArguments[0],
            "Subtree" when definition == Symbols.World => call.SemanticModel?.Compilation.GetTypeByMetadataName("EasyEcs.ChildOf"),
            _ => null,
        };
    }

    /// <summary>
    /// Whether the walk of <paramref name="loop"/> ends right after <paramref name="call"/>: a later statement of a
    /// block around the call (inside the loop) returns, throws or breaks out of the loop, so no other list is read.
    /// </summary>
    private static bool LeavesAfter(IOperation call, IForEachLoopOperation loop)
    {
        for (IOperation current = call, parent = call.Parent!; parent != null && current != loop.Body; current = parent, parent = parent.Parent!)
        {
            if (parent is not IBlockOperation block) continue;
            bool after = false;
            foreach (var statement in block.Operations)
            {
                if (statement == current)
                {
                    after = true;
                    continue;
                }

                if (after && statement is IReturnOperation or IExpressionStatementOperation { Operation: IThrowOperation } or IThrowOperation
                        || after && statement is IBranchOperation { BranchKind: BranchKind.Break } exit && SymbolEqualityComparer.Default.Equals(exit.Target, loop.ExitLabel))
                    return true;
            }
        }

        return false;
    }

    // ---------------- EECS043: a record struct key that prints itself ----------------

    private static void AnalyzeRecordKey(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type is not { TypeKind: TypeKind.Struct, IsRecord: true }) return;
        // A ToString() or PrintMembers(StringBuilder) of its own decides what the record prints: Key is not printed then.
        if (type.GetMembers().Any(m => m is IMethodSymbol { IsImplicitlyDeclared: false, IsStatic: false } own
                && (own.Name == "ToString" && own.Parameters.Length == 0
                    || own.Name == "PrintMembers" && own.Parameters.Length == 1 && own.Parameters[0].Type.ToDisplayString() == "System.Text.StringBuilder")))
            return;
        foreach (var face in type.AllInterfaces)
        {
            if (face.OriginalDefinition.ToDisplayString() != Symbols.Indexed || !SymbolEqualityComparer.Default.Equals(face.TypeArguments[0], type)
                || face.GetMembers("Key").FirstOrDefault() is not { } key
                || type.FindImplementationForInterfaceMember(key) is not IPropertySymbol { ExplicitInterfaceImplementations.Length: 0, IsStatic: false } property
                || property.DeclaredAccessibility != Accessibility.Public || !SymbolEqualityComparer.Default.Equals(property.ContainingType, type))
                continue;
            string self = type.Name;
            context.ReportDiagnostic(Diagnostic.Create(KeyPrintsItself, property.Locations.FirstOrDefault(), self, $"{self} IIndexed<{self}>.Key => this;"));
        }
    }

    // ---------------- EECS050: helpers ----------------

    /// <summary>The group a QueryFilter call names (<c>InGroup&lt;T&gt;(group)</c>, the generated <c>InX(group)</c>): the component and the group value.</summary>
    private static (ITypeSymbol Type, IOperation Value)? GroupOfCall(IInvocationOperation call, Core core)
    {
        var method = call.TargetMethod;
        if (method.ReturnType.ToDisplayString() != "EasyEcs.QueryFilter" || call.Arguments.Length == 0) return null;
        var value = call.Arguments[call.Arguments.Length - 1].Value;
        if (method is { Name: "InGroup", TypeArguments.Length: 1 } && method.ContainingType?.ToDisplayString() == "EasyEcs.QueryFilter")
            return (method.TypeArguments[0], value);
        if (Symbols.IsGenerated(method) && method.Name.StartsWith("In") && method.Name.Length > 2 && Named(method, core) is { } named
            && Symbols.Implements(named, Symbols.Group))
            return (named, value);
        return null;
    }

    /// <summary>The group call as written, from its name on: <c>InGroup&lt;Zone&gt;(1)</c>.</summary>
    private static string GroupText(IInvocationOperation call) =>
        call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } syntax
            ? access.Name + syntax.ArgumentList.ToString() : Shorten(call.Syntax);

    // ---------------- EECS016, EECS049: filters no entity passes ----------------

    /// <summary>
    /// EECS016 for a component a [Removed&lt;T&gt;] method requires, EECS049 for one a method both requires and excludes
    /// with [Without&lt;T&gt;]: one list of what the method's entities must have and lack (<see cref="Symbols.Requirements"/>).
    /// </summary>
    private static void AnalyzeFilters(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!Symbols.IsForEach(method)) return;
        var all = Symbols.Requirements(method, context.CancellationToken);
        var location = method.Locations.FirstOrDefault();
        foreach (var with in all)
            if (with.Has && all.Any(r => r.Kind == Symbols.FilterKind.Removed && SymbolEqualityComparer.Default.Equals(r.Type, with.Type)))
                context.ReportDiagnostic(Diagnostic.Create(RemovedRequired, with.Parameter ? with.Where : location, method.Name, with.Type.Name,
                    with.Why + " requires it"));
        foreach (var without in all)
        {
            if (without.Kind != Symbols.FilterKind.Without
                || all.FirstOrDefault(r => r.Has && SymbolEqualityComparer.Default.Equals(r.Type, without.Type)) is not { } with) continue;
            context.ReportDiagnostic(Diagnostic.Create(NeverMatches, without.Where ?? with.Where ?? location,
                method.Name, with.Type.Name, with.Why, without.Why));
        }
    }

    // ---------------- EECS047: a ref used after a structural change ----------------

    /// <summary>The structural members that leave every row where it is: they copy, schedule or touch no table.</summary>
    private static readonly HashSet<string> RowsStay =
    [
        "Capture", "Save", "SaveWithout", "SaveEntities", "SaveEntitiesWith", "AddSingleton", "RemoveSingleton", "AddSystem",
        "RemoveSystem", "EnsureEntityCapacity", "Run",
    ];

    /// <summary>
    /// Whether a structural call leaves rows where they are: <see cref="RowsStay"/>, and the removals and enables, which
    /// flip a presence bit of the row (Remove, RemoveAll, Enable, EnableAll, RemoveRelation, the generated RemoveX,
    /// EnableX, UnlinkX). RemoveTypes drops tables.
    /// </summary>
    private static bool KeepsRows(IMethodSymbol method) =>
        RowsStay.Contains(method.Name) || method.Name != "RemoveTypes"
        && (method.Name.StartsWith("Remove") || method.Name.StartsWith("Enable") || method.Name.StartsWith("Unlink"));

    /// <summary>EECS047 for a ref local: <c>ref var p = ref e.Get&lt;P&gt;(); e.Set(new Q()); p.V = 1;</c>.</summary>
    private static void AnalyzeRefAcrossChange(OperationAnalysisContext context, Core core)
    {
        var declarator = (IVariableDeclaratorOperation)context.Operation;
        if (declarator.Symbol.RefKind == RefKind.None || declarator.Initializer?.Value is not { } value
            || RefSource(value, core) is not { } source
            || declarator.Parent?.Parent is not IVariableDeclarationGroupOperation { Parent: IBlockOperation block } statement) return;
        var local = declarator.Symbol;
        int at = block.Operations.IndexOf(statement);
        IInvocationOperation? change = null;
        for (int k = at + 1; k < block.Operations.Length; k++)
        {
            var current = block.Operations[k];
            if (change == null)
            {
                change = Change(current, source, core);
                continue;
            }

            foreach (var operation in current.DescendantsAndSelf())
            {
                if (operation is ISimpleAssignmentOperation { IsRef: true, Target: ILocalReferenceOperation target }
                    && SymbolEqualityComparer.Default.Equals(target.Local, local)) return; // taken again
                if (operation is not ILocalReferenceOperation use || !SymbolEqualityComparer.Default.Equals(use.Local, local)) continue;
                context.ReportDiagnostic(Diagnostic.Create(RefAcrossChange, use.Syntax.GetLocation(), local.Name,
                    $"is a ref from '{Shorten(source.Syntax)}' taken", Shorten(change.Syntax), "take it again after the change"));
                return;
            }
        }
    }

    /// <summary>EECS047 for a query item: <c>item.Entity.Set(new Q()); item.Component1.V = 1;</c> in one iteration.</summary>
    private static void AnalyzeItemAcrossChange(OperationAnalysisContext context, Core core)
    {
        if (context.Operation is not IForEachLoopOperation loop || Query(loop.Collection, core) is null
            || (loop.LoopControlVariable as IVariableDeclaratorOperation)?.Symbol is not { } item) return;
        foreach (var block in loop.Body.DescendantsAndSelf().OfType<IBlockOperation>())
        {
            if (Inside<IAnonymousFunctionOperation>(block, loop) || Inside<ILocalFunctionOperation>(block, loop)) continue;
            IInvocationOperation? change = null;
            foreach (var statement in block.Operations)
            {
                if (change == null)
                {
                    change = statement.DescendantsAndSelf().OfType<IInvocationOperation>().FirstOrDefault(c =>
                        Symbols.IsStructural(c.TargetMethod) && !KeepsRows(c.TargetMethod) && c.TargetMethod.Name != "Destroy"
                        && IsItemEntity(Symbols.Receiver(c), item) && Symbols.AlwaysRuns(c, statement));
                    continue;
                }

                foreach (var use in statement.DescendantsAndSelf().OfType<IPropertyReferenceOperation>())
                    if (use.Instance is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, item)
                        && use.Property.Name != "Entity" && (use.Property.ReturnsByRef || use.Property.ReturnsByRefReadonly))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(RefAcrossChange, use.Syntax.GetLocation(), use.Syntax.ToString(),
                            "is a ref the query item took", Shorten(change.Syntax), "read it through the entity after the change"));
                        return;
                    }
            }
        }
    }

    private static bool Inside<T>(IOperation operation, IOperation stop) where T : IOperation
    {
        for (var node = operation.Parent; node != null && node != stop; node = node.Parent)
            if (node is T) return true;
        return false;
    }

    /// <summary>
    /// The accessor a ref local is bound to, through fields (<c>ref int v = ref e.Get&lt;P&gt;().V</c>): Entity's ref
    /// members (Get, Read, GetOrAdd, TryGetRef), the generated members of an entity or a query item, a query item's
    /// ComponentN; null for other refs (singletons stay where they are).
    /// </summary>
    private static IOperation? RefSource(IOperation value, Core core)
    {
        while (value is IFieldReferenceOperation { Instance: { } owner }) value = owner;
        switch (value)
        {
            case IInvocationOperation call when call.TargetMethod.ReturnsByRef || call.TargetMethod.ReturnsByRefReadonly:
                return core.Is(call.TargetMethod.ContainingType, core.Entity)
                       || Symbols.IsGenerated(call.TargetMethod) && core.Is(Symbols.Receiver(call)?.Type, core.Entity) ? call : null;
            case IPropertyReferenceOperation { Property: { IsIndexer: false } property } reference when property.ReturnsByRef || property.ReturnsByRefReadonly:
                if (Symbols.IsItem(reference.Instance?.Type))
                    return property.Name.StartsWith("Component") || Symbols.IsGenerated(property) ? reference : null;
                return Symbols.IsGenerated(property) && core.Is(reference.Instance?.Type, core.Entity) ? reference : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The structural change that <paramref name="statement"/> always makes (no branch, loop or lambda around it) that
    /// can move the row <paramref name="source"/> points into, or null. A Set of the same component on the same entity
    /// overwrites it in place; a destroy during a query loop leaves the item's row where it is.
    /// </summary>
    private static IInvocationOperation? Change(IOperation statement, IOperation source, Core core)
    {
        var sourceReceiver = source is IInvocationOperation call ? Symbols.Receiver(call) : ((IPropertyReferenceOperation)source).Instance;
        bool item = Symbols.IsItem(sourceReceiver?.Type);
        var held = source.Type;
        foreach (var candidate in statement.DescendantsAndSelf().OfType<IInvocationOperation>())
        {
            var method = candidate.TargetMethod;
            if (!Symbols.IsStructural(method) || KeepsRows(method) || OwnWorld(candidate) || !Symbols.AlwaysRuns(candidate, statement)) continue;
            if (item && method.Name == "Destroy") continue;
            var receiver = Symbols.Receiver(candidate);
            if (!core.Is(receiver?.Type, core.Entity) && !core.Is(receiver?.Type, core.World)) continue;
            // e.Set(new P()) after a ref to e's P: the entity has P, so the value is overwritten where it is.
            bool overwrite = (method.Name.StartsWith("Set") || method.Name.StartsWith("GetOrAdd")) && !method.Name.StartsWith("SetAll")
                             && ComponentOf(method, core) is { } written && SymbolEqualityComparer.Default.Equals(written, held)
                             && sourceReceiver != null && receiver!.Syntax.ToString() == sourceReceiver.Syntax + (item ? ".Entity" : "");
            if (!overwrite) return candidate;
        }

        return null;
    }
}
