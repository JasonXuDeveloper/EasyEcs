using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of views (EECS098-EECS101, EECS053 through views): a [View] the generators cannot use (EECS098), a component
/// a [ForEach] method reaches twice through views (EECS099), a view member that reaches beyond its entity (EECS100), a
/// view built by hand where the job or the entity builds it (EECS101), and a view member that writes a tracked component
/// through a 'ref' field on some paths only (EECS053). Each computes its code fix as diagnostic properties.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor BadView = new("EECS098", "Invalid [View]",
        "[View] '{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "A view is a ref struct whose instance fields are 'ref' (written), 'ref readonly' (read) or Mut<T> (written, " +
        "stamped only where written) fields of components, public or internal and not readonly (the generated code sets " +
        "them in an initializer), each component once, at most nine; an indexed component is 'ref readonly'. The generators skip a view that breaks this: [ForEach] methods taking it get no job, and AsX / TryGetX are not written.",
        helpLinkUri: Help("EECS098"));

    public static readonly DiagnosticDescriptor ViewOverlap = new("EECS099", "A component reached twice through views",
        "{0} {1}",
        Category, DiagnosticSeverity.Error, true,
        "A pass queries each component once, and its views and component parameters are refs into that one column. A " +
        "component reached through a view and through another view or a parameter would be two refs to one value, one of " +
        "them perhaps read-only while the other writes. Take it once: through the view, or through a parameter while no " +
        "view covers it.",
        helpLinkUri: Help("EECS099"));

    public static readonly DiagnosticDescriptor ViewReachesOut = new("EECS100", "A view member that reaches beyond its entity",
        "View '{0}': '{1}' {2}",
        Category, DiagnosticSeverity.Warning, true,
        "A view covers one entity's components, so a [ForEach] method can take it and the scheduler knows what it reads and " +
        "writes. A member that takes the World, an Entity, an EntityId, a Lookup or another view reaches other entities the " +
        "schedule does not see, and a static field is state outside the world. Do that in the system: a Lookup<T> " +
        "parameter reads other entities, a component or singleton holds state.",
        helpLinkUri: Help("EECS100"));

    public static readonly DiagnosticDescriptor HandWrittenView = new("EECS101", "A view built by hand",
        "'{0}' is built by hand from {1}: {2}",
        Category, DiagnosticSeverity.Warning, true,
        "A [ForEach] or [ForEntity] method takes a view as a parameter, and the generated job builds it over the row (the " +
        "same refs, no copy); an entity builds it with the generated AsX(). Building it from the method's component " +
        "parameters or from the entity's components by hand repeats what the generator writes.",
        helpLinkUri: Help("EECS101"));

    /// <summary>EECS101's view parameter fix: the parameters the view replaces (<c>name=Field;...</c>), the new parameter's name and type.</summary>
    public const string ViewFields = "ViewFields", ViewName = "ViewName", ViewType = "ViewType";

    private static void InitializeViews(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzeViewDeclarations, SymbolKind.NamedType);
        context.RegisterSymbolAction(AnalyzeViewOverlap, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeHandWrittenView, OperationKind.ObjectCreation);
        context.RegisterOperationBlockAction(AnalyzeViewMemberBody);
        context.RegisterCompilationStartAction(AnalyzeUnwrittenViewFields);
    }

    // ---------------- EECS025 through views: a 'ref' field nothing writes ----------------

    /// <summary>
    /// EECS025 for a valid view's 'ref' field that no code of the compilation writes through (<see cref="Uses.MayWrite"/>
    /// of a reference to it; binding the ref is no write): every pass taking the view is scheduled as the component's
    /// writer and, for a tracked component, stamps every row. The field's modifier is the view's, shared by every method
    /// and lambda taking it, so the rule looks at all of them at the end of the compilation (a method alone that does not
    /// write it could only take another view). Fix: 'ref readonly'. A public view of a library is left to the
    /// assemblies using it.
    /// </summary>
    private static void AnalyzeUnwrittenViewFields(CompilationStartAnalysisContext start)
    {
        var views = new System.Collections.Concurrent.ConcurrentQueue<INamedTypeSymbol>();
        var written = new System.Collections.Concurrent.ConcurrentDictionary<IFieldSymbol, byte>(SymbolEqualityComparer.Default);
        start.RegisterSymbolAction(context =>
        {
            if (context.Symbol is INamedTypeSymbol type && Views.IsValid(type)) views.Enqueue(type);
        }, SymbolKind.NamedType);
        start.RegisterOperationAction(context =>
        {
            var reference = (IFieldReferenceOperation)context.Operation;
            if (reference.Field.RefKind != RefKind.Ref || !Views.IsView(reference.Field.ContainingType)
                || reference.Parent is ISimpleAssignmentOperation { IsRef: true } bind && bind.Target == reference) return;
            if (Uses.MayWrite(reference)) written.TryAdd(reference.Field.OriginalDefinition, 0);
        }, OperationKind.FieldReference);
        start.RegisterCompilationEndAction(context =>
        {
            bool library = context.Compilation.Options.OutputKind == OutputKind.DynamicallyLinkedLibrary;
            foreach (var view in views)
            {
                if (library && Symbols.IsPublic(view)) continue;
                foreach (var field in Views.Fields(view))
                {
                    if (field is not { Writes: true, Mut: false } || written.ContainsKey(field.Symbol)) continue;
                    var declaration = field.Symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken).Parent as VariableDeclarationSyntax;
                    var fix = declaration is { Variables.Count: 1 } ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "RefReadOnly") : null;
                    string component = field.Component.Name;
                    context.ReportDiagnostic(Diagnostic.Create(WriteAccessOnlyReads, field.Symbol.Locations.FirstOrDefault(), fix,
                        $"Field '{field.Symbol.Name}' of view {view.Name} (ref {component})",
                        Symbols.Implements(field.Component, Symbols.TrackChanges)
                            ? $"every row a pass builds the view over is stamped as changed, so [Changed<{component}>] systems visit rows nobody changed"
                            : "every pass taking the view is scheduled as the component's writer, so no parallel stage runs it beside a reader",
                        "no code writes through it; make it 'ref readonly'"));
                }
            }
        });
    }

    // ---------------- EECS098, EECS100: the declarations ----------------

    private static void AnalyzeViewDeclarations(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!Views.IsView(type)) return;
        if (Views.Problem(type) is var (where, problem))
            context.ReportDiagnostic(Diagnostic.Create(BadView, where.Locations.FirstOrDefault(), type.Name, problem));
        if (ReachOut(type) is var (location, member, what))
            context.ReportDiagnostic(Diagnostic.Create(ViewReachesOut, location, type.Name, member, what));
    }

    /// <summary>
    /// EECS100: the first member of view <paramref name="view"/> that holds static state, or takes or uses a value that
    /// reaches other entities (<see cref="Reaches"/>): where, the member's name and what it does.
    /// </summary>
    private static (Location, string, string)? ReachOut(INamedTypeSymbol view)
    {
        foreach (var member in view.GetMembers())
        {
            if (member.IsImplicitlyDeclared || Symbols.IsGenerated(member)) continue;
            if (member is IFieldSymbol { IsStatic: true, IsConst: false, IsReadOnly: false } field)
                return (field.Locations.FirstOrDefault() ?? Location.None, field.Name, "is static state: a view holds refs to its entity's components, and state belongs in a component or singleton");
            if (member is not (IMethodSymbol or IPropertySymbol)) continue;
            var parameters = member is IMethodSymbol m ? m.Parameters : ((IPropertySymbol)member).Parameters;
            foreach (var p in parameters)
                if (Reaches(p.Type) is { } reached)
                    return (p.Locations.FirstOrDefault() ?? Location.None, member.Name, $"takes {reached} '{p.Name}'");
        }

        return null;
    }

    /// <summary>
    /// EECS100 in a view member's body: the first value it uses that reaches other entities (<see cref="Reaches"/>), a
    /// reference to the view itself (<c>this</c>) aside.
    /// </summary>
    private static void AnalyzeViewMemberBody(OperationBlockAnalysisContext context)
    {
        var owner = context.OwningSymbol;
        var view = owner.ContainingType;
        if (view == null || !Views.IsView(view) || owner.IsImplicitlyDeclared || Symbols.IsGenerated(owner)) return;
        string member = owner is IMethodSymbol { AssociatedSymbol: { } property } ? property.Name : owner.Name;
        if (!owner.IsStatic && owner is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet })
            ViewMemberWritesSomePaths(context, view, member);
        foreach (var block in context.OperationBlocks)
        foreach (var operation in block.DescendantsAndSelf())
        {
            if (operation is not (IParameterReferenceOperation or ILocalReferenceOperation or IInvocationOperation or IFieldReferenceOperation
                or IPropertyReferenceOperation or IObjectCreationOperation)) continue;
            if (operation.Type is not { } type || Reaches(type) is not { } reached) continue;
            if (Views.IsView(type) && SymbolEqualityComparer.Default.Equals(type, view) && Symbols.Unwrap(operation) is IInstanceReferenceOperation) continue;
            context.ReportDiagnostic(Diagnostic.Create(ViewReachesOut, operation.Syntax.GetLocation(), view.Name, member, $"uses {reached} ({operation.Syntax})"));
            return;
        }
    }

    /// <summary>
    /// EECS053 in a view member: a 'ref' field of a tracked component (<see cref="TrackedRefFields"/>) the member writes
    /// on some paths only (<see cref="Unwritten"/>). Every row the view is built over is stamped, so a caller that runs
    /// the member on every row stamps rows the member leaves unwritten. Writes are uses of <c>this</c> that may write the
    /// field (<see cref="WritesField"/>).
    /// </summary>
    private static void ViewMemberWritesSomePaths(OperationBlockAnalysisContext context, INamedTypeSymbol view, string member)
    {
        foreach (var field in TrackedRefFields(view))
        foreach (var block in context.OperationBlocks)
        {
            if (block is not (IMethodBodyOperation or IBlockOperation)) continue;
            var writes = new HashSet<SyntaxNode>();
            var located = new List<Location>();
            foreach (var operation in block.DescendantsAndSelf())
                if (operation is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
                    && WritesField(operation, view, field) && writes.Add(operation.Syntax))
                    located.Add((operation.Parent ?? operation).Syntax.GetLocation());
            if (writes.Count == 0 || !Unwritten(context.GetControlFlowGraph(block), writes)) continue;
            context.ReportDiagnostic(Diagnostic.Create(SomeRowsWritten, located[0], located.Skip(1),
                $"View member '{member}' writes", field.Component.Name, $" through field '{field.Symbol.Name}' of view {view.Name}",
                "some paths through it return without a write, and every row the view is built over is stamped as changed", "make the field"));
        }
    }

    /// <summary>What a value of <paramref name="type"/> reaches beyond one entity's components, or null.</summary>
    private static string? Reaches(ITypeSymbol type) =>
        Symbols.Named(type, Symbols.World) ? "the World"
        : Symbols.Named(type, Symbols.Entity) ? "an Entity"
        : Symbols.Named(type, Symbols.EntityId) ? "an EntityId"
        : Symbols.LookupElement(type) != null ? "a Lookup"
        : Views.IsView(type) ? $"view {type.Name} (another entity's components)"
        : null;

    // ---------------- EECS099: a component reached twice ----------------

    private static void AnalyzeViewOverlap(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!Symbols.IsGeneratedJob(method) || Views.Overlap(method) is not var (parameter, text)) return;
        context.ReportDiagnostic(Diagnostic.Create(ViewOverlap, parameter.Locations.FirstOrDefault(), Symbols.Describe(method), text));
    }

    // ---------------- EECS101: a view built by hand ----------------

    /// <summary>
    /// EECS101: <c>new View { F = ref x, ... }</c> setting every field of a valid view, each from a component parameter of
    /// the [ForEach] / [ForEntity] method it is in (fix: the view as its parameter), or each from one entity's component
    /// (fix: <c>entity.AsView()</c>).
    /// </summary>
    private static void AnalyzeHandWrittenView(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is not INamedTypeSymbol view || !Views.IsValid(view) || creation.Initializer is not { } initializer) return;
        var fields = Views.Fields(view);
        var set = new Dictionary<IFieldSymbol, IOperation>(SymbolEqualityComparer.Default);
        foreach (var init in initializer.Initializers)
        {
            if (init is not ISimpleAssignmentOperation { IsRef: true, Target: IFieldReferenceOperation { Field: var field }, Value: var value }) return;
            if (Symbols.Unwrap(value) is not { } unwrapped) return;
            set[field] = unwrapped;
        }

        if (fields.Any(f => !set.ContainsKey(f.Symbol))) return;

        // From the method's component parameters.
        if (context.ContainingSymbol is IMethodSymbol method && Symbols.IsGeneratedJob(method)
            && fields.All(f => set[f.Symbol] is IParameterReferenceOperation { Parameter: var p } && Symbols.Classify(p) == Symbols.ParamKind.Component
                && SymbolEqualityComparer.Default.Equals(p.Type, f.Component)))
        {
            var used = fields.Select(f => ((IParameterReferenceOperation)set[f.Symbol]).Parameter).ToList();
            var names = string.Join(", ", used.Select(p => $"'{p.Name}'"));
            // The fix: each parameter used for one field, written exactly when the field is.
            bool fixable = used.Distinct(SymbolEqualityComparer.Default).Count() == used.Count
                && fields.All(f => ((IParameterReferenceOperation)set[f.Symbol]).Parameter.RefKind == (f.Writes ? RefKind.Ref : RefKind.In));
            string name = Code.Escape(char.ToLowerInvariant(view.Name[0]) + view.Name.Substring(1));
            while (method.Parameters.Any(p => p.Name == name)) name = "_" + name;
            var properties = fixable
                ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "ViewParameter").Add(ViewName, name)
                    .Add(ViewType, view.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                    .Add(ViewFields, string.Join(";", fields.Select(f => $"{((IParameterReferenceOperation)set[f.Symbol]).Parameter.Name}={f.Symbol.Name}")))
                : null;
            context.ReportDiagnostic(Diagnostic.Create(HandWrittenView, creation.Syntax.GetLocation(), properties, view.Name, $"parameters {names}",
                $"take '{view.Name} {name}' as the parameter of {Symbols.Describe(method)} instead, and the job builds it over the row"));
            return;
        }

        // From one entity's components.
        ISymbol? entity = null;
        string? receiver = null;
        foreach (var f in fields)
        {
            if (EntityComponent(set[f.Symbol], f.Component) is not var (owner, text)) return;
            if (entity != null && !SymbolEqualityComparer.Default.Equals(entity, owner)) return;
            (entity, receiver) = (owner, text);
        }

        string call = $"{receiver}.As{view.Name}()";
        context.ReportDiagnostic(Diagnostic.Create(HandWrittenView, creation.Syntax.GetLocation(),
            ImmutableDictionary<string, string?>.Empty.Add(FixTitle, $"Use {call}").Add(Replace, call),
            view.Name, $"the components of '{receiver}'", $"{call} builds it (and stamps the tracked components it writes)"));
    }

    /// <summary>
    /// The entity (a parameter or local) and its name whose component <paramref name="component"/> <paramref name="value"/>
    /// returns by ref: <c>e.Get&lt;T&gt;()</c>, <c>e.Read&lt;T&gt;()</c>, or a generated <c>e.X</c> / <c>e.WriteX()</c>; else null.
    /// </summary>
    private static (ISymbol, string)? EntityComponent(IOperation value, ITypeSymbol component)
    {
        IOperation? instance = value switch
        {
            IInvocationOperation { TargetMethod: { Name: "Get" or "Read", TypeArguments.Length: 1 } target } call
                when Symbols.Named(target.ContainingType, Symbols.Entity) && SymbolEqualityComparer.Default.Equals(target.TypeArguments[0], component) => call.Instance,
            IInvocationOperation { TargetMethod: { ReturnsByRef: true } target } call
                when Symbols.IsGenerated(target) && SymbolEqualityComparer.Default.Equals(target.ReturnType, component) => call.Instance ?? call.Arguments.FirstOrDefault()?.Value,
            IPropertyReferenceOperation { Property: { ReturnsByRef: true } or { ReturnsByRefReadonly: true } } property
                when Symbols.IsGenerated(property.Property) && SymbolEqualityComparer.Default.Equals(property.Type, component) => property.Instance,
            _ => null,
        };
        return Symbols.Unwrap(instance) switch
        {
            IParameterReferenceOperation { Parameter: var p } when Symbols.Named(p.Type, Symbols.Entity) => (p, p.Name),
            ILocalReferenceOperation { Local: var l } when Symbols.Named(l.Type, Symbols.Entity) => (l, l.Name),
            _ => null,
        };
    }
}
