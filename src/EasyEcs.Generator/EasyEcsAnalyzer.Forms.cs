using System;
using System.Collections.Concurrent;
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
/// The rules of the declarations the generator turns into code for one entity, a schedule and a consuming pass
/// (EECS080-EECS085): a hand-written one-entity job [EntityMethod] would write (EECS080), an [EntityMethod] method that cannot
/// be one (EECS081), an [UpdateInterval] that cannot apply (EECS083), a [ForEach] method that destroys every entity it visits by hand (EECS084), a [Consume] that
/// cannot apply (EECS085); a batch method's vector loop written by hand that the generator writes from the method
/// for one row (EECS103, <see cref="HandSimd"/>); a [Drain] method the generator cannot write (EECS108) and the
/// walk and clear of Emitted&lt;T&gt; it writes, written by hand (EECS109); rows collected and looped over by hand in an OnUpdate,
/// which a [ForEach(MaxRows = N)] method gets generated (EECS119). Each computes its code fix as diagnostic properties.
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor HandWrittenEntityJob = new("EECS080", "A hand-written one-entity job",
        "'{0}' is a job written by hand and run only with entity.Run: [EntityMethod] on a static method writes the job, its WriteMask and its entry point ({1})",
        Category, DiagnosticSeverity.Warning, true,
        "A struct implementing IForEachEntity<T1..Tn>, created and handed to entity.Run, is what [EntityMethod] generates " +
        "from a static method: the method's 'ref' parameters make the WriteMask, its filters the QueryFilter, and its other " +
        "parameters the values the entry point takes. The fix turns the struct into a static class with an [EntityMethod] " +
        "method Run and each call into Type.Run(entity, values), when every use of the struct is such a call.",
        helpLinkUri: Help("EECS080"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor BadEntityMethod = new("EECS081", "Invalid [EntityMethod] method",
        "[EntityMethod] method '{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "An [EntityMethod] method is static and runs on one entity through its generated entry point, which takes the " +
        "entity. What belongs to a pass over many rows (component spans, an EntitySpan, a Reduce partial result, a " +
        "Scratch) has no meaning for one entity, and a method cannot be both a pass ([ForEach]) and a run on one entity.",
        helpLinkUri: Help("EECS081"));

    public static readonly DiagnosticDescriptor BadUpdateInterval = new("EECS083", "An [UpdateInterval] that cannot apply",
        "[UpdateInterval] on '{0}' cannot apply: {1}",
        Category, DiagnosticSeverity.Error, true,
        "[UpdateInterval(n, Phase = p)] runs a system on the updates whose count modulo n is p: n is 1 or more, p is 0 to " +
        "n - 1, and the type is an [EcsSystem]. The generator leaves an invalid one out, so the system would run every update.",
        helpLinkUri: Help("EECS083"));

    public static readonly DiagnosticDescriptor HandWrittenConsume = new("EECS084", "A [ForEach] method that destroys every entity it visits",
        "[ForEach] method '{0}' ends by destroying the entity it visits, on every path: [Consume] does it ({1})",
        Category, DiagnosticSeverity.Warning, true,
        "A method that handles event or command entities and destroys each one is a consuming pass: [Consume] destroys " +
        "exactly the rows the pass visited. A method that makes no structural change has them destroyed at once when " +
        "the pass ends (one DestroyAll of its filter, an emptied table at a time), instead of a destroy and a hole per row.",
        helpLinkUri: Help("EECS084"));

    public static readonly DiagnosticDescriptor BadConsume = new("EECS085", "A [Consume] that cannot apply",
        "[Consume] on '{0}' cannot apply: {1}",
        Category, DiagnosticSeverity.Error, true,
        "[Consume] destroys the rows a [ForEach] pass visited, when it ends. A method without [ForEach] has no pass, and a " +
        "sliced one would move the rows of the other slices with every destroy.", helpLinkUri: Help("EECS085"));

    public static readonly DiagnosticDescriptor HandWrittenVectorLoop = new("EECS103", "Vector code written by hand that the generator writes from the method for one row",
        "[ForEach] method '{0}' writes by hand the vector loop the generator writes from the method for one row: {1}",
        Category, DiagnosticSeverity.Warning, true,
        "A batch method that loads its spans into Vector<T> or Vector128/256/512<T> values, combines them lane by lane " +
        "and stores them back, with a scalar loop for the rows left over, computes the method for one row on every lane. " +
        "The generator writes that loop from the method for one row: at the widest vectors the hardware has (Vector512 " +
        "and Vector<T>), with the last rows and masked words handled, and the same results bit for bit. The method for " +
        "one row is shorter, runs on hardware without vectors, and is checked by the analyzer like any [ForEach] method. " +
        "The fix writes it: each span a 'ref' (written) or 'in' component, each vector statement one statement per field, " +
        "each broadcast a scalar local; it is offered only when the generator gives that method its vector loop. Shuffles, " +
        "horizontal operations, loads at other offsets, accumulators across vectors and calls into user code are left alone.",
        helpLinkUri: Help("EECS103"));

    public static readonly DiagnosticDescriptor BadRecordPass = new("EECS108", "Invalid [Drain] method",
        "[Drain] method '{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "A [Drain] method is static and takes the record it handles first, by 'in': the generator runs it on each " +
        "record of Emitted<T> in order, then clears them. What belongs to rows (components, Maybe, Mut, Reduce, Scratch, " +
        "the Entity, filters, [Consume]) has no meaning for a record, and an Emit<T> of its own record type would be " +
        "cleared unhandled. The generator writes nothing for an invalid method.",
        helpLinkUri: Help("EECS108"));

    public static readonly DiagnosticDescriptor HandWrittenRecordPass = new("EECS109", "The records of Emitted<T> walked and cleared by hand",
        "'{0}' walks the records of Emitted<{1}> and clears them by hand: a [Drain] method taking 'in {1} {2}' gets the walk and the Clear generated",
        Category, DiagnosticSeverity.Info, true,
        "Code that gets Emitted<T>'s list, runs a loop over it and clears it handles each record once, in the order the " +
        "passes emitted them: what a [Drain] method does, written once per record type. The method holds only the " +
        "loop's body; its other parameters are what the body used (singletons, the World). The fix writes it when the " +
        "loop is a system's OnUpdate whole: the singletons it took by ref, the list, the loop and the Clear.",
        helpLinkUri: Help("EECS109"));

    public static readonly DiagnosticDescriptor HandWrittenCollected = new("EECS119", "Rows collected and run by hand",
        "'{0}' collects up to {1} rows of {2} and loops over them by hand: a [ForEach(MaxRows = {1})] method over {2} gets the collection and the loop generated, and runs outside the walk as this loop does",
        Category, DiagnosticSeverity.Warning, true,
        "A system's OnUpdate that collects the ids of the first N matching rows (CollectIds into a span of N) and loops over " +
        "them serves a budget of N rows a run outside any pass, where it may load, save, create and destroy: what a " +
        "[ForEach(MaxRows = N)] method does, its components handed to it as copies and its 'ref' ones written back, " +
        "[Consume] destroying each row after it. The method holds only the loop's body. The fix writes it when the hook " +
        "is that whole: the span, the CollectIds (its filter a QueryFilter of With / Without), a foreach over the ids that " +
        "takes each entity with GetEntity, guards the method does not need (Any of the rows' components, a count of 0) and " +
        "declarations it can move into the method.",
        helpLinkUri: Help("EECS119"));

    /// <summary>
    /// EECS119's properties: the [ForEach] method's attributes, name, parameters and body (one line per line, indented
    /// from 0), and the filter field the fix deletes (no other use), if any.
    /// </summary>
    public const string CollectedAttributes = "CollectedAttributes", CollectedName = "CollectedName", CollectedParameters = "CollectedParameters",
        CollectedBody = "CollectedBody", CollectedFilter = "CollectedFilter";

    /// <summary>EECS109's property: the record method's parameters after the record ("ref global::S s, ..."), when the fix applies.</summary>
    public const string RecordParameters = "RecordParameters", RecordName = "Record";

    /// <summary>EECS103's properties: the method for one row's parameter list and its statements (one per line).</summary>
    public const string PerRowParameters = "PerRowParameters", PerRowBody = "PerRowBody";

    /// <summary>EECS080's properties: field=parameter pairs, the components' modifiers, the filter attributes, the calls' replacements; EECS084's entity parameter to drop.</summary>
    public const string JobParameters = "Parameters", JobModifiers = "Modifiers", JobFilter = "Filter", JobCall = "Call", DropEntity = "DropEntity";

    internal static void InitializeForms(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzeScheduleAttribute, SymbolKind.NamedType);
        context.RegisterSyntaxNodeAction(AnalyzeHandWrittenConsume, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeHandWrittenVectorLoop, SyntaxKind.MethodDeclaration);
        context.RegisterSymbolAction(AnalyzeConsume, SymbolKind.Method);
        context.RegisterSymbolAction(AnalyzeRecordPass, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(AnalyzeHandWrittenRecordPass, SyntaxKind.ForEachStatement);
        context.RegisterSyntaxNodeAction(AnalyzeHandWrittenCollected, SyntaxKind.InvocationExpression);
        context.RegisterCompilationStartAction(AnalyzeEntityJobs);
    }

    // ---------------- EECS083: [UpdateInterval] ----------------

    private static void AnalyzeScheduleAttribute(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (Symbols.ScheduleProblem(type) is not { } problem || Symbols.Schedule(type) is not var (_, _, attribute)) return;
        context.ReportDiagnostic(Diagnostic.Create(BadUpdateInterval,
            attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations.FirstOrDefault(),
            type.Name, problem));
    }

    // ---------------- EECS084 / EECS085: [Consume] ----------------

    private static void AnalyzeConsume(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (Symbols.ConsumeProblem(method) is { } problem)
            context.ReportDiagnostic(Diagnostic.Create(BadConsume, method.Locations.FirstOrDefault(), method.Name, problem));
    }

    /// <summary>
    /// EECS084: a [ForEach] method (without [Consume], on one thread, not sliced) whose body's last statement destroys
    /// its Entity parameter, with no return before it, after at least one other statement (a method that only destroys
    /// is EECS030's: DestroyAll).
    /// </summary>
    private static void AnalyzeHandWrittenConsume(SyntaxNodeAnalysisContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (syntax.Body is not { Statements: { Count: >= 2 } statements } body
            || statements[statements.Count - 1] is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax last } destroy
            || context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } method
            || !Symbols.IsForEach(method) || Symbols.IsConsume(method) || Symbols.IsParallel(method) || Symbols.Slices(method) != 1
            || Symbols.IsEntityMethod(method)) return;
        if (context.SemanticModel.GetOperation(last, context.CancellationToken) is not IInvocationOperation { TargetMethod: { Name: "Destroy", Parameters.Length: 0 } target } call
            || !Symbols.Named(target.ContainingType, Symbols.Entity)
            || Symbols.Unwrap(call.Instance) is not IParameterReferenceOperation { Parameter: var entity }
            || !SymbolEqualityComparer.Default.Equals(entity.ContainingSymbol, method)) return;
        // Every path reaches it: no return (or goto) before it, outside lambdas and local functions.
        if (body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .Any(n => n is ReturnStatementSyntax or GotoStatementSyntax or YieldStatementSyntax)) return;

        // The Entity goes too when the destroy was its only use: the method may then make no structural change, and
        // its rows go at once after the pass.
        int uses = body.DescendantNodes().OfType<IdentifierNameSyntax>().Count(n => n.Identifier.ValueText == entity.Name
            && SymbolEqualityComparer.Default.Equals(context.SemanticModel.GetSymbolInfo(n, context.CancellationToken).Symbol, entity));
        bool drop = uses == 1;
        bool bulk = drop && method.IsStatic && !method.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.World);
        var properties = ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Consume");
        if (drop) properties = properties.Add(DropEntity, entity.Name);
        context.ReportDiagnostic(Diagnostic.Create(HandWrittenConsume, destroy.GetLocation(), properties, method.Name,
            bulk ? $"without '{entity.Name}', the method changes no structure, and its rows go at once when the pass ends"
                : "each entity goes after its row"));
    }

    // ---------------- EECS108 / EECS109: [Drain] ----------------

    private static void AnalyzeRecordPass(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (Symbols.IsDrain(method) && Symbols.RecordProblem(method) is { } problem)
            context.ReportDiagnostic(Diagnostic.Create(BadRecordPass, method.Locations.FirstOrDefault(), method.Name, problem));
    }

    /// <summary>
    /// EECS109: a foreach over a local that holds <c>ref world.GetSingleton&lt;Emitted&lt;T&gt;&gt;().Items</c> (or
    /// GetOrAddSingleton, or its AsReadOnlySpan()), followed in the same block by that local's Clear(). The fix's
    /// parameters when the block is a system's OnUpdate holding only 'ref' singleton locals, the list, the loop and the
    /// Clear, and the loop's body uses only the record, those locals and the World.
    /// </summary>
    private static void AnalyzeHandWrittenRecordPass(SyntaxNodeAnalysisContext context)
    {
        var loop = (ForEachStatementSyntax)context.Node;
        var model = context.SemanticModel;
        var ct = context.CancellationToken;
        var walked = loop.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AsReadOnlySpan" or "AsSpan" } access, ArgumentList.Arguments.Count: 0 }
            ? access.Expression
            : loop.Expression;
        if (walked is not IdentifierNameSyntax || model.GetSymbolInfo(walked, ct).Symbol is not ILocalSymbol { IsRef: true } list
            || loop.Parent is not BlockSyntax block
            || list.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not VariableDeclaratorSyntax { Initializer.Value: RefExpressionSyntax { Expression: var initializer } } declarator
            || Emitted(model.GetOperation(initializer, ct), out var world) is not { } record) return;
        int at = block.Statements.IndexOf(loop);
        bool Clears(StatementSyntax s) => s is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Clear", Expression: IdentifierNameSyntax cleared }, ArgumentList.Arguments.Count: 0 } }
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(cleared, ct).Symbol, list);
        int clear = -1;
        for (int i = at + 1; i < block.Statements.Count && clear < 0; i++)
            if (Clears(block.Statements[i])) clear = i;
        if (clear < 0 || model.GetDeclaredSymbol(loop, ct) is not ILocalSymbol item) return;

        string name = record.ToMinimalDisplayString(model, loop.SpanStart);
        var properties = ImmutableDictionary<string, string?>.Empty;
        if (RecordFix(block, loop, clear, declarator, world, item, model, ct) is { } parameters)
            properties = properties.Add(FixKind, "Drain").Add(RecordParameters, parameters).Add(RecordName, name);
        string where = model.GetEnclosingSymbol(loop.SpanStart, ct) is { } method ? method.ContainingType?.Name is { } type ? $"{type}.{method.Name}" : method.Name : "the code";
        context.ReportDiagnostic(Diagnostic.Create(HandWrittenRecordPass, loop.ForEachKeyword.GetLocation(), properties, where, name, item.Name));
    }

    /// <summary>
    /// The record type when <paramref name="operation"/> is <c>w.GetSingleton&lt;Emitted&lt;T&gt;&gt;().Items</c> or
    /// <c>w.GetOrAddSingleton&lt;Emitted&lt;T&gt;&gt;().Items</c>, with the World it reads (<paramref name="world"/>), else null.
    /// </summary>
    private static ITypeSymbol? Emitted(IOperation? operation, out IOperation? world)
    {
        world = null;
        if (operation is not IFieldReferenceOperation { Field.Name: "Items", Instance: IInvocationOperation { TargetMethod: { Name: "GetSingleton" or "GetOrAddSingleton", Parameters.Length: 0 } get } call }
            || get.TypeArguments.Length != 1 || get.TypeArguments[0] is not INamedTypeSymbol { IsGenericType: true } emitted
            || !Symbols.Named(emitted.ConstructedFrom, "EasyEcs.Emitted<T>") || !Symbols.Named(get.ContainingType, Symbols.World)) return null;
        world = call.Instance;
        return emitted.TypeArguments[0];
    }

    /// <summary>
    /// The parameters after the record of the [Drain] method EECS109's fix writes, or null when it does not
    /// apply: the block is a system's static OnUpdate(World), its statements are 'ref' locals of singletons read from
    /// that World, the list, the loop (one variable) and the Clear, and the loop's body uses no other local.
    /// </summary>
    private static string? RecordFix(BlockSyntax block, ForEachStatementSyntax loop, int clear, VariableDeclaratorSyntax list,
        IOperation? world, ILocalSymbol item, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (block.Parent is not MethodDeclarationSyntax hook || model.GetDeclaredSymbol(hook, ct) is not { } method
            || Symbols.Hook(method) != Symbols.HookKind.Update || !method.IsStatic || method.Parameters.Length != 1
            || Symbols.Unwrap(world) is not IParameterReferenceOperation { Parameter: var worldParameter }
            || !SymbolEqualityComparer.Default.Equals(worldParameter, method.Parameters[0])
            || clear != block.Statements.Count - 1 || block.Statements.IndexOf(loop) != clear - 1) return null;
        var singletons = new List<ILocalSymbol>();
        for (int i = 0; i < clear - 1; i++)
        {
            if (block.Statements[i] is not LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } variables } || variables[0] == list) continue;
            if (model.GetDeclaredSymbol(variables[0], ct) is not ILocalSymbol { IsRef: true, Type: var type } local || !type.IsValueType
                || !Symbols.Implements(type, Symbols.Singleton) || variables[0].Initializer?.Value is not RefExpressionSyntax { Expression: var from }
                || !model.GetOperation(from, ct)!.DescendantsAndSelf().Any(o => o is IParameterReferenceOperation { Parameter: var p } && SymbolEqualityComparer.Default.Equals(p, worldParameter)))
                return null;
            singletons.Add(local);
        }
        if (block.Statements.Take(clear - 1).OfType<LocalDeclarationStatementSyntax>().Count() != clear - 1) return null; // only declarations before the loop

        bool usesWorld = false;
        foreach (var name in loop.Statement.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            switch (model.GetSymbolInfo(name, ct).Symbol)
            {
                case ILocalSymbol local when !SymbolEqualityComparer.Default.Equals(local, item) && !singletons.Contains(local, SymbolEqualityComparer.Default)
                                             && !(local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is { } declared && loop.Statement.Span.Contains(declared.Span)):
                    return null; // a local of the hook the method would not have
                case IParameterSymbol p when SymbolEqualityComparer.Default.Equals(p, worldParameter):
                    usesWorld = true;
                    break;
            }

        var parameters = singletons.Select(s => $"ref {s.Type.ToMinimalDisplayString(model, loop.SpanStart)} {s.Name}").ToList();
        if (usesWorld) parameters.Add($"{worldParameter.Type.ToMinimalDisplayString(model, loop.SpanStart)} {worldParameter.Name}");
        return string.Join(", ", parameters);
    }

    // ---------------- EECS119: rows collected and run by hand ----------------

    /// <summary>
    /// EECS119: <c>int n = world.CollectIds&lt;T..&gt;(ids, filter)</c> in a system's OnUpdate, followed in the same block
    /// by a loop over <c>ids</c>. The fix's properties when the hook is the canonical whole (<see cref="CollectedFix"/>).
    /// </summary>
    private static void AnalyzeHandWrittenCollected(SyntaxNodeAnalysisContext context)
    {
        var syntax = (InvocationExpressionSyntax)context.Node;
        if (syntax.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "CollectIds" } }
            || syntax.ArgumentList.Arguments.Count is 0 or > 2
            || syntax.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax collect } } counted }
            || collect.Parent is not BlockSyntax { Parent: MethodDeclarationSyntax hookSyntax } block) return;
        var model = context.SemanticModel;
        var ct = context.CancellationToken;
        if (model.GetDeclaredSymbol(hookSyntax, ct) is not { } hook || Symbols.Hook(hook) != Symbols.HookKind.Update
            || model.GetOperation(syntax, ct) is not IInvocationOperation { TargetMethod: var target } call || !Symbols.Named(target.ContainingType, Symbols.World)
            || call.Arguments.Length == 0 || Symbols.Unwrap(call.Arguments[0].Value) is not ILocalReferenceOperation { Local: var ids }
            || model.GetDeclaredSymbol(counted, ct) is not ILocalSymbol count) return;
        int at = block.Statements.IndexOf(collect);
        StatementSyntax? loop = null;
        for (int i = at + 1; i < block.Statements.Count && loop == null; i++)
            if (block.Statements[i] is ForEachStatementSyntax or ForStatementSyntax && Names(block.Statements[i], ids, model, ct)) loop = block.Statements[i];
        if (loop == null) return;

        // The budget: the span's length, a constant for the attribute.
        var size = ids.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is VariableDeclaratorSyntax { Initializer.Value: var made }
            ? made switch
            {
                StackAllocArrayCreationExpressionSyntax { Type: ArrayTypeSyntax { RankSpecifiers: { Count: 1 } ranks } } when ranks[0].Sizes.Count == 1 => ranks[0].Sizes[0],
                ArrayCreationExpressionSyntax { Type.RankSpecifiers: { Count: 1 } ranks } when ranks[0].Sizes.Count == 1 => ranks[0].Sizes[0],
                _ => null,
            }
            : null;
        string budget = size?.WithoutTrivia().ToString() ?? "N";
        string types = string.Join(", ", target.TypeArguments.Select(t => t.ToMinimalDisplayString(model, syntax.SpanStart)));
        var properties = ImmutableDictionary<string, string?>.Empty;
        if (size != null && model.GetConstantValue(size, ct) is { HasValue: true, Value: int }
            && CollectedFix(block, collect, loop, call, hook, ids, count, budget, model, ct) is { } fix)
            properties = fix.Add(FixKind, "Collected");
        context.ReportDiagnostic(Diagnostic.Create(HandWrittenCollected, syntax.GetLocation(), properties,
            $"{hook.ContainingType.Name}.{hook.Name}", budget, types));
    }

    /// <summary>Whether <paramref name="node"/> names <paramref name="symbol"/>.</summary>
    private static bool Names(SyntaxNode node, ISymbol symbol, SemanticModel model, System.Threading.CancellationToken ct) =>
        node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(n => n.Identifier.ValueText == symbol.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, ct).Symbol, symbol));

    /// <summary>
    /// The [ForEach(MaxRows = N)] method EECS119's fix writes, as properties, or null when the hook is not the canonical
    /// whole: a static OnUpdate(World) holding the span, the CollectIds (no filter, or a QueryFilter of With / Without
    /// built inline or in a static readonly field), guards (<c>if (!world.Any&lt;T&gt;()) return;</c> of the rows'
    /// components, <c>if (n == 0) return;</c>), declarations without calls the method can make per row, and last a
    /// <c>foreach (var id in ids[..n])</c> whose body starts with <c>var e = world.GetEntity(id);</c> and neither
    /// returns nor breaks out of the loop (a 'continue' becomes a 'return'; the id becomes <c>e.Id</c>).
    /// </summary>
    private static ImmutableDictionary<string, string?>? CollectedFix(BlockSyntax block, LocalDeclarationStatementSyntax collect, StatementSyntax loop,
        IInvocationOperation call, IMethodSymbol hook, ILocalSymbol ids, ILocalSymbol count, string budget, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (!hook.IsStatic || hook.Parameters.Length != 1 || !Symbols.Named(hook.Parameters[0].Type, Symbols.World)
            || block.Statements[block.Statements.Count - 1] != loop || loop is not ForEachStatementSyntax { Statement: BlockSyntax { Statements.Count: >= 1 } body } each
            || Symbols.Unwrap(call.Instance) is not IParameterReferenceOperation { Parameter: var world } || !SymbolEqualityComparer.Default.Equals(world, hook.Parameters[0])) return null;
        bool Is(ExpressionSyntax? e, ISymbol symbol) => e is IdentifierNameSyntax && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(e, ct).Symbol, symbol);
        int position = call.Syntax.SpanStart;
        string Short(ITypeSymbol type) => type.ToMinimalDisplayString(model, position);

        // The loop: foreach over ids[..n] (or ids[0..n], ids.Slice(0, n)), its first statement the entity.
        bool walksIds = each.Expression switch
        {
            ElementAccessExpressionSyntax { Expression: var span, ArgumentList.Arguments: { Count: 1 } index } =>
                index[0].Expression is RangeExpressionSyntax { RightOperand: var end, LeftOperand: var start }
                && Is(span, ids) && Is(end, count) && (start == null || start is LiteralExpressionSyntax { Token.ValueText: "0" }),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Slice", Expression: var span }, ArgumentList.Arguments: { Count: 2 } slice } =>
                slice[0].Expression is LiteralExpressionSyntax { Token.ValueText: "0" } && Is(span, ids) && Is(slice[1].Expression, count),
            _ => false,
        };
        if (!walksIds || model.GetDeclaredSymbol(each, ct) is not ILocalSymbol id
            || body.Statements[0] is not LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } declared } || declared[0] is not { Initializer.Value: InvocationExpressionSyntax get } entityDeclarator
            || model.GetOperation(get, ct) is not IInvocationOperation { TargetMethod.Name: "GetEntity", Arguments: { Length: 1 } gottenArguments } got
            || gottenArguments[0] is not { } gotten
            || !Symbols.Named(got.TargetMethod.ContainingType, Symbols.World) || Symbols.Unwrap(got.Instance) is not IParameterReferenceOperation { Parameter: var from }
            || !SymbolEqualityComparer.Default.Equals(from, world) || Symbols.Unwrap(gotten.Value) is not ILocalReferenceOperation { Local: var gottenId }
            || !SymbolEqualityComparer.Default.Equals(gottenId, id) || model.GetDeclaredSymbol(entityDeclarator, ct) is not ILocalSymbol entity) return null;

        // The filter: none, an inline chain, or a static readonly field initialized with one.
        var queried = call.TargetMethod.TypeArguments;
        var filters = new List<string>();
        var required = new List<ITypeSymbol>(queried);
        string? field = null;
        if (call.Arguments.Length == 2 && !call.Arguments[1].IsImplicit)
        {
            var chain = Symbols.Unwrap(call.Arguments[1].Value);
            if (chain is IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } filterField })
            {
                if (filterField.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not VariableDeclaratorSyntax { Initializer.Value: { } initializer, Parent: VariableDeclarationSyntax { Variables.Count: 1 } }
                    || initializer.SyntaxTree != block.SyntaxTree) return null;
                chain = Symbols.Unwrap(model.GetOperation(initializer, ct));
                // Deleted when the call is its only use in the type.
                if (filterField.ContainingType.DeclaringSyntaxReferences.Length == 1
                    && filterField.ContainingType.DeclaringSyntaxReferences[0].GetSyntax(ct).DescendantNodes().OfType<IdentifierNameSyntax>().Count(n => n.Identifier.ValueText == filterField.Name) == 1)
                    field = filterField.Name;
            }
            while (chain is IInvocationOperation { TargetMethod: var step } link && Symbols.Named(step.ContainingType, "EasyEcs.QueryFilter"))
            {
                if (step.Name == "Create" && step.IsStatic && link.Arguments.Length == 0)
                {
                    chain = null;
                    break;
                }
                if (step.Name is not ("With" or "Without") || step.TypeArguments.Length != 1 || link.Arguments.Length != 0) return null;
                filters.Insert(0, $"{step.Name}<{Short(step.TypeArguments[0])}>"); // the chain is read from its end
                if (step.Name == "With") required.Add(step.TypeArguments[0]);
                chain = Symbols.Unwrap(link.Instance);
            }
            if (chain != null) return null;
        }
        var attributes = new List<string> { $"ForEach(MaxRows = {budget})" };
        attributes.AddRange(queried.Select(t => $"With<{Short(t)}>"));
        attributes.AddRange(filters);

        // The other statements: the span, guards, declarations the method makes per row.
        var moved = new List<StatementSyntax>();
        foreach (var statement in block.Statements)
        {
            if (statement == collect || statement == loop) continue;
            switch (statement)
            {
                case LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } span } when SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(span[0], ct), ids):
                    continue;
                case IfStatementSyntax { Statement: ReturnStatementSyntax { Expression: null }, Else: null } guard when Guard(guard.Condition):
                    continue;
                case LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } local
                    when !local.DescendantNodes().Any(n => n is InvocationExpressionSyntax or AssignmentExpressionSyntax or PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax or AnonymousFunctionExpressionSyntax)
                         && !Names(local, ids, model, ct) && !Names(local, count, model, ct) && block.Statements.IndexOf(local) > block.Statements.IndexOf(collect):
                    moved.Add(local);
                    continue;
                default:
                    return null;
            }
        }

        bool Guard(ExpressionSyntax condition) => condition switch
        {
            // if (!world.Any<T>()) return;  of the rows' components: no rows, nothing to collect.
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression, Operand: InvocationExpressionSyntax any }
                => model.GetOperation(any, ct) is IInvocationOperation { TargetMethod: { Name: "Any" } anyMethod } anyCall
                   && anyCall.Arguments.All(a => a.IsImplicit)
                   && Symbols.Named(anyMethod.ContainingType, Symbols.World) && Symbols.Unwrap(anyCall.Instance) is IParameterReferenceOperation { Parameter: var p }
                   && SymbolEqualityComparer.Default.Equals(p, world)
                   && anyMethod.TypeArguments.All(t => required.Contains(t, SymbolEqualityComparer.Default)),
            // if (n == 0) return;  (or n <= 0)
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression or (int)SyntaxKind.LessThanOrEqualExpression, Left: var left, Right: LiteralExpressionSyntax { Token.ValueText: "0" } } => Is(left, count),
            _ => false,
        };

        // The body: no return, no break out of the loop, no use of the span or the count; continue -> return, id -> e.Id.
        var rest = body.Statements.Skip(1).ToList();
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var statement in rest)
            foreach (var node in statement.DescendantNodesAndSelf(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
                switch (node)
                {
                    case ReturnStatementSyntax or YieldStatementSyntax or GotoStatementSyntax:
                        return null;
                    case BreakStatementSyntax or ContinueStatementSyntax when !InnerLoop(node, each):
                        if (node is BreakStatementSyntax) return null;
                        replacements[node] = SyntaxFactory.ReturnStatement().WithTriviaFrom(node);
                        break;
                    case IdentifierNameSyntax named when named.Identifier.ValueText == ids.Name || named.Identifier.ValueText == count.Name || named.Identifier.ValueText == id.Name:
                        var symbol = model.GetSymbolInfo(named, ct).Symbol;
                        if (SymbolEqualityComparer.Default.Equals(symbol, ids) || SymbolEqualityComparer.Default.Equals(symbol, count)) return null;
                        if (SymbolEqualityComparer.Default.Equals(symbol, id))
                            replacements[named] = SyntaxFactory.ParseExpression($"{entity.Name}.Id").WithTriviaFrom(named);
                        break;
                }

        var lines = new List<string>();
        foreach (var statement in moved.Concat(rest))
        {
            var leading = statement.GetLeadingTrivia();
            string indent = leading.Count > 0 && leading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? leading.Last().ToFullString() : "";
            var text = replacements.TryGetValue(statement, out var whole) ? whole
                : statement.ReplaceNodes(statement.DescendantNodes().Where(replacements.ContainsKey), (old, _) => replacements[old]);
            foreach (var line in text.ToFullString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            {
                if (line.Trim().Length == 0 && lines.Count == 0) continue;
                lines.Add(line.StartsWith(indent, StringComparison.Ordinal) ? line.Substring(indent.Length) : line.TrimStart());
            }
        }

        bool usesWorld = moved.Concat(rest).Any(st => Names(st, world, model, ct));
        bool usesEntity = rest.Any(st => Names(st, entity, model, ct) || Names(st, id, model, ct));
        var parameters = new List<string>();
        if (usesEntity) parameters.Add($"{Short(entity.Type)} {entity.Name}");
        if (usesWorld) parameters.Add($"{Short(world.Type)} {world.Name}");

        string system = hook.ContainingType.Name;
        string name = system.EndsWith("System", StringComparison.Ordinal) && system.Length > 6 ? system.Substring(0, system.Length - 6) : "Run";
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add(CollectedAttributes, string.Join(", ", attributes)).Add(CollectedName, name)
            .Add(CollectedParameters, string.Join(", ", parameters)).Add(CollectedBody, string.Join("\n", lines));
        return field != null ? properties.Add(CollectedFilter, field) : properties;
    }

    /// <summary>A break or continue inside a loop (or, for a break, a switch section) nested in <paramref name="outer"/> belongs to it.</summary>
    private static bool InnerLoop(SyntaxNode node, SyntaxNode outer)
    {
        for (var at = node.Parent; at != null && at != outer; at = at.Parent)
            if (at is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                || node is BreakStatementSyntax && at is SwitchSectionSyntax) return true;
        return false;
    }

    // ---------------- EECS103: a vector loop written by hand ----------------

    /// <summary>
    /// EECS103: a batch [ForEach] method whose body is a vector loop written by hand that the generator writes from the
    /// method for one row (<see cref="HandSimd"/>: the syntactic prefilter first, then the rewrite bound in a copy of the
    /// compilation, which must get the generator's loop).
    /// </summary>
    private static void AnalyzeHandWrittenVectorLoop(SyntaxNodeAnalysisContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (!HandSimd.MayBe(syntax) || context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } method
            || HandSimd.Of(method, syntax, context.SemanticModel, context.CancellationToken) is not { } perRow) return;
        var properties = ImmutableDictionary<string, string?>.Empty.Add(FixKind, "PerRow")
            .Add(PerRowParameters, perRow.Parameters).Add(PerRowBody, string.Join("\n", perRow.Statements));
        context.ReportDiagnostic(Diagnostic.Create(HandWrittenVectorLoop, syntax.Identifier.GetLocation(), properties, method.Name,
            perRow.Summary(method.Name)));
    }

    // ---------------- EECS080: hand-written one-entity jobs ----------------

    /// <summary>A struct job whose declaration [EntityMethod] can write: what the fix needs of it.</summary>
    private sealed record JobShape(INamedTypeSymbol Type, Location Location, string Parameters, string Modifiers, bool UsesEntity);

    /// <summary>A use of a candidate job: a creation and its entity.Run call (Pattern), the Run call's type argument (Covered), or anything else.</summary>
    private sealed record JobUse(INamedTypeSymbol Type, int Kind, Location? Statement, Location? Call, string? Replacement, string? Filter, ISymbol? FilterField);

    private const int UseBad = 0, UsePattern = 1, UseCovered = 2;

    private static void AnalyzeEntityJobs(CompilationStartAnalysisContext start)
    {
        var compilation = start.Compilation;
        if (compilation.GetTypeByMetadataName(Symbols.Entity) is not { } entityType) return;
        var shapes = new ConcurrentDictionary<INamedTypeSymbol, JobShape>(SymbolEqualityComparer.Default);
        var uses = new ConcurrentBag<JobUse>();
        // The names of the structs whose base list names IForEachEntity: only identifiers with one of them are bound.
        // Read from syntax, once per tree (an edit rereads its own tree only), instead of every type symbol of the
        // compilation at each compilation start; EntityJobInterface still decides from the symbol.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
            foreach (var name in EntityJobNames(tree, start.CancellationToken)) names.Add(name);
        if (names.Count == 0) return;

        start.RegisterSyntaxNodeAction(context =>
        {
            var declaration = (StructDeclarationSyntax)context.Node;
            if (!names.Contains(declaration.Identifier.ValueText)
                || context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type
                || JobShapeOf(declaration, type, context.SemanticModel, context.CancellationToken) is not { } shape) return;
            shapes[type] = shape;
        }, SyntaxKind.StructDeclaration);

        start.RegisterSyntaxNodeAction(context =>
        {
            var name = (IdentifierNameSyntax)context.Node;
            if (!names.Contains(name.Identifier.ValueText)
                || context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct } type
                || EntityJobInterface(type) == null || !type.Locations.Any(l => l.IsInSource)) return;
            uses.Add(JobUseOf(name, type, entityType, context.SemanticModel, context.CancellationToken));
        }, SyntaxKind.IdentifierName);

        start.RegisterCompilationEndAction(context =>
        {
            foreach (var group in uses.GroupBy(u => u.Type, SymbolEqualityComparer.Default))
            {
                if (!shapes.TryGetValue((INamedTypeSymbol)group.Key!, out var shape)) continue;
                var all = group.ToList();
                var patterns = all.Where(u => u.Kind == UsePattern).ToList();
                if (patterns.Count == 0 || all.Any(u => u.Kind == UseBad)
                    || all.Where(u => u.Kind == UseCovered).Any(c => !patterns.Any(p => p.Call == c.Call))
                    || patterns.Select(p => p.Filter).Distinct().Count() != 1) continue;
                var locations = new List<Location>();
                var properties = ImmutableDictionary<string, string?>.Empty.Add(FixKind, "EntityMethod")
                    .Add(JobParameters, shape.Parameters).Add(JobModifiers, shape.Modifiers).Add(JobFilter, patterns[0].Filter)
                    .Add(DropEntity, shape.UsesEntity ? null : "1");
                for (int k = 0; k < patterns.Count; k++)
                {
                    locations.Add(patterns[k].Statement!);
                    locations.Add(patterns[k].Call!);
                    properties = properties.Add(JobCall + k, patterns[k].Replacement);
                }

                if (patterns[0].FilterField?.Locations.FirstOrDefault(l => l.IsInSource) is { } field) locations.Add(field);
                string example = patterns[0].Replacement!;
                context.ReportDiagnostic(Diagnostic.Create(HandWrittenEntityJob, shape.Location, locations, properties, shape.Type.Name,
                    example.Length > 60 ? example.Substring(0, 57) + "..." : example));
            }
        });
    }

    /// <summary>The names of the structs of <paramref name="tree"/> whose base list names IForEachEntity, cached per tree.</summary>
    private static string[] EntityJobNames(SyntaxTree tree, System.Threading.CancellationToken token) =>
        EntityJobNamesOf.GetValue(tree, t =>
        {
            var found = new List<string>();
            // Declarations only: member bodies hold no struct declarations.
            foreach (var node in t.GetRoot(token).DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax))
                if (node is StructDeclarationSyntax { BaseList: { } bases } declaration
                    && bases.Types.Any(b => RightmostName(b.Type) == "IForEachEntity"))
                    found.Add(declaration.Identifier.ValueText);
            return found.ToArray();
        });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxTree, string[]> EntityJobNamesOf = new();

    private static string? RightmostName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The one IForEachEntity&lt;T1..Tn&gt; a struct implements (directly), else null.</summary>
    private static INamedTypeSymbol? EntityJobInterface(INamedTypeSymbol type)
    {
        INamedTypeSymbol? found = null;
        foreach (var face in type.Interfaces)
        {
            if (face.Name == "IForEachEntity" && Symbols.Named(face.ContainingNamespace, "EasyEcs") && face.Arity is >= 1 and <= Symbols.MaxComponents)
            {
                if (found != null) return null;
                found = face;
            }
            else if (!Symbols.Named(face, "EasyEcs.INoStructuralChanges")) return null;
        }

        return found;
    }

    /// <summary>
    /// The shape [EntityMethod] can write: a struct (not partial, not generic, no attributes) with only instance fields
    /// without initializers, a constant static WriteMask and Execute, whose body reads its fields and writes no
    /// component WriteMask leaves out. Null otherwise.
    /// </summary>
    private static JobShape? JobShapeOf(StructDeclarationSyntax declaration, INamedTypeSymbol type, SemanticModel model, System.Threading.CancellationToken token)
    {
        if (EntityJobInterface(type) is not { } face || type.IsGenericType || type.DeclaringSyntaxReferences.Length != 1
            || declaration.Modifiers.Any(SyntaxKind.PartialKeyword) || declaration.AttributeLists.Count > 0
            || type.ContainingType is { IsGenericType: true } || declaration.ParameterList != null) return null;
        MethodDeclarationSyntax? execute = null;
        PropertyDeclarationSyntax? writeMask = null;
        var fields = new List<IFieldSymbol>();
        foreach (var member in declaration.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword)
                                                       && field.AttributeLists.Count == 0 && field.Declaration.Variables.All(v => v.Initializer == null):
                    fields.AddRange(field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, token)).OfType<IFieldSymbol>());
                    break;
                case PropertyDeclarationSyntax { Identifier.ValueText: "WriteMask" } property when property.Modifiers.Any(SyntaxKind.StaticKeyword) && writeMask == null:
                    writeMask = property;
                    break;
                case MethodDeclarationSyntax { Identifier.ValueText: "Execute", TypeParameterList: null } method when execute == null && (method.Body != null || method.ExpressionBody != null):
                    execute = method;
                    break;
                default:
                    return null;
            }
        }

        int n = face.TypeArguments.Length;
        if (execute == null || model.GetDeclaredSymbol(execute, token) is not { } run || run.Parameters.Length != n + 1
            || !Symbols.Named(run.Parameters[0].Type, Symbols.Entity) || run.Parameters.Skip(1).Any(p => p.RefKind != RefKind.Ref)) return null;
        int declared = -1;
        if (writeMask != null)
        {
            if (Symbols.Returned(writeMask) is not { } value || model.GetConstantValue(value, token) is not { HasValue: true, Value: int mask }) return null;
            declared = mask;
        }

        var body = (SyntaxNode?)execute.Body ?? execute.ExpressionBody;
        if (body == null || model.GetOperation(body, token) is not { } operation) return null;
        int written = 0, used = 0;
        bool usesEntity = false;
        foreach (var o in operation.DescendantsAndSelf())
        {
            switch (o)
            {
                case IFieldReferenceOperation { Instance: IInstanceReferenceOperation, Field: var f } reference
                    when fields.Contains(f, SymbolEqualityComparer.Default):
                    if (Uses.MayWrite(reference)) return null; // a result the caller reads back: not a value it passes
                    break;
                case IInstanceReferenceOperation { Parent: not IFieldReferenceOperation }:
                    return null; // 'this' itself: the job as a value
                case IParameterReferenceOperation { Parameter: var p } when SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, run):
                    if (p.Ordinal == 0) usesEntity = true;
                    else
                    {
                        used |= 1 << (p.Ordinal - 1);
                        if (Uses.MayWrite(o)) written |= 1 << (p.Ordinal - 1);
                    }
                    break;
            }
        }

        var modifiers = new List<string>();
        for (int k = 0; k < n; k++)
        {
            bool inMask = (declared & (1 << k)) != 0;
            if (!inMask && (written & (1 << k)) != 0) return null; // the WriteMask leaves out a write: EECS037's
            // A component the body never names only filters the entity: [With<T>], not an unused parameter (EECS026).
            modifiers.Add(inMask ? "ref" : (used & (1 << k)) != 0 ? "in" : "with");
        }

        // Each field becomes a parameter, named in camelCase unless that name is taken.
        var taken = new HashSet<string>(run.Parameters.Select(p => p.Name));
        foreach (var local in body.DescendantNodes().OfType<VariableDeclaratorSyntax>()) taken.Add(local.Identifier.ValueText);
        foreach (var designation in body.DescendantNodes().OfType<SingleVariableDesignationSyntax>()) taken.Add(designation.Identifier.ValueText);
        var pairs = new List<string>();
        foreach (var field in fields)
        {
            string name = char.ToLowerInvariant(field.Name[0]) + field.Name.Substring(1);
            if (taken.Contains(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None) name = field.Name;
            if (!taken.Add(name)) return null;
            pairs.Add(field.Name + "=" + name);
        }

        return new JobShape(type, declaration.Identifier.GetLocation(), string.Join(";", pairs), string.Join(",", modifiers), usesEntity);
    }

    /// <summary>
    /// What a use of candidate job <paramref name="type"/> is: <c>var job = new Job { F = value };</c> followed by a
    /// statement whose only use of <c>job</c> is <c>entity.Run&lt;..., Job&gt;(ref job[, Filter])</c> (a Pattern, with its
    /// replacement <c>Job.Run(entity, values)</c>), the type argument of such a call (Covered), or anything else (Bad).
    /// </summary>
    private static JobUse JobUseOf(IdentifierNameSyntax name, INamedTypeSymbol type, INamedTypeSymbol entityType, SemanticModel model,
        System.Threading.CancellationToken token)
    {
        var bad = new JobUse(type, UseBad, null, null, null, null, null);
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node || node.Parent is AliasQualifiedNameSyntax alias && alias.Name == node)
            node = node.Parent;

        // The type argument of the Run call: covered by the creation that precedes it.
        if (node.Parent is TypeArgumentListSyntax { Parent: GenericNameSyntax { Identifier.ValueText: "Run" } generic } arguments
            && arguments.Arguments.Last() == node && generic.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax invocation })
            return new JobUse(type, UseCovered, null, invocation.GetLocation(), null, null, null);

        if (node.Parent is not ObjectCreationExpressionSyntax creation
            || creation.ArgumentList is { Arguments.Count: > 0 }
            || creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
            || declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1, Type: var declared } variable
            || !(declared.IsVar || model.GetTypeInfo(declared, token).Type is { } t && SymbolEqualityComparer.Default.Equals(t, type))
            || variable.Parent is not LocalDeclarationStatementSyntax { Parent: BlockSyntax block } statement
            || model.GetDeclaredSymbol(declarator, token) is not ILocalSymbol local) return bad;
        int at = block.Statements.IndexOf(statement);
        if (at + 1 >= block.Statements.Count) return bad;
        var next = block.Statements[at + 1];

        // The local's only use in its method: the Run call's 'ref job'.
        var scope = (SyntaxNode?)block.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? block;
        var references = scope.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.ValueText == local.Name
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(i, token).Symbol, local)).ToList();
        if (references.Count != 1 || !next.Span.Contains(references[0].Span)
            || references[0].Parent is not ArgumentSyntax { RefKindKeyword.ValueText: "ref", Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } list }
            || list.Arguments[0] != references[0].Parent
            || model.GetOperation(call, token) is not IInvocationOperation { TargetMethod: { Name: "Run" } run } operation
            || !SymbolEqualityComparer.Default.Equals(run.ContainingType, entityType)
            || !SymbolEqualityComparer.Default.Equals(run.TypeArguments.LastOrDefault(), type)
            || call.Expression is not MemberAccessExpressionSyntax { Expression: var receiver }
            || Symbols.Unwrap(operation.Instance) is not (ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation)) return bad;

        // The filter: none, or a static readonly field built by QueryFilter.Create() and With / Without / WithAny.
        string filter = "";
        ISymbol? field = null;
        if (list.Arguments.Count == 2)
        {
            if (operation.Arguments.Length != 2 || Symbols.Unwrap(operation.Arguments[1].Value) is not IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } f }
                || FilterAttributes(f, token) is not { } attributes) return bad;
            filter = attributes;
            field = f;
        }

        // The values, in the fields' order; side-effect free, since they are now evaluated where the call is.
        var values = new Dictionary<string, string>();
        if (creation.Initializer is { } initializer)
            foreach (var expression in initializer.Expressions)
            {
                if (expression is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax member, Right: var value } assignment
                    || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    || model.GetOperation(value, token) is not { } evaluated
                    || evaluated.DescendantsAndSelf().Any(o => o is IInvocationOperation or IObjectCreationOperation or IAssignmentOperation
                        or IIncrementOrDecrementOperation or IAnonymousFunctionOperation or IAwaitOperation)) return bad;
                values[member.Identifier.ValueText] = value.ToString();
            }

        var parts = new List<string> { receiver.ToString() };
        foreach (var member in type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsImplicitlyDeclared))
            parts.Add(values.TryGetValue(member.Name, out var v) ? v : "default");
        string replacement = $"{creation.Type}.Run({string.Join(", ", parts)})";
        return new JobUse(type, UsePattern, statement.GetLocation(), call.GetLocation(), replacement, filter, field);
    }

    /// <summary>
    /// The attributes a filter field's initializer stands for (<c>QueryFilter.Create().Without&lt;Closed&gt;()</c> is
    /// <c>Without&lt;Closed&gt;</c>), or null when it calls anything else.
    /// </summary>
    private static string? FilterAttributes(IFieldSymbol field, System.Threading.CancellationToken token)
    {
        if (!Symbols.Named(field.Type, "EasyEcs.QueryFilter")
            || field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(token) is not VariableDeclaratorSyntax { Initializer.Value: var value }) return null;
        var attributes = new List<string>();
        while (value is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0, Expression: MemberAccessExpressionSyntax access })
        {
            if (access.Name is GenericNameSyntax { Identifier.ValueText: "With" or "Without" or "WithAny" } filter)
                attributes.Insert(0, $"{filter.Identifier.ValueText}<{string.Join(", ", filter.TypeArgumentList.Arguments)}>");
            else if (access is { Name.Identifier.ValueText: "Create", Expression: IdentifierNameSyntax { Identifier.ValueText: "QueryFilter" } or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "QueryFilter" } })
                return string.Join(", ", attributes);
            else return null;
            value = access.Expression;
        }

        return null;
    }
}

/// <summary>The rules of what the generator declares for code written by hand (EasyEcsAnalyzer.Forms.cs), as one analyzer: Roslyn skips an analyzer only when every rule it reports is suppressed, so a
/// NoWarn of these rules saves their cost.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FormAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        EasyEcsAnalyzer.BadConsume, EasyEcsAnalyzer.BadUpdateInterval, EasyEcsAnalyzer.HandWrittenConsume,
        EasyEcsAnalyzer.HandWrittenEntityJob, EasyEcsAnalyzer.HandWrittenVectorLoop, EasyEcsAnalyzer.BadRecordPass,
        EasyEcsAnalyzer.HandWrittenRecordPass, EasyEcsAnalyzer.HandWrittenCollected);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeForms(context);
}
