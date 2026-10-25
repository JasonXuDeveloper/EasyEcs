using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules for [ForEach] methods that do by hand what a parameter of the pass does (EECS086, EECS087, EECS089): a
/// singleton accumulated row by row through 'ref' (a <c>Reduce&lt;T&gt;</c> keeps a partial result per chunk), the
/// components of other entities read through the World (a <c>Lookup&lt;T&gt;</c> reads them without it), and records
/// appended to a singleton's list (an <c>Emit&lt;T&gt;</c> appends them per chunk). The hand-written forms keep the
/// method on one thread, out of a vector loop or alone in its stage; the parameter does the same work without those
/// costs. None has a code fix: a reduction makes the reset of its singleton in another system redundant, a lookup's
/// reads are not the World's calls one for one, and emitted records land in another singleton, which their readers
/// must read.
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor HandWrittenReduce = new("EECS086", "A singleton accumulated row by row",
        "Parameter '{0}' of {1} accumulates into singleton {2} row by row ({3}), so the method runs on one thread and without a vector loop; {4}: each chunk keeps a partial result, and the pass's result replaces {2}, so its reset in '{5}' goes",
        Category, DiagnosticSeverity.Warning, true,
        "A 'ref' singleton parameter is one value every row writes: the method cannot run in parallel (each chunk would " +
        "race on it), and its vector loop has no lane to keep it in. A Reduce<T> parameter gives each chunk of rows its own " +
        "partial result, starting at T's Identity, and combines them in row order into T when the pass ends: the same " +
        "sums, minimums and maximums, with Parallel = true and the vector loop open to the method. The pass's result " +
        "replaces T, so the rule reports a singleton that system code resets (assigns whole, sets, removes, or gives a " +
        "field a constant) outside OnCreate: one that holds a pass's result. A total that grows across updates is left " +
        "alone; it would reduce into a singleton of its own, which a later system adds to the total.",
        helpLinkUri: Help("EECS086"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor HandWrittenLookup = new("EECS087", "Other entities' components read through the World",
        "{0} takes the World only to read {1} of other entities ('{2}'), so its system runs alone; {3}",
        Category, DiagnosticSeverity.Warning, true,
        "A [ForEach] method that takes the World could change structure, so its system runs alone in its stage and its " +
        "rows are walked with the checks a structural change needs. A Lookup<T> parameter reads T of any entity by id " +
        "(lookup[id] is a ReadOnlyMaybe<T>, Read(id) a ref readonly, Has(id) a presence test) without the World: the system " +
        "is scheduled as a reader of T, shares a stage with systems that do not write T, and the method may run in parallel.",
        helpLinkUri: Help("EECS087"));

    public static readonly DiagnosticDescriptor HandWrittenEmit = new("EECS089", "Records appended to a singleton row by row",
        "Parameter '{0}' of {1} appends {2} records to '{3}' row by row, so the method runs on one thread; an 'Emit<{2}> {4}' parameter appends them per chunk, joined in row order into singleton Emitted<{2}> when the pass ends",
        Category, DiagnosticSeverity.Warning, true,
        "A list a singleton holds, filled through a 'ref' parameter, is one list every row writes: the method cannot run in " +
        "parallel, and the list grows by doubling as the records come. An Emit<T> parameter gives each chunk of rows a " +
        "pooled list and appends the lists to Emitted<T> in chunk order when the pass ends: the same records in the same " +
        "row order, on one thread or several. Systems after the pass read world.GetSingleton<Emitted<T>>().Items and clear it.",
        helpLinkUri: Help("EECS089"));

    /// <summary>
    /// EECS086, EECS087 and EECS089 in a [ForEach] method's body (from AnalyzeBody, with the rules of its parameters).
    /// EECS086 waits for the end of the compilation in <paramref name="resets"/>: only a singleton that system code
    /// resets holds one pass's result, which is what a reduction writes.
    /// </summary>
    private static void AnalyzeHandWritten(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation body, Core core, Resets resets)
    {
        if (!Symbols.IsForEach(method) || Symbols.IsForEntity(method)) return; // one entity per call: nothing to split into chunks
        foreach (var (p, fields) in Accumulations(method, body))
        {
            var singleton = (INamedTypeSymbol)p.Type;
            string ops = string.Join(", ", fields.Select(f => $"{f.Field.Name}: {Verb(f.Op)}"));
            string advice = Reduces(singleton)
                ? $"take it as 'ref Reduce<{singleton.Name}>' and accumulate into '{p.Name}.Value'"
                : $"declare {singleton.Name} partial with {string.Join(", ", fields.Select(f => $"[{f.Op}] on {f.Field.Name}"))} and take it as 'ref Reduce<{singleton.Name}>'";
            resets.Pending.Enqueue((p, singleton, method, [p.Name, Symbols.Describe(method), singleton.Name, ops, advice]));
        }

        foreach (var (p, list, element) in Appends(method, body))
            context.ReportDiagnostic(Diagnostic.Create(HandWrittenEmit, p.Locations.FirstOrDefault(), p.Name, Symbols.Describe(method),
                element.ToMinimalDisplayString(body.SemanticModel!, body.Syntax.SpanStart), p.Name + "." + list.Name,
                Plural(element.ToMinimalDisplayString(body.SemanticModel!, body.Syntax.SpanStart))));

        if (OtherEntityReads(method, body, core) is var (world, reads))
        {
            var types = reads.Select(r => r.Type).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToList();
            var model = body.SemanticModel!;
            int at = body.Syntax.SpanStart;
            string names = string.Join(", ", types.Select(t => t.ToMinimalDisplayString(model, at)));
            string parameters = string.Join(", ", types.Select(t => $"Lookup<{t.ToMinimalDisplayString(model, at)}>"));
            context.ReportDiagnostic(Diagnostic.Create(HandWrittenLookup, world.Locations.FirstOrDefault(),
                reads.Select(r => r.Operation.Syntax.GetLocation()), Symbols.Describe(method), names, Shorten(reads[0].Operation.Syntax),
                $"take {parameters} instead of '{world.Name}' and read them by id (lookup[id], Read(id), Has(id))"));
        }
    }

    /// <summary>
    /// EECS086's evidence, gathered over the compilation: the singletons system code resets, by the methods that reset
    /// them. A reset is a whole assignment (<c>World.Score = default</c>, <c>s = new(..)</c>), a constant given to a field
    /// (<c>World.Score.Kills = 0</c>), or a call that sets or removes it (<c>AddSingleton</c>, <c>RemoveSingleton</c>,
    /// the generated <c>SetX</c> / <c>RemoveX</c>), in a method of a system other than OnCreate and OnDestroy (which set
    /// it up once). A singleton nothing resets keeps a total across updates: a reduction would replace it per pass.
    /// </summary>
    private sealed class Resets
    {
        private readonly ConcurrentDictionary<ITypeSymbol, ConcurrentQueue<IMethodSymbol>> _types = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<IMethodSymbol>> _names = new(); // the generated SetX / RemoveX: X

        /// <summary>
        /// The EECS086 reports waiting for the end of the compilation: the parameter, its singleton, the method and the
        /// message's arguments but the last (the reset).
        /// </summary>
        public readonly ConcurrentQueue<(IParameterSymbol Parameter, INamedTypeSymbol Singleton, IMethodSymbol Method, object[] Arguments)> Pending = new();

        public void Register(CompilationStartAnalysisContext start)
        {
            start.RegisterOperationAction(Collect, OperationKind.SimpleAssignment, OperationKind.Invocation);
            start.RegisterCompilationEndAction(context =>
            {
                foreach (var (p, singleton, method, arguments) in Pending)
                    if ((By(_types, singleton.OriginalDefinition, method) ?? By(_names, singleton.Name, method)) is { } reset)
                        context.ReportDiagnostic(Diagnostic.Create(HandWrittenReduce, p.Locations.FirstOrDefault(),
                            [.. arguments, reset.ContainingType.Name + "." + reset.Name]));
            });
        }

        /// <summary>A method other than <paramref name="except"/> that resets <paramref name="key"/>, or null.</summary>
        private static IMethodSymbol? By<TKey>(ConcurrentDictionary<TKey, ConcurrentQueue<IMethodSymbol>> resets, TKey key, IMethodSymbol except) =>
            resets.TryGetValue(key, out var methods) ? methods.FirstOrDefault(m => !SymbolEqualityComparer.Default.Equals(m, except)) : null;

        private void Collect(OperationAnalysisContext context)
        {
            if (context.ContainingSymbol is not IMethodSymbol method || Symbols.Hook(method) is Symbols.HookKind.Create or Symbols.HookKind.Destroy
                || method.ContainingType is not { } system || !Symbols.IsSystem(system)) return;
            switch (context.Operation)
            {
                case ISimpleAssignmentOperation { Target: var target } assignment:
                    if (IsSingleton(target.Type)) Add(_types, target.Type!.OriginalDefinition, method);
                    else if (target is IMemberReferenceOperation { Instance: { } instance } && IsSingleton(instance.Type)
                             && assignment.Value.ConstantValue.HasValue)
                        Add(_types, instance.Type!.OriginalDefinition, method);
                    break;
                case IInvocationOperation { TargetMethod: var called }:
                    if (called is { Name: "AddSingleton" or "RemoveSingleton", TypeArguments.Length: 1 } && Symbols.Named(called.ContainingType, Symbols.World))
                        Add(_types, called.TypeArguments[0].OriginalDefinition, method);
                    else if (called.Name.Length > 6 && (called.Name.StartsWith("Set") || called.Name.StartsWith("Remove")) && Symbols.IsGenerated(called)
                             && Symbols.OnWorld((IInvocationOperation)context.Operation))
                        Add(_names, called.Name.Substring(called.Name[0] == 'S' ? 3 : 6), method);
                    break;
            }
        }

        private static bool IsSingleton(ITypeSymbol? type) => type is INamedTypeSymbol named && Symbols.Implements(named, Symbols.Singleton);

        private static void Add<TKey>(ConcurrentDictionary<TKey, ConcurrentQueue<IMethodSymbol>> resets, TKey key, IMethodSymbol method) =>
            resets.GetOrAdd(key, _ => new ConcurrentQueue<IMethodSymbol>()).Enqueue(method);
    }

    /// <summary>Whether <paramref name="type"/> combines with itself: a Reduce&lt;T&gt; of it compiles.</summary>
    private static bool Reduces(INamedTypeSymbol type) => Parts.Of(type)?.WritesReduction == true
        || type.AllInterfaces.Any(i => Symbols.Named(i.OriginalDefinition, "EasyEcs.IReduction<T>") && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type));

    // ---------------- EECS086: a reduction written by hand ----------------

    /// <summary>One field (or auto-property) a method accumulates into, and how.</summary>
    private readonly record struct Accumulated(ISymbol Field, Parts.Op Op);

    /// <summary>
    /// EECS086: the 'ref' singleton parameters of a per-entity [ForEach] method on one thread (a parallel one is
    /// EECS010's, a sliced one replaces its result per slice) whose every use accumulates into a field: <c>s.F += x</c>,
    /// <c>-=</c>, <c>++</c>, <c>--</c>, <c>*=</c>, <c>|=</c> and <c>&amp;=</c> of a bool, <c>s.F = Math.Max(s.F, x)</c>,
    /// <c>if (x &gt; s.F) s.F = x;</c>, with <c>x</c> free of <c>s</c>, one way per field. A singleton that combines with
    /// itself is reported when the ways match its attributes (any way for a hand-written Combine); one that does not
    /// when every field is accumulated, so the attributes can declare all of them. The vector-loop message (EECS055)
    /// leaves the method to this rule.
    /// </summary>
    private static IEnumerable<(IParameterSymbol Parameter, List<Accumulated> Fields)> Accumulations(IMethodSymbol method, IOperation body)
    {
        if (!Symbols.IsForEach(method) || Symbols.IsForEntity(method) || Symbols.IsBatch(method) || Symbols.IsParallel(method)
            || Symbols.Slices(method) != 1) yield break;
        foreach (var p in method.Parameters)
        {
            if (p.RefKind != RefKind.Ref || Symbols.Classify(p) != Symbols.ParamKind.Singleton || p.Type is not INamedTypeSymbol singleton
                || method.Parameters.Any(o => !SymbolEqualityComparer.Default.Equals(o, p) && (SymbolEqualityComparer.Default.Equals(o.Type, singleton)
                    || SymbolEqualityComparer.Default.Equals(Symbols.ReduceElement(o.Type), singleton))))
                continue; // a second access of the singleton: EECS057's when it is a Reduce
            if (Accumulations(body, p) is not { Count: > 0 } fields) continue;
            if (Parts.Of(singleton) is { WritesReduction: true } parts)
            {
                if (fields.Any(f => parts.Fields.All(d => d.Name != f.Field.Name || d.Op != f.Op))) continue;
            }
            else if (!Reduces(singleton))
            {
                // Every field accumulated: the attributes declare them all (a field left out would be reset by the pass).
                int data = singleton.GetMembers().OfType<IFieldSymbol>().Count(f => !f.IsStatic && !f.IsConst);
                if (data != fields.Count) continue;
            }

            yield return (p, fields);
        }
    }

    /// <summary>The accumulations of <paramref name="p"/>'s fields, or null when some use of it is anything else.</summary>
    private static List<Accumulated>? Accumulations(IOperation body, IParameterSymbol p)
    {
        var covered = new HashSet<IOperation>();
        var fields = new Dictionary<ISymbol, Parts.Op>(SymbolEqualityComparer.Default);
        var references = new List<IMemberReferenceOperation>();
        foreach (var operation in body.Descendants())
        {
            if (operation is not IParameterReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Parameter, p)) continue;
            if (reference.Parent is not IMemberReferenceOperation access || access.Instance != reference || !Data(access.Member)
                || !SymbolEqualityComparer.Default.Equals(access.Member.ContainingType, p.Type)) return null;
            references.Add(access);
        }

        foreach (var access in references)
        {
            Parts.Op? op = null;
            IMemberReferenceOperation? read = null; // the use of the field the accumulation reads: s.F in Math.Max(s.F, x)
            switch (access.Parent)
            {
                case ICompoundAssignmentOperation compound when compound.Target == access && !Mentions(compound.Value, p):
                    op = (compound.OperatorKind, Numeric(access.Type)) switch
                    {
                        (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, true) => Parts.Op.Sum,
                        (BinaryOperatorKind.Multiply, true) => Parts.Op.Product,
                        (BinaryOperatorKind.Or, false) when access.Type?.SpecialType == SpecialType.System_Boolean => Parts.Op.Any,
                        (BinaryOperatorKind.And, false) when access.Type?.SpecialType == SpecialType.System_Boolean => Parts.Op.All,
                        _ => null,
                    };
                    break;
                case IIncrementOrDecrementOperation step when step.Target == access && Numeric(access.Type):
                    op = Parts.Op.Sum;
                    break;
                case ISimpleAssignmentOperation assignment when assignment.Target == access && Numeric(access.Type):
                    (op, read) = Extremum(assignment, access, p);
                    break;
                default:
                    continue; // a read: covered by the accumulation it belongs to, or nothing covers it
            }

            if (op is not { } found || fields.TryGetValue(access.Member, out var before) && before != found) return null;
            fields[access.Member] = found;
            covered.Add(access);
            if (read != null) covered.Add(read);
        }

        return references.All(covered.Contains) ? fields.Select(f => new Accumulated(f.Key, f.Value)).ToList() : null;
    }

    /// <summary>
    /// The minimum or maximum <paramref name="assignment"/> of <paramref name="target"/> makes, and the read of the
    /// field it compares: <c>s.F = Math.Max(s.F, x)</c> (or MathF, Min), or <c>if (x &gt; s.F) s.F = x;</c> without an else.
    /// </summary>
    private static (Parts.Op?, IMemberReferenceOperation?) Extremum(ISimpleAssignmentOperation assignment, IMemberReferenceOperation target, IParameterSymbol p)
    {
        if (Stripped(assignment.Value) is IInvocationOperation { TargetMethod: { Name: "Max" or "Min" } call, Arguments.Length: 2 } invocation
            && (Symbols.Named(call.ContainingType, "System.Math") || Symbols.Named(call.ContainingType, "System.MathF")))
        {
            var a = Stripped(invocation.Arguments[0].Value);
            var b = Stripped(invocation.Arguments[1].Value);
            var (field, other) = Same(a, target) ? (a, b) : Same(b, target) ? (b, a) : (null, null);
            return field == null || Mentions(other!, p) ? (null, null) : (call.Name == "Max" ? Parts.Op.Max : Parts.Op.Min, (IMemberReferenceOperation)field);
        }

        // if (x > s.F) s.F = x;  (or s.F < x, and >= / <=; the opposite comparisons make a minimum)
        var statement = assignment.Parent as IExpressionStatementOperation;
        var branch = statement?.Parent is IBlockOperation { Operations.Length: 1 } block ? block : (IOperation?)statement;
        if (branch?.Parent is not IConditionalOperation { WhenFalse: null } guard || guard.WhenTrue != branch
            || Stripped(guard.Condition) is not IBinaryOperation { OperatorKind: var kind } comparison) return (null, null);
        var left = Stripped(comparison.LeftOperand);
        var right = Stripped(comparison.RightOperand);
        bool fieldLeft = Same(left, target);
        if (!fieldLeft && !Same(right, target)) return (null, null);
        var value = fieldLeft ? right : left;
        if (Mentions(value, p) || !SyntaxFactory.AreEquivalent(value.Syntax, Stripped(assignment.Value).Syntax)) return (null, null);
        bool greater = kind is BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual;
        if (!greater && kind is not (BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual)) return (null, null);
        // x > s.F (value left, greater) and s.F < x keep the larger value.
        return (greater != fieldLeft ? Parts.Op.Max : Parts.Op.Min, (IMemberReferenceOperation)(fieldLeft ? left : right));
    }

    /// <summary>Whether <paramref name="operation"/> reads the same field of the same parameter as <paramref name="target"/>.</summary>
    private static bool Same(IOperation operation, IMemberReferenceOperation target) =>
        operation is IMemberReferenceOperation { Instance: IParameterReferenceOperation { Parameter: var p } } field
        && SymbolEqualityComparer.Default.Equals(field.Member, target.Member)
        && target.Instance is IParameterReferenceOperation { Parameter: var q } && SymbolEqualityComparer.Default.Equals(p, q);

    /// <summary>A field or an auto-property of an instance: what the reduction attributes combine.</summary>
    private static bool Data(ISymbol member) => member switch
    {
        IFieldSymbol field => !field.IsStatic && !field.IsConst,
        IPropertySymbol property => !property.IsStatic && !property.IsIndexer && property.SetMethod != null
                                    && property.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property)),
        _ => false,
    };

    private static IOperation Stripped(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion) operation = conversion.Operand;
        return operation;
    }

    private static bool Mentions(IOperation operation, IParameterSymbol p) => operation.DescendantsAndSelf()
        .Any(o => o is IParameterReferenceOperation r && SymbolEqualityComparer.Default.Equals(r.Parameter, p));

    private static bool Numeric(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Decimal;

    /// <summary>
    /// Whether the vector-loop message leaves <paramref name="method"/> to EECS086 or EECS089, whose parameter removes
    /// the singleton write it would name (D60: one mistake, one diagnostic).
    /// </summary>
    private static bool AccumulatesByHand(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken token) =>
        method.Parameters.Any(p => p.RefKind == RefKind.Ref && Symbols.Classify(p) == Symbols.ParamKind.Singleton)
        && model.GetOperation(syntax, token) is { } body && (Accumulations(method, body).Any() || Appends(method, body).Any());

    // ---------------- EECS089: records appended by hand ----------------

    /// <summary>
    /// EECS089: the 'ref' singleton parameters of a per-entity [ForEach] method on one thread whose every use appends a
    /// record to one list-valued field (<c>s.Hits.Add(x)</c> on a <c>List&lt;T&gt;</c> or a <c>DynamicBuffer&lt;T&gt;</c>
    /// of a struct <c>T</c>, with <c>x</c> free of <c>s</c>): what an <c>Emit&lt;T&gt;</c> parameter does per chunk.
    /// </summary>
    private static IEnumerable<(IParameterSymbol Parameter, IFieldSymbol List, ITypeSymbol Element)> Appends(IMethodSymbol method, IOperation body)
    {
        if (Symbols.IsBatch(method) || Symbols.IsParallel(method) || Symbols.Slices(method) != 1) yield break;
        foreach (var p in method.Parameters)
        {
            if (p.RefKind != RefKind.Ref || Symbols.Classify(p) != Symbols.ParamKind.Singleton) continue;
            IFieldSymbol? list = null;
            ITypeSymbol? element = null;
            bool appends = true;
            foreach (var operation in body.Descendants())
            {
                if (operation is not IParameterReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Parameter, p)) continue;
                // s.F.Add(x): an instance call on a List<T>, or the buffer's Add extension (its receiver an argument).
                if (reference.Parent is not IFieldReferenceOperation { Field: { IsStatic: false } field } access || access.Instance != reference
                    || (access.Parent is IArgumentOperation { Parent: var parent } ? parent : access.Parent) is not IInvocationOperation
                    {
                        TargetMethod.Name: "Add",
                    } add
                    || (add.Instance != null ? add.Instance != access || add.Arguments.Length != 1 : add.Arguments.Length != 2 || add.Arguments[0].Value != access)
                    || Mentions(add.Arguments[add.Arguments.Length - 1].Value, p)
                    || (list != null && !SymbolEqualityComparer.Default.Equals(list, field))
                    || ListElement(field.Type) is not { IsValueType: true } item)
                {
                    appends = false;
                    break;
                }

                list = field;
                element = item;
            }

            if (appends && list != null) yield return (p, list, element!);
        }
    }

    /// <summary>The element of a <c>List&lt;T&gt;</c> or a <c>DynamicBuffer&lt;T&gt;</c>, or null.</summary>
    private static ITypeSymbol? ListElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeArguments.Length: 1 } named && Symbols.Named(named.OriginalDefinition, "System.Collections.Generic.List<T>")
            ? named.TypeArguments[0] : Symbols.BufferElement(type);

    /// <summary>A parameter name for records of <paramref name="name"/>: <c>hits</c> for Hit, <c>records</c> for a name that is no identifier.</summary>
    private static string Plural(string name) => !SyntaxFacts.IsValidIdentifier(name) ? "records"
        : char.ToLowerInvariant(name[0]) + name.Substring(1) + (name.EndsWith("s") ? "" : "s");

    // ---------------- EECS087: lookups written by hand ----------------

    /// <summary>A component of another entity the method reads, and the read.</summary>
    private readonly record struct OtherRead(IOperation Operation, ITypeSymbol Type);

    /// <summary>
    /// EECS087: a [ForEach] method on a system's or a runner's rows whose World parameter is used only to find entities by
    /// id (<c>w.TryGetEntity(id, out var e)</c> as a condition, <c>w.GetEntity(id)</c>) whose components it only reads
    /// (<c>Read&lt;T&gt;()</c>, <c>Has&lt;T&gt;()</c>, the generated <c>e.T</c> and <c>e.HasT</c>, a <c>Get&lt;T&gt;()</c> not
    /// written through), of types the method does not write itself (EECS059: a lookup of them would read rows the pass
    /// writes). Null otherwise. EECS033 leaves these reads to this rule.
    /// </summary>
    private static (IParameterSymbol World, List<OtherRead> Reads)? OtherEntityReads(IMethodSymbol method, IOperation body, Core core)
    {
        if (!Symbols.IsForEach(method) || Symbols.IsForEntity(method) || Symbols.IsBatch(method)
            || method.Parameters.FirstOrDefault(p => Symbols.Classify(p) == Symbols.ParamKind.World) is not { } world) return null;
        var reads = new List<OtherRead>();
        var found = new List<IOperation>(); // the entity handles TryGetEntity / GetEntity give
        foreach (var operation in body.Descendants())
        {
            if (operation is not IParameterReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Parameter, world)) continue;
            if (reference.Parent is not IInvocationOperation { TargetMethod.Name: "TryGetEntity" or "GetEntity" } find || find.Instance != reference) return null;
            if (find.TargetMethod.Name == "GetEntity") found.Add(find);
            else if (find.Parent is IConditionalOperation or IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd }
                     && find.Arguments.Length == 2 && find.Arguments[1].Value is IDeclarationExpressionOperation { Expression: ILocalReferenceOperation { Local: var local } })
                found.AddRange(body.Descendants().Where(o => o is ILocalReferenceOperation { IsDeclaration: false } l && SymbolEqualityComparer.Default.Equals(l.Local, local)));
            else return null;
        }

        foreach (var handle in found)
        {
            // A read of the handle's component (Get, Read, Has and the generated reads; not TryGet, whose copy Lookup's
            // [id] hands out as a ReadOnlyMaybe, nor a write through Get's ref).
            var (member, receiver) = handle.Parent switch
            {
                IInvocationOperation call => ((ISymbol)call.TargetMethod, Symbols.Receiver(call)),
                IPropertyReferenceOperation { Property.IsIndexer: false } property => (property.Property, property.Instance),
                _ => (null, null),
            };
            if (member == null || receiver != handle || Lookup(member, true, core) is not { Member: not ("TryGet" or "TryGetRef") } read
                || Uses.MayWrite(handle.Parent!)) return null;
            reads.Add(new OtherRead(handle.Parent!, read.Type));
        }

        if (reads.Count == 0) return null;
        // The method writes a type it reads elsewhere: a lookup of it is EECS059's error.
        foreach (var p in method.Parameters)
            if (Symbols.QueriedComponent(p) is { } own && reads.Any(r => SymbolEqualityComparer.Default.Equals(r.Type, own))
                && (p.RefKind == RefKind.Ref || Symbols.Classify(p) is Symbols.ParamKind.Mutable or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Optional))
                return null;
        return (world, reads);
    }
}
