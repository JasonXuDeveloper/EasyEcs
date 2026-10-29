using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of lambda passes (<see cref="LambdaPass"/>, EECS095-EECS097): a call the generator cannot compile, which
/// would throw (EECS095; a capturing lambda's fix passes what it captures as values); a body that runs through its
/// delegate because it names what generated code cannot reach (EECS096; the fix makes the private member internal); a body
/// left without a vector loop, with <see cref="Kernel.Vectorize"/>'s reason (EECS097); and EECS049 for one whose own filters
/// and parameters no entity passes (a lambda pass takes no group's filters).
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor BadLambdaPass = new("EECS095", "A lambda pass the generator cannot compile",
        "world.{0}(…) gets no job and throws: {1}",
        Category, DiagnosticSeverity.Error, true,
        "world.ForEach(static (ref Position p, in Velocity v) => …) runs the job the generator writes at the call: the " +
        "lambda's body copied into the method a [ForEach] job calls, the values passed before the lambda in the job's " +
        "fields. It needs a static lambda written at the call (a capturing one would allocate a closure and a delegate " +
        "per call), whose parameters are those of a [ForEach] method plus the values before it, in order and of their " +
        "types, and whose attributes are the filters of a [ForEach] method (a change filter takes its tick as the first value " +
        "before the lambda). A call it cannot compile has no job, " +
        "and World.ForEach(Delegate) throws.", helpLinkUri: Help("EECS095"));

    public static readonly DiagnosticDescriptor DelegateLambdaPass = new("EECS096", "A lambda pass that runs through its delegate",
        "The lambda of world.{0}(…) runs through its delegate, a call per row and no vector loop: {1}",
        Category, DiagnosticSeverity.Warning, true,
        "The generator copies a lambda pass's body into a generated file, where it is inlined into the job's row loop and " +
        "may become a vector loop. A body that names a private or protected member of the type around it, or a file-local " +
        "type, cannot be copied (constants can: they are written as their values), so the job calls the lambda through " +
        "its delegate on every row. Make the member internal, or move the body into a [ForEach] method of the type, whose " +
        "job reaches its private members.", helpLinkUri: Help("EECS096"));

    public static readonly DiagnosticDescriptor ScalarLambdaPass = new("EECS097", "A lambda pass that runs one row at a time",
        "The lambda of world.{0}(…) runs one row at a time, without a vector loop: {1}",
        Category, DiagnosticSeverity.Info, true,
        "A lambda pass gets the vector loop a [ForEach] method with its parameters and body would get (EECS055 lists what " +
        "qualifies). This lambda takes components of plain values but misses one of those: the message names the first.",
        helpLinkUri: Help("EECS097"));

    /// <summary>EECS095's property: the values a capturing lambda reads, passed before it by the fix (names, comma-separated).</summary>
    public const string CapturedValues = "CapturedValues";

    internal static void InitializeLambdas(AnalysisContext context) =>
        context.RegisterOperationAction(AnalyzeLambdaPass, OperationKind.Invocation);

    private static void AnalyzeLambdaPass(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.TargetMethod.Name is not (LambdaPass.ForEach or LambdaPass.ForEachParallel) || LambdaPass.Read(invocation) is not { } call
            || invocation.SemanticModel is not { } model)
            return;
        string name = invocation.TargetMethod.Name;
        // The lambda's parameters: what the rules are about (the call's name for a body that is no lambda).
        var location = (call.Syntax as Microsoft.CodeAnalysis.CSharp.Syntax.ParenthesizedLambdaExpressionSyntax)?.ParameterList.GetLocation()
            ?? (call.Invocation.Expression as Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax)?.Name.GetLocation()
            ?? invocation.Syntax.GetLocation();
        if (LambdaPass.Problem(call, model, context.CancellationToken, out var captured) is { } problem)
        {
            var properties = ImmutableDictionary<string, string?>.Empty;
            // The fix passes locals and parameters the lambda only reads; 'this' or a write leaves it to the user.
            if (captured.Count > 0 && captured.All(c => c is ILocalSymbol or IParameterSymbol { IsThis: false })
                && captured.Count + call.Values.Length <= ArityGenerator.MaxLambdaValues && !WritesCaptured(call, captured))
                properties = properties.Add(CapturedValues, string.Join(",", captured.Select(c => c.Name)));
            context.ReportDiagnostic(Diagnostic.Create(BadLambdaPass, location, properties, name, problem));
            return;
        }

        // EECS049: the lambda's parameters and its own filters (a lambda pass takes no group's); a pass that visits
        // nothing has no delegate or loop worth a word.
        if (NeverPassed(context.ReportDiagnostic, $"The lambda of world.{name}(…)", Symbols.Requirements(call.Lambda!, context.CancellationToken, groups: false),
                AnyOf(call.Lambda!, false, context.CancellationToken), location, "the pass visits no entity"))
            return;

        if (LambdaPass.Unreachable(call, model, context.CancellationToken, out var blocking) is { } unreachable)
        {
            // The fix makes a private member declared in this file internal: its declaration is the additional location.
            var member = blocking is { DeclaredAccessibility: Accessibility.Private } and not INamedTypeSymbol
                         && blocking.DeclaringSyntaxReferences.Length == 1 && blocking.DeclaringSyntaxReferences[0].SyntaxTree == invocation.Syntax.SyntaxTree
                ? new[] { Location.Create(blocking.DeclaringSyntaxReferences[0].SyntaxTree, blocking.DeclaringSyntaxReferences[0].Span) }
                : null;
            context.ReportDiagnostic(Diagnostic.Create(DelegateLambdaPass, location, member, name, unreachable));
            return;
        }

        // A lambda the generator gives no vector loop runs its row job; its reads of other entities the generator failed to
        // read (Chases.Of, the same call) leave that job without its prefetching twin: a generator bug, reported first.
        bool eligible = LambdaPass.IsStable(call.Function!) && Kernel.LooksEligible(call.Lambda!, true);
        string? reason = null;
        if (eligible && Kernel.Vectorize(call.Lambda!, call.Function!, call.Parallel, true, model, static (_, _) => "", context.CancellationToken, out reason) != null) return;
        Chases.Of(call.Function!, out string? failed);
        if (failed != null || eligible) context.ReportDiagnostic(Diagnostic.Create(ScalarLambdaPass, location, name, failed ?? reason));
    }

    /// <summary>Whether the lambda assigns, increments or passes by ref one of the variables it captures.</summary>
    private static bool WritesCaptured(LambdaPass.Call call, System.Collections.Generic.List<ISymbol> captured) =>
        call.Function!.Body.Descendants().Any(op => op switch
        {
            ILocalReferenceOperation local => captured.Contains(local.Local, SymbolEqualityComparer.Default) && Uses.MayWrite(local),
            IParameterReferenceOperation parameter => captured.Contains(parameter.Parameter, SymbolEqualityComparer.Default) && Uses.MayWrite(parameter),
            _ => false,
        });
}

/// <summary>The rules of lambda passes (EasyEcsAnalyzer.Lambdas.cs), as one analyzer: Roslyn skips an analyzer only when every rule it reports is suppressed, so a
/// NoWarn of these rules saves their cost.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LambdaPassAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        EasyEcsAnalyzer.BadLambdaPass, EasyEcsAnalyzer.DelegateLambdaPass, EasyEcsAnalyzer.ScalarLambdaPass,
        EasyEcsAnalyzer.NeverMatches);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeLambdas(context);
}
