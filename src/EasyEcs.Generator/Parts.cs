using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// What a struct's attributes declare that the generator writes into a part of the struct (PartGenerator): a
/// reduction's <c>Identity</c> and <c>Combine</c> from <c>[Sum]</c> / <c>[Min]</c> / ... fields. Read once from the
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

    /// <summary>The rule a problem breaks: the analyzer's descriptor for it.</summary>
    public enum Rule { NotPartial, HandWritten, BadField }

    /// <summary>One thing the generator cannot write: where, which rule, and the message's text.</summary>
    public readonly record struct Problem(ISymbol Where, Rule Rule, string Text);

    /// <summary>A reduction field: its name as an initializer sets it, its type as C# writes it, its op.</summary>
    public readonly record struct Field(string Name, string Type, Op Op);

    public INamedTypeSymbol Type { get; }

    /// <summary>The reduction's fields, in declaration order (empty when it declares none).</summary>
    public List<Field> Fields { get; } = [];

    /// <summary>What keeps the generator from writing the part (each reported by the analyzer).</summary>
    public List<Problem> Problems { get; } = [];

    private Parts(INamedTypeSymbol type) => Type = type;

    /// <summary>Whether the type declares a reduction by attributes.</summary>
    public bool Reduces => Fields.Count > 0 || Problems.Any(p => p.Rule == Rule.BadField);

    /// <summary>Whether the generator writes the reduction's members: declared, and nothing in the way.</summary>
    public bool WritesReduction => Fields.Count > 0 && Problems.Count == 0;

    /// <summary>
    /// What <paramref name="type"/> declares, or null when it declares nothing a part would hold. Cheap for a type
    /// without the attributes: one look at each member's attributes.
    /// </summary>
    public static Parts? Of(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct) return null;
        var members = Members(type);
        Parts? parts = null;
        foreach (var member in members)
        {
            Op? op = null;
            bool twice = false;
            foreach (var attribute in member.Attributes)
            {
                int index = ReductionIndex(attribute.AttributeClass);
                if (index < 0) continue;
                parts ??= new Parts(type);
                if (op != null && !twice)
                {
                    twice = true;
                    parts.Problems.Add(new Problem(member.Where, Rule.BadField, $"carries two reduction attributes ([{op}] and [{(Op)index}]): one combine per field"));
                }

                op ??= (Op)index;
            }

            if (op is not { } found || twice) continue;
            if (!member.Data) parts!.Problems.Add(new Problem(member.Where, Rule.BadField, $"is a computed property, but [{found}] combines a field (or an auto-property)"));
            else if (Unsupported(found, member.Type) is { } why) parts!.Problems.Add(new Problem(member.Where, Rule.BadField, why));
            else if (!member.Settable) parts!.Problems.Add(new Problem(member.Where, Rule.BadField, "cannot be set in an object initializer (readonly, or a property without a setter): Combine builds a new value"));
            else parts!.Fields.Add(new Field(Code.Escape(member.Name), member.Type.ToDisplayString(Symbols.Fqn), found));
        }

        if (parts == null) return null;
        // Every instance field takes part: an unattributed one would be dropped by every Combine.
        foreach (var member in members)
            if (member.Data && !member.Attributes.Any(a => ReductionIndex(a.AttributeClass) >= 0))
                parts.Problems.Add(new Problem(member.Where, Rule.BadField, "has no reduction attribute, and the generated Combine would drop it: give it one ([Sum], [Product], [Min], [Max], [All], [Any]) or write IReduction<T> by hand"));
        foreach (var name in new[] { "Identity", "Combine" })
            foreach (var own in type.GetMembers(name))
                if (!own.IsImplicitlyDeclared && !IsGeneratedPart(own))
                    parts.Problems.Add(new Problem(own, Rule.HandWritten, name));
        if (NotPartial(type) is { } level)
            parts.Problems.Add(new Problem(level, Rule.NotPartial, level.Name));
        return parts;
    }

    /// <summary>
    /// A member that may carry a declaration: <paramref name="Where"/> it is declared (a record's positional parameter
    /// stands for its property), the attributes on it (a positional parameter's and its property's), its name and type,
    /// whether it is data (an instance field or auto-property: what a value holds) and settable in an initializer.
    /// </summary>
    public sealed record Member(ISymbol Where, IEnumerable<AttributeData> Attributes, string Name, ITypeSymbol Type, bool Data, bool Settable);

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
                        property.Name, property.Type, true, property.SetMethod != null));
                    break;
                case IFieldSymbol { IsStatic: false, IsConst: false, IsImplicitlyDeclared: false } field:
                    list.Add(new Member(field, field.GetAttributes(), field.Name, field.Type, true, !field.IsReadOnly));
                    break;
            }

        foreach (var member in type.GetMembers())
            if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, ExplicitInterfaceImplementations.Length: 0 } property && !auto.Contains(property))
                list.Add(new Member(property, property.GetAttributes(), property.Name, property.Type, false, false));
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

    /// <summary>Why <paramref name="op"/> cannot combine a field of <paramref name="type"/>, or null.</summary>
    private static string? Unsupported(Op op, ITypeSymbol type)
    {
        bool boolean = type.SpecialType == SpecialType.System_Boolean;
        if (op is Op.All or Op.Any)
            return boolean ? null : $"is {type.ToDisplayString()}, but [{op}] combines bools";
        return Numeric(type) ? null : $"is {type.ToDisplayString()}, but [{op}] combines numbers (an integer type, float, double or decimal)";
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
