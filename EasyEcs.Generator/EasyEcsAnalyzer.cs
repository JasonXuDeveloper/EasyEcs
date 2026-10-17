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

    public static readonly DiagnosticDescriptor StatefulSystem = new("EECS007", "Systems must be stateless",
        "System '{0}' keeps mutable state in '{1}'; store it in a component (per entity) or a singleton (per world)",
        Category, DiagnosticSeverity.Error, true,
        "A system is behavior only. State in a system is invisible to other systems, queries and persistence, and it is " +
        "lost or duplicated when the system is removed or re-added. Readonly fields (dependencies, reusable scratch " +
        "buffers) are allowed.");

    public static readonly DiagnosticDescriptor SystemReference = new("EECS008", "Systems must not reference systems",
        "System '{0}' references system '{1}' through '{2}'; communicate through components or singletons",
        Category, DiagnosticSeverity.Error, true,
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

    public static readonly DiagnosticDescriptor ReadOnlyWrite = new("EECS011", "Write through a read-only parameter",
        "[ForEach] method '{0}' changes data that '{1}' holds by reference, but takes '{1}' as read-only; take it by 'ref'",
        Category, DiagnosticSeverity.Error, true,
        "A read-only component or singleton ('in', ReadOnlySpan) is scheduled as a read and never stamped as changed. A " +
        "collection or class it holds is still writable: changing it would race with parallel systems that read it, " +
        "and [Changed<T>] systems would not see the change. Take it by 'ref' so the write is scheduled and stamped.");

    public static readonly DiagnosticDescriptor BufferCopy = new("EECS012", "DynamicBuffer<T> held by value",
        "'{0}' copies a DynamicBuffer<{1}>; reach the buffer through its entity ('ref' or 'in')",
        Category, DiagnosticSeverity.Error, true,
        "A DynamicBuffer<T> is a component: its writes are tracked through the 'ref' that reaches it. A copy refers to " +
        "the same list, so writes through the copy are never stamped as changed, and a snapshot or another entity " +
        "would share the list. Use 'ref' (write) or 'in' (read) parameters, ref locals and Entity.Get / Read; a new " +
        "component value is 'new DynamicBuffer<T>()' (no list until its first write).");

    public static readonly DiagnosticDescriptor HiddenStructuralChange = new("EECS013", "Structural change in a [ForEach] method that promises none",
        "[ForEach] method '{0}' calls '{1}', a structural change, but takes no Entity or World to make it with; take the Entity as a parameter, or record the change as data",
        Category, DiagnosticSeverity.Error, true,
        "A [ForEach] method that is static and takes no Entity or World (or uses its Entity only to read) cannot change " +
        "structure, so the generator walks its rows without re-checking storage, and the system shares a parallel batch " +
        "with others. A change made through a handle a component holds (a relation) or a world kept elsewhere breaks " +
        "both: refs into moved rows, and a change while other systems run (which throws). Taking the Entity as a " +
        "parameter makes the system run alone with a pass that notices the change.");

    public static readonly DiagnosticDescriptor OffThreadStructuralChange = new("EECS014", "Structural change on another thread",
        "'{0}' is a structural change, made in a delegate that '{1}' runs on another thread; structural changes belong to the world's owner thread",
        Category, DiagnosticSeverity.Error, true,
        "A world's structure (create/destroy, Add/Set/Remove, singletons, systems, Capture/Restore, Update) is changed by " +
        "one owner thread. A change from another thread races with the owner's and can tear the tables. Hand the " +
        "change to the owner as data (a queue the owner drains in a system), or give that thread its own world.");

    public static readonly DiagnosticDescriptor IndexedByRef = new("EECS015", "Indexed component written through a ref",
        "'{0}' is indexed: {1}; write it with Set so its index follows",
        Category, DiagnosticSeverity.Error, true,
        "The index of an IIndexed<TKey> component follows the values written with Set and CreateEntity as they are " +
        "written; a write through a ref (Get, Add<T>(), GetOrAdd, TryGetRef, a 'ref' or Span [ForEach] parameter, a " +
        "query item, a job's 'ref' parameter) would leave it stale. Read it with Read, 'in' or ReadOnlySpan; write it " +
        "with Set, or assign the property the generator adds to Entity.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        NotPartial, BadParameter, ComponentCount, Generic, NotASystem, NotStruct, StatefulSystem, SystemReference,
        MixedBatch, NotParallel, ReadOnlyWrite, BufferCopy, HiddenStructuralChange, OffThreadStructuralChange, IndexedByRef);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterSymbolAction(AnalyzeBufferHolder, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeBufferCopy, OperationKind.VariableDeclarator, OperationKind.SimpleAssignment,
            OperationKind.Argument);
        context.RegisterOperationAction(AnalyzeWrite, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment,
            OperationKind.CoalesceAssignment, OperationKind.Increment, OperationKind.Decrement, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeStructuralCall, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeIndexedRef, OperationKind.Invocation, OperationKind.VariableDeclarator);
    }

    /// <summary>
    /// EECS015: a ref to an indexed component handed out by Entity (Get, Add&lt;T&gt;(), GetOrAdd, TryGetRef), or a ref
    /// local bound to a query item's or a job parameter's indexed component. Writes through those (and through
    /// [ForEach] parameters, see <see cref="AnalyzeMethod"/>) are reported by <see cref="AnalyzeWrite"/>.
    /// </summary>
    private static void AnalyzeIndexedRef(OperationAnalysisContext context)
    {
        switch (context.Operation)
        {
            case IInvocationOperation { TargetMethod: { ReturnsByRef: true, TypeArguments.Length: 1 } method } call
                when method.ContainingType?.ToDisplayString() == Symbols.Entity && Symbols.IsIndexed(method.TypeArguments[0]):
                context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, call.Syntax.GetLocation(), method.TypeArguments[0].Name,
                    $"Entity.{method.Name} hands out a ref (read it with Read)"));
                break;
            case IVariableDeclaratorOperation { Symbol.RefKind: RefKind.Ref, Initializer.Value: var value }
                when Symbols.IndexedRoot(value) is { } root:
                context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, context.Operation.Syntax.GetLocation(), root.Name,
                    "a ref local can write it (use 'ref readonly')"));
                break;
        }
    }

    /// <summary>
    /// EECS013: a structural call in a [ForEach] method the generator treats as stable. EECS014: a structural call in
    /// a lambda or anonymous method handed to a thread-starting call. Both see the call itself only: a change made in
    /// a helper method the delegate or [ForEach] method calls is not followed (the runtime still throws for one made
    /// while the world runs work on several threads).
    /// </summary>
    private static void AnalyzeStructuralCall(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (!Symbols.IsStructural(call.TargetMethod)) return;
        string name = call.TargetMethod.ContainingType.Name + "." + call.TargetMethod.Name;

        for (var node = call.Parent; node != null; node = node.Parent)
        {
            if (node is not IAnonymousFunctionOperation function) continue;
            var start = node.Parent is IDelegateCreationOperation creation ? creation.Parent : node.Parent;
            if (start is not IArgumentOperation { Parent: { } handoff } || Starter(handoff) is not { } starter
                || !Symbols.StartsThread(starter)) continue;
            if (!Threads.Concurrent(call, function, handoff, starter, context.CancellationToken)) return;
            context.ReportDiagnostic(Diagnostic.Create(OffThreadStructuralChange, call.Syntax.GetLocation(), name,
                starter.ContainingType.Name + (starter.MethodKind == MethodKind.Constructor ? "" : "." + starter.Name)));
            return;
        }

        if (context.ContainingSymbol is IMethodSymbol method && Symbols.IsForEach(method)
            && method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is MethodDeclarationSyntax syntax
            && Symbols.IsStable(method, syntax))
            context.ReportDiagnostic(Diagnostic.Create(HiddenStructuralChange, call.Syntax.GetLocation(), method.Name, name));
    }

    /// <summary>EECS011: a write or a mutating call that reaches a read-only [ForEach] parameter's reference.</summary>
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
        if (Symbols.IndexedRoot(target) is { } indexed)
            context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, context.Operation.Syntax.GetLocation(), indexed.Name,
                "this writes it through a ref"));
    }

    /// <summary>EECS012: a field, property, by-value parameter or by-value return of a dynamic buffer.</summary>
    private static void AnalyzeBufferHolder(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared) return;
        switch (symbol)
        {
            case IFieldSymbol field:
                Report(field.Type, field, field.Locations.FirstOrDefault());
                break;
            case IPropertySymbol property when !property.ReturnsByRef && !property.ReturnsByRefReadonly:
                Report(property.Type, property, property.Locations.FirstOrDefault());
                break;
            case IMethodSymbol method when method.AssociatedSymbol == null:
                if (!method.ReturnsByRef && !method.ReturnsByRefReadonly) Report(method.ReturnType, method, method.Locations.FirstOrDefault());
                foreach (var p in method.Parameters)
                    if (p.RefKind == RefKind.None) Report(p.Type, p, p.Locations.FirstOrDefault());
                break;
        }

        void Report(ITypeSymbol type, ISymbol holder, Location? location)
        {
            if (Symbols.BufferElement(type) is { } element)
                context.ReportDiagnostic(Diagnostic.Create(BufferCopy, location, holder.Name, element.ToDisplayString()));
        }
    }

    /// <summary>
    /// EECS012: a DynamicBuffer&lt;T&gt; copied by a local (ref locals are fine), an assignment or an argument of a
    /// generic method (Add, Set, CreateEntity: the value is stored), unless it is a new, empty buffer.
    /// </summary>
    private static void AnalyzeBufferCopy(OperationAnalysisContext context)
    {
        var (name, type, value) = context.Operation switch
        {
            IVariableDeclaratorOperation { Symbol.RefKind: RefKind.None } declarator =>
                (declarator.Symbol.Name, declarator.Symbol.Type, declarator.Initializer?.Value),
            ISimpleAssignmentOperation { IsRef: false } assignment => (Describe(assignment.Target), assignment.Target.Type, assignment.Value),
            IArgumentOperation { Parameter.OriginalDefinition.Type: ITypeParameterSymbol } argument =>
                (argument.Parameter!.Name, argument.Value.Type, argument.Value),
            _ => (null, null, null),
        };
        if (type == null || Symbols.BufferElement(type) is not { } element) return;
        if (context.Operation is not IVariableDeclaratorOperation && Symbols.IsNewBuffer(value)) return; // locals never hold one
        context.ReportDiagnostic(Diagnostic.Create(BufferCopy, context.Operation.Syntax.GetLocation(), name,
            element.ToDisplayString()));

        static string Describe(IOperation target) => target switch
        {
            ILocalReferenceOperation local => local.Local.Name,
            IParameterReferenceOperation parameter => parameter.Parameter.Name,
            IFieldReferenceOperation field => field.Field.Name,
            _ => target.Syntax.ToString(),
        };
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
                var component = kind == Symbols.ParamKind.Component ? p.Type : Symbols.SpanElement(p.Type);
                bool writable = kind == Symbols.ParamKind.Component ? p.RefKind == RefKind.Ref
                    : p.Type.OriginalDefinition.ToDisplayString() == "System.Span<T>";
                if (writable && component != null && Symbols.IsIndexed(component))
                    context.ReportDiagnostic(Diagnostic.Create(IndexedByRef, p.Locations.FirstOrDefault(), component.Name,
                        $"parameter '{p.Name}' can write it (take it as 'in' or ReadOnlySpan)"));
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

    private static IMethodSymbol? Starter(IOperation handoff) => handoff switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation construction => construction.Constructor,
        _ => null,
    };

    private static bool HasForEach(IMethodSymbol method) => Symbols.IsForEach(method);
}
