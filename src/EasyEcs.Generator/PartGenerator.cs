using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EasyEcs.Generator;

/// <summary>
/// Writes the members a struct's attributes declare into a part of the struct (so the struct and every type
/// containing it are partial, EECS062): a reduction's <c>IReduction&lt;T&gt;</c> from its <c>[Sum]</c> /
/// <c>[Product]</c> / <c>[Min]</c> / <c>[Max]</c> / <c>[All]</c> / <c>[Any]</c> fields, an index's explicit
/// <c>IIndexed&lt;TKey&gt;.Key</c> from <c>[LookupKey]</c> / <c>[UniqueKey]</c> (on a member, or the type for the value
/// itself), a group's explicit <c>IGroup.Group</c> from <c>[Group]</c>. Each attribute is found with
/// <c>ForAttributeWithMetadataName</c>; a type's declaration is read once (<see cref="Parts"/>), the part written only
/// when nothing is in the way (the analyzer reports what is).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PartGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // One provider per attribute. A type with several attributed declarations is found by each, and only its first
        // attribute (FirstAttribute: of the type, else of its first attributed member) builds the model: one model and
        // one file per type, without collecting every part of the compilation into one array that any change regroups.
        foreach (var attribute in Attributes)
        {
            var parts = context.SyntaxProvider.ForAttributeWithMetadataName(attribute,
                    static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax or ParameterSyntax or TypeDeclarationSyntax,
                    static (ctx, _) => (ctx.TargetSymbol as INamedTypeSymbol ?? ctx.TargetSymbol.ContainingType) is { } type
                                    && FirstAttribute(type) is ({ } first, { } attribute)
                                    && SymbolEqualityComparer.Default.Equals(first, ctx.TargetSymbol)
                                    && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, ctx.Attributes[0].AttributeClass)
                        ? PartModel.Create(type) : null)
                .Where(static m => m is not null)
                .WithTrackingName("Parts");
            context.RegisterSourceOutput(parts, static (spc, m) => spc.AddSource($"{m!.HintName}.Part.g.cs", SourceText.From(Emit(m), Encoding.UTF8)));
        }
    }

    private static readonly string[] Attributes = [.. Parts.Reductions, Parts.Key, Parts.UniqueKey, Parts.Group, Parts.GroupBy];

    /// <summary>
    /// The first attribute a part is written for on <paramref name="type"/>'s declarations, and what carries it: the type
    /// itself, else its members in declaration order (a constructor's parameters at the constructor), nested types
    /// excluded; (null, null) when there is none.
    /// </summary>
    private static (ISymbol?, AttributeData?) FirstAttribute(INamedTypeSymbol type)
    {
        if (PartAttribute(type) is { } own) return (type, own);
        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol) continue;
            if (PartAttribute(member) is { } found) return (member, found);
            if (member is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
                foreach (var parameter in constructor.Parameters)
                    if (PartAttribute(parameter) is { } onParameter) return (parameter, onParameter);
        }

        return (null, null);
    }

    private static AttributeData? PartAttribute(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
            foreach (var name in Attributes)
                if (Symbols.Named(attribute.AttributeClass, name)) return attribute;
        return null;
    }

    /// <summary>
    /// The part of one struct, symbol-free: where it goes (its namespace, the heads of it and its containing types,
    /// <see cref="Parts.Heads"/>), its name inside the part, and the reduction's fields.
    /// </summary>
    private sealed record PartModel(string Namespace, EquatableArray<string> Heads, string HintName, string Self,
        EquatableArray<Parts.Field> Fields, Parts.KeyPart? Key, Parts.GroupPart? Group)
    {
        public static PartModel? Create(INamedTypeSymbol type)
        {
            if (Parts.Of(type) is not { Writes: true } parts) return null;
            return new PartModel(type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
                new EquatableArray<string>(Parts.Heads(type)), Symbols.HintName(type), Parts.SelfName(type),
                new EquatableArray<Parts.Field>(parts.WritesReduction ? parts.Fields.ToArray() : []),
                parts.WritesKey ? parts.KeyOf : null, parts.WritesGroup ? parts.GroupOf : null);
        }
    }

    private static string Emit(PartModel m)
    {
        var body = new StringBuilder();
        string self = m.Self, code = Code.GeneratedCode(Symbols.GeneratorName);
        if (m.Fields.Length > 0)
        {
            string Initializer(System.Func<Parts.Field, string> value) =>
                $"new {self} {{ {string.Join(", ", m.Fields.Select(f => f.Name + " = " + value(f)))} }}";
            body.Append($"/// <summary>The value that changes nothing it is combined with: {string.Join(", ", m.Fields.Select(f => $"{f.Name} {Parts.Identity(f.Op, f.Type)}"))} (from the fields' attributes).</summary>\n");
            body.Append($"{code}\npublic static {self} Identity => {Initializer(f => Parts.Identity(f.Op, f.Type))};\n\n");
            body.Append($"/// <summary><paramref name=\"left\"/> combined with <paramref name=\"right\"/>, field by field: {string.Join(", ", m.Fields.Select(f => $"{f.Name} by {f.Op}"))}.</summary>\n");
            body.Append($"{code}\npublic static {self} Combine({self} left, {self} right) => {Initializer(f => Parts.Combine(f.Op, f.Name, f.Type))};\n");
        }

        var bases = new List<string>();
        if (m.Fields.Length > 0) bases.Add($"global::EasyEcs.IReduction<{self}>");
        if (m.Key is { } key)
        {
            if (key.Interface.Length > 0) bases.Add(key.Interface);
            string type = key.Type;
            body.Append($"/// <summary>The key the world indexes the entity by: {(key.Member == "this" ? "the whole value" : key.Member)} (from [{(key.Unique ? "UniqueKey" : "LookupKey")}]).</summary>\n");
            body.Append($"{code}\n{(key.ReadOnly ? "readonly " : "")}{type} global::EasyEcs.IIndexed<{type}>.Key => {key.Member};\n");
        }

        if (m.Group is { } group)
        {
            if (group.Declares) bases.Add("global::EasyEcs.IGroup");
            body.Append($"/// <summary>The group the world keeps the entity's rows in: {group.Value} (from [Group]).</summary>\n");
            body.Append($"{code}\n{(group.ReadOnly ? "readonly " : "")}int global::EasyEcs.IGroup.Group => {group.Value};\n");
        }

        return Code.Part(m.Namespace, m.Heads, string.Join(", ", bases), body.ToString());
    }
}
