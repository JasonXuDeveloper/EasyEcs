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
/// The rules for code that runs on several threads at once (EECS060, EECS061): a parallel [ForEach] method, or the
/// <c>Execute</c> of a job run by <c>ForEachParallel</c> / <c>ForEachEntityParallel</c> / <c>ForEachBatchParallel</c>, that writes memory every
/// thread shares, and a parallel job whose fields the caller reads after the call (each chunk ran a copy). Both find
/// the writes with <see cref="Threads.Writes"/>.
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor SharedWrite = new("EECS060", "Code running on several threads writes shared memory",
        "{0} writes {1}, which every thread running its chunks shares: the writes race{2}",
        Category, DiagnosticSeverity.Warning, true,
        "A parallel [ForEach] method, and the Execute of a job run by ForEachParallel, ForEachEntityParallel, ForEachBatchParallel or " +
        "ForParallel, run on " +
        "several threads at once, one chunk of rows each. A static field or auto-property, an object a context parameter " +
        "or a job's field refers to, is the same memory for all of them: an increment or a compound assignment loses " +
        "updates, a collection's Add corrupts it, and which plain write is kept depends on the threads' timing. Write the " +
        "result into a component of the row (and combine the rows in a system after the pass), or make the write atomic " +
        "(Interlocked, a lock). A constant written by every thread, [ThreadStatic] fields, concurrent collections, writes " +
        "inside a lock and elements of arrays and lists (rows may own different ones) are not reported.",
        helpLinkUri: Help("EECS060"));

    public static readonly DiagnosticDescriptor LostJobWrite = new("EECS061", "A parallel job's field read after the pass",
        "'{0}' is run by {1}, which gives each chunk its own copy of the job, so what Execute writes to its field '{2}' never reaches '{3}', read after the call",
        Category, DiagnosticSeverity.Warning, true,
        "ForEachParallel, ForEachEntityParallel, ForEachBatchParallel and ForParallel run a copy of the job per chunk, on several " +
        "threads, and merge nothing back unless the job merges (Merges): the job passed by ref keeps the values it had before the " +
        "call. A field Execute writes (a sum, a count, a flag) and the caller reads afterwards holds its old value. Run the job " +
        "on one thread (ForEach, ForEachEntity, ForEachBatch), " +
        "which writes the job back, or write the results into the rows. A job's own Merges that returns the constant false " +
        "merges nothing and is reported; one whose value is not a constant (an auto-property, a field, a computation) is taken " +
        "as merging and is not.",
        helpLinkUri: Help("EECS061"));

    public static readonly DiagnosticDescriptor SharedRandom = new("EECS071", "Random numbers a [ForEach] method cannot repeat",
        "[ForEach] method '{0}' draws from '{1}', {2}: draw from EntityRandom (a pure function of a seed, the entity, the tick and a stream)",
        Category, DiagnosticSeverity.Warning, true,
        "System.Random gives numbers that depend on things outside the world. Random.Shared is seeded differently on every " +
        "run, and in a parallel pass its numbers go to rows in the order the threads happen to ask, so a replay, a " +
        "test or another machine sees other results. A new Random() per row allocates, is seeded from the clock unless " +
        "given a seed, and a seed given gives every row the same numbers. A Random a field holds is shared by every row: a " +
        "row's numbers depend on how many rows drew before it, and in a parallel pass several threads draw at once " +
        "(System.Random is not thread-safe). EntityRandom (a singleton holding a seed and a tick, " +
        "or its static members) is a pure function of the seed, the entity, the tick and a stream: the same numbers on " +
        "every run, whatever the threads. The fix takes the world's EntityRandom and the entity, and gives each draw of the " +
        "method its own stream.", helpLinkUri: Help("EECS071"));

    public static readonly DiagnosticDescriptor SharedStream = new("EECS088", "EntityRandom draws of one stream",
        "'{0}' {1}: {2}",
        Category, DiagnosticSeverity.Warning, true,
        "An EntityRandom draw is a pure function of the seed, the entity, the tick and the stream: the same four give the " +
        "same bits. Two draws for one entity with the same stream in one tick are the same number, or numbers made from " +
        "the same bits (a Chance and a Range of stream 0 agree), and a draw with a constant stream in a loop gives every " +
        "iteration the same number. Draws in the two branches of an if (or two arms of a switch) never both run and may " +
        "share a stream. Give each draw a stream of its own, and a draw in a loop the iteration in its stream " +
        "(stream + (uint)i, with the other draws' streams outside that range).",
        helpLinkUri: Help("EECS088"));

    /// <summary>EECS071's properties for its code fix: the draw, and the parameters to add (null: the method has them).</summary>
    public const string RandomText = "Text", RandomSingleton = "Random", RandomEntity = "Entity";

    internal static void InitializeParallel(AnalysisContext context)
    {
        context.RegisterOperationBlockAction(AnalyzeParallelWrites);
        context.RegisterOperationBlockAction(AnalyzeRandom);
        context.RegisterOperationAction(AnalyzeParallelJob, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeStreams, OperationKind.Invocation);
    }

    // ---------------- EECS088: EntityRandom streams ----------------

    /// <summary>
    /// An EntityRandom draw (Bits, Float, Range, Chance; the instance members or the static ones taking the seed and the
    /// tick), its stream when constant, and the text that names the same bits: the receiver (or seed and tick), the entity.
    /// </summary>
    private static (uint? Stream, string Source, IArgumentOperation StreamArgument)? Draw(IInvocationOperation call)
    {
        if (call.TargetMethod is not { Name: "Bits" or "Float" or "Range" or "Chance" } method
            || !Symbols.Named(method.ContainingType, "EasyEcs.EntityRandom")) return null;
        IArgumentOperation? Argument(string name) => call.Arguments.FirstOrDefault(a => a.Parameter?.Name == name);
        if (Argument("stream") is not { } stream || Argument("entity") is not { } entity) return null;
        string Text(IOperation? operation) => operation == null ? "" : Stripped(operation).Syntax.ToString().Replace(" ", "");
        string source = method.IsStatic ? Text(Argument("seed")?.Value) + "|" + Text(Argument("tick")?.Value) : Text(call.Instance);
        var constant = stream.Value.ConstantValue;
        return (constant is { HasValue: true, Value: uint or int } ? System.Convert.ToUInt32(constant.Value) : null,
            source + "|" + Text(entity.Value), stream);
    }

    /// <summary>
    /// EECS088 on the first draw of a [ForEach] or [ForEntity] method's body (or a job's Execute) (the others are seen from it): a draw whose stream, entity and source an
    /// earlier draw of the body shares, unless the two sit in the two branches of an if or two arms of a switch (the fix:
    /// the next stream no draw uses); a draw with a constant stream in a loop, of an entity and source the loop does not
    /// change (no fix: the stream needs the iteration).
    /// </summary>
    private static void AnalyzeStreams(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        // In the code a pass runs per row, where EntityRandom belongs (a test comparing a draw with itself is elsewhere).
        if (Draw(call) == null || context.ContainingSymbol is not IMethodSymbol method
            || !Symbols.IsGeneratedJob(method) && Symbols.JobInterface(method) == null) return;
        var root = Symbols.Root(call);
        var draws = root.Descendants().OfType<IInvocationOperation>().Select(c => (Call: c, Draw: Draw(c)))
            .Where(d => d.Draw != null).OrderBy(d => d.Call.Syntax.SpanStart).ToList();
        if (draws[0].Call != call) return;

        var used = new HashSet<uint>(draws.Where(d => d.Draw!.Value.Stream != null).Select(d => d.Draw!.Value.Stream!.Value));
        uint next = 0;
        for (int i = 0; i < draws.Count; i++)
        {
            var (draw, (stream, source, argument)) = (draws[i].Call, draws[i].Draw!.Value);
            if (stream == null) continue;
            var earlier = draws.Take(i).FirstOrDefault(d => d.Draw!.Value.Stream == stream && d.Draw.Value.Source == source && !Exclusive(d.Call, draw));
            if (earlier.Call != null)
            {
                while (used.Contains(next)) next++;
                used.Add(next);
                context.ReportDiagnostic(Diagnostic.Create(SharedStream, argument.Value.Syntax.GetLocation(), [earlier.Call.Syntax.GetLocation()],
                    Fix(next.ToString(System.Globalization.CultureInfo.InvariantCulture), "Give it a stream of its own"),
                    Shorten(draw.Syntax), $"draws stream {stream} for the same entity as '{Shorten(earlier.Call.Syntax)}'",
                    "both come from the same bits; give it a stream of its own"));
                continue;
            }

            if (SameEveryIteration(draw) != null)
                context.ReportDiagnostic(Diagnostic.Create(SharedStream, argument.Value.Syntax.GetLocation(), Shorten(draw.Syntax),
                    $"draws stream {stream} in a loop, for an entity the loop does not change",
                    "every iteration gets the same number; add the iteration to the stream (stream + (uint)i)"));
        }
    }

    /// <summary>Whether two operations of one body never both run: the two branches of an if (or ?:), two arms of a switch.</summary>
    private static bool Exclusive(IOperation a, IOperation b)
    {
        var above = new List<IOperation>();
        for (var o = a; o != null; o = o.Parent) above.Add(o);
        IOperation? fromB = b;
        while (fromB?.Parent != null && !above.Contains(fromB.Parent)) fromB = fromB.Parent;
        var common = fromB?.Parent;
        if (common == null) return false;
        var fromA = above[above.IndexOf(common) - 1];
        return common switch
        {
            IConditionalOperation c => c.WhenFalse != null && (fromA == c.WhenTrue && fromB == c.WhenFalse || fromA == c.WhenFalse && fromB == c.WhenTrue),
            ISwitchOperation or ISwitchExpressionOperation => fromA != fromB,
            _ => false,
        };
    }

    /// <summary>
    /// The loop <paramref name="draw"/> runs in whose iterations give it the same arguments: none of the arguments but
    /// the range's bounds reads a local the loop declares or writes (its variable, a local of its body).
    /// </summary>
    private static ILoopOperation? SameEveryIteration(IInvocationOperation draw)
    {
        ILoopOperation? loop = null;
        for (var o = draw.Parent; o != null && loop == null; o = o.Parent)
            if (o is ILoopOperation l && (l is not IForEachLoopOperation each || !each.Collection.Syntax.Span.Contains(draw.Syntax.Span))) loop = l;
            else if (o is IAnonymousFunctionOperation or ILocalFunctionOperation) return null;
        if (loop == null) return null;
        var span = loop.Syntax.Span;
        var written = new HashSet<ILocalSymbol>(loop.Descendants().Select(o => o switch
        {
            IAssignmentOperation { Target: ILocalReferenceOperation target } => target.Local,
            IIncrementOrDecrementOperation { Target: ILocalReferenceOperation target } => target.Local,
            _ => null,
        }).Where(l => l != null)!, SymbolEqualityComparer.Default);
        var inputs = draw.Arguments.Where(a => a.Parameter?.Name is not ("min" or "max" or "probability")).Select(a => a.Value)
            .Concat(draw.Instance == null ? [] : [draw.Instance]);
        foreach (var input in inputs)
            foreach (var reference in input.DescendantsAndSelf().OfType<ILocalReferenceOperation>())
                if (written.Contains(reference.Local) || reference.Local.DeclaringSyntaxReferences.Any(r => span.Contains(r.Span)))
                    return null;
        return loop;
    }

    /// <summary>EECS060 in a [ForEach(Parallel = true)] method that can run in parallel (else it is EECS010's).</summary>
    private static void AnalyzeParallelWrites(OperationBlockAnalysisContext context)
    {
        if (context.OwningSymbol is not IMethodSymbol method || !Symbols.IsForEach(method) || !Symbols.IsParallel(method)) return;
        var body = context.OperationBlocks.FirstOrDefault(b => b is IMethodBodyOperation or IBlockOperation);
        var syntax = body?.Syntax as MethodDeclarationSyntax ?? body?.Syntax.Parent as MethodDeclarationSyntax;
        if (body == null || syntax == null || BodyFacts.ParallelProblem(method, syntax, body.SemanticModel!) != null) return;
        foreach (var write in Threads.Writes(body, job: false))
            if (Races(write))
                context.ReportDiagnostic(Diagnostic.Create(SharedWrite, write.Operation.Syntax.GetLocation(),
                    $"[ForEach(Parallel = true)] method '{method.Name}'", write.What, ""));
    }

    /// <summary>
    /// EECS071: <c>Random.Shared</c>, <c>new Random()</c> or a <c>Random</c> a field holds (<see cref="Draws"/>) in a [ForEach] method. A draw the fix can translate
    /// (<c>NextSingle</c>, <c>NextDouble</c>, <c>Next</c> with 0 to 2 arguments, on either receiver) is reported on the
    /// call, with its replacement: the method's <c>in EntityRandom</c> and <c>Entity</c> parameters (added when missing), and the
    /// next stream no draw of the method uses yet, so draws fixed one at a time still get one stream each.
    /// </summary>
    private static void AnalyzeRandom(OperationBlockAnalysisContext context)
    {
        if (context.OwningSymbol is not IMethodSymbol method || !Symbols.IsForEach(method)) return;
        var body = context.OperationBlocks.FirstOrDefault(b => b is IMethodBodyOperation or IBlockOperation);
        if (body == null) return;
        var draws = body.Descendants().Where(Draws).OrderBy(o => o.Syntax.SpanStart).ToList();
        if (draws.Count == 0) return;

        bool outside = Symbols.IsRunner(method);
        var rng = method.Parameters.FirstOrDefault(p => Symbols.Named(p.Type, "EasyEcs.EntityRandom") && Symbols.Classify(p, outside) == Symbols.ParamKind.Singleton);
        var entity = method.Parameters.FirstOrDefault(p => Symbols.Classify(p, outside) == Symbols.ParamKind.Entity);
        var names = new HashSet<string>(method.Parameters.Select(p => p.Name));
        string rngName = rng?.Name ?? Free("random"), entityName = entity?.Name ?? Free("entity");
        // The streams the method's EntityRandom draws already use (a constant second argument), so a fix never repeats one.
        var used = new HashSet<int>(body.Descendants().OfType<IInvocationOperation>()
            .Where(c => Symbols.Named(c.TargetMethod.ContainingType, "EasyEcs.EntityRandom") && c.Arguments.Length > 1)
            .Select(c => c.Arguments.FirstOrDefault(a => a.Parameter?.Name == "stream")?.Value.ConstantValue.Value)
            .Where(v => v is uint or int).Select(v => System.Convert.ToInt32(v)));
        bool fixable = !Symbols.IsBatch(method); // a batch method has no Entity parameter (its EntitySpan names the rows)
        bool parallel = Symbols.IsParallel(method);
        int next = 0;
        foreach (var draw in draws)
        {
            string why = draw is IPropertyReferenceOperation { Property.Name: "Shared", Property.IsStatic: true }
                ? parallel ? "which every thread of the pass shares, in the order the threads ask, and which is seeded anew on every run"
                    : "which is seeded anew on every run"
                : draw is not IObjectCreationOperation creation
                    ? parallel ? "a generator every row shares, which several threads draw from at once (System.Random is not thread-safe)"
                        : "a generator every row shares, so a row's numbers depend on how many rows drew before it"
                : creation.Arguments.Length == 0
                    ? "which allocates a generator per row, seeded from the clock"
                    : "which allocates a generator per row, and a seed gives every row the same numbers";
            string? text = null;
            if (fixable && draw.Parent is IInvocationOperation call && call.Instance == draw && !call.Arguments.Any(a => a.ArgumentKind != ArgumentKind.Explicit))
            {
                var args = call.Arguments.Select(a => a.Value.Syntax.ToString()).ToArray();
                while (used.Contains(next)) next++;
                string id = $"{entityName}.Id, {next}";
                text = (call.TargetMethod.Name, args.Length) switch
                {
                    ("NextSingle" or "NextDouble", 0) => $"{rngName}.Float({id})",
                    ("Next", 0) => $"{rngName}.Range({id}, 0, int.MaxValue)",
                    ("Next", 1) => $"{rngName}.Range({id}, 0, {args[0]})",
                    ("Next", 2) => $"{rngName}.Range({id}, {args[0]}, {args[1]})",
                    _ => null,
                };
                if (text != null) used.Add(next);
            }

            var location = text != null ? draw.Parent!.Syntax.GetLocation() : draw.Syntax.GetLocation();
            var properties = text == null ? null : ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Random").Add(RandomText, text)
                .Add(RandomSingleton, rng == null ? rngName : null).Add(RandomEntity, entity == null ? entityName : null);
            context.ReportDiagnostic(Diagnostic.Create(SharedRandom, location, properties, method.Name, Shorten(draw.Syntax), why));
        }

        string Free(string name)
        {
            while (names.Contains(name)) name = "_" + name;
            return name;
        }
    }

    /// <summary>
    /// What EECS071 reports: <c>Random.Shared</c>, <c>new Random(…)</c>, or a draw from a <c>Random</c> a field or
    /// property holds (the receiver of a call: a generator every row shares).
    /// </summary>
    private static bool Draws(IOperation operation) => operation switch
    {
        IPropertyReferenceOperation { Property: { Name: "Shared", IsStatic: true } shared } => Symbols.Named(shared.ContainingType, "System.Random"),
        IObjectCreationOperation { Type: var created } => Symbols.Named(created, "System.Random"),
        IFieldReferenceOperation or IPropertyReferenceOperation => Symbols.Named(operation.Type, "System.Random")
                                                                  && operation.Parent is IInvocationOperation call && call.Instance == operation,
        _ => false,
    };

    /// <summary>
    /// A shared write that races: not a constant every thread writes the same, and not a system's own mutable static
    /// (state in a system is EECS007's, whose fix moves it out).
    /// </summary>
    private static bool Races(Threads.Write write) =>
        write is { Shared: true, Constant: false }
        && !(write.Symbol is IFieldSymbol { IsStatic: true, IsReadOnly: false } or IPropertySymbol { IsStatic: true }
             && Symbols.IsSystem(write.Symbol.ContainingType));

    /// <summary>
    /// EECS060 and EECS061 at a <c>ForEachParallel</c> / <c>ForEachEntityParallel</c> / <c>ForEachBatchParallel</c> /
    /// <c>ForParallel</c> call: the job's Execute writes shared memory, or a field of the job the caller reads after the call.
    /// </summary>
    private static void AnalyzeParallelJob(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (call.TargetMethod is not { Name: "ForEachParallel" or "ForEachEntityParallel" or "ForEachBatchParallel" or "ForParallel", TypeArguments.Length: > 0 } method
            || !Symbols.Named(method.ContainingType, Symbols.World)
            || method.TypeArguments[method.TypeArguments.Length - 1] is not INamedTypeSymbol job
            || Execute(job, method) is not ({ } execute, var face)
            || execute.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not MethodDeclarationSyntax declaration
            || call.SemanticModel is not { } model) return;
        var bodyModel = Symbols.ModelOf(declaration.SyntaxTree, model);
        if (bodyModel.GetOperation(declaration, context.CancellationToken) is not { } body) return;
        var writes = Threads.Writes(body, job: true).ToList();

        if (writes.FirstOrDefault(Races) is { } shared)
            context.ReportDiagnostic(Diagnostic.Create(SharedWrite, call.Syntax.GetLocation(),
                $"Job '{job.Name}', run by {method.Name},", shared.What, $" (Execute: '{Shorten(shared.Operation.Syntax)}')"));

        // EECS061: the first use of the job local after the call reads a field Execute writes; a job that merges
        // its chunks' copies back (Merges, as generated jobs with Reduce<T> do) keeps them.
        if (Merges(job, face, model, context.CancellationToken)) return;
        var own = new HashSet<ISymbol>(writes.Where(w => !w.Shared).Select(w => w.Symbol), SymbolEqualityComparer.Default);
        if (own.Count == 0 || call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "job")?.Value is not ILocalReferenceOperation { Local: var local }) return;
        int after = call.Syntax.Span.End;
        foreach (var use in Symbols.Root(call).Descendants().OfType<ILocalReferenceOperation>()
                     .Where(r => SymbolEqualityComparer.Default.Equals(r.Local, local) && r.Syntax.SpanStart >= after)
                     .OrderBy(r => r.Syntax.SpanStart))
        {
            if (use.Parent is not IFieldReferenceOperation { Field: var field } read || read.Parent is ISimpleAssignmentOperation { Target: var t } && t == read)
                break; // the job itself used again, or the field set: what follows may see that, not the call's state
            if (!own.Contains(field)) continue;
            var fix = method.Name != "ForParallel" // no serial twin: the caller calls Execute itself
                      && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: var name } }
                      && call.Arguments.All(a => a.Parameter?.Name != "chunkRows" || a.IsImplicit)
                ? Fix(call.Syntax.ReplaceToken(name.Identifier, SyntaxFactory.Identifier(method.Name.Substring(0, method.Name.Length - "Parallel".Length))).ToString(),
                    "Run it on one thread")
                : null;
            context.ReportDiagnostic(Diagnostic.Create(LostJobWrite, call.Syntax.GetLocation(), [read.Syntax.GetLocation()], fix,
                job.Name, method.Name, field.Name, Shorten(read.Syntax)));
            return;
        }
    }

    /// <summary>
    /// The job's Execute for the type arguments of a parallel call before the job's, and the interface it implements:
    /// IForEach&lt;…&gt;, IForEachEntity&lt;…&gt; or IForEachBatch&lt;…&gt; of the components; IForRange, or IForSpan&lt;T&gt;
    /// of the element, for ForParallel.
    /// </summary>
    private static (IMethodSymbol? Execute, INamedTypeSymbol Face)? Execute(INamedTypeSymbol job, IMethodSymbol call)
    {
        int n = call.TypeArguments.Length - 1;
        string face = call.Name switch
        {
            "ForEachParallel" => "IForEach",
            "ForEachEntityParallel" => "IForEachEntity",
            "ForEachBatchParallel" => "IForEachBatch",
            _ => n == 0 ? "IForRange" : "IForSpan",
        };
        foreach (var candidate in job.AllInterfaces)
        {
            if (candidate.Name != face || candidate.TypeArguments.Length != n || !Symbols.Named(candidate.ContainingNamespace, "EasyEcs")) continue;
            bool same = true;
            for (int i = 0; i < n && same; i++) same = SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[i], call.TypeArguments[i]);
            if (same && candidate.GetMembers("Execute").FirstOrDefault() is { } member)
                return (job.FindImplementationForInterfaceMember(member) as IMethodSymbol, candidate);
        }

        return null;
    }

    /// <summary>
    /// Whether the job merges its chunks' copies back into the caller's (<c>Merges</c>, implicitly or explicitly
    /// implemented: generated jobs with a <c>Reduce&lt;T&gt;</c> do). The interface's own default merges nothing, and
    /// neither does a <c>Merges</c> of the job that returns the constant <c>false</c>; one whose value is not a
    /// constant may merge, so the rule stays quiet (no false positive).
    /// </summary>
    private static bool Merges(INamedTypeSymbol job, INamedTypeSymbol face, SemanticModel model, System.Threading.CancellationToken token)
    {
        var merges = face.GetMembers("Merges").FirstOrDefault() is { } member
            ? job.FindImplementationForInterfaceMember(member)
            : job.GetMembers("Merges").FirstOrDefault();
        if (merges is null || !SymbolEqualityComparer.Default.Equals(merges.ContainingType, job)) return false;
        return merges is not IPropertySymbol property || Returned(property, model, token) is not false;
    }

    /// <summary>
    /// The constant a property's getter returns: <c>=&gt; c</c>, <c>get =&gt; c</c> or <c>get { return c; }</c>, with
    /// <c>c</c> a constant of the language (a literal, a const); null for any other body (an auto-property, a field read).
    /// </summary>
    private static bool? Returned(IPropertySymbol property, SemanticModel model, System.Threading.CancellationToken token)
    {
        var value = property.GetMethod?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(token) switch
        {
            ArrowExpressionClauseSyntax arrow => arrow.Expression,
            AccessorDeclarationSyntax { ExpressionBody: { } arrow } => arrow.Expression,
            AccessorDeclarationSyntax { Body.Statements: { Count: 1 } statements } => (statements[0] as ReturnStatementSyntax)?.Expression,
            _ => null,
        };
        return value is not null && Symbols.ModelOf(value.SyntaxTree, model).GetConstantValue(value, token) is { HasValue: true, Value: bool constant }
            ? constant
            : null;
    }
}

/// <summary>The rules of code on several threads and of random streams (EasyEcsAnalyzer.Parallel.cs), as one analyzer: Roslyn skips an analyzer only when every rule it reports is suppressed, so a
/// NoWarn of these rules saves their cost.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ParallelAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        EasyEcsAnalyzer.LostJobWrite, EasyEcsAnalyzer.SharedRandom, EasyEcsAnalyzer.SharedStream,
        EasyEcsAnalyzer.SharedWrite);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeParallel(context);
}
