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
/// EECS033: a generic call (<c>entity.Get&lt;Health&gt;()</c>, <c>item.Component1</c>, a guarded <c>Set</c>) that a
/// generated member says shorter. Its own analyzer: proving the member exists binds the shorter text speculatively at
/// every generic call, and projects that keep the generic API (libraries, the unit tests) suppress the rule, which
/// lets Roslyn skip the analyzer and that work with it.
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor GeneratedMember = new("EECS033", "A generated member says it shorter",
        "'{0}' can be written '{1}'",
        Category, DiagnosticSeverity.Warning, true,
        "The generator writes named members for every component and singleton (entity.Health, HasHealth, SetHealth(…), " +
        "IsDead, world.Clock, world.ZoneIndex, item.Position) that call the generic API: the same call, without " +
        "repeating the type.",
        helpLinkUri: Help("EECS033"));

    internal static void InitializeMembers(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (Core.Create(start.Compilation) is not { } core) return;
            start.RegisterOperationAction(c => AnalyzeGenericCall(c, core), OperationKind.Invocation);
            start.RegisterOperationAction(AnalyzeItem, OperationKind.PropertyReference);
            start.RegisterOperationAction(c => AnalyzeGuardedSet(c, core), OperationKind.Conditional);
        });

    /// <summary>EECS033 for a call of Entity's or World's generic API.</summary>
    private static void AnalyzeGenericCall(OperationAnalysisContext context, Core core)
    {
        var call = (IInvocationOperation)context.Operation;
        if (call.TargetMethod.ContainingType is not { } owner || !core.Is(owner, core.Entity) && !core.Is(owner, core.World)) return;
        GeneratedEquivalent(context, call, core);
    }

    /// <summary>
    /// Whether another rule reports <paramref name="call"/> and its fix removes or rewrites it, so EECS033 says nothing
    /// (one mistake, one diagnostic): a copy changed and dropped (EECS038), a tracked component reached for writing
    /// and only read (EECS025, whose fix reads it through the generated member already), a lookup per row (EECS027), a
    /// read of another entity a Lookup&lt;T&gt; takes (EECS087), a Set folded into the creation before it (EECS052).
    /// </summary>
    private static bool ReportedElsewhere(OperationAnalysisContext context, IInvocationOperation call, Core core)
    {
        var method = call.TargetMethod;
        if (IsDroppedCopy(call)) return true;
        if (core.Is(method.ContainingType, core.Entity) && method is { Name: "Get", TypeArguments.Length: 1 } && core.Tracked(method.TypeArguments[0])
            && !Symbols.IsIndexed(method.TypeArguments[0]) && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax }
            && !Uses.MayWrite(call))
            return true;
        return RowLookupOf(call, context.ContainingSymbol, core) || OtherLookupOf(call, context.ContainingSymbol, core) || SetAfterCreation(call, core);
    }

    /// <summary>
    /// Whether <paramref name="call"/> is a lookup EECS027 reports (in a [ForEach] method or a job): its fix takes the
    /// lookup away, so EECS033's shorter spelling of it would only be undone.
    /// </summary>
    private static bool RowLookupOf(IInvocationOperation call, ISymbol containing, Core core) =>
        containing is IMethodSymbol method && (Symbols.IsGeneratedJob(method) || Symbols.JobInterface(method) != null)
        && RowLookups(core, method, Symbols.Root(call)).Any(l => l.Operation == call);

    /// <summary>Whether <paramref name="call"/> is a read EECS087 hands to a Lookup&lt;T&gt; (which reads by id, no shorter spelling to give).</summary>
    private static bool OtherLookupOf(IInvocationOperation call, ISymbol containing, Core core) =>
        containing is IMethodSymbol method && Symbols.IsForEach(method)
        && method.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.World)
        && OtherEntityReads(method, Symbols.Root(call), core) is { } found && found.Reads.Any(r => r.Operation == call);

    /// <summary>Whether <paramref name="call"/> is the Set EECS052 folds into the creation before it (and so removes).</summary>
    private static bool SetAfterCreation(IInvocationOperation call, Core core) =>
        call.Parent is IExpressionStatementOperation { Parent: IBlockOperation block } statement && block.Operations.IndexOf(statement) is > 0 and var at
        && block.Operations[at - 1] is IVariableDeclarationGroupOperation group
        && group.Declarations.SelectMany(d => d.Declarators).Any(d => CreateThenSetOf(d, core)?.Set == call);

    /// <summary>EECS033: a generic call with a generated member of the same meaning.</summary>
    private static void GeneratedEquivalent(OperationAnalysisContext context, IInvocationOperation call, Core core)
    {
        var method = call.TargetMethod;
        if (call.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } syntax) return;
        if (method.TypeArguments.Any(t => t is not INamedTypeSymbol || t is INamedTypeSymbol { IsGenericType: true } g
                && !Symbols.Named(g.OriginalDefinition, Symbols.DynamicBuffer))) return;
        var model = call.SemanticModel!;
        string receiver = access.Expression.ToString();
        string args = syntax.ArgumentList.ToString();
        bool onEntity = core.Is(method.ContainingType, core.Entity);
        string? candidate = null;
        bool property = false;
        if (method.TypeArguments.Length == 1 && method.TypeArguments[0] is INamedTypeSymbol type)
        {
            var element = Symbols.BufferElement(type);
            string name = (element ?? type).Name;
            bool tag = element == null && !type.GetMembers().Any(m => m is IFieldSymbol { IsStatic: false });
            (candidate, property) = (onEntity, method.Name) switch
            {
                (true, "Get") => (name, true),
                (true, "Read") => (name, true),
                (true, "Has") => (tag ? "Is" + name : "Has" + name, true),
                (true, "TryGet") => ("TryGet" + name, false),
                (true, "GetOrAdd") when call.Arguments.Length == 0 => ("GetOrAdd" + name, false),
                (true, "GetOrAdd") when !tag && SetCandidate(call, name, tag, out args) is ({ }, _) => ("GetOrAdd" + name, false), // GetOrAdd(new T(a)) as GetOrAddT(a)
                (true, "Remove") => ("Remove" + name, false),
                (true, "Disable") => ("Disable" + name, false),
                (true, "Enable") => ("Enable" + name, false),
                (true, "IsDisabled") => ("Is" + name + "Disabled", true),
                (true, "Set") => SetCandidate(call, name, tag, out args),
                (true, "SetIfChanged") when !tag && SetCandidate(call, name, tag, out args) is ({ } set, _) => (set + "IfChanged", false),
                (false, "GetSingleton") => (name, true),
                (false, "HasSingleton") => ("Has" + name, true),
                (false, "GetOrAddSingleton") when call.Arguments.Length == 0 => ("GetOrAdd" + name, false),
                (false, "GetOrAddSingleton") when !tag && SetCandidate(call, name, tag, out args) is ({ }, _) => ("GetOrAdd" + name, false),
                (false, "RemoveSingleton") => ("Remove" + name, false),
                (false, "Relations") => (name + "Index", true),
                _ => (null, false),
            };
        }
        else if (!onEntity && method is { Name: "Index", TypeArguments.Length: 2 })
            (candidate, property) = (method.TypeArguments[0].Name + "Index", true);
        if (candidate == null) return;

        // Cheap tests first, speculative binding last: no generated class for the type, or a buffer no other code names,
        // has no member to suggest.
        if (method.TypeArguments.Length == 1 && method.TypeArguments[0] is INamedTypeSymbol argument && !HasGeneratedMembers(model.Compilation, argument)) return;
        // A buffer's members exist while some code names DynamicBuffer<E>: the call must not be the only name.
        if (method.TypeArguments.Length == 1 && Symbols.BufferElement(method.TypeArguments[0]) is { } buffered
            && !BufferNamedElsewhere(core, buffered.Name, syntax)) return;

        // What another rule reports (and whose fix removes this call) is checked once a member is a candidate, before
        // the speculative binding.
        if (ReportedElsewhere(context, call, core)) return;
        var receiverType = call.Instance?.Syntax == access.Expression ? call.Instance.Type : null;
        var component = (INamedTypeSymbol)method.TypeArguments[0];
        string replacement = receiver + "." + candidate + (property ? "" : args);
        if (!Binds(model, syntax, replacement, out var bound, receiverType) || !GeneratedFor(bound!, component, model.Compilation)) return;
        // The same access: Get<T>() is a 'ref' (a watched component's property is 'ref readonly'; its writer is WriteX()).
        if (method.Name == "Get" && bound is IPropertySymbol { RefKind: not RefKind.Ref })
        {
            replacement = receiver + ".Write" + candidate + "()";
            if (!Binds(model, syntax, replacement, out bound, receiverType) || !GeneratedFor(bound!, component, model.Compilation)) return;
            candidate = "Write" + candidate + "()";
        }

        context.ReportDiagnostic(Diagnostic.Create(GeneratedMember, syntax.GetLocation(), Fix(replacement, "Use ." + candidate.Split('(')[0]),
            Shorten(syntax), replacement));
    }

    /// <summary>
    /// EECS033 for a guarded write: <c>if (e.Score.Points != v) e.SetScore(v);</c> (or <c>e.Set(new Score(v))</c>) on a
    /// component of one field is <c>e.SetScoreIfChanged(v);</c>, the generated writer that compares the value itself
    /// (<c>SetIfChanged</c>). The field is read through the generated property, <c>Read&lt;T&gt;()</c> or <c>Get&lt;T&gt;()</c> of
    /// the same receiver; the if has no else and does nothing else.
    /// </summary>
    private static void AnalyzeGuardedSet(OperationAnalysisContext context, Core core)
    {
        var conditional = (IConditionalOperation)context.Operation;
        if (conditional is not { WhenFalse: null, Syntax: IfStatementSyntax syntax, Condition: IBinaryOperation { OperatorKind: BinaryOperatorKind.NotEquals } test }) return;
        var write = conditional.WhenTrue is IBlockOperation { Operations.Length: 1 } block ? block.Operations[0] : conditional.WhenTrue;
        if (write is not IExpressionStatementOperation { Operation: IInvocationOperation set }
            || set.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax setAccess }
            || set.Arguments.Length != 1)
            return;

        // The written value: the generated SetX(v), or Set(new X(v)) with one argument.
        var method = set.TargetMethod;
        INamedTypeSymbol? component = null;
        IOperation? value = null;
        if (Symbols.IsGenerated(method) && method.Name.StartsWith("Set") && method.Parameters.Length == 1)
        {
            // The generated writer names its component: the struct of that name in scope where the if is, whose
            // generated class holds the writer (a same-named struct of another namespace or assembly does not).
            component = conditional.SemanticModel!.LookupNamespacesAndTypes(syntax.SpanStart, name: method.Name.Substring("Set".Length))
                .OfType<INamedTypeSymbol>().FirstOrDefault(t => t.TypeKind == TypeKind.Struct && Symbols.Implements(t, Symbols.Component)
                    && GeneratedFor(method, t, conditional.SemanticModel.Compilation));
            value = set.Arguments[0].Value;
        }
        else if (core.Is(method.ContainingType, core.Entity) && method is { Name: "Set", TypeArguments.Length: 1 }
                 && Unwrap(set.Arguments[0].Value) is IObjectCreationOperation { Initializer: null, Arguments.Length: 1 } creation)
        {
            component = method.TypeArguments[0] as INamedTypeSymbol;
            value = creation.Arguments[0].Value;
        }

        if (component == null || value == null) return;
        var fields = component.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();
        if (fields.Count != 1) return;
        // The compared field and value, either way round.
        var (read, compared) = Field(Unwrap(test.LeftOperand), fields[0]) is { } left ? (left, test.RightOperand)
            : Field(Unwrap(test.RightOperand), fields[0]) is { } right ? (right, test.LeftOperand) : (null, null);
        if (read == null || !SyntaxFactory.AreEquivalent(Unwrap(compared!).Syntax, Unwrap(value).Syntax)) return;
        string receiver = setAccess.Expression.ToString();
        if (Receiver(read, component) is not { } readFrom || readFrom != receiver) return;

        string replacement = receiver + ".Set" + component.Name + "IfChanged(" + Unwrap(value).Syntax + ")";
        if (!Binds(conditional.SemanticModel!, syntax, replacement, out var bound) || !GeneratedFor(bound!, component, conditional.SemanticModel!.Compilation)) return;
        context.ReportDiagnostic(Diagnostic.Create(GeneratedMember, syntax.GetLocation(), Fix(replacement + ";", "Use .Set" + component.Name + "IfChanged"),
            Shorten(syntax), replacement));

        static IOperation Unwrap(IOperation operation)
        {
            while (operation is IConversionOperation { IsImplicit: true } conversion) operation = conversion.Operand;
            return operation;
        }

        static IOperation? Field(IOperation operation, IFieldSymbol field) =>
            operation is IFieldReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Field, field) ? reference.Instance : null;

        // The receiver whose component the comparison reads: e.X (generated), e.Read<X>(), e.Get<X>().
        static string? Receiver(IOperation instance, INamedTypeSymbol component) => instance switch
        {
            IPropertyReferenceOperation { Syntax: MemberAccessExpressionSyntax access } property when Symbols.IsGenerated(property.Property)
                && SymbolEqualityComparer.Default.Equals(property.Type, component) => access.Expression.ToString(),
            IInvocationOperation { TargetMethod: { Name: "Read" or "Get", TypeArguments.Length: 1 } read, Syntax: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } }
                when SymbolEqualityComparer.Default.Equals(read.TypeArguments[0], component) => access.Expression.ToString(),
            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="member"/> is one the generator wrote for <paramref name="type"/> (a component, singleton,
    /// buffer or relation): it sits in the type's generated class (<c>{FlatName}EcsExtensions</c>, a buffer element's
    /// <c>{FlatName}EcsBufferExtensions</c>), in the type's namespace, written by the type's assembly or this one. A
    /// member that binds by name is not enough: a component of the same name in another namespace or assembly has a
    /// generated class of the same name, and its member binds wherever that class is in scope (EECS033 suggested the
    /// program's <c>entity.Spot</c> for a library's <c>Read&lt;Lib.Spot&gt;()</c>, and the fix read the wrong component).
    /// </summary>
    private static bool GeneratedFor(ISymbol member, INamedTypeSymbol type, Compilation compilation)
    {
        if (!Symbols.IsGenerated(member)) return false;
        var element = Symbols.BufferElement(type) as INamedTypeSymbol;
        var owner = (element ?? type).OriginalDefinition;
        var generated = member.ContainingType;
        while (generated?.ContainingType != null) generated = generated.ContainingType; // an extension block's class
        if (generated == null || generated.Name != Symbols.FlatName(owner) + (element != null ? ComponentGenerator.Classes.Buffer : ComponentGenerator.Classes.Members)
            || !SymbolEqualityComparer.Default.Equals(generated.ContainingAssembly, owner.ContainingAssembly)
            && !SymbolEqualityComparer.Default.Equals(generated.ContainingAssembly, compilation.Assembly))
            return false;
        // The same namespace by name, level by level (each assembly has its own namespace symbols).
        INamespaceSymbol? a = generated.ContainingNamespace, b = owner.ContainingNamespace;
        for (; a is { IsGlobalNamespace: false } && b is { IsGlobalNamespace: false }; a = a.ContainingNamespace, b = b.ContainingNamespace)
            if (a.Name != b.Name) return false;
        return a is { IsGlobalNamespace: true } && b is { IsGlobalNamespace: true };
    }

    /// <summary>
    /// Whether code outside <paramref name="replaced"/> names <c>DynamicBuffer&lt;E&gt;</c> for an element called
    /// <paramref name="element"/>: the generator writes a buffer's members only for elements some code names that way
    /// (ComponentGenerator), so replacing the only name with a member would remove the member with it. Syntax only:
    /// the name's last identifier is compared.
    /// </summary>
    private static bool BufferNamedElsewhere(Core core, string element, SyntaxNode replaced)
    {
        if (!core.BufferNames.Value.TryGetValue(element, out var names)) return false;
        foreach (var (tree, span) in names)
            if (!(tree == replaced.SyntaxTree && replaced.Span.Contains(span))) return true;
        return false;
    }

    /// <summary>
    /// Every <c>DynamicBuffer&lt;E&gt;</c> name of the compilation's hand-written files, by the last identifier of
    /// <c>E</c>: one walk of the compilation, made the first time EECS033 asks (it asked once per call before, which walked
    /// every file for every buffer call). Generated files (the buffer's own members among them) name it because the
    /// members exist: not a reason.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, List<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span)>> BufferNamesOf(Compilation compilation)
    {
        var names = new System.Collections.Generic.Dictionary<string, List<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan)>>(System.StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.FilePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var node in tree.GetRoot().DescendantNodes())
                if (node is GenericNameSyntax { Identifier.ValueText: "DynamicBuffer", TypeArgumentList.Arguments.Count: 1 } name
                    && Last(name.TypeArgumentList.Arguments[0]) is { } element)
                {
                    if (!names.TryGetValue(element, out var list)) names[element] = list = [];
                    list.Add((tree, name.Span));
                }
        }

        return names;

        static string? Last(TypeSyntax type) => type switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            SimpleNameSyntax n => n.Identifier.ValueText,
            _ => null,
        };
    }

    /// <summary>
    /// Whether the generator wrote members for <paramref name="type"/> (a component, singleton or buffer): its class
    /// (<c>{FlatName}EcsExtensions</c>, a buffer element's <c>{FlatName}EcsBufferExtensions</c>) sits in the type's
    /// namespace (in this compilation or the type's assembly), so one lookup by name answers before any binding.
    /// </summary>
    private static bool HasGeneratedMembers(Compilation compilation, INamedTypeSymbol type)
    {
        var element = Symbols.BufferElement(type) as INamedTypeSymbol;
        var owner = element ?? type;
        return owner.ContainingNamespace is { } declared && compilation.GetCompilationNamespace(declared) is { } ns && ns.GetTypeMembers(Symbols.FlatName(owner)
            + (element != null ? ComponentGenerator.Classes.Buffer : ComponentGenerator.Classes.Members)).Length > 0;
    }

    /// <summary>Set(new T(args)) as SetT(args), Set(new T()) / Set(default(T)) of a tag as SetT(); else null.</summary>
    private static (string?, bool) SetCandidate(IInvocationOperation call, string name, bool tag, out string args)
    {
        args = "()";
        if (call.Arguments.Length != 1) return (null, false);
        var value = call.Arguments[0].Value;
        while (value is IConversionOperation { IsImplicit: true } c) value = c.Operand;
        switch (value)
        {
            case IObjectCreationOperation { Initializer: null, Syntax: BaseObjectCreationExpressionSyntax creation }:
                args = creation.ArgumentList?.ToString() ?? "()";
                return ("Set" + name, false);
            case IDefaultValueOperation when tag:
                return ("Set" + name, false);
            default:
                return (null, false);
        }
    }

    /// <summary>EECS033 for query items: <c>item.Component1</c> as <c>item.Position</c>.</summary>
    private static void AnalyzeItem(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        var property = reference.Property;
        if (!Symbols.IsItemComponent(property) || reference.Syntax is not MemberAccessExpressionSyntax access
            || property.Type is not INamedTypeSymbol { IsGenericType: false } type) return;
        string replacement = access.Expression + "." + type.Name;
        if (!Binds(reference.SemanticModel!, access, replacement, out var bound) || !Symbols.IsGenerated(bound!)
            || bound is IPropertySymbol { RefKind: not RefKind.Ref } && Uses.MayWrite(reference)) return;
        context.ReportDiagnostic(Diagnostic.Create(GeneratedMember, access.GetLocation(), Fix(replacement, "Use ." + type.Name),
            access.ToString(), replacement));
    }
}

/// <summary>EECS033 (EasyEcsAnalyzer.Members.cs), as one analyzer: Roslyn skips an analyzer only when every rule it
/// reports is suppressed, so a NoWarn of EECS033 saves its speculative binding.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MemberAnalyzer : EasyEcsAnalyzer.Area
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(EasyEcsAnalyzer.GeneratedMember);

    protected override void Register(AnalysisContext context) => EasyEcsAnalyzer.InitializeMembers(context);
}
