using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of declarations the generator writes parts for (EECS062-EECS067, and EECS036 for keys and groups): a type
/// that is not partial along its chain, a reduction declared by attributes that also writes its members or has a field
/// the attributes cannot combine, a key or group the attributes cannot declare,
/// and the hand-written reductions the attributes could declare (EECS065) or whose Identity is not the identity of
/// their Combine (EECS066). <see cref="Parts"/> decides what the generator writes; these rules report what keeps it.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor NotPartial = new("EECS062", "A type with generated members is not partial",
        "'{0}' has members the generator writes ({1}), so it and every type containing it must be partial: {2}",
        Category, DiagnosticSeverity.Error, true,
        "An attribute such as [Sum], [IndexKey] or [Group] declares what the generator writes into the type itself: the " +
        "members of IReduction<T>, IIndexed<TKey> or IGroup, in a part of the type. A part can only be added to a partial " +
        "type, nested in partial types; a file type (file struct) cannot get one, since the part lives in another file.",
        helpLinkUri: Help("EECS062"));

    public static readonly DiagnosticDescriptor ReductionWrittenTwice = new("EECS063", "A reduction declared by attributes writes its own members",
        "'{0}' declares its reduction with field attributes, and also writes '{1}': the generator writes Identity and Combine from the attributes; remove '{1}', or the attributes",
        Category, DiagnosticSeverity.Error, true,
        "The reduction attributes ([Sum], [Product], [Min], [Max], [All], [Any]) declare IReduction<T> field by field, and " +
        "the generator writes its Identity and Combine. A hand-written member of the same name would collide with them; a " +
        "reduction that combines otherwise (a first and last value, a concatenation) writes IReduction<T> by hand, " +
        "without the attributes.", helpLinkUri: Help("EECS063"));

    public static readonly DiagnosticDescriptor BadReductionField = new("EECS064", "A field the reduction attributes cannot combine",
        "'{0}' of reduction '{1}' {2}",
        Category, DiagnosticSeverity.Error, true,
        "A reduction declared by attributes combines every instance field: [Sum], [Product], [Min] and [Max] combine " +
        "numbers (integers, float, double, decimal), [All] and [Any] combine bools, and Combine sets each field of a new " +
        "value, so each is a field or an auto-property with a setter. A field without an attribute would be dropped by " +
        "every Combine.", helpLinkUri: Help("EECS064"));

    public static readonly DiagnosticDescriptor HandWrittenReduction = new("EECS065", "A reduction the field attributes can declare",
        "'{0}' combines field by field ({1}): declare it with attributes, and the generator writes Identity and Combine",
        Category, DiagnosticSeverity.Warning, true,
        "A hand-written IReduction<T> whose Identity and Combine apply one of sum, product, minimum, maximum, and, or to " +
        "each field is what the reduction attributes declare: [Sum] public int Alive; [Min] public float Lowest; on a " +
        "partial struct. The generator then writes both members, with the right identity for each field.",
        helpLinkUri: Help("EECS065"));

    public static readonly DiagnosticDescriptor WrongIdentity = new("EECS066", "A reduction's Identity is not the identity of its Combine",
        "Identity of '{0}' is not the identity of its Combine: {1}",
        Category, DiagnosticSeverity.Warning, true,
        "Each chunk of a pass starts its partial result at Identity, and the chunks are combined when the pass ends. An " +
        "Identity that changes what it is combined with (0 for a minimum, a field left at 0 for a product or a maximum " +
        "of negative values) adds itself once per chunk, so the result depends on how many chunks the pass had: on the " +
        "thread count and the table sizes.", helpLinkUri: Help("EECS066"));

    public static readonly DiagnosticDescriptor HandWrittenKey = new("EECS067", "An index key or a group an attribute can declare",
        "'{0}.{1}' returns {2}: declare it with [{3}] on {4}, and the generator writes {1}",
        Category, DiagnosticSeverity.Warning, true,
        "A Key that returns a field, a property or the value itself is what [IndexKey] (and [UniqueKey] for a unique index) " +
        "declares on that member, or on the type for the value itself, of a partial struct; a Group that returns an int " +
        "member, or an enum of int cast to int, is what [Group] declares on that member. The generator writes the " +
        "interface and its explicit member, which no ToString() prints and no caller reaches by mistake. A computed key " +
        "or group stays hand-written.", helpLinkUri: Help("EECS067"));

    public static readonly DiagnosticDescriptor BadSetup = new("EECS068", "A world setup attribute on a type it does not fit",
        "[{0}] on '{1}': '{1}' {2}",
        Category, DiagnosticSeverity.Error, true,
        "[GroupBy] makes every world group its tables by a grouped component (IGroup, or a member declared with [Group]); " +
        "[WhenTargetDestroyed] sets what destroying an entity does to the links to it, so it goes on a relation " +
        "(IRelation) or a component indexed by an EntityId; [NotSaved] leaves a component or a singleton out of saves. " +
        "On any other type no world would apply it.",
        helpLinkUri: Help("EECS068"));

    public static readonly DiagnosticDescriptor NoFormatter = new("EECS069", "A managed component or singleton without a Nino formatter",
        "'{0}' holds references and has no Nino formatter, so a save that holds it throws: mark it [NinoType] (and partial) for Nino to write it, or [NotSaved] for saves to leave it out",
        Category, DiagnosticSeverity.Info, true,
        "A save writes unmanaged components as raw rows and every other component and singleton with Nino, which needs " +
        "the formatter [NinoType] generates. Runtime state that a restart rebuilds (a connection, a cache) is [NotSaved] " +
        "instead. A warning when the compilation saves what holds the type (a component: Save, SaveWithout, SaveEntities; " +
        "a singleton: Save, SaveWithout, since entity saves write no singletons), a suggestion otherwise.",
        helpLinkUri: Help("EECS069"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    // EECS065 / EECS066's properties for their code fixes: the attribute of each field (name=Op;...), and the Identity
    // that fits the Combine.
    public const string ReductionOps = "Ops", FixedIdentity = "Identity";

    /// <summary>What a declaration's attributes need that it lacks (EECS062-EECS064): <see cref="Parts.Problems"/>.</summary>
    private static void AnalyzeParts(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        if (Parts.Of(type) is not { } parts) return;
        var location = type.Locations.FirstOrDefault(l => l.IsInSource);
        foreach (var problem in parts.Problems)
        {
            var where = problem.Where.Locations.FirstOrDefault(l => l.IsInSource) ?? location;
            switch (problem.Rule)
            {
                case Parts.Rule.NotPartial:
                    var level = (INamedTypeSymbol)problem.Where;
                    context.ReportDiagnostic(Diagnostic.Create(NotPartial, location, type.Name, What(parts),
                        level.IsFileLocal ? $"'{level.Name}' is a file type, which no generated file can add to"
                        : SymbolEqualityComparer.Default.Equals(level, type) ? $"declare '{level.Name}' partial"
                        : $"declare '{level.Name}' partial, and every type between it and '{type.Name}'"));
                    break;
                case Parts.Rule.HandWritten:
                    context.ReportDiagnostic(Diagnostic.Create(ReductionWrittenTwice, where, type.Name, problem.Text));
                    break;
                case Parts.Rule.BadField:
                    context.ReportDiagnostic(Diagnostic.Create(BadReductionField, where, problem.Where.Name, type.Name, problem.Text));
                    break;
                case Parts.Rule.BadKey or Parts.Rule.BadGroup:
                    context.ReportDiagnostic(Diagnostic.Create(BadIndexKey, where, type.Name, problem.Text));
                    break;
            }
        }
    }

    // EECS067's property for its code fix: the member the attribute goes on ("this" for the type).
    public const string KeyMember = "Member";

    /// <summary>
    /// EECS067: a struct indexed by one key whose hand-written Key returns one of its own members or <c>this</c>, of
    /// exactly the key's type, or a grouped struct whose hand-written Group returns an <c>int</c> member or an enum of
    /// <c>int</c> cast to <c>int</c>: what <see cref="Parts"/> accepts for <c>[IndexKey]</c> / <c>[UniqueKey]</c> and
    /// <c>[Group]</c>. Not for a file type (no part can be added), nor the public self key of a record EECS043 reports.
    /// </summary>
    private static void AnalyzeHandWrittenKey(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        for (var level = type; level != null; level = level.ContainingType)
            if (level.IsFileLocal) return;
        if (!Parts.Declares(type, Parts.Key) && !Parts.Declares(type, Parts.UniqueKey)
            && Symbols.InterfaceKey(type, out bool unique) is { } key
            && type.AllInterfaces.Count(i => Symbols.Named(i.OriginalDefinition, Symbols.Indexed)) == 1
            && SelfPrintingKey(type) == null
            && HandWrittenPart(type, type.AllInterfaces.First(i => Symbols.Named(i.OriginalDefinition, Symbols.Indexed)), "Key", context.CancellationToken) is ({ } syntax, { } property, { } returned)
            && ReturnedMember(returned) is { } member)
        {
            if (member == "this" ? SymbolEqualityComparer.Default.Equals(key, type)
                : Parts.Members(type).FirstOrDefault(m => m.Name == member) is { } target
                  && SymbolEqualityComparer.Default.Equals(target.Type, key) && !SymbolEqualityComparer.Default.Equals(target.Where, property))
                Report(unique ? "UniqueKey" : "IndexKey", "Key", syntax, member);
        }

        if (!Parts.Declares(type, Parts.Group)
            && type.AllInterfaces.FirstOrDefault(i => Symbols.Named(i, Symbols.Group)) is { } group
            && HandWrittenPart(type, group, "Group", context.CancellationToken) is ({ } groupSyntax, { } groupProperty, { } value))
        {
            // [Group] on an int member returns it; on an enum of int, the member cast to int (Parts.ReadGroup).
            bool cast = value is CastExpressionSyntax { Type: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword } };
            if (ReturnedMember(value is CastExpressionSyntax c && cast ? c.Expression : value) is { } grouped && grouped != "this"
                && Parts.Members(type).FirstOrDefault(m => m.Name == grouped) is { } target && !SymbolEqualityComparer.Default.Equals(target.Where, groupProperty)
                && (cast ? target.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType.SpecialType: SpecialType.System_Int32 }
                    : target.Type.SpecialType == SpecialType.System_Int32))
                Report("Group", "Group", groupSyntax, grouped);
        }

        void Report(string attribute, string name, PropertyDeclarationSyntax at, string member) =>
            context.ReportDiagnostic(Diagnostic.Create(HandWrittenKey, at.Identifier.GetLocation(),
                ImmutableDictionary<string, string?>.Empty.Add(KeyMember, member), type.Name, name,
                member == "this" ? "the value itself" : $"'{member}'", attribute, member == "this" ? "the type" : $"'{member}'"));
    }

    /// <summary>
    /// The hand-written implementation of <paramref name="face"/>'s <paramref name="name"/> in <paramref name="type"/>
    /// itself (not a generated part) and the expression it returns (an expression body or a lone <c>get =&gt;</c>).
    /// </summary>
    private static (PropertyDeclarationSyntax Syntax, IPropertySymbol Property, ExpressionSyntax Returned)? HandWrittenPart(INamedTypeSymbol type,
        INamedTypeSymbol face, string name, CancellationToken token)
    {
        if (face.GetMembers(name).FirstOrDefault() is not { } declared
            || type.FindImplementationForInterfaceMember(declared) is not IPropertySymbol { IsStatic: false } property
            || !SymbolEqualityComparer.Default.Equals(property.ContainingType, type) || Parts.IsGeneratedPart(property)
            || property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(token) is not PropertyDeclarationSyntax syntax)
            return null;
        var returned = syntax.ExpressionBody?.Expression
                       ?? (syntax.AccessorList?.Accessors is { Count: 1 } accessors && accessors[0] is { Keyword.ValueText: "get", ExpressionBody.Expression: { } body } ? body : null);
        return returned == null ? null : (syntax, property, returned);
    }

    /// <summary>The member an expression names (<c>N</c>, <c>this.N</c>), <c>"this"</c> for the value, else null.</summary>
    private static string? ReturnedMember(ExpressionSyntax returned) => returned switch
    {
        ThisExpressionSyntax => "this",
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// EECS069: the managed components and singletons of the compilation with neither [NinoType] nor [NotSaved], reported
    /// when it ends: a warning if some code saves what holds them (a component: a call of World.Save, SaveWithout or
    /// SaveEntities*; a singleton: Save or SaveWithout, since entity saves write no singletons), else a suggestion. A type the code names in a ComponentSet (With&lt;T&gt; / WithSingleton&lt;T&gt;) is left alone: the
    /// code already chooses which saves hold it (SaveWithout, SaveEntitiesWith / Without).
    /// </summary>
    private static void AnalyzeFormatters(CompilationStartAnalysisContext start)
    {
        if (start.Compilation.GetTypeByMetadataName(Symbols.World) is not { } world) return;
        var unwritten = new System.Collections.Concurrent.ConcurrentQueue<INamedTypeSymbol>();
        var chosen = new System.Collections.Concurrent.ConcurrentDictionary<ITypeSymbol, byte>(SymbolEqualityComparer.Default);
        var set = start.Compilation.GetTypeByMetadataName(Symbols.ComponentSet);
        int saves = 0, worldSaves = 0;
        start.RegisterSymbolAction(context =>
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type is { TypeKind: TypeKind.Struct, IsUnmanagedType: false } && type.Locations.Any(l => l.IsInSource)
                && !Symbols.Named(type.OriginalDefinition, Symbols.DynamicBuffer) // its elements are written one by one
                && !Symbols.Implements(type, "EasyEcs.IBuffer") // a singleton holding a list (Emitted<T>): written as one too
                && (Symbols.Implements(type, Symbols.Component) || Symbols.Implements(type, Symbols.Singleton))
                && !type.GetAttributes().Any(a => a.AttributeClass is { Name: "NinoTypeAttribute" } || Symbols.Named(a.AttributeClass, Symbols.NotSavedAttribute)))
                unwritten.Enqueue(type);
        }, SymbolKind.NamedType);
        start.RegisterOperationAction(context =>
        {
            var method = ((IInvocationOperation)context.Operation).TargetMethod;
            if (method is { Name: "With" or "WithSingleton", TypeArguments.Length: 1 } && SymbolEqualityComparer.Default.Equals(method.ContainingType, set))
                chosen.TryAdd(method.TypeArguments[0].OriginalDefinition, 0);
            else if (Volatile.Read(ref worldSaves) == 0 && method is { Name: "Save" or "SaveWithout" or "SaveEntities" or "SaveEntitiesWith" or "SaveEntitiesWithout" }
                && SymbolEqualityComparer.Default.Equals(method.ContainingType, world))
            {
                Interlocked.Exchange(ref saves, 1);
                if (method.Name is "Save" or "SaveWithout") Interlocked.Exchange(ref worldSaves, 1);
            }
        }, OperationKind.Invocation);
        start.RegisterCompilationEndAction(context =>
        {
            foreach (var type in unwritten)
                if (!chosen.ContainsKey(type.OriginalDefinition))
                    context.ReportDiagnostic(Diagnostic.Create(NoFormatter, type.Locations.First(l => l.IsInSource),
                        (Symbols.Implements(type, Symbols.Singleton) ? worldSaves : saves) != 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                        null, null, type.Name));
        });
    }

    /// <summary>EECS068: [GroupBy] / [WhenTargetDestroyed] on a type that cannot take it (<see cref="Symbols.SetupProblem"/>).</summary>
    private static void AnalyzeSetup(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (!Symbols.Named(attribute.AttributeClass, Symbols.GroupByAttribute) && !Symbols.Named(attribute.AttributeClass, Symbols.WhenTargetDestroyedAttribute)
                && !Symbols.Named(attribute.AttributeClass, Symbols.NotSavedAttribute)
                || Symbols.SetupProblem(type, attribute) is not { } problem)
                continue;
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations.FirstOrDefault();
            context.ReportDiagnostic(Diagnostic.Create(BadSetup, location, attribute.AttributeClass!.Name.Replace("Attribute", ""), type.Name, problem));
        }
    }

    /// <summary>What the generator writes into the type, for EECS062's message.</summary>
    private static string What(Parts parts) => string.Join(", ", new[]
    {
        parts.Fields.Count > 0 || parts.Reduces ? "IReduction<T> from its field attributes" : null,
        parts.KeyOf != null ? $"the Key of [{(parts.KeyOf.Unique ? "UniqueKey" : "IndexKey")}]" : null,
        parts.GroupOf != null ? "the Group of [Group]" : null,
    }.Where(w => w != null));

    // ---------------- EECS065, EECS066: hand-written reductions ----------------

    /// <summary>
    /// A struct that implements <c>IReduction&lt;T&gt;</c> for itself by hand: its Combine read field by field
    /// (<see cref="Recognize"/>) and its Identity's constant values (<see cref="IdentityValues"/>). EECS066 when a
    /// field's identity does not fit its combine; else EECS065 when every field combines one way the attributes say.
    /// </summary>
    private static void AnalyzeReduction(SyntaxNodeAnalysisContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol type
            || !type.AllInterfaces.Any(i => Symbols.Named(i.OriginalDefinition, "EasyEcs.IReduction<T>") && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type))
            || Parts.Of(type) is { Reduces: true })
            return;
        var combine = declaration.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "Combine"
            && m.Modifiers.Any(SyntaxKind.StaticKeyword) && m.ParameterList.Parameters.Count == 2);
        var identity = declaration.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.ValueText == "Identity"
            && p.Modifiers.Any(SyntaxKind.StaticKeyword));
        if (combine == null || identity == null
            || context.SemanticModel.GetDeclaredSymbol(combine, context.CancellationToken) is not { } method
            || Recognize(context.SemanticModel, combine, method, type, context.CancellationToken) is not { Count: > 0 } ops)
            return;

        var members = Parts.Members(type).Where(m => m.Data).ToList();
        var value = IdentityValue(identity);
        var values = value == null ? null : IdentityValues(context.SemanticModel, value, type, context.CancellationToken);
        if (values == null) return; // an Identity read elsewhere (a field, a call): nothing to compare
        bool all = members.Count > 0 && members.All(m => ops.ContainsKey(m.Name)) && members.All(m => m.Settable)
                   && members.All(m => Fits(ops[m.Name], m.Type));
        var opsText = string.Join(";", members.Where(m => ops.ContainsKey(m.Name)).Select(m => m.Name + "=" + ops[m.Name]));

        var wrong = new List<string>();
        foreach (var member in members)
        {
            if (!ops.TryGetValue(member.Name, out var op) || !Fits(op, member.Type)) continue;
            values.TryGetValue(member.Name, out var constant); // a field the Identity leaves out is 0 (false)
            if (!IsIdentity(op, member.Type, constant ?? Zero(member.Type)))
                wrong.Add($"'{member.Name}' starts at {(constant == null ? "0 (it is left out)" : Show(constant))}, but {Verb(op)} needs {Parts.Identity(op, member.Type.ToDisplayString())}");
        }

        if (wrong.Count > 0)
        {
            var properties = ImmutableDictionary<string, string?>.Empty
                .Add(FixedIdentity, Fixed(context.SemanticModel, value!, declaration, members, ops, context.CancellationToken));
            if (all) properties = properties.Add(ReductionOps, opsText);
            context.ReportDiagnostic(Diagnostic.Create(WrongIdentity, value!.GetLocation(), properties, type.Name, string.Join("; ", wrong)));
            return;
        }

        if (all)
            context.ReportDiagnostic(Diagnostic.Create(HandWrittenReduction, declaration.Identifier.GetLocation(),
                ImmutableDictionary<string, string?>.Empty.Add(ReductionOps, opsText), type.Name,
                string.Join(", ", members.Select(m => $"{m.Name} by {ops[m.Name]}"))));
    }

    private static string Verb(Parts.Op op) => op switch
    {
        Parts.Op.Sum => "a sum",
        Parts.Op.Product => "a product",
        Parts.Op.Min => "a minimum",
        Parts.Op.Max => "a maximum",
        Parts.Op.All => "an and",
        _ => "an or",
    };

    /// <summary>Whether <paramref name="op"/> can combine <paramref name="type"/> (what the attribute accepts).</summary>
    private static bool Fits(Parts.Op op, ITypeSymbol type) =>
        op is Parts.Op.All or Parts.Op.Any ? type.SpecialType == SpecialType.System_Boolean : Number(type.SpecialType);

    private static bool Number(SpecialType t) => t is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
        or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64
        or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_Single
        or SpecialType.System_Double or SpecialType.System_Decimal;

    /// <summary>The Identity's value as written: its expression body, its getter's one return, or its initializer.</summary>
    private static ExpressionSyntax? IdentityValue(PropertyDeclarationSyntax identity) =>
        identity.ExpressionBody?.Expression ?? identity.Initializer?.Value ?? Symbols.Returned(identity);

    /// <summary>
    /// The fields' constant values in <paramref name="value"/>: none for <c>default</c> or <c>new T()</c> (every field
    /// 0), the members an initializer sets; null when a value is not constant or the expression is another form.
    /// </summary>
    private static Dictionary<string, object?>? IdentityValues(SemanticModel model, ExpressionSyntax value, INamedTypeSymbol type, System.Threading.CancellationToken token)
    {
        var operation = Symbols.Unwrap(model.GetOperation(value, token));
        var values = new Dictionary<string, object?>();
        switch (operation)
        {
            case IDefaultValueOperation:
                return values;
            case IObjectCreationOperation { Arguments.Length: 0 } creation when SymbolEqualityComparer.Default.Equals(creation.Type, type):
                if (creation.Initializer == null) return values;
                foreach (var initializer in creation.Initializer.Initializers)
                {
                    if (initializer is not ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Member: var member } } assignment
                        || !assignment.Value.ConstantValue.HasValue)
                        return null;
                    values[member.Name] = assignment.Value.ConstantValue.Value;
                }

                return values;
            default:
                return null;
        }
    }

    /// <summary>The zero of a field of <paramref name="type"/>: what a field the Identity leaves out holds.</summary>
    private static object Zero(ITypeSymbol type) => type.SpecialType == SpecialType.System_Boolean ? false : 0;

    private static string Show(object? constant) => constant switch
    {
        bool b => b ? "true" : "false",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(constant, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    /// <summary>
    /// Whether <paramref name="constant"/> is an identity of <paramref name="op"/> on <paramref name="type"/>: 0 for a
    /// sum, 1 for a product, the largest value (or +∞, or <c>float.MaxValue</c> for a float) for a minimum, the smallest
    /// for a maximum, true for an and, false for an or.
    /// </summary>
    private static bool IsIdentity(Parts.Op op, ITypeSymbol type, object constant)
    {
        if (op is Parts.Op.All or Parts.Op.Any) return constant is bool b && b == (op == Parts.Op.All);
        if (constant is bool) return false;
        double value = System.Convert.ToDouble(constant, System.Globalization.CultureInfo.InvariantCulture);
        switch (op)
        {
            case Parts.Op.Sum: return value == 0;
            case Parts.Op.Product: return value == 1;
        }

        bool min = op == Parts.Op.Min;
        if (type.SpecialType is SpecialType.System_Single)
            return min ? value >= float.MaxValue : value <= float.MinValue;
        if (type.SpecialType is SpecialType.System_Double)
            return min ? value >= double.MaxValue : value <= double.MinValue;
        // Integers and decimal: exact, through decimal (which holds every long and ulong). nint and nuint have no
        // constant bounds: not judged.
        if (Bounds(type.SpecialType) is not { } bounds) return true;
        return System.Convert.ToDecimal(constant, System.Globalization.CultureInfo.InvariantCulture) == (min ? bounds.Max : bounds.Min);
    }

    private static (decimal Min, decimal Max)? Bounds(SpecialType t) => t switch
    {
        SpecialType.System_SByte => (sbyte.MinValue, sbyte.MaxValue),
        SpecialType.System_Byte => (byte.MinValue, byte.MaxValue),
        SpecialType.System_Int16 => (short.MinValue, short.MaxValue),
        SpecialType.System_UInt16 => (ushort.MinValue, ushort.MaxValue),
        SpecialType.System_Int32 => (int.MinValue, int.MaxValue),
        SpecialType.System_UInt32 => (uint.MinValue, uint.MaxValue),
        SpecialType.System_Int64 => (long.MinValue, long.MaxValue),
        SpecialType.System_UInt64 => (ulong.MinValue, ulong.MaxValue),
        SpecialType.System_Decimal => (decimal.MinValue, decimal.MaxValue),
        _ => null,
    };

    /// <summary>
    /// The Identity that fits the Combine: <c>new T { … }</c> with each recognized field's identity, every other field
    /// as the original set it.
    /// </summary>
    private static string Fixed(SemanticModel model, ExpressionSyntax value, TypeDeclarationSyntax declaration, List<Parts.Member> members,
        Dictionary<string, Parts.Op> ops, System.Threading.CancellationToken token)
    {
        var original = (Symbols.Unwrap(model.GetOperation(value, token)) as IObjectCreationOperation)?.Initializer?.Initializers
            .OfType<ISimpleAssignmentOperation>()
            .Where(a => a.Target is IMemberReferenceOperation)
            .ToDictionary(a => ((IMemberReferenceOperation)a.Target).Member.Name, a => a.Value.Syntax.ToString()) ?? new Dictionary<string, string>();
        var sets = new List<string>();
        foreach (var member in members)
            if (ops.TryGetValue(member.Name, out var op) && Fits(op, member.Type))
                sets.Add($"{member.Name} = {Parts.Identity(op, member.Type.ToDisplayString())}");
            else if (original.TryGetValue(member.Name, out var kept))
                sets.Add($"{member.Name} = {kept}");
        return $"new {declaration.Identifier.ValueText}{declaration.TypeParameterList} {{ {string.Join(", ", sets)} }}";
    }

    /// <summary>
    /// How each field of <paramref name="type"/> a hand-written Combine sets combines its two values, by field name:
    /// <c>new T { F = left.F + right.F, … }</c>, or statements <c>left.F += right.F; left.G = Math.Min(left.G, right.G);
    /// return left;</c>. Null when the method is another form; a field set some other way is left out.
    /// </summary>
    private static Dictionary<string, Parts.Op>? Recognize(SemanticModel model, MethodDeclarationSyntax combine, IMethodSymbol method,
        INamedTypeSymbol type, System.Threading.CancellationToken token)
    {
        if (method.Parameters.Length != 2 || !method.Parameters.All(p => SymbolEqualityComparer.Default.Equals(p.Type, type))) return null;
        IParameterSymbol left = method.Parameters[0], right = method.Parameters[1];
        var ops = new Dictionary<string, Parts.Op>();
        SyntaxNode? body = (SyntaxNode?)combine.ExpressionBody?.Expression ?? combine.Body;
        if (body == null || model.GetOperation(body, token) is not { } operation) return null;
        if (operation is IBlockOperation block)
        {
            // Statements writing left's fields, then 'return left;' (or one 'return new T { … };').
            var statements = block.Operations;
            if (statements.Length == 1 && statements[0] is IReturnOperation { ReturnedValue: { } only }) return FromCreation(Symbols.Unwrap(only), type, left, right, ops);
            if (statements.Length < 2 || statements[statements.Length - 1] is not IReturnOperation { ReturnedValue: var returned }
                || Symbols.Unwrap(returned) is not IParameterReferenceOperation { Parameter: var back } || !SymbolEqualityComparer.Default.Equals(back, left))
                return null;
            for (int i = 0; i < statements.Length - 1; i++)
            {
                if (statements[i] is not IExpressionStatementOperation { Operation: var write }) return null;
                switch (write)
                {
                    case ICompoundAssignmentOperation { Target: IFieldReferenceOperation or IPropertyReferenceOperation } compound
                        when Field(compound.Target, left) is { } name && Field(Symbols.Unwrap(compound.Value), right) == name
                             && OpOf(compound.OperatorKind) is { } op:
                        ops[name] = op;
                        break;
                    case ISimpleAssignmentOperation simple when Field(simple.Target, left) is { } name && OpOf(Symbols.Unwrap(simple.Value), name, left, right) is { } op:
                        ops[name] = op;
                        break;
                    default:
                        return null;
                }
            }

            return ops;
        }

        return FromCreation(Symbols.Unwrap(operation is IReturnOperation r ? r.ReturnedValue : operation), type, left, right, ops);
    }

    private static Dictionary<string, Parts.Op>? FromCreation(IOperation? value, INamedTypeSymbol type, IParameterSymbol left, IParameterSymbol right,
        Dictionary<string, Parts.Op> ops)
    {
        if (value is not IObjectCreationOperation { Arguments.Length: 0, Initializer: { } initializer } creation
            || !SymbolEqualityComparer.Default.Equals(creation.Type, type))
            return null;
        foreach (var set in initializer.Initializers)
        {
            if (set is not ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Member: var member } } assignment) return null;
            if (OpOf(Symbols.Unwrap(assignment.Value), member.Name, left, right) is { } op) ops[member.Name] = op;
        }

        return ops;
    }

    /// <summary>The field <paramref name="target"/> reads of parameter <paramref name="of"/> (<c>left.F</c>), else null.</summary>
    private static string? Field(IOperation? target, IParameterSymbol of) =>
        target is IMemberReferenceOperation { Member: IFieldSymbol or IPropertySymbol, Instance: IParameterReferenceOperation { Parameter: var p } } access
        && SymbolEqualityComparer.Default.Equals(p, of) ? access.Member.Name : null;

    private static Parts.Op? OpOf(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => Parts.Op.Sum,
        BinaryOperatorKind.Multiply => Parts.Op.Product,
        BinaryOperatorKind.And or BinaryOperatorKind.ConditionalAnd => Parts.Op.All,
        BinaryOperatorKind.Or or BinaryOperatorKind.ConditionalOr => Parts.Op.Any,
        _ => null,
    };

    /// <summary>
    /// How <paramref name="value"/> combines field <paramref name="name"/> of <paramref name="left"/> and
    /// <paramref name="right"/>: <c>l + r</c>, <c>l * r</c>, <c>l &amp; r</c> / <c>&amp;&amp;</c>, <c>l | r</c> /
    /// <c>||</c>, <c>Math.Min</c> / <c>Max</c> (or MathF), <c>l &lt; r ? l : r</c>; the two sides in either order.
    /// </summary>
    private static Parts.Op? OpOf(IOperation? value, string name, IParameterSymbol left, IParameterSymbol right)
    {
        bool Pair(IOperation? a, IOperation? b) =>
            Field(Symbols.Unwrap(a), left) == name && Field(Symbols.Unwrap(b), right) == name
            || Field(Symbols.Unwrap(a), right) == name && Field(Symbols.Unwrap(b), left) == name;
        bool Same(IOperation? a, IOperation? b) => Field(Symbols.Unwrap(a), left) == name ? Field(Symbols.Unwrap(b), left) == name
            : Field(Symbols.Unwrap(a), right) == name && Field(Symbols.Unwrap(b), right) == name;
        switch (value)
        {
            case IBinaryOperation binary when Pair(binary.LeftOperand, binary.RightOperand):
                return OpOf(binary.OperatorKind);
            case IInvocationOperation { TargetMethod: { Name: "Min" or "Max" } m, Arguments.Length: 2 } call
                when (Symbols.Named(m.ContainingType, "System.Math") || Symbols.Named(m.ContainingType, "System.MathF"))
                     && Pair(call.Arguments[0].Value, call.Arguments[1].Value):
                return m.Name == "Min" ? Parts.Op.Min : Parts.Op.Max;
            case IConditionalOperation { Condition: IBinaryOperation compare, WhenTrue: var yes, WhenFalse: { } no }
                when Pair(compare.LeftOperand, compare.RightOperand) && Pair(yes, no):
                bool less = compare.OperatorKind is BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual;
                bool greater = compare.OperatorKind is BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual;
                if (!less && !greater) return null;
                bool first = Same(compare.LeftOperand, yes); // the condition's left side when true
                return less == first ? Parts.Op.Min : Parts.Op.Max;
            default:
                return null;
        }
    }
}
