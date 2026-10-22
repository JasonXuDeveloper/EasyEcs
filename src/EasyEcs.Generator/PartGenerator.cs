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
/// <c>IIndexed&lt;TKey&gt;.Key</c> from <c>[Key]</c> / <c>[UniqueKey]</c> (on a member, or the type for the value
/// itself), a group's explicit <c>IGroup.Group</c> from <c>[Group]</c>. Each attribute is found with
/// <c>ForAttributeWithMetadataName</c>; a type's declaration is read once (<see cref="Parts"/>), the part written only
/// when nothing is in the way (the analyzer reports what is).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PartGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // One provider per attribute; a type with several attributed fields is found by each, so the models are merged.
        IncrementalValueProvider<ImmutableArray<PartModel>>? found = null;
        foreach (var attribute in Parts.Reductions.Append(Parts.Key).Append(Parts.UniqueKey).Append(Parts.Group))
        {
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(attribute,
                    static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax or ParameterSyntax or TypeDeclarationSyntax,
                    static (ctx, _) => (ctx.TargetSymbol as INamedTypeSymbol ?? ctx.TargetSymbol.ContainingType) is { } type ? PartModel.Create(type) : null)
                .Where(static m => m is not null)
                .Select(static (m, _) => m!)
                .Collect();
            found = found == null ? models : found.Value.Combine(models).Select(static (pair, _) => pair.Left.AddRange(pair.Right));
        }

        var parts = found!.Value.SelectMany(static (all, _) => all.Distinct()).WithTrackingName("Parts");
        context.RegisterSourceOutput(parts, static (spc, m) => spc.AddSource($"{m.HintName}.Part.g.cs", SourceText.From(Emit(m), Encoding.UTF8)));
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
            body.Append($"/// <summary>The key the world indexes the entity by: {(key.Member == "this" ? "the whole value" : key.Member)} (from [{(key.Unique ? "UniqueKey" : "Key")}]).</summary>\n");
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
