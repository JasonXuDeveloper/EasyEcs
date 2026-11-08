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

    /// <summary>The attributes of a reduction that keeps the row with the lowest (highest) key: <see cref="KeptPart"/>.</summary>
    public const string MinBy = "EasyEcs.MinByAttribute", MaxBy = "EasyEcs.MaxByAttribute";

    public const string Key = "EasyEcs.LookupKeyAttribute", UniqueKey = "EasyEcs.UniqueKeyAttribute", Group = "EasyEcs.GroupAttribute",
        GroupBy = "EasyEcs.GroupByAttribute", OrderBy = "EasyEcs.OrderByAttribute", Tile = TileParts.Attribute;

    /// <summary>
    /// The rule a problem breaks: the analyzer's descriptor for it (EECS062, EECS063, EECS064; a key's and a group's are
    /// EECS036's; a tile's EECS124, a [Tile] on a type it does not fit EECS068's; a window's EECS126).
    /// </summary>
    public enum Rule { NotPartial, HandWritten, BadField, BadKey, BadGroup, BadOrder, BadTile, TileSetup, BadWindow }

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

    /// <summary>
    /// An order declared by <c>[OrderBy]</c>: the key member and its type (as the part writes it), the expression of the
    /// box point (a <c>Vector3</c>) and its axes, the cadence and margin the registration passes, and whether both
    /// members may be <c>readonly</c>.
    /// </summary>
    public sealed record OrderPart(string Member, string Type, string Point, int Axes, int Interval, float Margin, bool ReadOnly);

    /// <summary>
    /// A reduction that keeps the row with the lowest key (<c>[MinBy]</c>, <paramref name="Lowest"/>) or the highest
    /// (<c>[MaxBy]</c>): the key member and its type (as the part writes it); the other fields travel with it.
    /// </summary>
    public sealed record KeptPart(string Key, string Type, bool Lowest);

    /// <summary>
    /// A tile's members: its key type (as the part writes it), whether the part adds <c>ITile&lt;TKey&gt;</c> (from
    /// <c>[Tile]</c>), the shape members <c>[Tile]</c> sets (C#, one per line, "" for none) and the body of the inferred
    /// <c>Beside</c> ("" when the type writes its own).
    /// </summary>
    public sealed record TilePart(string Key, bool Declares, string Shape, string Beside);

    /// <summary>
    /// A direct-mapped window (<c>[UniqueKey(SlotsPerAxis = N)]</c>): the key type (as the part writes it), the slot count
    /// and the body of <c>SlotOf(key)</c>.
    /// </summary>
    public sealed record WindowPart(string Key, int Slots, string SlotOf);

    public INamedTypeSymbol Type { get; }

    /// <summary>The reduction's fields, in declaration order (empty when it declares none).</summary>
    public List<Field> Fields { get; } = [];

    /// <summary>The declared key, or null.</summary>
    public KeyPart? KeyOf { get; private set; }

    /// <summary>The declared key's type (a symbol, for other generators), or null.</summary>
    public ITypeSymbol? KeyType { get; private set; }

    /// <summary>The declared group, or null.</summary>
    public GroupPart? GroupOf { get; private set; }

    /// <summary>The declared order, or null.</summary>
    public OrderPart? OrderOf { get; private set; }

    /// <summary>The key of a reduction that keeps one row (<c>[MinBy]</c> / <c>[MaxBy]</c>), or null.</summary>
    public KeptPart? KeptOf { get; private set; }

    /// <summary>The tile's members the generator writes (<c>[Tile]</c>, an inferred <c>Beside</c>), or null.</summary>
    public TilePart? TileOf { get; private set; }

    /// <summary>The direct-mapped window of the key, or null.</summary>
    public WindowPart? WindowOf { get; private set; }

    /// <summary>What keeps the generator from writing the part (each reported by the analyzer).</summary>
    public List<Problem> Problems { get; } = [];

    private Parts(INamedTypeSymbol type) => Type = type;

    /// <summary>Whether the type declares a reduction by attributes.</summary>
    public bool Reduces => Fields.Count > 0 || KeptOf != null || Problems.Any(p => p.Rule == Rule.BadField);

    private bool Blocked(Rule rule) => Problems.Any(p => p.Rule == Rule.NotPartial || p.Rule == rule || rule == Rule.BadField && p.Rule == Rule.HandWritten);

    /// <summary>Whether the generator writes the reduction's members: declared, and nothing in the way.</summary>
    public bool WritesReduction => (Fields.Count > 0 || KeptOf != null) && !Blocked(Rule.BadField);

    /// <summary>Whether the generator writes the key.</summary>
    public bool WritesKey => KeyOf != null && !Blocked(Rule.BadKey);

    /// <summary>Whether the generator writes the group.</summary>
    public bool WritesGroup => GroupOf != null && !Blocked(Rule.BadGroup);

    /// <summary>Whether the generator writes the order's members (and its registration: ComponentGenerator).</summary>
    public bool WritesOrder => OrderOf != null && !Blocked(Rule.BadOrder);

    /// <summary>Whether the generator writes the tile's members.</summary>
    public bool WritesTile => TileOf != null && !Blocked(Rule.BadTile) && !Blocked(Rule.TileSetup);

    /// <summary>Whether the generator writes the window's <c>Slots</c> and <c>SlotOf</c>: they need the key it writes.</summary>
    public bool WritesWindow => WindowOf != null && WritesKey && !Blocked(Rule.BadWindow);

    /// <summary>Whether the generator writes anything into the type.</summary>
    public bool Writes => WritesReduction || WritesKey || WritesGroup || WritesOrder || WritesTile || WritesWindow;

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
        parts.ReadOrder(members);
        parts.ReadTile();
        if (parts.Fields.Count == 0 && parts.KeptOf == null && parts.KeyOf == null && parts.GroupOf == null && parts.OrderOf == null
            && parts.TileOf == null && parts.Problems.Count == 0) return null;
        if (NotPartial(type) is { } level)
            parts.Problems.Add(new Problem(level, Rule.NotPartial, level.Name));
        return parts;
    }

    private void ReadReduction(List<Member> members)
    {
        var kept = members.Where(m => m.Attributes.Any(a => Symbols.Named(a.AttributeClass, MinBy) || Symbols.Named(a.AttributeClass, MaxBy))).ToList();
        if (kept.Count > 0)
        {
            ReadKept(members, kept);
            return;
        }

        // A reduction attribute on the struct is the op of every field that carries none of its own.
        Op? all = null;
        foreach (var attribute in Type.GetAttributes())
        {
            int index = ReductionIndex(attribute.AttributeClass);
            if (index < 0) continue;
            if (all != null)
            {
                Problems.Add(new Problem(Type, Rule.BadField, $"carries two reduction attributes ([{all}] and [{(Op)index}]): one for the fields without their own"));
                break;
            }

            all = (Op)index;
        }

        bool any = all != null;
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

            if (op == null && member.Data) op = all;
            if (op is not { } found || twice) continue;
            if (!member.Data) Problems.Add(new Problem(member.Where, Rule.BadField, $"is a computed property, but [{found}] combines a field (or an auto-property)"));
            else if (Unsupported(found, member.Type) is { } why) Problems.Add(new Problem(member.Where, Rule.BadField, why));
            else if (!member.Settable) Problems.Add(new Problem(member.Where, Rule.BadField, "cannot be set in an object initializer (readonly, or a property without a setter): Combine builds a new value"));
            else Fields.Add(new Field(Code.Escape(member.Name), member.Type.ToDisplayString(Symbols.Fqn), found));
        }

        if (!any) return;
        // Every instance field takes part: an unattributed one would be dropped by every Combine.
        foreach (var member in members)
            if (all == null && member.Data && !member.Attributes.Any(a => ReductionIndex(a.AttributeClass) >= 0))
                Problems.Add(new Problem(member.Where, Rule.BadField, "has no reduction attribute, and the generated Combine would drop it: give it one ([Sum], [Product], [Min], [Max], [All], [Any]), mark the field that picks a whole row [MinBy] or [MaxBy], or write IReduction<T> by hand"));
        HandWrittenReduction();
    }

    /// <summary>EECS063: an Identity or Combine written by hand beside the attributes that declare them.</summary>
    private void HandWrittenReduction()
    {
        foreach (var name in new[] { "Identity", "Combine" })
            foreach (var own in Type.GetMembers(name))
                if (!own.IsImplicitlyDeclared && !IsGeneratedPart(own))
                    Problems.Add(new Problem(own, Rule.HandWritten, name));
    }

    /// <summary>
    /// <c>[MinBy]</c> / <c>[MaxBy]</c> (<paramref name="kept"/>, the members carrying one): one numeric key, a field or an
    /// auto-property an initializer can set (Identity sets it), and no other reduction attribute on the struct or its
    /// fields, whose values travel with the row the key keeps (EECS064).
    /// </summary>
    private void ReadKept(List<Member> members, List<Member> kept)
    {
        var key = kept[0];
        bool lowest = key.Attributes.Any(a => Symbols.Named(a.AttributeClass, MinBy));
        string Of(Member m) => $"[{(m.Attributes.Any(a => Symbols.Named(a.AttributeClass, MinBy)) ? "MinBy" : "MaxBy")}] on '{m.Where.Name}'";
        string own = lowest ? "MinBy" : "MaxBy";
        foreach (var attribute in Type.GetAttributes())
            if (ReductionIndex(attribute.AttributeClass) is var index and >= 0)
                Problems.Add(new Problem(Type, Rule.BadField, $"carries [{(Op)index}] for its fields, but the fields of a [{own}] reduction travel with the row its key keeps: remove [{(Op)index}] (a total and a row are two singletons)"));
        foreach (var member in members)
            foreach (var attribute in member.Attributes)
                if (ReductionIndex(attribute.AttributeClass) is var index and >= 0)
                    Problems.Add(new Problem(member.Where, Rule.BadField, $"carries [{(Op)index}], but the fields of a [{own}] reduction travel with the row its key keeps: remove [{(Op)index}] (a total and a row are two singletons)"));
        if (kept.Count > 1 || key.Attributes.Count(a => Symbols.Named(a.AttributeClass, MinBy) || Symbols.Named(a.AttributeClass, MaxBy)) > 1)
        {
            var second = kept.Count > 1 ? kept[1] : key;
            Problems.Add(new Problem(second.Where, Rule.BadField, kept.Count > 1
                ? $"is a second key ({Of(key)} and {Of(second)}), but a reduction keeps its row by one key"
                : "carries both [MinBy] and [MaxBy], but a reduction keeps its row by one key"));
            return;
        }

        if (!key.Data) Problems.Add(new Problem(key.Where, Rule.BadField, $"is a computed property, but [{own}] keeps the row by a field (or an auto-property)"));
        else if (!Numeric(key.Type)) Problems.Add(new Problem(key.Where, Rule.BadField, $"is {key.Type.ToDisplayString()}, but [{own}] compares numbers (an integer type, float, double or decimal)"));
        else if (!key.Settable) Problems.Add(new Problem(key.Where, Rule.BadField, "cannot be set in an object initializer (readonly, or a property without a setter): Identity starts it at the value no row passes"));
        else KeptOf = new KeptPart(Code.Escape(key.Name), key.Type.ToDisplayString(Symbols.Fqn), lowest);
        HandWrittenReduction();
    }

    /// <summary>
    /// <c>[LookupKey]</c> / <c>[UniqueKey]</c> on a member or the type: one key, of a type that is <c>IEquatable</c> of itself,
    /// agreeing with the key of an interface the struct declares, no <c>Key</c> written by hand, and no unique
    /// <c>EntityId</c> key (EECS036).
    /// </summary>
    private void ReadKey(List<Member> members)
    {
        var keys = new List<(ISymbol Where, string Member, ITypeSymbol Type, bool Unique, bool ReadOnly)>();
        AttributeData? first = null;
        foreach (var a in Type.GetAttributes())
            if (Symbols.Named(a.AttributeClass, Key) || Symbols.Named(a.AttributeClass, UniqueKey))
            {
                first ??= a;
                keys.Add((Type, "this", Type, Symbols.Named(a.AttributeClass, UniqueKey), true));
            }
        foreach (var member in members)
            foreach (var a in member.Attributes)
                if (Symbols.Named(a.AttributeClass, Key) || Symbols.Named(a.AttributeClass, UniqueKey))
                {
                    first ??= a;
                    keys.Add((member.Where, Code.Escape(member.Name), member.Type, Symbols.Named(a.AttributeClass, UniqueKey), member.ReadOnly));
                }
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
        if (first?.NamedArguments.FirstOrDefault(n => n.Key == "SlotsPerAxis").Value.Value is int side and not 0) ReadWindow(key.Where, key.Type, side);
    }

    /// <summary>
    /// <c>[UniqueKey(SlotsPerAxis = N)]</c>: a window of N keys per axis over the key's coordinates
    /// (<see cref="TileParts.SlotOf"/>), with no <c>Slots</c> or <c>SlotOf</c> written by hand (EECS126).
    /// </summary>
    private void ReadWindow(ISymbol where, ITypeSymbol key, int side)
    {
        foreach (var name in new[] { "Slots", "SlotOf" })
            if (TileParts.HandWritten(Type, name, Symbols.DirectMapped) is { } own)
            {
                Problems.Add(new Problem(own, Rule.BadWindow, $"writes {name} itself and declares its window with SlotsPerAxis: remove one"));
                return;
            }

        if (TileParts.SlotOf(key, side, out int slots, out string? why) is { } body) WindowOf = new WindowPart(KeyOf!.Type, slots, body);
        else Problems.Add(new Problem(where, Rule.BadWindow, why!));
    }

    /// <summary>
    /// A tile (<c>[Tile]</c>, or <c>ITile&lt;TKey&gt;</c> declared by the type): the key (ITile's, else the declared
    /// key's), the shape members <c>[Tile]</c> sets with none of them written by hand, and <c>Beside</c> inferred from the
    /// key when the type writes none (<see cref="TileParts.Beside"/>, EECS124); a <c>[Tile]</c> on a type with no key or
    /// that is no component is EECS068's.
    /// </summary>
    private void ReadTile()
    {
        AttributeData? tile = null;
        foreach (var a in Type.GetAttributes())
            if (Symbols.Named(a.AttributeClass, Tile)) tile = a;
        var declared = Type.AllInterfaces.FirstOrDefault(i => Symbols.Named(i.OriginalDefinition, TileParts.Interface));
        if (tile == null && declared == null) return;
        var key = declared?.TypeArguments[0] ?? (KeyOf != null ? KeyType : null) ?? Symbols.InterfaceKey(Type, out _);
        if (tile != null && !Symbols.Implements(Type, Symbols.Component))
        {
            Problems.Add(new Problem(Type, Rule.TileSetup, "is not a component: [Tile] makes a component the tile of a grid"));
            return;
        }

        if (key == null)
        {
            if (tile != null)
                Problems.Add(new Problem(Type, Rule.TileSetup, "has no key: [Tile] needs the member that places the tile in its grid marked [LookupKey] or [UniqueKey]"));
            return;
        }

        string keyText = SymbolEqualityComparer.Default.Equals(key, Type) ? SelfName(Type) : key.ToDisplayString(Symbols.Fqn);
        var shape = new System.Text.StringBuilder();
        if (tile != null)
        {
            var given = new HashSet<string>();
            foreach (var (name, type) in TileParts.Shape)
            {
                var argument = tile.NamedArguments.FirstOrDefault(n => n.Key == name);
                if (argument.Key == null || argument.Value.Value is not { } boxed) continue;
                int value = System.Convert.ToInt32(boxed, System.Globalization.CultureInfo.InvariantCulture);
                given.Add(name);
                if (TileParts.HandWritten(Type, name, Symbols.TileShape) is { } own)
                {
                    Problems.Add(new Problem(own, Rule.BadTile, $"writes {name} itself and declares it with [Tile]: remove one"));
                    continue;
                }

                string text = type switch
                {
                    "int" => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "TileFaces" => TileParts.FacesText(value, TileParts.FacesAlias),
                    _ => TileParts.EnumText(type, value),
                };
                string declaredType = type switch { "int" => "int", "TileFaces" => TileParts.FacesAlias, _ => "global::EasyEcs." + type };
                shape.Append("static ").Append(declaredType).Append(" global::EasyEcs.ITileShape.").Append(name).Append(" => ").Append(text).Append(";\n");
            }

            foreach (var required in new[] { "Width", "Depth" })
                if (!given.Contains(required))
                    Problems.Add(new Problem(Type, Rule.BadTile, $"gives [Tile] no {required}: a tile is Width cells by Depth rows ([Tile(Width = 16, Depth = 16)])"));
        }

        string beside = "";
        if (TileParts.HandWritten(Type, "Beside", TileParts.Interface) == null)
        {
            if (TileParts.Beside(key, TileParts.FacesAlias, out string? why) is { } body) beside = body;
            else Problems.Add(new Problem(Type, Rule.BadTile, $"writes no Beside, and the generator cannot infer it: {why}"));
        }

        if (tile != null || beside.Length > 0) TileOf = new TilePart(keyText, tile != null, shape.ToString(), beside);
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

    /// <summary>
    /// <c>[OrderBy]</c> on one member of a component, whose type a radix sort orders (an integer, <c>char</c>,
    /// <c>bool</c>, <c>float</c>, <c>double</c> or an enum); the box members it names (up to three <c>float</c> members,
    /// or one <c>Vector2</c> / <c>Vector3</c>), an interval of 1 or more, a finite margin of 0 or more, and no
    /// <c>OrderKey</c> or <c>BoxPoint</c> written by hand (EECS104).
    /// </summary>
    private void ReadOrder(List<Member> members)
    {
        var orders = new List<(Member Member, AttributeData Attribute)>();
        foreach (var member in members)
            foreach (var a in member.Attributes)
                if (Symbols.Named(a.AttributeClass, OrderBy)) orders.Add((member, a));
        if (orders.Count == 0) return;
        var (key, attribute) = orders[0];
        string Name(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        void Bad(ISymbol where, string text) => Problems.Add(new Problem(where, Rule.BadOrder, text));

        int interval = 10;
        float margin = 0;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Interval" && named.Value.Value is int i) interval = i;
            if (named.Key == "Margin" && named.Value.Value is float f) margin = f;
        }

        // The box members: the names the attribute gives, each an instance float, or one Vector2 / Vector3.
        var names = attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
            ? attribute.ConstructorArguments[0].Values.Select(v => v.Value as string ?? "").ToList()
            : new List<string>();
        var axes = new List<string>();
        int count = 0;
        bool readOnly = key.ReadOnly;
        string? boxProblem = null;
        foreach (var name in names)
        {
            var axis = members.FirstOrDefault(m => m.Name == name);
            if (axis == null)
            {
                boxProblem ??= $"names '{name}' for its box, which is no field or property of '{Type.Name}'";
                continue;
            }

            readOnly &= axis.ReadOnly;
            int width = axis.Type.SpecialType == SpecialType.System_Single ? 1
                : Symbols.Named(axis.Type, "System.Numerics.Vector2") ? 2
                : Symbols.Named(axis.Type, "System.Numerics.Vector3") ? 3 : 0;
            if (width == 0) boxProblem ??= $"names '{name}', a {Name(axis.Type)}, for its box: a box covers float members, or one Vector2 or Vector3";
            else if (width > 1 && names.Count > 1) boxProblem ??= $"names '{name}', a {Name(axis.Type)}, beside other members: a Vector2 or Vector3 is the box's only member";
            count += width;
            axes.Add(width == 2 ? $"new global::System.Numerics.Vector3({Code.Escape(name)}, 0f)" : Code.Escape(name));
        }

        string point = count == 0 || boxProblem != null || count > 3 ? "default"
            : names.Count == 1 && count > 1 ? axes[0]
            : $"new global::System.Numerics.Vector3({string.Join(", ", axes.Concat(Enumerable.Repeat("0f", 3 - count)))})";
        OrderOf = new OrderPart(Code.Escape(key.Name), key.Type.ToDisplayString(Symbols.Fqn), point, count, interval, margin, readOnly);

        string Of(Member m) => $"'{m.Where.Name}'";
        if (orders.Count > 1) Bad(orders[1].Member.Where, $"has two order keys ([OrderBy] on {Of(key)} and {Of(orders[1].Member)}), but a table keeps one order");
        else if (!Symbols.Implements(Type, Symbols.Component) || Symbols.Implements(Type, Symbols.Singleton))
            Bad(key.Where, "declares [OrderBy], but only a component's tables have rows to order");
        else if (!Orderable(key.Type))
            Bad(key.Where, $"is ordered by {Name(key.Type)} ([OrderBy] on {Of(key)}), which a radix sort cannot order: a key is an integer, char, bool, float, double or an enum");
        else if (boxProblem != null) Bad(key.Where, boxProblem);
        else if (count > 3) Bad(key.Where, $"names {count} axes for its box, but a box covers up to 3");
        else if (interval < 1) Bad(key.Where, $"is sorted every {interval} updates: the interval is 1 or more");
        else if (!(margin >= 0) || float.IsInfinity(margin)) Bad(key.Where, $"widens its boxes by {margin}: the margin is a finite distance, 0 or more");
        else if (Type.GetMembers().FirstOrDefault(m => m is IPropertySymbol p && (p.Name is "OrderKey" or "BoxPoint"
                     || p.ExplicitInterfaceImplementations.Any(i => i.Name is "OrderKey" or "BoxPoint")) && !IsGeneratedPart(m)) is { } own)
            Bad(own, $"writes {own.Name.Substring(own.Name.LastIndexOf('.') + 1)} itself and declares it with [OrderBy]: remove one");
    }

    /// <summary>
    /// Null: a <c>[ForEach]</c> method reducing into a <c>[MinBy]</c> / <c>[MaxBy]</c> singleton gets its vector loop, or
    /// its reason, from <c>Kernel.Vectorize</c> (Kernel.KeptReduction, KeptRowWrite) like any other method. EECS055 still
    /// calls it before Kernel (EasyEcsAnalyzer.Performance.cs, the analyzer lane's file: docs/plans/backlog.md G9 note),
    /// so it stays until that call goes.
    /// </summary>
    public static Kernel.ScalarReason? KeptRowReason(IMethodSymbol method) => null;

    /// <summary>Whether a radix sort orders <paramref name="type"/>: an integer, char, bool, float, double or an enum of an integer.</summary>
    public static bool Orderable(ITypeSymbol type) =>
        (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying } ? underlying : type).SpecialType
            is SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double;

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
