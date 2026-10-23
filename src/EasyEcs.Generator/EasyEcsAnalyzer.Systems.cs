using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of systems as declarations (EECS007, EECS008, EECS074-EECS077): a system is an [EcsSystem] ref struct of
/// static members (EECS074, EECS075) that keeps no state of its own (EECS007) and reaches no other system (EECS008);
/// its hooks have one signature each (EECS076); and the API that takes a system type takes an [EcsSystem] (EECS077).
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
        "[{0}] method '{1}' {2}",
        Category, DiagnosticSeverity.Error, true,
        "[OnCreate], [OnUpdate] and [OnDestroy] mark the static methods the world calls when a system is added, on each of " +
        "its runs and when it is removed: one method per hook and system, returning void, taking the World, the " +
        "SystemHandle, both (in either order) or neither. The generator calls no other.", helpLinkUri: Help("EECS076"));

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

    /// <summary>EECS076: a hook method that cannot be called (an instance hook of a system is EECS075's: one report).</summary>
    private static void AnalyzeHook(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!method.IsStatic && Symbols.IsSystem(method.ContainingType)) return;
        if (Symbols.HookProblem(method) is { } problem)
            context.ReportDiagnostic(Diagnostic.Create(BadHook, method.Locations.FirstOrDefault(), "On" + Symbols.Hook(method), method.Name, problem));
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
