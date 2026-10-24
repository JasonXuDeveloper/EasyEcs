using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace EasyEcs.Generator;

/// <summary>
/// <c>[View]</c> ref structs: one entity's components as <c>ref</c> / <c>ref readonly</c> fields, with the business
/// methods that use them. Read once from the symbols for the generators (a view parameter of a <c>[ForEach]</c> method,
/// the members on <c>Entity</c>, the vector loop through a view) and the analyzer (EECS098 to EECS101), so what the
/// generators skip is what the analyzer reports.
/// </summary>
internal static class Views
{
    public const string ViewAttribute = "EasyEcs.ViewAttribute";

    /// <summary>
    /// A component field of a view: the field, its component, whether the view writes it (<c>ref</c> or <c>Mut&lt;T&gt;</c>,
    /// not <c>ref readonly</c>), and whether it is a <c>Mut&lt;T&gt;</c> (stamped on the rows it writes, not on every row).
    /// </summary>
    public readonly record struct Field(IFieldSymbol Symbol, ITypeSymbol Component, bool Writes, bool Mut = false);

    /// <summary>The type carries <c>[View]</c> (valid or not: <see cref="Problem"/> tells).</summary>
    public static bool IsView(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) return false;
        foreach (var attribute in named.GetAttributes())
            if (Symbols.Named(attribute.AttributeClass, ViewAttribute)) return true;
        return false;
    }

    /// <summary>
    /// The view's component fields (its instance <c>ref</c> fields of components and its <c>Mut&lt;T&gt;</c> fields), in
    /// declaration order.
    /// </summary>
    public static List<Field> Fields(INamedTypeSymbol view)
    {
        var fields = new List<Field>();
        foreach (var member in view.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false } field) continue;
            if (field.RefKind is RefKind.Ref or RefKind.RefReadOnly && IsComponent(field.Type))
                fields.Add(new Field(field, field.Type, field.RefKind == RefKind.Ref));
            else if (field.RefKind == RefKind.None && Symbols.MutElement(field.Type) is { } component && IsComponent(component))
                fields.Add(new Field(field, component, true, true));
        }

        return fields;
    }

    private static bool IsComponent(ITypeSymbol type) => type.IsValueType && Symbols.Implements(type, Symbols.Component);

    /// <summary>
    /// Why the generators cannot use <paramref name="view"/> (EECS098), with the symbol to report it on, or null: it is
    /// no ref struct, it is generic or out of reach of generated code, it has a field that is not a settable ref of a
    /// component, it covers a component twice, writes an indexed component, or covers no component or more than a pass
    /// takes.
    /// </summary>
    public static (ISymbol Where, string Text)? Problem(INamedTypeSymbol view)
    {
        if (!view.IsRefLikeType)
            return (view, "is no ref struct: a view holds refs into its entity's row (declare it 'ref struct')");
        if (Symbols.ChainTypeParameters(view).Count > 0)
            return (view, "is generic: a view names the components it covers");
        if (!Symbols.IsVisible(view))
            return (view, "is out of reach of the generated code: make it and the types around it internal or public");
        int count = 0;
        var seen = new Dictionary<ITypeSymbol, IFieldSymbol>(SymbolEqualityComparer.Default);
        foreach (var member in view.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false } field) continue;
            ISymbol where = field.AssociatedSymbol ?? field; // an auto-property's backing field: the property
            var mut = field.RefKind == RefKind.None ? Symbols.MutElement(field.Type) : null;
            if (field.RefKind == RefKind.None && mut == null)
                return (where, $"holds '{where.Name}' by value: a view holds refs (or a Mut<T>) to its entity's components only, and state belongs in a component");
            var component = mut ?? field.Type;
            if (!IsComponent(component))
                return (where, $"refers to {component.Name} through '{field.Name}', which is no component");
            if (field.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return (where, $"hides '{field.Name}': the generated code sets it (make it internal or public)");
            if (field.IsReadOnly)
                return (where, $"declares '{field.Name}' readonly: the generated code sets it in an initializer (drop 'readonly')");
            if (field.RefKind != RefKind.RefReadOnly && Symbols.IsIndexed(component))
                return (where, $"writes indexed component {component.Name} through '{field.Name}', a write its index would miss: make it 'ref readonly'");
            if (seen.TryGetValue(component, out var first))
                return (where, $"covers {component.Name} twice, through '{first.Name}' and '{field.Name}'");
            seen[component] = field;
            count++;
        }

        if (count == 0) return (view, "covers no component: declare a 'ref', 'ref readonly' or Mut<T> field per component");
        if (count > Symbols.MaxComponents) return (view, $"covers {count} components: a pass takes at most {Symbols.MaxComponents}");
        return null;
    }

    /// <summary>A valid view (<see cref="Problem"/> is null).</summary>
    public static bool IsValid(ITypeSymbol? type) => IsView(type) && Problem((INamedTypeSymbol)type!) == null;

    /// <summary>
    /// Why the components a method takes through its views collide (EECS099), or null: a component reached through a view
    /// and through another view or a component parameter (a <c>ref</c> / <c>in</c> component, a span, a <c>Mut</c>, a
    /// <c>Maybe</c>) is two refs to one column, and a pass queries each component once.
    /// </summary>
    public static (IParameterSymbol Parameter, string Text)? Overlap(IMethodSymbol method)
    {
        var owners = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var p in method.Parameters)
        {
            if (Symbols.Classify(p) == Symbols.ParamKind.View) continue;
            if ((Symbols.QueriedComponent(p) ?? Symbols.MaybeElement(p.Type)) is { } component && !owners.ContainsKey(component))
                owners[component] = $"parameter '{p.Name}'";
        }

        foreach (var p in method.Parameters)
        {
            if (Symbols.Classify(p) != Symbols.ParamKind.View || !IsValid(p.Type)) continue;
            foreach (var field in Fields((INamedTypeSymbol)p.Type))
            {
                if (owners.TryGetValue(field.Component, out var other))
                    return (p, $"reaches {field.Component.Name} through view '{p.Name}' and {other}: take each component once (through the view, or a field the view adds)");
                owners[field.Component] = $"view '{p.Name}'";
            }
        }

        return null;
    }

    /// <summary>The components of the views among <paramref name="method"/>'s parameters (valid views only).</summary>
    public static int ComponentCount(IMethodSymbol method)
    {
        int count = 0;
        foreach (var p in method.Parameters)
            if (Symbols.Classify(p) == Symbols.ParamKind.View && IsValid(p.Type))
                count += Fields((INamedTypeSymbol)p.Type).Count;
        return count;
    }
}
