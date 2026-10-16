using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Reports misuse of EasyEcs at compile time. Diagnostics live here rather than in the generators (the recommended
/// split: generators stay pure and cacheable, analyzers run on the symbols they need).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EasyEcsAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "EasyEcs";

    public static readonly DiagnosticDescriptor NotPartial = new("EECS001", "System must be partial",
        "System '{0}' declares [ForEach] methods and must be partial (including containing types)",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadParameter = new("EECS002", "Invalid [ForEach] parameter",
        "Parameter '{0}' of [ForEach] method '{1}' must be a 'ref'/'in' IComponent or ISingletonComponent struct, a Span/ReadOnlySpan of an IComponent struct, an Entity or the World",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ComponentCount = new("EECS003", "Invalid component count",
        "[ForEach] method '{0}' must take between 1 and 9 components, or none with a [With<T>] or [Changed<T>] filter", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Generic = new("EECS004", "Generic systems are not supported",
        "[ForEach] method '{0}' is declared in a generic type or is generic itself",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NotASystem = new("EECS005", "[ForEach] outside a system",
        "Type '{0}' declares [ForEach] methods but does not derive from EcsSystem", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NotStruct = new("EECS006", "ECS data must be a struct",
        "'{0}' implements {1} and must be a struct: components and singletons are stored by value",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor StatefulSystem = new("EECS007", "Systems should be stateless",
        "System '{0}' keeps mutable state in '{1}'; store it in a component (per entity) or a singleton (per world)",
        Category, DiagnosticSeverity.Warning, true,
        "A system is behavior only. State in a system is invisible to other systems, queries and persistence, and it is " +
        "lost or duplicated when the system is removed or re-added. Readonly fields (dependencies, reusable scratch " +
        "buffers) are allowed.");

    public static readonly DiagnosticDescriptor SystemReference = new("EECS008", "Systems must not reference systems",
        "System '{0}' references system '{1}' through '{2}'; communicate through components or singletons",
        Category, DiagnosticSeverity.Warning, true,
        "Systems that call each other are coupled: neither can be removed, reordered or tested alone. Write the data " +
        "another system reads (a component or a singleton).");

    public static readonly DiagnosticDescriptor MixedBatch = new("EECS009", "Batch [ForEach] mixes spans and per-entity parameters",
        "[ForEach] method '{0}' takes component spans, so its other parameters can only be singletons ('ref'/'in')",
        Category, DiagnosticSeverity.Error, true,
        "A batch method receives runs of rows as spans. A per-entity parameter (ref/in component, Entity) has no single " +
        "value for a run, and the World could change rows while the spans point at them.");

    public static readonly DiagnosticDescriptor NotParallel = new("EECS010", "[ForEach(Parallel = true)] cannot run on several threads",
        "[ForEach] method '{0}' cannot run in parallel: {1}",
        Category, DiagnosticSeverity.Error, true,
        "A parallel [ForEach] runs chunks of rows on several threads at once. It must make no structural change, run per " +
        "entity, and write nothing that another row's run reads, such as a singleton.");

    public static readonly DiagnosticDescriptor ReadOnlyWrite = new("EECS011", "Write through a read-only component",
        "[ForEach] method '{0}' changes data that '{1}' holds by reference, but takes '{1}' as read-only; take it by 'ref' (or a Span)",
        Category, DiagnosticSeverity.Warning, true,
        "A read-only component ('in', ReadOnlySpan) is scheduled as a read and never stamped as changed. A collection or " +
        "class it holds is still writable: changing it would race with parallel systems that read the component, and " +
        "[Changed<T>] systems would not see the change. Take the component by 'ref' so the write is scheduled and stamped.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        NotPartial, BadParameter, ComponentCount, Generic, NotASystem, NotStruct, StatefulSystem, SystemReference,
        MixedBatch, NotParallel, ReadOnlyWrite);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeWrite, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment,
            OperationKind.CoalesceAssignment, OperationKind.Increment, OperationKind.Decrement, OperationKind.Invocation);
    }

    /// <summary>EECS011: a write or a mutating call that reaches a read-only [ForEach] component's reference.</summary>
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
            context.ReportDiagnostic(Diagnostic.Create(ReadOnlyWrite, context.Operation.Syntax.GetLocation(),
                parameter.ContainingSymbol.Name, parameter.Name));
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var location = type.Locations.FirstOrDefault();

        if (type.TypeKind is TypeKind.Class or TypeKind.Interface)
        {
            foreach (var name in new[] { Symbols.Component, Symbols.Singleton })
            {
                // Interfaces may extend the marker (e.g. a project-wide IStateComponent); classes may not implement it.
                if (type.TypeKind == TypeKind.Class && Symbols.Implements(type, name))
                    context.ReportDiagnostic(Diagnostic.Create(NotStruct, location, type.Name, name.Substring("EasyEcs.".Length)));
            }
        }

        if (type.TypeKind != TypeKind.Class || !Symbols.IsSystem(type)) return;
        AnalyzeSystemFields(context, type);

        if (!type.GetMembers().OfType<IMethodSymbol>().Any(HasForEach)) return;
        if (!Symbols.IsPartialChain(type))
            context.ReportDiagnostic(Diagnostic.Create(NotPartial, location, type.Name));
        if (Symbols.IsGenericChain(type))
            context.ReportDiagnostic(Diagnostic.Create(Generic, location, type.Name));
    }

    /// <summary>EECS007/008: a system's own fields and auto-properties (inherited ones are reported on their declaring type).</summary>
    private static void AnalyzeSystemFields(SymbolAnalysisContext context, INamedTypeSymbol system)
    {
        foreach (var member in system.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            ITypeSymbol? type;
            bool mutable;
            switch (member)
            {
                case IFieldSymbol field when field.AssociatedSymbol == null && !field.IsConst:
                    type = field.Type;
                    mutable = !field.IsReadOnly;
                    break;
                case IPropertySymbol { IsIndexer: false } property when IsAutoProperty(property):
                    type = property.Type;
                    mutable = property.SetMethod is { IsInitOnly: false };
                    break;
                default:
                    continue;
            }

            var location = member.Locations.FirstOrDefault();
            if (type is INamedTypeSymbol named && Symbols.IsSystem(named))
                context.ReportDiagnostic(Diagnostic.Create(SystemReference, location, system.Name, named.Name, member.Name));
            else if (mutable)
                context.ReportDiagnostic(Diagnostic.Create(StatefulSystem, location, system.Name, member.Name));
        }
    }

    /// <summary>An auto-property has a compiler-generated backing field (computed properties hold no state).</summary>
    private static bool IsAutoProperty(IPropertySymbol property) => property.ContainingType.GetMembers()
        .OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!HasForEach(method)) return;
        var location = method.Locations.FirstOrDefault();

        if (!Symbols.IsSystem(method.ContainingType))
            context.ReportDiagnostic(Diagnostic.Create(NotASystem, location, method.ContainingType.Name));
        if (method.IsGenericMethod)
            context.ReportDiagnostic(Diagnostic.Create(Generic, location, method.Name));

        int components = 0;
        bool valid = true;
        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p);
            if (kind == Symbols.ParamKind.Invalid)
            {
                valid = false;
                context.ReportDiagnostic(Diagnostic.Create(BadParameter, p.Locations.FirstOrDefault(), p.Name, method.Name));
            }
            else if (kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan)
            {
                components++;
            }
        }

        if (valid && !Symbols.ComponentCountValid(method, components))
            context.ReportDiagnostic(Diagnostic.Create(ComponentCount, location, method.Name));
        if (valid && Symbols.MixesBatch(method))
            context.ReportDiagnostic(Diagnostic.Create(MixedBatch, location, method.Name));
        if (Symbols.IsParallel(method) && method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken)
                is MethodDeclarationSyntax syntax && Symbols.ParallelProblem(method, syntax) is { } problem)
            context.ReportDiagnostic(Diagnostic.Create(NotParallel, location, method.Name, problem));
    }

    private static bool HasForEach(IMethodSymbol method) => Symbols.IsForEach(method);
}
