using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// What a tile's key and a direct-mapped key's window are made of, read once for the generator (PartGenerator, through
/// <see cref="Parts"/>) and the analyzer: the axes of a key (an <c>int</c>, or integer members <c>X</c>, <c>Z</c> and
/// <c>Y</c>, or a tuple's items), <see cref="Beside"/> (a tile's neighbor key, one step along the face's axis as
/// <c>TileFaces</c> names them) and <see cref="SlotOf"/> (a window's slot, each coordinate masked, row-major). Each returns
/// the C# the part holds, or null with the reason (EECS124, EECS126), so what the generator skips is what the analyzer
/// reports.
/// </summary>
internal static class TileParts
{
    public const string Attribute = "EasyEcs.TileAttribute", Interface = "EasyEcs.ITile<TKey>";

    /// <summary>The alias of <c>TileFaces</c> in a part that names it (<c>using __TileFaces = global::EasyEcs.TileFaces;</c>).</summary>
    public const string FacesAlias = "__TileFaces";

    /// <summary>The faces of <c>TileFaces</c> in the order Beside lists them, with the axis each steps and the step.</summary>
    public static readonly (string Face, char Axis, int Step)[] Faces =
        [("North", 'Z', -1), ("South", 'Z', 1), ("West", 'X', -1), ("East", 'X', 1), ("Below", 'Y', -1), ("Above", 'Y', 1)];

    /// <summary>The shape members [Tile] sets, as ITileShape names them, and the type of each.</summary>
    public static readonly (string Name, string Type)[] Shape =
        [("Width", "int"), ("Depth", "int"), ("Height", "int"), ("Faces", "TileFaces"), ("Halo", "int"), ("Neighborhood", "TileNeighborhood"), ("Boundary", "TileBoundary")];

    /// <summary>
    /// A coordinate of a key: the member read (<c>""</c> for an <c>int</c> key itself), its type, and whether
    /// <c>with</c> can set it.
    /// </summary>
    public readonly record struct Axis(string Member, ITypeSymbol Type, bool Settable);

    /// <summary>
    /// The integer members of <paramref name="key"/> named <paramref name="name"/> (an instance field, or a property with a
    /// getter), or null.
    /// </summary>
    private static Axis? Member(ITypeSymbol key, string name)
    {
        foreach (var member in key.GetMembers(name))
            switch (member)
            {
                case IFieldSymbol { IsStatic: false, IsConst: false } field when Integer(field.Type):
                    return new Axis(name, field.Type, !field.IsReadOnly);
                case IPropertySymbol { IsStatic: false, IsIndexer: false, GetMethod: not null } property when Integer(property.Type):
                    return new Axis(name, property.Type, property.SetMethod != null);
            }

        return null;
    }

    /// <summary>An integer type a coordinate may have.</summary>
    public static bool Integer(ITypeSymbol type) => type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64;

    /// <summary>A type whose arithmetic is done in a wider one: a coordinate stepped or masked is cast back.</summary>
    private static bool Narrow(ITypeSymbol type) => type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
        or SpecialType.System_Int16 or SpecialType.System_UInt16;

    /// <summary>
    /// The neighbor key of a tile keyed by <paramref name="key"/>, as the body of <c>Beside(key, face)</c> (a switch
    /// expression over <c>face</c>, one arm per face the key can step, the others throwing), or null with
    /// <paramref name="why"/>: an <c>int</c> key steps west −1 and east +1; a key with integer members <c>X</c> and
    /// <c>Z</c> (and <c>Y</c>) that <c>with</c> can set steps the member of the face's axis. <paramref name="faces"/>
    /// names the alias of <c>TileFaces</c> the part declares.
    /// </summary>
    public static string? Beside(ITypeSymbol key, string faces, out string? why)
    {
        why = null;
        var axes = BesideAxes(key, out why);
        if (axes == null) return null;
        var arms = new List<string>();
        foreach (var (face, axis, step) in Faces)
        {
            if (!axes.TryGetValue(axis, out var a)) continue;
            string next = Step(a, step);
            arms.Add($"    {faces}.{face} => {(a.Member.Length == 0 ? next : $"key with {{ {Code.Escape(a.Member)} = {next} }}")},");
        }

        arms.Add("    _ => throw new global::System.ArgumentOutOfRangeException(nameof(face), face, \"A face the key has no axis for\"),");
        return "face switch\n{\n" + string.Join("\n", arms) + "\n}";
    }

    /// <summary>The axes Beside steps (x, z, y by member; an <c>int</c> key: x as the key itself), or null with the reason.</summary>
    public static Dictionary<char, Axis>? BesideAxes(ITypeSymbol key, out string? why)
    {
        why = null;
        string name = key.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        if (key.SpecialType == SpecialType.System_Int32) return new Dictionary<char, Axis> { ['X'] = new Axis("", key, true) };
        if (key.TypeKind != TypeKind.Struct || key.IsRefLikeType || key.SpecialType != SpecialType.None)
        {
            why = $"its key {name} is neither an int (a row of tiles) nor a struct with integer members X and Z";
            return null;
        }

        Axis? x = Member(key, "X"), y = Member(key, "Y"), z = Member(key, "Z");
        if (x != null && y != null && z == null)
        {
            why = $"its key {name} names its axes X and Y, but TileFaces makes z the depth (north is -z, south +z) and y the "
                  + "layers, so reading Y as the depth could step the wrong axis without an error: name the depth Z, or write Beside";
            return null;
        }

        if (x == null || z == null)
        {
            why = $"its key {name} has no integer members X and Z (and Y for layers): name them so, or write Beside";
            return null;
        }

        var axes = new Dictionary<char, Axis> { ['X'] = x.Value, ['Z'] = z.Value };
        if (y != null) axes['Y'] = y.Value;
        foreach (var axis in axes.Values)
            if (!axis.Settable && !key.IsTupleType)
            {
                why = $"its key {name} has a member {axis.Member} that 'with' cannot set (readonly, or no setter): give it a setter or init, or write Beside";
                return null;
            }

        return axes;
    }

    /// <summary>The coordinate <paramref name="axis"/> of <c>key</c> moved by <paramref name="step"/> (1 or −1), cast back when narrow.</summary>
    private static string Step(Axis axis, int step)
    {
        string read = axis.Member.Length == 0 ? "key" : "key." + Code.Escape(axis.Member);
        string moved = $"{read} {(step < 0 ? '-' : '+')} 1";
        return Narrow(axis.Type) ? $"({axis.Type.ToDisplayString()})({moved})" : moved;
    }

    /// <summary>
    /// The axes of a window over <paramref name="key"/>, fastest first: an <c>int</c> key itself; the integer members
    /// <c>X</c>, <c>Z</c> and <c>Y</c> it has (x, then z, then y: a tile's cell order); a tuple's <c>Item1..Item3</c>; or
    /// null with the reason.
    /// </summary>
    public static List<Axis>? WindowAxes(ITypeSymbol key, out string? why)
    {
        why = null;
        string name = key.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        if (Integer(key)) return [new Axis("", key, true)];
        if (key.TypeKind != TypeKind.Struct || key.IsRefLikeType || key.SpecialType != SpecialType.None)
        {
            why = $"its key {name} is neither an integer nor a struct of integer coordinates";
            return null;
        }

        var axes = new List<Axis>();
        foreach (var member in new[] { "X", "Z", "Y" })
            if (Member(key, member) is { } axis) axes.Add(axis);
        if (axes.Count == 0 && key.IsTupleType && key is INamedTypeSymbol tuple)
            foreach (var element in tuple.TupleElements)
            {
                if (!Integer(element.Type) || axes.Count == 3)
                {
                    why = $"its key {name} is a tuple of {tuple.TupleElements.Length} items, not 1 to 3 integers";
                    return null;
                }

                axes.Add(new Axis(element.CorrespondingTupleField?.Name ?? element.Name, element.Type, true));
            }

        if (axes.Count == 0) why = $"its key {name} has no integer members X, Z or Y: name its coordinates so";
        return axes.Count == 0 ? null : axes;
    }

    /// <summary>
    /// A window of <paramref name="side"/> keys per axis over <paramref name="key"/>: the slot count (side to the power
    /// of the axes) and the body of <c>SlotOf(key)</c> (each coordinate masked by side − 1, row-major), or null with
    /// <paramref name="why"/>: a side that is not a power of two of 2 or more, more slots than an array holds, or a key
    /// with no axes.
    /// </summary>
    public static string? SlotOf(ITypeSymbol key, int side, out int slots, out string? why)
    {
        slots = 0;
        if (side < 2 || (side & (side - 1)) != 0)
        {
            why = $"has a window of {side} keys per axis, but a window is a power of two of 2 or more, so that a mask keeps each coordinate's slot (negative ones too)";
            return null;
        }

        if (WindowAxes(key, out why) is not { } axes) return null;
        long count = 1;
        foreach (var _ in axes) count *= side;
        if (count > 1 << 30)
        {
            why = $"has a window of {side} keys per axis over {axes.Count} axes, {count} slots: an index holds at most {1 << 30}";
            return null;
        }

        slots = (int)count;
        string Masked(Axis axis)
        {
            string read = axis.Member.Length == 0 ? "key" : "key." + Code.Escape(axis.Member);
            return axis.Type.SpecialType == SpecialType.System_Int32 || Narrow(axis.Type) ? $"({read} & {side - 1})" : $"(int)({read} & {side - 1})";
        }

        // Row-major, the fastest axis last: ((y * n + z) * n + x).
        string body = Masked(axes[axes.Count - 1]);
        for (int i = axes.Count - 2; i >= 0; i--)
            body = $"{(i == axes.Count - 2 ? body : $"({body})")} * {side} + {Masked(axes[i])}";
        if (axes.Count == 1) body = body.Substring(1, body.Length - 2); // 'key & 63', no parentheses around the whole
        return body;
    }

    /// <summary>
    /// The C# of a <c>TileFaces</c> value as the alias <paramref name="faces"/> names it: a name of the enum when one
    /// has the value (Sides, All), else its faces joined by <c>|</c>.
    /// </summary>
    public static string FacesText(int value, string faces)
    {
        string? whole = value switch { 0 => "None", 15 => "Sides", 63 => "All", _ => null };
        if (whole != null) return $"{faces}.{whole}";
        var parts = new List<string>();
        for (int i = 0; i < Faces.Length; i++)
            if ((value & (1 << i)) != 0) parts.Add($"{faces}.{Faces[i].Face}");
        return parts.Count == 0 ? $"({faces}){value}" : string.Join(" | ", parts);
    }

    /// <summary>The C# of a value of <c>TileNeighborhood</c> or <c>TileBoundary</c> (<paramref name="type"/>).</summary>
    public static string EnumText(string type, int value)
    {
        string[] names = type == "TileNeighborhood" ? ["Faces", "FacesAndEdges", "All"] : ["Border", "Clamp", "Mirror"];
        return value >= 0 && value < names.Length ? $"global::EasyEcs.{type}.{names[value]}" : $"(global::EasyEcs.{type}){value}";
    }

    // ---------------- written by hand (EECS125, EECS127) ----------------

    /// <summary>
    /// The value of shape member <paramref name="name"/> of tile <paramref name="type"/>: <c>[Tile]</c>'s argument, the
    /// constant a hand-written member returns, or <paramref name="fallback"/> (ITileShape's default); null when a member
    /// computes it.
    /// </summary>
    public static int? ShapeValue(INamedTypeSymbol type, string name, int fallback, Compilation compilation, CancellationToken ct)
    {
        foreach (var attribute in type.GetAttributes())
            if (Symbols.Named(attribute.AttributeClass, Attribute))
                foreach (var argument in attribute.NamedArguments)
                    if (argument.Key == name && argument.Value.Value is { } given)
                        return System.Convert.ToInt32(given, System.Globalization.CultureInfo.InvariantCulture);
        if (HandWritten(type, name, Symbols.TileShape) is not IPropertySymbol own) return fallback;
        return Constant(own, compilation, ct) is { } value ? System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The constant a static property returns (its expression body or one return), with the expression; else null.</summary>
    public static object? Constant(IPropertySymbol property, Compilation compilation, CancellationToken ct) =>
        Returned(property, ct) is { } body && compilation.GetSemanticModel(body.SyntaxTree).GetConstantValue(body, ct) is { HasValue: true, Value: { } value } ? value : null;

    /// <summary>The expression a property returns as written (an expression body or one return), or null.</summary>
    public static ExpressionSyntax? Returned(IPropertySymbol property, CancellationToken ct) =>
        property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is PropertyDeclarationSyntax declaration ? Symbols.Returned(declaration) : null;

    /// <summary>
    /// Whether hand-written <paramref name="beside"/> steps every face of <paramref name="faces"/> (a <c>TileFaces</c>
    /// mask) as the inferred one does over <paramref name="axes"/>: its body a switch expression over the face (constant
    /// arms and a discard) or <c>face == F ? a : b</c>, each arm the key moved one along the face's axis (<c>key with {
    /// Z = key.Z - 1 }</c>, <c>new K(key.X, key.Z - 1)</c> through a constructor setting every member by name, <c>key -
    /// 1</c> for an <c>int</c> key). Any other form is left alone (it may wrap: a periodic grid).
    /// </summary>
    public static bool SameBeside(IMethodSymbol beside, Dictionary<char, Axis> axes, int faces, Compilation compilation, CancellationToken ct)
    {
        if (beside.Parameters.Length != 2
            || beside.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not MethodDeclarationSyntax syntax) return false;
        var body = syntax.ExpressionBody?.Expression
                   ?? (syntax.Body?.Statements is { Count: 1 } statements && statements[0] is ReturnStatementSyntax { Expression: { } returned } ? returned : null);
        if (body == null || compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(body, ct) is not { } operation) return false;
        IParameterSymbol key = beside.Parameters[0], face = beside.Parameters[1];
        bool IsFace(IOperation? o) => Symbols.Unwrap(o) is IParameterReferenceOperation r && SymbolEqualityComparer.Default.Equals(r.Parameter, face);

        // Each face's arm (by its value) and the arm of every other face.
        var arms = new Dictionary<int, IOperation>();
        IOperation? rest = null;
        switch (Symbols.Unwrap(operation))
        {
            case ISwitchExpressionOperation choice when IsFace(choice.Value):
                foreach (var arm in choice.Arms)
                {
                    if (arm.Guard != null) return false;
                    if (arm.Pattern is IDiscardPatternOperation) rest = arm.Value;
                    else if (arm.Pattern is IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: { } constant } })
                        arms[System.Convert.ToInt32(constant, System.Globalization.CultureInfo.InvariantCulture)] = arm.Value;
                    else return false;
                }

                break;
            case IConditionalOperation { Condition: IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } test, WhenFalse: { } otherwise } branch:
                var value = IsFace(test.LeftOperand) ? test.RightOperand : IsFace(test.RightOperand) ? test.LeftOperand : null;
                if (value?.ConstantValue is not { HasValue: true, Value: { } named }) return false;
                bool equals = test.OperatorKind == BinaryOperatorKind.Equals;
                arms[System.Convert.ToInt32(named, System.Globalization.CultureInfo.InvariantCulture)] = equals ? branch.WhenTrue : otherwise;
                rest = equals ? otherwise : branch.WhenTrue;
                break;
            default:
                return false;
        }

        for (int i = 0; i < Faces.Length; i++)
        {
            if ((faces & (1 << i)) == 0) continue;
            var (_, axis, step) = Faces[i];
            if (!axes.ContainsKey(axis)) return false;
            var arm = arms.TryGetValue(1 << i, out var own) ? own : rest;
            if (arm == null || Moved(arm, key, axes) is not { Count: 1 } moved || !moved.TryGetValue(axis, out int by) || by != step) return false;
        }

        return true;
    }

    /// <summary>
    /// How <paramref name="value"/> moves <paramref name="key"/> along each axis (+1 or −1), or null when it is not the key
    /// with some coordinates moved by one.
    /// </summary>
    private static Dictionary<char, int>? Moved(IOperation value, IParameterSymbol key, Dictionary<char, Axis> axes)
    {
        var moved = new Dictionary<char, int>();
        bool IsKey(IOperation? o) => Symbols.Unwrap(o) is IParameterReferenceOperation r && SymbolEqualityComparer.Default.Equals(r.Parameter, key);
        char? AxisOf(string member)
        {
            foreach (var pair in axes)
                if (pair.Value.Member == member) return pair.Key;
            return null;
        }

        // The step of 'read' or 'read ± 1' where read is the coordinate 'member' (null: the key itself); null otherwise.
        int? Step(IOperation? operation, string? member)
        {
            bool Reads(IOperation? o) => member == null ? IsKey(o)
                : Symbols.Unwrap(o) is IMemberReferenceOperation { Member: var m, Instance: var instance } && m.Name == member && IsKey(instance);
            operation = Symbols.Unwrap(operation);
            if (Reads(operation)) return 0;
            if (operation is not IBinaryOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract } binary) return null;
            bool one(IOperation o) => Symbols.Unwrap(o)?.ConstantValue is { HasValue: true, Value: { } c } && System.Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (Reads(binary.LeftOperand) && one(binary.RightOperand)) return binary.OperatorKind == BinaryOperatorKind.Add ? 1 : -1;
            if (binary.OperatorKind == BinaryOperatorKind.Add && one(binary.LeftOperand) && Reads(binary.RightOperand)) return 1;
            return null;
        }

        switch (Symbols.Unwrap(value))
        {
            case var _ when axes.TryGetValue('X', out var x) && x.Member.Length == 0: // an int key
                if (Step(value, null) is not { } by) return null;
                if (by != 0) moved['X'] = by;
                return moved;
            case IWithOperation { Operand: var operand, Initializer.Initializers: var sets } when IsKey(operand):
                foreach (var set in sets)
                {
                    if (set is not ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Member.Name: var name } } assignment
                        || AxisOf(name) is not { } axis || Step(assignment.Value, name) is not { } step || moved.ContainsKey(axis)) return null;
                    if (step != 0) moved[axis] = step;
                }

                return moved;
            case IObjectCreationOperation { Constructor: { } constructor, Initializer: null } creation
                when SymbolEqualityComparer.Default.Equals(creation.Type, key.Type)
                     && constructor.Parameters.Length == key.Type.GetMembers().OfType<IFieldSymbol>().Count(f => !f.IsStatic && !f.IsConst):
                foreach (var argument in creation.Arguments)
                {
                    // Each parameter sets the member of its name (a record's positional parameter, or the same name in
                    // another case): that member read back, moved by one along an axis or not at all.
                    var member = key.Type.GetMembers().FirstOrDefault(m => !m.IsStatic && m is IFieldSymbol or IPropertySymbol
                        && string.Equals(m.Name, argument.Parameter?.Name, System.StringComparison.OrdinalIgnoreCase));
                    if (member == null || Step(argument.Value, member.Name) is not { } shift) return null;
                    if (shift == 0) continue;
                    if (AxisOf(member.Name) is not { } axis) return null;
                    moved[axis] = shift;
                }

                return moved;
            default:
                return null;
        }
    }

    /// <summary>
    /// The window a hand-written <c>Slots</c> / <c>SlotOf</c> pair of <paramref name="type"/> keyed by
    /// <paramref name="key"/> maps, as <c>[UniqueKey(SlotsPerAxis = N)]</c> would write it: N, or null when Slots is not
    /// a constant N to the power of the key's axes or SlotOf is not the text the generator writes for N (whitespace and
    /// the parameter's name aside).
    /// </summary>
    public static int? HandWindow(INamedTypeSymbol type, ITypeSymbol key, Compilation compilation, CancellationToken ct)
    {
        if (HandWritten(type, "Slots", Symbols.DirectMapped) is not IPropertySymbol slots
            || HandWritten(type, "SlotOf", Symbols.DirectMapped) is not IMethodSymbol { Parameters.Length: 1 } slotOf
            || Constant(slots, compilation, ct) is not int count || count < 2
            || WindowAxes(key, out _) is not { } axes
            || slotOf.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not MethodDeclarationSyntax { ExpressionBody.Expression: { } body })
            return null;
        int side = (int)System.Math.Round(System.Math.Pow(count, 1.0 / axes.Count));
        if (side < 2 || SlotOf(key, side, out int expected, out _) is not { } text || expected != count) return null;
        string parameter = slotOf.Parameters[0].Name;
        string Squeeze(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        // The generated text reads 'key': renamed to the hand-written parameter, token by token (key.X, (key &...).
        string renamed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(text) is var parsed
            ? parsed.ReplaceNodes(parsed.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == "key"),
                (_, n) => n.WithIdentifier(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Identifier(parameter))).ToString()
            : text;
        return Squeeze(renamed) == Squeeze(body.ToString()) ? side : null;
    }

    /// <summary>
    /// Whether <paramref name="type"/> writes the static member <paramref name="name"/> of <paramref name="face"/> itself
    /// (explicitly, or as a public static member of that name), not a generated part.
    /// </summary>
    public static ISymbol? HandWritten(INamedTypeSymbol type, string name, string face)
    {
        foreach (var member in type.GetMembers())
        {
            if (!member.IsStatic || Parts.IsGeneratedPart(member)) continue;
            bool named = member switch
            {
                IPropertySymbol p => p.Name == name || p.ExplicitInterfaceImplementations.Any(i => i.Name == name && Symbols.Named(i.ContainingType.OriginalDefinition, face)),
                IMethodSymbol m => m.MethodKind != MethodKind.PropertyGet && (m.Name == name || m.ExplicitInterfaceImplementations.Any(i => i.Name == name && Symbols.Named(i.ContainingType.OriginalDefinition, face))),
                _ => false,
            };
            if (named) return member;
        }

        return null;
    }
}
