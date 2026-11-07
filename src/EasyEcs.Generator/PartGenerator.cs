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

        // A tile that declares ITile<TKey> itself and carries none of the attributes above: its inferred Beside. Found by
        // the base list's syntax (a type named ITile with one type argument), bound only for those; the declaration that
        // lists it first builds the model, so a partial type is written once.
        var tiles = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: { } bases } && bases.Types.Any(static b => IsTileName(b.Type)),
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node, ct) is INamedTypeSymbol { TypeKind: TypeKind.Struct } type
                                    && FirstAttribute(type).Item1 == null && FirstTileDeclaration(type, ct) == ctx.Node
                    ? PartModel.Create(type) : null)
            .Where(static m => m is not null)
            .WithTrackingName("TileParts");
        context.RegisterSourceOutput(tiles, static (spc, m) => spc.AddSource($"{m!.HintName}.Part.g.cs", SourceText.From(Emit(m), Encoding.UTF8)));
    }

    private static readonly string[] Attributes = [.. Parts.Reductions, Parts.MinBy, Parts.MaxBy, Parts.Key, Parts.UniqueKey, Parts.Group, Parts.GroupBy, Parts.OrderBy, Parts.Tile];

    /// <summary>Whether a base type's syntax names <c>ITile&lt;TKey&gt;</c> (<c>ITile&lt;K&gt;</c>, <c>EasyEcs.ITile&lt;K&gt;</c>).</summary>
    private static bool IsTileName(TypeSyntax type) => type switch
    {
        GenericNameSyntax { Identifier.ValueText: "ITile", TypeArgumentList.Arguments.Count: 1 } => true,
        QualifiedNameSyntax { Right: { } right } => IsTileName(right),
        AliasQualifiedNameSyntax { Name: { } name } => IsTileName(name),
        _ => false,
    };

    /// <summary>The first declaration of <paramref name="type"/> whose base list names ITile, or null.</summary>
    private static SyntaxNode? FirstTileDeclaration(INamedTypeSymbol type, System.Threading.CancellationToken ct)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
            if (reference.GetSyntax(ct) is TypeDeclarationSyntax { BaseList: { } bases } declaration && bases.Types.Any(static b => IsTileName(b.Type)))
                return declaration;
        return null;
    }

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
        EquatableArray<Parts.Field> Fields, Parts.KeptPart? Kept, Parts.KeyPart? Key, Parts.GroupPart? Group, Parts.OrderPart? Order,
        Parts.TilePart? Tile, Parts.WindowPart? Window)
    {
        public static PartModel? Create(INamedTypeSymbol type)
        {
            if (Parts.Of(type) is not { Writes: true } parts) return null;
            return new PartModel(type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
                new EquatableArray<string>(Parts.Heads(type)), Symbols.HintName(type), Parts.SelfName(type),
                new EquatableArray<Parts.Field>(parts.WritesReduction ? parts.Fields.ToArray() : []), parts.WritesReduction ? parts.KeptOf : null,
                parts.WritesKey ? parts.KeyOf : null, parts.WritesGroup ? parts.GroupOf : null, parts.WritesOrder ? parts.OrderOf : null,
                parts.WritesTile ? parts.TileOf : null, parts.WritesWindow ? parts.WindowOf : null);
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

        if (m.Kept is { } kept)
        {
            string attribute = kept.Lowest ? "MinBy" : "MaxBy", identity = Parts.Identity(kept.Lowest ? Parts.Op.Min : Parts.Op.Max, kept.Type);
            body.Append($"/// <summary>The value no row replaces: {kept.Key} at {identity}, the other fields at their defaults (from [{attribute}]).</summary>\n");
            body.Append($"{code}\npublic static {self} Identity => new {self} {{ {kept.Key} = {identity} }};\n\n");
            body.Append($"/// <summary>The side with the {(kept.Lowest ? "lower" : "higher")} {kept.Key}, its other fields with it; on a tie <paramref name=\"left\"/>, the earlier rows (from [{attribute}]).</summary>\n");
            body.Append($"{code}\npublic static {self} Combine({self} left, {self} right) => right.{kept.Key} {(kept.Lowest ? "<" : ">")} left.{kept.Key} ? right : left;\n");
        }

        var bases = new List<string>();
        if (m.Fields.Length > 0 || m.Kept != null) bases.Add($"global::EasyEcs.IReduction<{self}>");
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

        if (m.Order is { } order)
        {
            string face = $"global::EasyEcs.IOrderKey<{order.Type}>", modifier = order.ReadOnly ? "readonly " : "";
            bases.Add(face);
            body.Append($"/// <summary>The key the world keeps the entity's table ordered by: {order.Member} (from [OrderBy]).</summary>\n");
            body.Append($"{code}\n{modifier}{order.Type} {face}.OrderKey => {order.Member};\n");
            body.Append($"/// <summary>The point the boxes of the table's clusters cover ({order.Axes} axes, from [OrderBy]).</summary>\n");
            body.Append($"{code}\n{modifier}global::System.Numerics.Vector3 {face}.BoxPoint => {order.Point};\n");
        }

        if (m.Window is { } window)
        {
            string face = $"global::EasyEcs.IDirectMapped<{window.Key}>";
            bases.Add(face);
            body.Append($"/// <summary>The slots of the window the index maps directly (from [UniqueKey(SlotsPerAxis)]).</summary>\n");
            body.Append($"{code}\nstatic int {face}.Slots => {window.Slots};\n");
            body.Append("/// <summary>The slot of <paramref name=\"key\"/>: each coordinate masked to the window, row-major (from [UniqueKey(SlotsPerAxis)]).</summary>\n");
            body.Append($"{code}\nstatic int {face}.SlotOf({window.Key} key) => {window.SlotOf};\n");
        }

        string usings = "";
        if (m.Tile is { } tile)
        {
            string face = $"global::EasyEcs.ITile<{tile.Key}>";
            if (tile.Declares) bases.Add(face);
            if (tile.Shape.Length > 0)
            {
                body.Append("/// <summary>The tile's shape (from [Tile]).</summary>\n");
                foreach (var line in tile.Shape.TrimEnd('\n').Split('\n')) body.Append($"{code}\n{line}\n");
            }

            if (tile.Beside.Length > 0)
                body.Append("/// <summary>The key of the tile beside <paramref name=\"key\"/> across <paramref name=\"face\"/>: one step along the face's axis (north -z, south +z, west -x, east +x, below -y, above +y).</summary>\n")
                    .Append($"{code}\nstatic {tile.Key} {face}.Beside({tile.Key} key, {TileParts.FacesAlias} face) => {tile.Beside};\n");
            if (tile.Beside.Length > 0 || tile.Shape.Contains(TileParts.FacesAlias)) usings = $"using {TileParts.FacesAlias} = global::EasyEcs.TileFaces;\n";
        }

        return Code.Part(m.Namespace, m.Heads, string.Join(", ", bases), body.ToString(), usings);
    }
}
