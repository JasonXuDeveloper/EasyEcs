using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// <c>[Prefab]</c> structs: a prefab declared as the components its entities start with, one field per component, the
/// field's initializer its default. Read once from the symbols for the generator (the static <c>Prefab</c> and the
/// constructor) and the analyzer (EECS102), so what the generator skips is what the analyzer reports.
/// </summary>
internal static class Prefabs
{
    public const string PrefabAttribute = "EasyEcs.PrefabAttribute";

    /// <summary>The name of the generated static member that holds the built prefab.</summary>
    public const string Member = "Prefab";

    /// <summary>The type carries <c>[Prefab]</c> (valid or not: <see cref="Problem"/> tells).</summary>
    public static bool IsPrefab(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) return false;
        foreach (var attribute in named.GetAttributes())
            if (Symbols.Named(attribute.AttributeClass, PrefabAttribute)) return true;
        return false;
    }

    /// <summary>The declaration's component fields (its instance fields), in declaration order.</summary>
    public static List<IFieldSymbol> Fields(INamedTypeSymbol declaration) =>
        declaration.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst).ToList();

    /// <summary>
    /// Why the generator cannot write the declaration's members (EECS102), with the symbol to report it on, or null: it
    /// or a type around it is not partial, it is a ref struct, generic or out of reach of generated code, it declares a
    /// member named <c>Prefab</c> itself, a field is no plain component (a property, a singleton, a buffer), a component
    /// appears twice, or it has no component.
    /// </summary>
    public static (ISymbol Where, string Text)? Problem(INamedTypeSymbol declaration)
    {
        if (declaration.IsRefLikeType) return (declaration, "is a ref struct: a prefab declaration holds the values its entities start with ([View] declares refs)");
        if (Symbols.ChainTypeParameters(declaration).Count > 0) return (declaration, "is generic: a prefab declaration names its components");
        if (!Symbols.IsVisible(declaration)) return (declaration, "is out of reach of the generated code: make it and the types around it internal or public");
        if (Parts.NotPartial(declaration) is { } level)
            return (level, $"is not partial{(SymbolEqualityComparer.Default.Equals(level, declaration) ? "" : $" (its containing type {level.Name})")}: the generator writes its static Prefab and constructor into it");
        if (declaration.GetMembers(Member).FirstOrDefault(m => !Parts.IsGeneratedPart(m)) is { } taken)
            return (taken, $"declares a member named '{Member}': the generator writes the static Prefab under that name");
        var seen = new Dictionary<ITypeSymbol, IFieldSymbol>(SymbolEqualityComparer.Default);
        foreach (var field in Fields(declaration))
        {
            ISymbol where = field.AssociatedSymbol ?? field;
            if (field.AssociatedSymbol != null)
                return (where, $"declares '{where.Name}' as a property: declare each component as a field");
            if (field.RefKind != RefKind.None)
                return (where, $"declares '{field.Name}' as a ref field: a prefab declaration holds values ([View] declares refs)");
            if (!field.Type.IsValueType || !Symbols.Implements(field.Type, Symbols.Component))
                return (where, $"declares '{field.Name}' of {field.Type.Name}, which is no component");
            if (field.Type is INamedTypeSymbol { IsGenericType: true } generic && Symbols.Named(generic.OriginalDefinition, Symbols.DynamicBuffer))
                return (where, $"declares '{field.Name}', a buffer, whose list a value cannot carry: give a variant of the Prefab its elements (new Prefab(X.Prefab).SetBuffer(...))");
            if (seen.TryGetValue(field.Type, out var first))
                return (where, $"declares {field.Type.Name} twice, as '{first.Name}' and '{field.Name}'");
            seen[field.Type] = field;
        }

        return seen.Count == 0 ? (declaration, "declares no component: declare one field per component") : null;
    }

    /// <summary>A valid declaration (<see cref="Problem"/> is null).</summary>
    public static bool IsValid(ITypeSymbol? type) => IsPrefab(type) && Problem((INamedTypeSymbol)type!) == null;

    /// <summary>The struct declares a parameterless constructor of its own (the generator writes none then).</summary>
    public static bool DeclaresConstructor(INamedTypeSymbol declaration) =>
        declaration.InstanceConstructors.Any(c => c.Parameters.Length == 0 && !c.IsImplicitlyDeclared && !Parts.IsGeneratedPart(c));

    /// <summary>The field has an initializer (its default value); a field without one starts at <c>default</c>.</summary>
    public static bool Initialized(IFieldSymbol field) =>
        field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null };
}
