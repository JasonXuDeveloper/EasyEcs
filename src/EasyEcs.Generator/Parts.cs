using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// What a struct's attributes declare that the generator writes into a part of the struct (PartGenerator): a
/// reduction's <c>Identity</c> and <c>Combine</c> from <c>[Sum]</c> / <c>[Min]</c> / ... fields, an index's <c>Key</c>
/// from <c>[LookupKey]</c> / <c>[UniqueKey]</c>, a group's <c>Group</c> from <c>[Group]</c>. Read once from the
/// symbols for the generator and the analyzer alike, so what the generator skips is what the analyzer reports
/// (<see cref="Problems"/>). The generators of a compilation do not see each other's output, so other generators read a
/// declaration from its attributes through these helpers, never through the interface the part adds.
/// </summary>
internal sealed class Parts
{
    public const string Sum = "EasyEcs.SumAttribute", Product = "EasyEcs.ProductAttribute", Min = "EasyEcs.MinAttribute",
        Max = "EasyEcs.MaxAttribute", All = "EasyEcs.AllAttribute", Any = "EasyEcs.AnyAttribute";

    /// <summary>The reduction attributes, in the order of <see cref="Op"/>.</summary>
    public static readonly string[] Reductions = [Sum, Product, Min, Max, All, Any];

    /// <summary>How a reduction field combines: the attribute it carries.</summary>
    public enum Op { Sum, Product, Min, Max, All, Any }

    public const string Key = "EasyEcs.LookupKeyAttribute", UniqueKey = "EasyEcs.UniqueKeyAttribute", Group = "EasyEcs.GroupAttribute",
        GroupBy = "EasyEcs.GroupByAttribute";

    /// <summary>
    /// The rule a problem breaks: the analyzer's descriptor for it (EECS062, EECS063, EECS064; a key's and a group's are
    /// EECS036's).
    /// </summary>
    public enum Rule { NotPartial, HandWritten, BadField, BadKey, BadGroup }

    /// <summary>One thing the generator cannot write: where, which rule, and the message's text.</summary>
    public readonly record struct Problem(ISymbol Where, Rule Rule, string Text);

    /// <summary>A reduction field: its name as an initializer sets it, its type as C# writes it, its op.</summary>
    public readonly record struct Field(string Name, string Type, Op Op);

    /// <summary>
    /// A key declared by <c>[LookupKey]</c> / <c>[UniqueKey]</c>: the member returned (<c>this</c> on the type), its type
    /// (<paramref name="Type"/> as the part writes it: the type's own name inside it for <c>this</c>), whether it is unique, the interface the part adds ("" when one the
    /// struct declares brings it) and whether the member may be read from a <c>readonly</c> member without a copy.
    /// </summary>
    public sealed record KeyPart(string Member, string Type, bool Unique, string Interface, bool ReadOnly);

    /// <summary>A group declared by <c>[Group]</c>: the expression returned (<c>(int)Zone</c> for an enum), whether the part adds IGroup.</summary>
    public sealed record GroupPart(string Value, bool Declares, bool ReadOnly);

    public INamedTypeSymbol Type { get; }

    /// <summary>The reduction's fields, in declaration order (empty when it declares none).</summary>
    public List<Field> Fields { get; } = [];

    /// <summary>The declared key, or null.</summary>
    public KeyPart? KeyOf { get; private set; }

    /// <summary>The declared key's type (a symbol, for other generators), or null.</summary>
    public ITypeSymbol? KeyType { get; private set; }

    /// <summary>The declared group, or null.</summary>
    public GroupPart? GroupOf { get; private set; }

    /// <summary>What keeps the generator from writing the part (each reported by the analyzer).</summary>
    public List<Problem> Problems { get; } = [];

    private Parts(INamedTypeSymbol type) => Type = type;

    /// <summary>Whether the type declares a reduction by attributes.</summary>
    public bool Reduces => Fields.Count > 0 || Problems.Any(p => p.Rule == Rule.BadField);

    private bool Blocked(Rule rule) => Problems.Any(p => p.Rule == Rule.NotPartial || p.Rule == rule || rule == Rule.BadField && p.Rule == Rule.HandWritten);

    /// <summary>Whether the generator writes the reduction's members: declared, and nothing in the way.</summary>
    public bool WritesReduction => Fields.Count > 0 && !Blocked(Rule.BadField);

    /// <summary>Whether the generator writes the key.</summary>
    public bool WritesKey => KeyOf != null && !Blocked(Rule.BadKey);

    /// <summary>Whether the generator writes the group.</summary>
    public bool WritesGroup => GroupOf != null && !Blocked(Rule.BadGroup);

    /// <summary>Whether the generator writes anything into the type.</summary>
    public bool Writes => WritesReduction || WritesKey || WritesGroup;

    /// <summary>
    /// Whether <paramref name="type"/> or one of its members carries <paramref name="attribute"/>: the cheap test the
    /// helpers of other generators make before reading a declaration (no allocation).
    /// </summary>
    public static bool Declares(ITypeSymbol type, string attribute)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) return false;
        foreach (var a in named.GetAttributes())
            if (Symbols.Named(a.AttributeClass, attribute)) return true;
        foreach (var member in named.GetMembers())
        {
            if (member is not (IFieldSymbol or IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Constructor })) continue;
            if (member is IMethodSymbol constructor)
            {
                if (!named.IsRecord) continue;
                foreach (var parameter in constructor.Parameters)
                    foreach (var a in parameter.GetAttributes())
                        if (Symbols.Named(a.AttributeClass, attribute)) return true;
                continue;
            }

            foreach (var a in member.GetAttributes())
                if (Symbols.Named(a.AttributeClass, attribute)) return true;
        }

        return false;
    }

    /// <summary>
    /// What <paramref name="type"/> declares, or null when it declares nothing a part would hold. Cheap for a type
    /// without the attributes: one look at each member's attributes.
    /// </summary>
    public static Parts? Of(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct) return null;
        var members = Members(type);
        var parts = new Parts(type);
        parts.ReadReduction(members);
        parts.ReadKey(members);
        parts.ReadGroup(members);
        if (parts.Fields.Count == 0 && parts.KeyOf == null && parts.GroupOf == null && parts.Problems.Count == 0) return null;
        if (NotPartial(type) is { } level)
            parts.Problems.Add(new Problem(level, Rule.NotPartial, level.Name));
        return parts;
    }

    private void ReadReduction(List<Member> members)
    {
        bool any = false;
        foreach (var member in members)
        {
            Op? op = null;
            bool twice = false;
            foreach (var attribute in member.Attributes)
            {
                int index = ReductionIndex(attribute.AttributeClass);
                if (index < 0) continue;
                any = true;
                if (op != null && !twice)
                {
                    twice = true;
                    Problems.Add(new Problem(member.Where, Rule.BadField, $"carries two reduction attributes ([{op}] and [{(Op)index}]): one combine per field"));
                }

                op ??= (Op)index;
            }

            if (op is not { } found || twice) continue;
            if (!member.Data) Problems.Add(new Problem(member.Where, Rule.BadField, $"is a computed property, but [{found}] combines a field (or an auto-property)"));
            else if (Unsupported(found, member.Type) is { } why) Problems.Add(new Problem(member.Where, Rule.BadField, why));
            else if (!member.Settable) Problems.Add(new Problem(member.Where, Rule.BadField, "cannot be set in an object initializer (readonly, or a property without a setter): Combine builds a new value"));
            else Fields.Add(new Field(Code.Escape(member.Name), member.Type.ToDisplayString(Symbols.Fqn), found));
        }

        if (!any) return;
        // Every instance field takes part: an unattributed one would be dropped by every Combine.
        foreach (var member in members)
            if (member.Data && !member.Attributes.Any(a => ReductionIndex(a.AttributeClass) >= 0))
                Problems.Add(new Problem(member.Where, Rule.BadField, "has no reduction attribute, and the generated Combine would drop it: give it one ([Sum], [Product], [Min], [Max], [All], [Any]) or write IReduction<T> by hand"));
        foreach (var name in new[] { "Identity", "Combine" })
            foreach (var own in Type.GetMembers(name))
                if (!own.IsImplicitlyDeclared && !IsGeneratedPart(own))
                    Problems.Add(new Problem(own, Rule.HandWritten, name));
    }

    /// <summary>
    /// <c>[LookupKey]</c> / <c>[UniqueKey]</c> on a member or the type: one key, of a type that is <c>IEquatable</c> of itself,
    /// agreeing with the key of an interface the struct declares, no <c>Key</c> written by hand, and no unique
    /// <c>EntityId</c> key (EECS036).
    /// </summary>
    private void ReadKey(List<Member> members)
    {
        var keys = new List<(ISymbol Where, string Member, ITypeSymbol Type, bool Unique, bool ReadOnly)>();
        foreach (var a in Type.GetAttributes())
            if (Symbols.Named(a.AttributeClass, Key) || Symbols.Named(a.AttributeClass, UniqueKey))
                keys.Add((Type, "this", Type, Symbols.Named(a.AttributeClass, UniqueKey), true));
        foreach (var member in members)
            foreach (var a in member.Attributes)
                if (Symbols.Named(a.AttributeClass, Key) || Symbols.Named(a.AttributeClass, UniqueKey))
                    keys.Add((member.Where, Code.Escape(member.Name), member.Type, Symbols.Named(a.AttributeClass, UniqueKey), member.ReadOnly));
        if (keys.Count == 0) return;
        var key = keys[0];
        string Name(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        string Of((ISymbol Where, string Member, ITypeSymbol Type, bool Unique, bool ReadOnly) k) =>
            $"[{(k.Unique ? "UniqueKey" : "LookupKey")}] on {(k.Member == "this" ? "the type" : $"'{k.Where.Name}'")}";
        KeyOf = new KeyPart(key.Member, key.Member == "this" ? SelfName(Type) : key.Type.ToDisplayString(Symbols.Fqn), key.Unique, "", key.ReadOnly);
        KeyType = key.Type;
        if (keys.Count > 1)
        {
            Problems.Add(new Problem(keys[1].Where, Rule.BadKey, $"has two keys ({Of(key)} and {Of(keys[1])}), but a component has one index"));
            return;
        }

        if (!Symbols.IsEquatable(key.Type))
        {
            Problems.Add(new Problem(key.Where, Rule.BadKey, $"is indexed by {Name(key.Type)} ({Of(key)}), which is not IEquatable<{Name(key.Type)}>: an index compares keys with it"));
            return;
        }

        if (key.Unique && Symbols.Named(key.Type, Symbols.EntityId))
        {
            Problems.Add(new Problem(key.Where, Rule.BadKey, "has a unique EntityId key ([UniqueKey]), but an EntityId key cannot be unique: index the relation with [LookupKey]"));
            return;
        }

        // The struct's own interfaces (in a generator's compilation, the ones it declares; in the analyzer's, the part's too).
        var declared = Symbols.InterfaceKey(Type, out bool unique);
        if (declared != null && !SymbolEqualityComparer.Default.Equals(declared, key.Type))
        {
            Problems.Add(new Problem(key.Where, Rule.BadKey, $"declares IIndexed<{Name(declared)}> and {Of(key)} of type {Name(key.Type)}, but a component has one key"));
            return;
        }

        foreach (var own in Type.GetMembers())
            if (own is IPropertySymbol property && (property.Name == "Key" || property.ExplicitInterfaceImplementations.Any(i => i.Name == "Key" && Symbols.Named(i.ContainingType.OriginalDefinition, Symbols.Indexed)))
                && !IsGeneratedPart(own))
            {
                Problems.Add(new Problem(own, Rule.BadKey, $"writes Key itself and declares it with {Of(key)}: remove one"));
                return;
            }

        string face = declared != null && (unique || !key.Unique) ? ""
            : $"global::EasyEcs.{(key.Unique ? "IUniqueIndexed" : "IIndexed")}<{KeyOf.Type}>";
        KeyOf = KeyOf with { Interface = face };
    }

    /// <summary>
    /// <c>[Group]</c> (or <c>[GroupBy]</c>, which groups in every world too) on one member, an <c>int</c> or an enum of
    /// <c>int</c>, with no <c>Group</c> written by hand (EECS036). <c>[GroupBy]</c> on the hand-written <c>Group</c> of an
    /// <c>IGroup</c> declares no part: the type groups already.
    /// </summary>
    private void ReadGroup(List<Member> members)
    {
        var groups = members.Where(m => m.Attributes.Any(a => Symbols.Named(a.AttributeClass, Group) || Symbols.Named(a.AttributeClass, GroupBy))
                                        && !IsOwnGroup(m)).ToList();
        if (groups.Count == 0) return;
        var group = groups[0];
        bool enumOfInt = group.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType.SpecialType: SpecialType.System_Int32 };
        GroupOf = new GroupPart(enumOfInt ? $"(int){Code.Escape(group.Name)}" : Code.Escape(group.Name), !Symbols.Implements(Type, Symbols.Group), group.ReadOnly);
        if (groups.Count > 1)
            Problems.Add(new Problem(groups[1].Where, Rule.BadGroup, $"has two groups ([Group] on '{group.Where.Name}' and '{groups[1].Where.Name}'), but a component is grouped by one value"));
        else if (group.Type.SpecialType != SpecialType.System_Int32 && !enumOfInt)
            Problems.Add(new Problem(group.Where, Rule.BadGroup, $"is grouped by '{group.Where.Name}', a {group.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}: a group is an int or an enum of int"));
        else if (Type.GetMembers().Any(m => m is IPropertySymbol p && (p.Name == "Group" || p.ExplicitInterfaceImplementations.Any(i => i.Name == "Group")) && !IsGeneratedPart(m)))
            Problems.Add(new Problem(group.Where, Rule.BadGroup, "writes Group itself and declares it with [Group]: remove one"));
    }

    /// <summary>The hand-written <c>Group</c> of an <c>IGroup</c> (the member a <c>[GroupBy]</c> may mark without declaring a part).</summary>
    private bool IsOwnGroup(Member member) =>
        member is { Name: "Group", Data: false } && Symbols.Implements(Type, Symbols.Group) && !IsGeneratedPart(member.Where);

    /// <summary>
    /// A member that may carry a declaration: <paramref name="Where"/> it is declared (a record's positional parameter
    /// stands for its property), the attributes on it (a positional parameter's and its property's), its name and type,
    /// whether it is data (an instance field or auto-property: what a value holds) and settable in an initializer.
    /// </summary>
    public sealed record Member(ISymbol Where, IEnumerable<AttributeData> Attributes, string Name, ITypeSymbol Type, bool Data, bool Settable, bool ReadOnly);

    /// <summary>The instance fields, auto-properties, computed properties and positional parameters of <paramref name="type"/>.</summary>
    public static List<Member> Members(INamedTypeSymbol type)
    {
        var list = new List<Member>();
        var auto = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var positional = Positional(type);
        foreach (var member in type.GetMembers())
            switch (member)
            {
                case IFieldSymbol { IsStatic: false, IsConst: false, AssociatedSymbol: IPropertySymbol property }:
                    auto.Add(property);
                    var parameter = positional.FirstOrDefault(p => p.Name == property.Name);
                    list.Add(new Member((ISymbol?)parameter ?? property,
                        parameter == null ? property.GetAttributes() : parameter.GetAttributes().Concat(property.GetAttributes()),
                        property.Name, property.Type, true, property.SetMethod != null, true));
                    break;
                case IFieldSymbol { IsStatic: false, IsConst: false, IsImplicitlyDeclared: false } field:
                    list.Add(new Member(field, field.GetAttributes(), field.Name, field.Type, true, !field.IsReadOnly, true));
                    break;
            }

        foreach (var member in type.GetMembers())
            if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, ExplicitInterfaceImplementations.Length: 0 } property && !auto.Contains(property))
                list.Add(new Member(property, property.GetAttributes(), property.Name, property.Type, false, false,
                    property.GetMethod is { IsReadOnly: true } || property.IsReadOnly || property.ContainingType.IsReadOnly));
        return list;
    }

    /// <summary>The positional parameters of a record (its primary constructor's), else none.</summary>
    private static IEnumerable<IParameterSymbol> Positional(INamedTypeSymbol type)
    {
        if (!type.IsRecord) return [];
        foreach (var constructor in type.InstanceConstructors)
            foreach (var reference in constructor.DeclaringSyntaxReferences)
                if (reference.GetSyntax() is RecordDeclarationSyntax)
                    return constructor.Parameters;
        return [];
    }

    private static int ReductionIndex(INamedTypeSymbol? attribute)
    {
        if (attribute == null) return -1;
        for (int i = 0; i < Reductions.Length; i++)
            if (Symbols.Named(attribute, Reductions[i])) return i;
        return -1;
    }

    /// <summary>Whether <paramref name="op"/> can combine a field of <paramref name="type"/> (what the attribute accepts).</summary>
    public static bool Fits(Op op, ITypeSymbol type) =>
        op is Op.All or Op.Any ? type.SpecialType == SpecialType.System_Boolean : Numeric(type);

    /// <summary>Why <paramref name="op"/> cannot combine a field of <paramref name="type"/>, or null.</summary>
    private static string? Unsupported(Op op, ITypeSymbol type)
    {
        if (Fits(op, type)) return null;
        return op is Op.All or Op.Any
            ? $"is {type.ToDisplayString()}, but [{op}] combines bools"
            : $"is {type.ToDisplayString()}, but [{op}] combines numbers (an integer type, float, double or decimal)";
    }

    private static bool Numeric(ITypeSymbol type) => type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
        or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    /// <summary>A type whose arithmetic is done in <c>int</c>: its sum or product is cast back.</summary>
    public static bool Narrow(string type) => type is "sbyte" or "byte" or "short" or "ushort";

    /// <summary>The identity of <paramref name="op"/> for a field of <paramref name="type"/> (C#).</summary>
    public static string Identity(Op op, string type) => op switch
    {
        Op.Sum => "0",
        Op.Product => "1",
        Op.Min => type is "float" or "double" ? type + ".PositiveInfinity" : type + ".MaxValue",
        Op.Max => type is "float" or "double" ? type + ".NegativeInfinity" : type + ".MinValue",
        Op.All => "true",
        _ => "false",
    };

    /// <summary><paramref name="op"/> of the field <paramref name="name"/> of <c>left</c> and <c>right</c> (C#).</summary>
    public static string Combine(Op op, string name, string type)
    {
        string l = "left." + name, r = "right." + name;
        string value = op switch
        {
            Op.Sum => $"{l} + {r}",
            Op.Product => $"{l} * {r}",
            Op.Min => $"global::System.Math.Min({l}, {r})",
            Op.Max => $"global::System.Math.Max({l}, {r})",
            Op.All => $"{l} & {r}",
            _ => $"{l} | {r}",
        };
        return op is Op.Sum or Op.Product && Narrow(type) ? $"({type})({value})" : value;
    }

    // ---------------- partial chains ----------------

    /// <summary>
    /// The first of <paramref name="type"/> and its containing types (innermost first) that is not partial, or a file
    /// type (no other file can add to it), else null: where a generated part cannot go.
    /// </summary>
    public static INamedTypeSymbol? NotPartial(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (t.IsFileLocal || !IsPartial(t)) return t;
        return null;
    }

    /// <summary>Whether a declaration of <paramref name="type"/> in source says <c>partial</c>.</summary>
    public static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                return true;
        return false;
    }

    /// <summary>
    /// The heads of a part of <paramref name="type"/>, outermost first: each level's accessibility, <c>partial</c>, its
    /// kind (<c>class</c>, <c>struct</c>, <c>record</c>, <c>record struct</c>, <c>interface</c>) and name with its type
    /// parameters (<c>internal partial class Outer&lt;T&gt;</c>). Constraints are left to the other parts.
    /// </summary>
    public static string[] Heads(INamedTypeSymbol type)
    {
        var heads = new List<string>();
        for (var t = type; t != null; t = t.ContainingType)
        {
            string access = t.DeclaredAccessibility switch
            {
                Accessibility.Public => "public ",
                Accessibility.Internal => "internal ",
                Accessibility.Private => "private ",
                Accessibility.Protected => "protected ",
                Accessibility.ProtectedOrInternal => "protected internal ",
                Accessibility.ProtectedAndInternal => "private protected ",
                _ => "",
            };
            string kind = t.TypeKind switch
            {
                TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
                TypeKind.Interface => "interface",
                _ => t.IsRecord ? "record" : "class",
            };
            string modifiers = (t.IsReadOnly && t.TypeKind == TypeKind.Struct ? "readonly " : "") + (t.IsRefLikeType ? "ref " : "");
            heads.Insert(0, $"{access}{modifiers}partial {kind} {Code.Escape(t.Name)}{Symbols.TypeParameterList(t.TypeParameters)}");
        }

        return heads.ToArray();
    }

    /// <summary>The type as code inside its own part names it: its name and type parameters (<c>Total&lt;T&gt;</c>).</summary>
    public static string SelfName(INamedTypeSymbol type) => Code.Escape(type.Name) + Symbols.TypeParameterList(type.TypeParameters);

    /// <summary>A member a generated part wrote: it carries the generator's <c>[GeneratedCode]</c>.</summary>
    public static bool IsGeneratedPart(ISymbol member) => member.GetAttributes().Any(a =>
        Symbols.Named(a.AttributeClass, "System.CodeDom.Compiler.GeneratedCodeAttribute")
        && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value as string == Symbols.GeneratorName);
}
