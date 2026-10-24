using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of systems as declarations (EECS007, EECS008, EECS074-EECS077, EECS090): a system is an [EcsSystem] ref
/// struct of static members (EECS074, EECS075) that keeps no state of its own (EECS007) and reaches no other system
/// (EECS008); its hooks, the methods named OnCreate / OnUpdate / OnDestroy, have one signature each (EECS076), and a
/// method named almost like one is reported (EECS090); the API that takes a system type takes an [EcsSystem] (EECS077).
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor StatefulSystem = new("EECS007", "A system keeps state in a static field",
        "System '{0}' keeps mutable state in static '{1}'; store it in a component (per entity) or a singleton (per world)",
        Category, DiagnosticSeverity.Warning, true,
        "A system is a name for static functions. State in one of its static fields is invisible to other systems, queries " +
        "and persistence, shared by every world, and kept when the system is removed or re-added. Readonly fields " +
        "(constants, lookup tables, reusable scratch buffers) are allowed.", helpLinkUri: Help("EECS007"));

    public static readonly DiagnosticDescriptor SystemReference = new("EECS008", "Systems must not reference systems",
        "System '{0}' references system '{1}' through '{2}'; communicate through components or singletons",
        Category, DiagnosticSeverity.Error, true,
        "Systems that call each other, through another system's members or World.System / TryGetSystem, are coupled: " +
        "neither can be removed, reordered or tested alone, and the scheduler cannot see what the call touches. Write the " +
        "data another system reads (a component or a singleton), or share the code as a static helper or a generic system.",
        helpLinkUri: Help("EECS008"));

    public static readonly DiagnosticDescriptor BadSystemType = new("EECS074", "[EcsSystem] on a type that is no ref struct",
        "'{0}' is [EcsSystem] but {1}",
        Category, DiagnosticSeverity.Error, true,
        "A system is a ref struct of static members: a ref struct cannot be boxed, stored or captured, so a system is never " +
        "an object, and it is still a generic type argument (AddSystem<T>, [UpdateAfter<T>]). The generator writes no " +
        "code for any other type.", helpLinkUri: Help("EECS074"));

    public static readonly DiagnosticDescriptor InstanceMember = new("EECS075", "A system with an instance member",
        "System '{0}' declares instance {1} '{2}': a system has no instance; make it static",
        Category, DiagnosticSeverity.Error, true,
        "The world never creates a system: its code is static and its bookkeeping is the world's (SystemHandle). An instance " +
        "member could never be reached. State belongs in components and singletons, configuration in a singleton.",
        helpLinkUri: Help("EECS075"));

    public static readonly DiagnosticDescriptor BadHook = new("EECS076", "An invalid system hook",
        "Hook '{0}' of system '{1}' {2}",
        Category, DiagnosticSeverity.Error, true,
        "The static methods of an [EcsSystem] named OnCreate, OnUpdate and OnDestroy are the hooks the world calls when the " +
        "system is added, on each of its runs and when it is removed: one method per hook and system, returning void, " +
        "taking the World, the SystemHandle, both (in either order) or neither. The generator calls no other.",
        helpLinkUri: Help("EECS076"));

    public static readonly DiagnosticDescriptor MisspelledHook = new("EECS090", "A system method named almost like a hook",
        "Method '{0}' of system '{1}' looks like hook {2}, but only a method named {2} is called: rename it",
        Category, DiagnosticSeverity.Warning, true,
        "A system's hooks are its static methods named OnCreate, OnUpdate and OnDestroy: the name alone makes a method a hook. " +
        "A method with a hook's signature, a name close to a hook's (OnUpate, Update, OnStart, Init, Dispose) and no caller " +
        "is never run. Rename it to the hook it was meant to be.", helpLinkUri: Help("EECS090"));

    public static readonly DiagnosticDescriptor NotASystemType = new("EECS077", "A type that is no system where a system is named",
        "'{0}' is no [EcsSystem]: {1} names a system (an [EcsSystem] ref struct)",
        Category, DiagnosticSeverity.Error, true,
        "AddSystem<T>, RemoveSystem<T>, HasSystem<T>, System<T>, TryGetSystem<T>, [UpdateBefore<T>] and [UpdateAfter<T>] " +
        "take a system type. Any other struct compiles, but no generated code runs it: AddSystem throws and the others " +
        "never find it.", helpLinkUri: Help("EECS077"));

    private static void InitializeSystems(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzeSystem, SymbolKind.NamedType);
        context.RegisterSymbolAction(AnalyzeHook, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeSystemType, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeSystemReference, OperationKind.Invocation, OperationKind.FieldReference,
            OperationKind.PropertyReference, OperationKind.MethodReference, OperationKind.EventReference);
    }

    /// <summary>EECS074, EECS022, EECS075 and EECS007 on a system type, EECS077 on its orderings.</summary>
    private static void AnalyzeSystem(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        foreach (var attribute in type.GetAttributes())
            if ((Symbols.Is(attribute.AttributeClass, Symbols.UpdateBeforeAttribute) || Symbols.Is(attribute.AttributeClass, Symbols.UpdateAfterAttribute))
                && attribute.AttributeClass!.TypeArguments[0] is INamedTypeSymbol { TypeKind: not TypeKind.Error } other && !Symbols.IsSystem(other))
                context.ReportDiagnostic(Diagnostic.Create(NotASystemType,
                    attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations.FirstOrDefault(),
                    other.Name, "[" + attribute.AttributeClass.Name.Replace("Attribute", "") + "<T>]"));
        if (!Symbols.IsSystem(type)) return;
        var location = type.Locations.FirstOrDefault();
        if (Symbols.SystemProblem(type) is { } problem)
        {
            bool fixable = type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsRecord && type.BaseType is { SpecialType: SpecialType.System_Object or SpecialType.System_ValueType };
            context.ReportDiagnostic(Diagnostic.Create(BadSystemType, location,
                fixable ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "RefStruct") : null, type.Name, problem));
            return;
        }

        if (!Symbols.IsVisible(type))
            context.ReportDiagnostic(Diagnostic.Create(Unnameable, location, Symbols.Hidden(type)?.Locations.Where(l => l.IsInSource) ?? [],
                $"The code of system '{type.Name}'", type.ToDisplayString()));

        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member is INamedTypeSymbol) continue;
            var at = member.Locations.FirstOrDefault();
            if (!member.IsStatic)
            {
                if (member is IFieldSymbol { AssociatedSymbol: not null }) continue; // an auto-property's: reported on the property
                string what = member switch
                {
                    IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
                    IMethodSymbol { MethodKind: MethodKind.Ordinary } => "method",
                    IMethodSymbol => "",
                    IPropertySymbol => "property",
                    IFieldSymbol => "field",
                    IEventSymbol => "event",
                    _ => "",
                };
                if (what.Length == 0) continue; // accessors: their property's or event's
                bool fix = member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method ? !UsesThis(method, context.CancellationToken)
                    : member is IFieldSymbol or IPropertySymbol;
                context.ReportDiagnostic(Diagnostic.Create(InstanceMember, at,
                    fix ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Static") : null, type.Name, what, member.Name));
                continue;
            }

            bool mutable = member switch
            {
                IFieldSymbol field => field.AssociatedSymbol == null && !field.IsConst && !field.IsReadOnly,
                IPropertySymbol property => IsAutoProperty(property) && property.SetMethod is { IsInitOnly: false },
                IEventSymbol { AddMethod.IsImplicitlyDeclared: true } => true, // a field-like event: a delegate field
                _ => false,
            };
            if (mutable)
                context.ReportDiagnostic(Diagnostic.Create(StatefulSystem, at,
                    member is IFieldSymbol plain && AssignedOnlyAtConstruction(plain, context.CancellationToken)
                        ? ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Readonly") : null,
                    type.Name, member.Name));
        }
    }

    /// <summary>Whether an instance method's body reads <c>this</c> (its fix cannot make it static then).</summary>
    private static bool UsesThis(IMethodSymbol method, System.Threading.CancellationToken token) =>
        method.DeclaringSyntaxReferences.Any(r => r.GetSyntax(token).DescendantNodes().Any(n => n is ThisExpressionSyntax or BaseExpressionSyntax));

    /// <summary>
    /// EECS076: a hook method that cannot be called (an instance hook of a system is EECS075's: one report); EECS090: a
    /// method named almost like a hook that would be one.
    /// </summary>
    private static void AnalyzeHook(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.MethodKind != MethodKind.Ordinary || !method.IsStatic || !Symbols.IsSystem(method.ContainingType)) return;
        if (Symbols.Hook(method) != Symbols.HookKind.None)
        {
            if (Symbols.HookProblem(method) is { } problem)
                context.ReportDiagnostic(Diagnostic.Create(BadHook, method.Locations.FirstOrDefault(), method.Name, method.ContainingType.Name, problem));
            return;
        }

        if (NearHook(method.Name) is not { } hook || !method.ReturnsVoid || method.IsGenericMethod || !Symbols.HookParameters(method, out _)
            || method.ContainingType.GetMembers(hook).Length > 0 || Symbols.IsForEach(method) || Symbols.IsForEntity(method)
            || Called(method, context.CancellationToken)) return;
        context.ReportDiagnostic(Diagnostic.Create(MisspelledHook, method.Locations.FirstOrDefault(),
            ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Hook").Add("Hook", hook), method.Name, method.ContainingType.Name, hook));
    }

    /// <summary>The names a hook is mistaken for: other engines' and frameworks' lifecycle methods.</summary>
    private static readonly ImmutableDictionary<string, string> HookAliases = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Update"] = "OnUpdate", ["Tick"] = "OnUpdate", ["OnTick"] = "OnUpdate", ["Execute"] = "OnUpdate", ["OnExecute"] = "OnUpdate",
        ["Run"] = "OnUpdate", ["OnRun"] = "OnUpdate", ["Step"] = "OnUpdate", ["OnStep"] = "OnUpdate",
        ["Create"] = "OnCreate", ["Start"] = "OnCreate", ["OnStart"] = "OnCreate", ["Init"] = "OnCreate", ["OnInit"] = "OnCreate",
        ["Initialize"] = "OnCreate", ["OnInitialize"] = "OnCreate", ["Awake"] = "OnCreate", ["OnAwake"] = "OnCreate",
        ["Setup"] = "OnCreate", ["OnSetup"] = "OnCreate", ["OnStartRunning"] = "OnCreate",
        ["Destroy"] = "OnDestroy", ["Dispose"] = "OnDestroy", ["OnDispose"] = "OnDestroy", ["Stop"] = "OnDestroy", ["OnStop"] = "OnDestroy",
        ["Cleanup"] = "OnDestroy", ["OnCleanup"] = "OnDestroy", ["Teardown"] = "OnDestroy", ["OnTeardown"] = "OnDestroy",
        ["OnRemove"] = "OnDestroy", ["OnRemoved"] = "OnDestroy",
    }.ToImmutableDictionary(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The hook <paramref name="name"/> was likely meant to be: a known alias (Update, OnStart, Dispose), or a name at most
    /// two edits from a hook's ignoring case (OnUpate, onUpdate, OnCreated); null for any other name.
    /// </summary>
    internal static string? NearHook(string name)
    {
        if (HookAliases.TryGetValue(name, out var alias)) return alias;
        if (name.Length < 6 || name.Length > 11) return null; // no hook name is within two edits of a shorter or longer one
        foreach (var hook in new[] { "OnCreate", "OnUpdate", "OnDestroy" })
            if (EditDistance(name, hook) <= 2) return hook;
        return null;
    }

    /// <summary>The edit distance of two names, ignoring case (Levenshtein, two rows).</summary>
    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int same = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                current[j] = System.Math.Min(System.Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + same);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>Whether the system's code names the method anywhere but its declaration (a call, a delegate).</summary>
    private static bool Called(IMethodSymbol method, System.Threading.CancellationToken token)
    {
        foreach (var reference in method.ContainingType.DeclaringSyntaxReferences)
            foreach (var name in reference.GetSyntax(token).DescendantNodes().OfType<IdentifierNameSyntax>())
                if (name.Identifier.ValueText == method.Name) return true;
        return false;
    }

    /// <summary>EECS077: a World call that takes a system type with a type that is no system.</summary>
    private static void AnalyzeSystemType(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (call.TargetMethod is not { Name: "AddSystem" or "RemoveSystem" or "HasSystem" or "System" or "TryGetSystem", TypeArguments.Length: 1 } method
            || !Symbols.Named(method.ContainingType, Symbols.World)
            || method.TypeArguments[0] is not INamedTypeSymbol { TypeKind: not TypeKind.Error } type || Symbols.IsSystem(type)) return;
        context.ReportDiagnostic(Diagnostic.Create(NotASystemType, call.Syntax.GetLocation(), type.Name, "World." + method.Name + "<T>"));
    }

    /// <summary>
    /// EECS008: the code of a system reaching another system, through one of its members (a method, a field, a
    /// [ForEntity] entry point) or World.System / TryGetSystem.
    /// </summary>
    private static void AnalyzeSystemReference(OperationAnalysisContext context)
    {
        if (Symbols.SystemOf(context.ContainingSymbol) is not { } system) return;
        if (context.Operation is IInvocationOperation { TargetMethod: { Name: "System" or "TryGetSystem", TypeArguments.Length: 1 } lookup }
            && Symbols.Named(lookup.ContainingType, Symbols.World))
        {
            if (lookup.TypeArguments[0] is { TypeKind: not TypeKind.Error } found)
                context.ReportDiagnostic(Diagnostic.Create(SystemReference, context.Operation.Syntax.GetLocation(), system.Name, found.Name, "World." + lookup.Name));
            return;
        }

        var member = context.Operation switch
        {
            IInvocationOperation invocation => (ISymbol)invocation.TargetMethod,
            IMemberReferenceOperation reference => reference.Member,
            _ => null,
        };
        // A generated [ForEntity] entry point is an extension member of its type: its owner is the extended type.
        var owner = member?.ContainingType is { IsExtension: true } extension && Symbols.IsGenerated(member!)
            ? extension.ExtensionParameter?.Type as INamedTypeSymbol : member?.ContainingType;
        // A constant is a value, not a reach into the other system's code or state.
        if (member is IFieldSymbol { IsConst: true } || owner == null || !Symbols.IsSystem(owner) || Symbols.SystemOf(owner) is not { } other
            || SymbolEqualityComparer.Default.Equals(other.OriginalDefinition, system.OriginalDefinition)) return;
        context.ReportDiagnostic(Diagnostic.Create(SystemReference, context.Operation.Syntax.GetLocation(), system.Name, other.Name,
            other.Name + "." + member!.Name));
    }
}
