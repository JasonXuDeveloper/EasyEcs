using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules for calls and declarations that compile but do not do what they say (EECS037, EECS038, EECS040-EECS045):
/// a job's <c>Writes</c> that disagrees with its <c>Execute</c>, a changed copy of a component that is dropped,
/// <c>SetIfChanged</c> on a type it cannot compare, calls a pass refuses made during one, an indexed component changed
/// while its index is walked, a record struct key that prints itself, a sliced filter where every row is needed, and
/// the world's own tags written by hand. Each is a call or a declaration the runtime throws for, or a write it loses.
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
        "'{0}' changes '{1}' while this foreach walks its index ('{2}'): collect the entities first, then change them",
        Category, DiagnosticSeverity.Error, true,
        "A foreach over an index (Find, Children, Range, Ascending, Descending) reads the index's lists as it goes. " +
        "Changing the indexed component of an entity (Set, SetIfChanged, Remove, Disable, Enable, GetOrAdd, the bulk " +
        "calls, the generated SetX / RemoveX) moves it between keys, and an emptied key hands its list to the keys that " +
        "grow next: the walk skips entities or visits others. Destroying entities during the walk is allowed.", helpLinkUri: Help("EECS042"));

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
        "ForEachEntity, ForEachBatch and their parallel forms). Query, Count, Any, DestroyEntities, entity.Run, SetAll, " +
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

    /// <summary>EECS037's property for its code fix: the Writes value Execute needs (<c>0b101</c>).</summary>
    public const string WritesValue = "Writes";

    /// <summary>The World members a pass refuses (they throw while one iterates the world).</summary>
    private static readonly HashSet<string> PassRefused =
    [
        "TrimExcess", "Capture", "Restore", "RemoveTypes", "GroupBy", "Save", "Load", "SaveEntities", "LoadEntities",
        "LoadEntitiesInto", "MoveEntities",
    ];

    /// <summary>The calls that need every matching row (they throw for a sliced filter).</summary>
    private static readonly HashSet<string> WholeRowCalls =
        ["Query", "Count", "Any", "DestroyEntities", "SetAll", "RemoveAll", "DisableAll", "EnableAll"];

    private static readonly HashSet<string> EntityTagWriters = ["Set", "GetOrAdd", "Remove", "SetIfChanged", "Disable", "Enable"];

    private static readonly HashSet<string> WorldTagWriters =
        ["CreateEntity", "CreateEntities", "Instantiate", "SetAll", "RemoveAll", "DisableAll", "EnableAll"];

    private static readonly HashSet<string> IndexChangers = ["Set", "SetIfChanged", "Remove", "Disable", "Enable", "GetOrAdd"];

    private static void InitializeUsage(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (Core.Create(start.Compilation) is not { } core) return;
            start.RegisterOperationBlockAction(c => AnalyzeJob(c, core));
            start.RegisterOperationAction(c => AnalyzeUsageCall(c, core), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeCopy(c), OperationKind.VariableDeclarator);
            start.RegisterSymbolAction(AnalyzeRecordKey, SymbolKind.NamedType);
        });

    // ---------------- EECS037: a hand-written job's Writes ----------------

    /// <summary>The one job interface (IForEach, IForEachEntity, IForEachBatch) whose Execute <paramref name="method"/> implements, or null.</summary>
    internal static INamedTypeSymbol? JobInterface(IMethodSymbol method)
    {
        if (method.Name != "Execute" || method.IsStatic || method.ContainingType is not { } type) return null;
        INamedTypeSymbol? found = null;
        foreach (var face in type.AllInterfaces)
        {
            if (face.Name is not ("IForEach" or "IForEachEntity" or "IForEachBatch") || face.ContainingNamespace?.ToDisplayString() != "EasyEcs") continue;
            if (found != null) return null; // several jobs in one type: their Writes is shared, nothing to say per Execute
            found = face;
        }

        if (found?.GetMembers("Execute").FirstOrDefault() is not { } execute
            || !SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(execute), method)) return null;
        return found;
    }

    private static void AnalyzeJob(OperationBlockAnalysisContext context, Core core)
    {
        if (context.OwningSymbol is not IMethodSymbol method || JobInterface(method) is not { } job) return;
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
        var getter = declaration.AccessorList?.Accessors.FirstOrDefault();
        var expression = declaration.ExpressionBody?.Expression ?? getter?.ExpressionBody?.Expression
            ?? (getter?.Body?.Statements is { Count: 1 } statements && statements[0] is ReturnStatementSyntax { Expression: { } returned } ? returned : null);
        return expression switch
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
        if (declarator.Symbol.RefKind != RefKind.None || declarator.Initializer?.Value is not { } value
            || Accessor(value) is not { } accessor || !DeadAfterWrite(declarator.Symbol, declarator)) return;
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
                bool core = property.ContainingNamespace?.ToDisplayString() == "EasyEcs"
                            && (property.ContainingType.Name == "QueryItem" && property.Name.StartsWith("Component")
                                || property.Name == "Value" && property.ContainingType.Name is "Maybe" or "Mut" or "SingletonRef");
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
    /// or settable properties) and no read after the last such write: not after it in the code, not in a loop around it,
    /// not in a lambda or local function (which could run later).
    /// </summary>
    private static bool DeadAfterWrite(ILocalSymbol local, IOperation declaration)
    {
        IOperation root = declaration;
        while (root.Parent != null) root = root.Parent;
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

            if (current == reference) return null; // the local itself: an assignment of a new value is a use
            return current.Parent switch
            {
                IAssignmentOperation assignment when assignment.Target == current => assignment,
                IIncrementOrDecrementOperation step when step.Target == current => step,
                _ => null,
            };
        }
    }

    // ---------------- calls: EECS038 (TryGet), EECS040, EECS041, EECS042, EECS044, EECS045 ----------------

    private static void AnalyzeUsageCall(OperationAnalysisContext context, Core core)
    {
        var call = (IInvocationOperation)context.Operation;
        var method = call.TargetMethod;
        string? owner = method.ContainingType?.ToDisplayString();

        // EECS040: SetIfChanged of a value it cannot compare.
        if (method.Name == "SetIfChanged" && (owner == Symbols.Entity && method.TypeArguments.Length == 1 ? method.TypeArguments[0]
                : method.ContainingType is { Name: "Mut", TypeArguments.Length: 1 } mut && mut.ContainingNamespace?.ToDisplayString() == "EasyEcs" ? mut.TypeArguments[0]
                : null) is { } compared && !Comparable(compared))
            context.ReportDiagnostic(Diagnostic.Create(NotComparable, call.Syntax.GetLocation(),
                Shorten(call.Syntax), compared.Name));

        // EECS045: the world's own tags.
        if ((owner == Symbols.Entity && EntityTagWriters.Contains(method.Name) || owner == Symbols.World && WorldTagWriters.Contains(method.Name)
             || owner == "EasyEcs.Prefab" && method.Name is "Set" or "Remove")
            && method.TypeArguments.FirstOrDefault(IsWorldTag) is { } tag)
            context.ReportDiagnostic(Diagnostic.Create(InternalTag, call.Syntax.GetLocation(),
                method.ContainingType!.Name + "." + method.Name, tag.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                tag.Name == "Destroyed"
                    ? "Destroy adds it while cleanup components remain, and removing the last of them frees the entity; destroy the entity and remove its cleanup components"
                    : $"Disable<{((INamedTypeSymbol)tag).TypeArguments[0].Name}>() adds it and hides the component, Enable<{((INamedTypeSymbol)tag).TypeArguments[0].Name}>() gives it back; call those"));

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
                context.ReportDiagnostic(Diagnostic.Create(IndexChangedInWalk, call.Syntax.GetLocation(), Shorten(call.Syntax),
                    changed.Name, Shorten(loop.Collection.Syntax)));
                break;
            }
    }

    /// <summary>Whether SetIfChanged can compare <paramref name="type"/>: IEquatable&lt;T&gt;, or unmanaged (its bytes).</summary>
    private static bool Comparable(ITypeSymbol type) =>
        type is ITypeParameterSymbol or IErrorTypeSymbol || type.IsUnmanagedType
        || type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>" && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type));

    /// <summary><c>Destroyed</c> or a <c>Disabled&lt;T&gt;</c>: tags the world adds and removes itself.</summary>
    private static bool IsWorldTag(ITypeSymbol type) =>
        type.ToDisplayString() == "EasyEcs.Destroyed" || type is INamedTypeSymbol { IsGenericType: true } named && named.OriginalDefinition.ToDisplayString() == "EasyEcs.Disabled<T>";

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
                    IOperation root = reference;
                    while (root.Parent != null) root = root.Parent;
                    IOperation? initial = null;
                    foreach (var operation in root.Descendants())
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
        var receiver = Unwrap(call.Instance);
        if (receiver == null) return null;
        var worlds = new List<IOperation> { receiver };
        if (call.TargetMethod.Name == "MoveEntities" && call.Arguments.Length > 0) worlds.Add(Unwrap(call.Arguments[0].Value)!);

        for (var node = call.Parent; node != null; node = node.Parent)
        {
            if (node is IAnonymousFunctionOperation or ILocalFunctionOperation) return null;
            if (node is IForEachLoopOperation loop && loop.Body.Syntax.Span.Contains(call.Syntax.Span) && Query(loop.Collection, core) is { } query
                && worlds.Any(w => w.Syntax.ToString() == query.Instance!.Syntax.ToString()))
                return "this foreach over Query";
        }

        if (containing is not IMethodSymbol method) return null;
        bool forEach = Symbols.IsForEach(method), job = !forEach && JobInterface(method) != null;
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

    /// <summary>The indexed component a call changes on an entity (Set, Remove, ..., the generated writers, the bulk calls), or null.</summary>
    private static ITypeSymbol? IndexChange(IInvocationOperation call, Core core)
    {
        var method = call.TargetMethod;
        if (core.Is(method.ContainingType, core.Entity) && IndexChangers.Contains(method.Name)
            || core.Is(method.ContainingType, core.World) && method.Name is "SetAll" or "RemoveAll" or "DisableAll" or "EnableAll")
            return method.TypeArguments.FirstOrDefault(t => t.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == Symbols.Indexed));
        if (Symbols.IsGenerated(method) && Symbols.IsGeneratedWriter(method) && !method.Name.StartsWith("Add") && Named(method, core) is { } named
            && named.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == Symbols.Indexed))
            return named;
        return null;
    }

    /// <summary>The component whose index a foreach walks (<c>Find</c>, <c>Children</c>, <c>Range</c>, ...: an index's <c>Matches</c> or <c>Ordered</c>), or null.</summary>
    private static ITypeSymbol? Walked(IOperation collection) =>
        Unwrap(collection) is IInvocationOperation { Type: INamedTypeSymbol { Name: "Matches" or "Ordered", ContainingType: { } index } }
        && index.OriginalDefinition.ToDisplayString() == "EasyEcs.ValueIndex<T, TKey>" ? index.TypeArguments[0] : null;

    // ---------------- EECS043: a record struct key that prints itself ----------------

    private static void AnalyzeRecordKey(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type is not { TypeKind: TypeKind.Struct, IsRecord: true }) return;
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
}
