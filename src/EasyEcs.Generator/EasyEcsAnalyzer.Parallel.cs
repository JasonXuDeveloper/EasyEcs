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
/// <c>Execute</c> of a job run by <c>ForEachParallel</c> / <c>ForEachEntityParallel</c>, that writes memory every
/// thread shares, and a parallel job whose fields the caller reads after the call (each chunk ran a copy). Both find
/// the writes with <see cref="Threads.Writes"/>.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor SharedWrite = new("EECS060", "Code running on several threads writes shared memory",
        "{0} writes {1}, which every thread running its chunks shares: the writes race{2}",
        Category, DiagnosticSeverity.Warning, true,
        "A parallel [ForEach] method, and the Execute of a job run by ForEachParallel or ForEachEntityParallel, run on " +
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
        "ForEachParallel and ForEachEntityParallel run a copy of the job per chunk, on several threads, and merge nothing " +
        "back: the job passed by ref keeps the values it had before the call. A field Execute writes (a sum, a count, a " +
        "flag) and the caller reads afterwards holds its old value. Run the job on one thread (ForEach, ForEachEntity), " +
        "which writes the job back, or write the results into the rows.",
        helpLinkUri: Help("EECS061"));

    private static void InitializeParallel(AnalysisContext context)
    {
        context.RegisterOperationBlockAction(AnalyzeParallelWrites);
        context.RegisterOperationAction(AnalyzeParallelJob, OperationKind.Invocation);
    }

    /// <summary>EECS060 in a [ForEach(Parallel = true)] method that can run in parallel (else it is EECS010's).</summary>
    private static void AnalyzeParallelWrites(OperationBlockAnalysisContext context)
    {
        if (context.OwningSymbol is not IMethodSymbol method || !Symbols.IsForEach(method) || !Symbols.IsParallel(method)) return;
        var body = context.OperationBlocks.FirstOrDefault(b => b is IMethodBodyOperation or IBlockOperation);
        var syntax = body?.Syntax as MethodDeclarationSyntax ?? body?.Syntax.Parent as MethodDeclarationSyntax;
        if (body == null || syntax == null || Symbols.ParallelProblem(method, syntax, body.SemanticModel!) != null) return;
        foreach (var write in Threads.Writes(body, job: false))
            if (Races(write))
                context.ReportDiagnostic(Diagnostic.Create(SharedWrite, write.Operation.Syntax.GetLocation(),
                    $"[ForEach(Parallel = true)] method '{method.Name}'", write.What, ""));
    }

    /// <summary>
    /// A shared write that races: not a constant every thread writes the same, and not a system's own mutable static
    /// (state in a system is EECS007's, whose fix moves it out).
    /// </summary>
    private static bool Races(Threads.Write write) =>
        write is { Shared: true, Constant: false }
        && !(write.Symbol is IFieldSymbol { IsStatic: true, IsReadOnly: false } or IPropertySymbol { IsStatic: true }
             && Symbols.IsSystem(write.Symbol.ContainingType));

    /// <summary>
    /// EECS060 and EECS061 at a <c>ForEachParallel</c> / <c>ForEachEntityParallel</c> call: the job's Execute writes
    /// shared memory, or a field of the job the caller reads after the call.
    /// </summary>
    private static void AnalyzeParallelJob(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (call.TargetMethod is not { Name: "ForEachParallel" or "ForEachEntityParallel", TypeArguments.Length: > 1 } method
            || !Symbols.Named(method.ContainingType, Symbols.World)
            || method.TypeArguments[method.TypeArguments.Length - 1] is not INamedTypeSymbol job
            || Execute(job, method) is not { } execute
            || execute.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not MethodDeclarationSyntax declaration
            || call.SemanticModel is not { } model) return;
        var bodyModel = declaration.SyntaxTree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(declaration.SyntaxTree);
        if (bodyModel.GetOperation(declaration, context.CancellationToken) is not { } body) return;
        var writes = Threads.Writes(body, job: true).ToList();

        if (writes.FirstOrDefault(Races) is { } shared)
            context.ReportDiagnostic(Diagnostic.Create(SharedWrite, call.Syntax.GetLocation(),
                $"Job '{job.Name}', run by {method.Name},", shared.What, $" (Execute: '{Shorten(shared.Operation.Syntax)}')"));

        // EECS061: the first use of the job local after the call reads a field Execute writes; a job that merges
        // its chunks' copies back (Merges, as generated jobs with Reduce<T> do) keeps them.
        if (job.GetMembers("Merges").Length > 0) return;
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
            var fix = call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: var name } }
                      && call.Arguments.All(a => a.Parameter?.Name != "chunkRows" || a.IsImplicit)
                ? Fix(call.Syntax.ReplaceToken(name.Identifier, SyntaxFactory.Identifier(method.Name.Substring(0, method.Name.Length - "Parallel".Length))).ToString(),
                    "Run it on one thread")
                : null;
            context.ReportDiagnostic(Diagnostic.Create(LostJobWrite, call.Syntax.GetLocation(), [read.Syntax.GetLocation()], fix,
                job.Name, method.Name, field.Name, Shorten(read.Syntax)));
            return;
        }
    }

    /// <summary>The job's Execute for the components of a parallel call (its interface: IForEach&lt;…&gt; or IForEachEntity&lt;…&gt;).</summary>
    private static IMethodSymbol? Execute(INamedTypeSymbol job, IMethodSymbol call)
    {
        string face = call.Name == "ForEachParallel" ? "IForEach" : "IForEachEntity";
        int n = call.TypeArguments.Length - 1;
        foreach (var candidate in job.AllInterfaces)
        {
            if (candidate.Name != face || candidate.TypeArguments.Length != n || !Symbols.IsJob(candidate)) continue;
            bool same = true;
            for (int i = 0; i < n && same; i++) same = SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[i], call.TypeArguments[i]);
            if (same && candidate.GetMembers("Execute").FirstOrDefault() is { } member)
                return job.FindImplementationForInterfaceMember(member) as IMethodSymbol;
        }

        return null;
    }
}
