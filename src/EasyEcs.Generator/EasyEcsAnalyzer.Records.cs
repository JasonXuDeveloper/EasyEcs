using System.Collections.Concurrent;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// EECS092: the records of an <c>Emit&lt;T&gt;</c> parameter that no code of the compilation clears. Passes append to
/// singleton <c>Emitted&lt;T&gt;</c> and never clear it, since only the system that applies the records knows when they
/// are applied; that system's <c>Clear()</c> is the one line users write around a data event, and the one they forget.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor UnclearedRecords = new("EECS092", "Emitted records that no code clears",
        "{0} emits {1} records, but no code clears Emitted<{1}>: each update adds its records to all the earlier ones, so a reader applies them again and the list grows; clear world.GetSingleton<Emitted<{1}>>().Items in the system that applies them, once applied",
        Category, DiagnosticSeverity.Warning, true,
        "An Emit<T> parameter appends the records of each pass to singleton Emitted<T>, and passes never clear it: the " +
        "system that applies the records does, once it has applied them (Items.Clear(), or RemoveAt / RemoveAtSwapBack of " +
        "the ones it consumed, or RemoveSingleton<Emitted<T>>()). When no code of the compilation does, the records of " +
        "every update stay: a reader applies each of them again on every later update, and the list grows without " +
        "bound. Reported at the end of the compilation for a record type it declares, unless another assembly could " +
        "clear the records (a public record type of a library).",
        helpLinkUri: Help("EECS092"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>
    /// EECS092: the record types the compilation's [ForEach] methods emit (and the first parameter emitting each), the
    /// ones its code clears, and at the end the emitted ones nothing clears.
    /// </summary>
    private static void AnalyzeRecords(CompilationStartAnalysisContext start)
    {
        var emitted = new ConcurrentQueue<(ITypeSymbol Record, IParameterSymbol Parameter, IMethodSymbol Method)>();
        var cleared = new ConcurrentDictionary<ITypeSymbol, byte>(SymbolEqualityComparer.Default);
        start.RegisterSymbolAction(context =>
        {
            var method = (IMethodSymbol)context.Symbol;
            if (!Symbols.IsForEach(method)) return;
            foreach (var p in method.Parameters)
                if (Symbols.Classify(p) == Symbols.ParamKind.Emit && Symbols.EmitElement(p.Type) is { } record)
                    emitted.Enqueue((record, p, method));
        }, SymbolKind.Method);

        // A clear: Clear / RemoveAt / RemoveAtSwapBack of a DynamicBuffer<T> (Items, or a ref local bound to it),
        // RemoveSingleton<Emitted<T>>(), or Emitted<T> or its Items assigned whole.
        start.RegisterOperationAction(context =>
        {
            switch (context.Operation)
            {
                case IInvocationOperation { TargetMethod: var method }:
                    if (method.Name is "Clear" or "RemoveAt" or "RemoveAtSwapBack" && method.Parameters.Length > 0
                        && Symbols.BufferElement(method.Parameters[0].Type) is { } element)
                        cleared.TryAdd(element, 0);
                    else if (method.Name == "RemoveSingleton" && method.TypeArguments.Length == 1 && Symbols.EmittedElement(method.TypeArguments[0]) is { } removed)
                        cleared.TryAdd(removed, 0);
                    break;
                case ISimpleAssignmentOperation { Target: var target }:
                    var whole = target is IFieldReferenceOperation { Field.Name: "Items", Instance: { } instance } ? instance.Type : target.Type;
                    if (whole != null && Symbols.EmittedElement(whole) is { } assigned) cleared.TryAdd(assigned, 0);
                    break;
            }
        }, OperationKind.Invocation, OperationKind.SimpleAssignment);

        start.RegisterCompilationEndAction(context =>
        {
            bool library = context.Compilation.Options.OutputKind == OutputKind.DynamicallyLinkedLibrary;
            foreach (var group in emitted.GroupBy(e => e.Record, SymbolEqualityComparer.Default))
            {
                var record = (ITypeSymbol)group.Key!;
                if (cleared.ContainsKey(record) || !record.Locations.Any(l => l.IsInSource) || library && Symbols.IsPublic(record)) continue;
                var (_, parameter, method) = group.OrderBy(e => e.Parameter.Locations[0].SourceTree?.FilePath, System.StringComparer.Ordinal)
                    .ThenBy(e => e.Parameter.Locations[0].SourceSpan.Start).First();
                context.ReportDiagnostic(Diagnostic.Create(UnclearedRecords, parameter.Locations.FirstOrDefault(),
                    Symbols.Describe(method), record.Name));
            }
        });
    }
}
