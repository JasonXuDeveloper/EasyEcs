using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Symbol checks shared by the generator (which silently skips invalid input) and the analyzer (which reports it),
/// so both always agree on what is valid.
/// </summary>
internal static class Symbols
{
    public const string ForEachAttribute = "EasyEcs.ForEachAttribute";
    public const string WithAttribute = "EasyEcs.WithAttribute<T>";
    public const string WithoutAttribute = "EasyEcs.WithoutAttribute<T>";
    public const string GroupWith = "EasyEcs.ISystemGroupWith<T>";
    public const string GroupWithout = "EasyEcs.ISystemGroupWithout<T>";
    public const string WithAnyAttribute = "EasyEcs.WithAnyAttribute<T>";
    public const string ChangedAttribute = "EasyEcs.ChangedAttribute<T>";
    public const string RemovedAttribute = "EasyEcs.RemovedAttribute<T>";
    public const string EcsSystem = "EasyEcs.EcsSystem";
    public const string Component = "EasyEcs.IComponent";
    public const string Singleton = "EasyEcs.ISingletonComponent";
    public const string Entity = "EasyEcs.Entity";
    public const string EntitySpan = "EasyEcs.EntitySpan";
    public const string World = "EasyEcs.World";
    public const string DynamicBuffer = "EasyEcs.DynamicBuffer<T>";
    public const string TrackChanges = "EasyEcs.ITracked";
    public const string Indexed = "EasyEcs.IIndexed<TKey>";
    public const string UniqueIndexed = "EasyEcs.IUniqueIndexed<TKey>";
    public const string Group = "EasyEcs.IGroup";
    public const string Cleanup = "EasyEcs.ICleanupComponent";
    public const string EntityId = "EasyEcs.EntityId";
    public const string UpdateBeforeAttribute = "EasyEcs.UpdateBeforeAttribute<TSystem>";
    public const string UpdateAfterAttribute = "EasyEcs.UpdateAfterAttribute<TSystem>";
    public const string UpdateInGroupAttribute = "EasyEcs.UpdateInGroupAttribute<TGroup>";
    public const int MaxComponents = 9;

    public static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// Whether <paramref name="symbol"/> (a namespace or a named type) is displayed as <paramref name="display"/>
    /// (<c>symbol.ToDisplayString() == display</c>: <c>EasyEcs.World</c>, a generic definition with its type parameters,
    /// <c>EasyEcs.IIndexed&lt;TKey&gt;</c>), without building a string or an array: generators and the analyzer ask this
    /// of every candidate on every edit. A nullable annotation does not count (<c>World?</c> is a <c>World</c>).
    /// </summary>
    public static bool Named(ISymbol? symbol, string display)
    {
        if (symbol is not (INamedTypeSymbol or INamespaceSymbol)) return false;
        int end = display.Length;
        if (display[end - 1] == '>')
        {
            // A generic definition of as many type parameters (their names are the definition's: no other type has them).
            if (symbol is not INamedTypeSymbol { IsGenericType: true, IsDefinition: true } generic) return false;
            int open = display.IndexOf('<'), arity = 1;
            for (int i = open; i < end; i++)
                if (display[i] == ',') arity++;
            if (generic.Arity != arity) return false;
            end = open;
        }
        else if (symbol is INamedTypeSymbol { IsGenericType: true } or INamedTypeSymbol { IsTupleType: true }) return false;

        // The names, innermost first: the type, its containing types, its namespaces.
        for (ISymbol? s = symbol; ;)
        {
            string name = s.Name;
            int start = end - name.Length;
            if (start < 0 || string.CompareOrdinal(display, start, name, 0, name.Length) != 0) return false;
            s = (ISymbol?)s.ContainingType ?? s.ContainingNamespace;
            if (s is null or INamespaceSymbol { IsGlobalNamespace: true }) return start == 0;
            if (s is INamedTypeSymbol { IsGenericType: true } || start == 0 || display[start - 1] != '.') return false;
            end = start - 1;
        }
    }

    /// <summary>
    /// The type a type name names (<paramref name="name"/>, or the qualified name it ends), or null when it names none
    /// or one with an error in it. Generators ask this of every candidate of every file on every edit: in a member
    /// body, <c>GetSymbolInfo</c> (and any speculation at a position there) binds the whole body and runs its nullable
    /// analysis first, so a name in a body is bound alone at <see cref="Outside"/> its member. Names there see what they
    /// see in the body (the usings, the namespaces, the types and their type parameters) but a method's type
    /// parameters, which no generated code can name anyway.
    /// </summary>
    public static ITypeSymbol? TypeNamed(SemanticModel model, SyntaxNode name)
    {
        while (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name || name.Parent is AliasQualifiedNameSyntax alias && alias.Name == name)
            name = name.Parent;
        if (name is not TypeSyntax type) return null;
        int outside = Outside(type);
        var bound = (outside < 0 ? model.GetSymbolInfo(type).Symbol
            : model.GetSpeculativeSymbolInfo(outside, type, SpeculativeBindingOption.BindAsTypeOrNamespace).Symbol) as ITypeSymbol;
        return bound == null || HasError(bound) ? null : bound;
    }

    /// <summary>
    /// A position inside the braces of the type whose member body (or initializer) holds <paramref name="node"/>, where
    /// a type name binds without the member; -1 outside a member body (a declaration's own types bind alone already)
    /// or in top-level statements.
    /// </summary>
    public static int Outside(SyntaxNode node)
    {
        bool body = false;
        for (var n = node.Parent; n != null; n = n.Parent)
            switch (n)
            {
                case BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax:
                    body = true;
                    break;
                case BaseTypeDeclarationSyntax type:
                    return body && !type.OpenBraceToken.IsMissing ? type.OpenBraceToken.Span.End : -1;
                case GlobalStatementSyntax:
                    return -1;
            }

        return -1;
    }

    private static bool HasError(ITypeSymbol type) => type switch
    {
        { TypeKind: TypeKind.Error } => true,
        INamedTypeSymbol named => named.TypeArguments.Any(HasError) || named.ContainingType is { } outer && HasError(outer),
        IArrayTypeSymbol array => HasError(array.ElementType),
        _ => false,
    };

    /// <summary>The element type when <paramref name="type"/> is a <c>DynamicBuffer&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? BufferElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named &&
        Named(named.OriginalDefinition, DynamicBuffer)
            ? named.TypeArguments[0]
            : null;

    /// <summary>Whether <paramref name="value"/> is <c>new DynamicBuffer&lt;T&gt;()</c> or <c>default</c> (no list).</summary>
    public static bool IsNewBuffer(IOperation? value)
    {
        while (value is IConversionOperation conversion) value = conversion.Operand;
        return value is IDefaultValueOperation or IObjectCreationOperation { Arguments.Length: 0, Initializer: null };
    }

    /// <summary>Whether <paramref name="type"/> is a construction of generic type <paramref name="definition"/> (its display name).</summary>
    public static bool Is(INamedTypeSymbol? type, string definition) => Named(type?.OriginalDefinition, definition);

    /// <summary>The group type of a system's (inherited) [UpdateInGroup&lt;T&gt;], or null for the default group.</summary>
    public static ITypeSymbol? GroupOf(INamedTypeSymbol system)
    {
        for (var type = system; type != null; type = type.BaseType)
            foreach (var attribute in type.GetAttributes())
                if (Is(attribute.AttributeClass, UpdateInGroupAttribute)) return attribute.AttributeClass!.TypeArguments[0];
        return null;
    }

    /// <summary>
    /// The filters a group type declares through the interfaces it implements (<c>ISystemGroupWith&lt;T&gt;</c>,
    /// <c>ISystemGroupWithout&lt;T&gt;</c>, also inherited from its own interfaces): as <c>[With&lt;T&gt;]</c> (true) or
    /// <c>[Without&lt;T&gt;]</c> (false) on the group.
    /// </summary>
    public static IEnumerable<(bool With, ITypeSymbol Type)> GroupInterfaceFilters(ITypeSymbol group)
    {
        foreach (var face in group.AllInterfaces)
            if (Is(face, GroupWith)) yield return (true, face.TypeArguments[0]);
            else if (Is(face, GroupWithout)) yield return (false, face.TypeArguments[0]);
    }

    public static bool IsSystem(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (Named(b, EcsSystem)) return true;
        return false;
    }

    /// <summary>The system whose code <paramref name="symbol"/> is (its type, or a type it is nested in), or null.</summary>
    public static INamedTypeSymbol? SystemOf(ISymbol? symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType; type != null; type = type.ContainingType)
            if (IsSystem(type)) return type;
        return null;
    }

    /// <summary>A hand-written job's interface: <c>IForEach</c>, <c>IForEachEntity</c> or <c>IForEachBatch</c> of EasyEcs.</summary>
    public static bool IsJob(INamedTypeSymbol face) =>
        face.Name is "IForEach" or "IForEachEntity" or "IForEachBatch" && Named(face.ContainingNamespace, "EasyEcs");

    /// <summary>The one job interface (<see cref="IsJob"/>) whose Execute <paramref name="method"/> implements, or null.</summary>
    public static INamedTypeSymbol? JobInterface(IMethodSymbol method)
    {
        if (method.Name != "Execute" || method.IsStatic || method.ContainingType is not { } type) return null;
        INamedTypeSymbol? found = null;
        foreach (var face in type.AllInterfaces)
        {
            if (!IsJob(face)) continue;
            if (found != null) return null; // several jobs in one type: their WriteMask is shared, nothing to say per Execute
            found = face;
        }

        if (found?.GetMembers("Execute").FirstOrDefault() is not { } execute
            || !SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(execute), method)) return null;
        return found;
    }

    /// <summary>A query item: EasyEcs's <c>QueryItem&lt;…&gt;</c> (refs) or <c>ReadItem&lt;…&gt;</c> (ref readonly).</summary>
    public static bool IsItem(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Name: "QueryItem" or "ReadItem" } item && Named(item.ContainingNamespace, "EasyEcs");

    /// <summary>A query item's positional component, <c>item.Component1</c> … <c>item.Component9</c>.</summary>
    public static bool IsItemComponent(IPropertySymbol property) => property.Name.StartsWith("Component") && IsItem(property.ContainingType);

    /// <summary>A tag: a component (or a buffer's element) with no instance field.</summary>
    public static bool IsTag(ITypeSymbol type) => !type.GetMembers().Any(m => m is IFieldSymbol { IsStatic: false });

    /// <summary>
    /// Whether SetIfChanged can compare <paramref name="type"/>: IEquatable&lt;T&gt;, or unmanaged (its bytes; Values.Same).
    /// A type parameter or an unresolved type is given the benefit of the doubt.
    /// </summary>
    public static bool IsComparable(ITypeSymbol type) =>
        type is ITypeParameterSymbol or IErrorTypeSymbol || type.IsUnmanagedType
        || type.AllInterfaces.Any(i => Named(i.OriginalDefinition, "System.IEquatable<T>") && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type));

    /// <summary>Whether Disable accepts <paramref name="component"/> (Entity.ThrowIfNotDisableable): not ChildOf, not a cleanup component.</summary>
    public static bool IsDisableable(ITypeSymbol component) =>
        component is ITypeParameterSymbol || !Named(component, "EasyEcs.ChildOf") && !Implements(component, Cleanup);

    /// <summary><c>Retired</c> or a <c>Disabled&lt;T&gt;</c>: tags the world adds and removes itself.</summary>
    public static bool IsWorldTag(ITypeSymbol type) =>
        Named(type, "EasyEcs.Retired") || type is INamedTypeSymbol { IsGenericType: true } named && Named(named.OriginalDefinition, "EasyEcs.Disabled<T>");

    /// <summary><paramref name="operation"/> without the conversions around it.</summary>
    public static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
    }

    /// <summary>The root of the operation tree <paramref name="operation"/> belongs to (a method's or an initializer's body).</summary>
    public static IOperation Root(IOperation operation)
    {
        while (operation.Parent != null) operation = operation.Parent;
        return operation;
    }

    /// <summary>
    /// Whether <paramref name="operation"/> runs whenever <paramref name="stop"/> (an ancestor) does: no branch between
    /// them (an if's or a switch's arm, the right of &amp;&amp; / || / ??, <c>?.</c>, a catch), no loop part that may run
    /// zero times (a body, a for loop's step; a foreach's collection and a for loop's start and condition run), no lambda
    /// or local function.
    /// </summary>
    public static bool AlwaysRuns(IOperation operation, IOperation stop)
    {
        for (IOperation current = operation, parent = operation.Parent!; parent != null && current != stop; current = parent, parent = parent.Parent!)
            switch (parent)
            {
                case IConditionalOperation conditional when conditional.Condition != current:
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } binary when binary.RightOperand == current:
                case ICoalesceOperation coalesce when coalesce.WhenNull == current:
                case ILoopOperation loop when !(loop is IForEachLoopOperation each && each.Collection == current
                                                || loop is IForLoopOperation forLoop && (forLoop.Condition == current || forLoop.Before.Contains(current))
                                                || loop is IWhileLoopOperation { ConditionIsTop: true } whileLoop && whileLoop.Condition == current):
                case ISwitchCaseOperation or ISwitchExpressionArmOperation or ICatchClauseOperation or IConditionalAccessOperation
                    or IAnonymousFunctionOperation or ILocalFunctionOperation:
                    return false;
            }

        return true;
    }

    /// <summary>
    /// The expression a property's getter returns, as written: <c>=&gt; x</c>, <c>{ get =&gt; x; }</c> or <c>{ get { return x; } }</c>;
    /// null for any other body.
    /// </summary>
    public static ExpressionSyntax? Returned(PropertyDeclarationSyntax declaration)
    {
        var getter = declaration.AccessorList?.Accessors.FirstOrDefault();
        return declaration.ExpressionBody?.Expression ?? getter?.ExpressionBody?.Expression
            ?? (getter?.Body?.Statements is { Count: 1 } statements && statements[0] is ReturnStatementSyntax { Expression: { } returned } ? returned : null);
    }

    /// <summary>
    /// The <c>EntityId</c> fields of struct <paramref name="type"/>, directly or inside struct fields (up to four levels),
    /// that code in <paramref name="compilation"/> can name: one path per field, its steps separated by '\n', each step
    /// "declaring type\tfield name\tfield type" (fully qualified; backing fields of auto-properties included). Private
    /// fields of types from other assemblies are not visible to the compiler and are left out.
    /// </summary>
    public static string[] EntityIdPaths(ITypeSymbol type, Compilation compilation)
    {
        var paths = new List<string>();
        Collect(type, "", 0);
        return paths.ToArray();

        void Collect(ITypeSymbol declaring, string prefix, int depth)
        {
            string owner = declaring.ToDisplayString(Fqn);
            foreach (var field in declaring.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsConst || field.IsFixedSizeBuffer) continue;
                string step = prefix + owner + "\t" + field.Name + "\t" + field.Type.ToDisplayString(Fqn);
                if (Named(field.Type, EntityId)) paths.Add(step);
                else if (depth < 4 && field.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false, SpecialType: SpecialType.None } inner
                         && !Named(inner, Entity)
                         && compilation.IsSymbolAccessibleWithin(inner, compilation.Assembly))
                    Collect(inner, step + "\n", depth + 1);
            }
        }
    }

    /// <summary>
    /// For an unmanaged struct, a hash (FNV-1a) of what its raw rows depend on: its fields in order with their names
    /// and types (nested structs field by field) and the layout attributes ([StructLayout], [FieldOffset]); else null.
    /// A save records it so a load can refuse rows written with other fields of the same size.
    /// </summary>
    public static string? LayoutHash(ITypeSymbol type)
    {
        if (!type.IsUnmanagedType || type.TypeKind != TypeKind.Struct) return null;
        var text = new System.Text.StringBuilder();
        Describe(type, 0);
        return "unchecked((int)0x" + Code.Fnv(text.ToString()).ToString("X8") + ")";

        void Describe(ITypeSymbol declaring, int depth)
        {
            text.Append('{');
            foreach (var attribute in declaring.GetAttributes()) text.Append(attribute).Append(';');
            foreach (var field in declaring.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsConst) continue;
                text.Append(field.Name).Append(':').Append(field.Type.ToDisplayString());
                foreach (var attribute in field.GetAttributes()) text.Append('[').Append(attribute).Append(']');
                if (field.IsFixedSizeBuffer) text.Append('*').Append(field.FixedSize);
                if (depth < 8 && field.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } inner)
                    Describe(inner, depth + 1);
                text.Append(';');
            }

            text.Append('}');
        }
    }

    public static bool Implements(ITypeSymbol type, string interfaceName)
    {
        foreach (var face in type.AllInterfaces)
            if (Named(face, interfaceName)) return true;
        return false;
    }

    /// <summary>
    /// <see cref="Implements"/>, or a type parameter constrained to the interface (<c>where T : struct, IComponent</c>):
    /// what a [ForEach] parameter of a generic system or runner may be.
    /// </summary>
    private static bool IsA(ITypeSymbol type, string interfaceName) => type is ITypeParameterSymbol parameter
        ? IsAny(parameter.ConstraintTypes, interfaceName)
        : Implements(type, interfaceName);

    private static bool IsAny(System.Collections.Immutable.ImmutableArray<ITypeSymbol> constraints, string interfaceName)
    {
        foreach (var constraint in constraints)
            if (Named(constraint, interfaceName) || IsA(constraint, interfaceName)) return true;
        return false;
    }

    /// <summary>
    /// A component the world indexes (<c>IIndexed&lt;TKey&gt;</c>, or a <c>DynamicBuffer</c> of <c>IRelation</c> links) or
    /// groups by (<c>IGroup</c>): written only with Set (and Link / Unlink), so its index or table follows.
    /// </summary>
    public static bool IsIndexed(ITypeSymbol type) =>
        type.AllInterfaces.Any(i => Named(i.OriginalDefinition, Indexed) || Named(i.OriginalDefinition, Group))
        || type is INamedTypeSymbol { IsGenericType: true } buffer && Named(buffer.OriginalDefinition, "EasyEcs.DynamicBuffer<T>")
        && Implements(buffer.TypeArguments[0], "EasyEcs.IRelation");

    /// <summary>
    /// Code outside the type's declaration, anywhere in its assembly, can name <paramref name="type"/>: it and every
    /// containing type (and type argument) are public or internal. The generated jobs of [ForEach] methods live outside
    /// the user's type.
    /// </summary>
    public static bool IsNameable(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => IsNameable(array.ElementType),
        ITypeParameterSymbol => true, // the generated code is generic over the same parameters
        IErrorTypeSymbol => true,     // an unresolved type while typing: the compiler reports it
        IPointerTypeSymbol or IFunctionPointerTypeSymbol => false,
        INamedTypeSymbol named => IsVisible(named) && named.TypeArguments.All(IsNameable),
        _ => true,
    };

    /// <summary>Whether <paramref name="type"/> is or holds a type parameter (an element, a type argument, a containing type's).</summary>
    public static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter) || named.ContainingType is { } outer && ContainsTypeParameter(outer),
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        _ => false,
    };

    /// <summary>
    /// Whether another file can name the non-generic <paramref name="type"/> with members of its own beside it: it and
    /// its containing types are public or internal, none generic or <c>file</c>-local. <paramref name="isPublic"/>:
    /// every level is public, so other assemblies see it too.
    /// </summary>
    public static bool IsExtensible(INamedTypeSymbol type, out bool isPublic)
    {
        isPublic = true;
        for (var t = type; t != null; t = t.ContainingType)
        {
            if (t.TypeParameters.Length > 0 || t.IsFileLocal) return false; // a file type: no other file can name it
            if (t.DeclaredAccessibility == Accessibility.Internal) isPublic = false;
            else if (t.DeclaredAccessibility != Accessibility.Public) return false;
        }

        return true;
    }

    /// <summary>A type every assembly can name: public, as are its containing types, elements and type arguments.</summary>
    public static bool IsPublic(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => IsPublic(array.ElementType),
        ITypeParameterSymbol => true,
        INamedTypeSymbol named => IsPublicChain(named) && named.TypeArguments.All(IsPublic),
        _ => true,
    };

    private static bool IsPublicChain(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (t.DeclaredAccessibility != Accessibility.Public || t.IsFileLocal) return false;
        return true;
    }

    /// <summary>
    /// The type and its containing types are public or internal (accessible anywhere in their assembly); a <c>file</c>
    /// type is not (its name is mangled, and other files cannot name it).
    /// </summary>
    public static bool IsVisible(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (t.IsFileLocal || t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return false;
        return true;
    }

    /// <summary>
    /// The innermost type in <paramref name="type"/> (itself, an element, a type argument) that a private or protected
    /// declaration hides, or null: what EECS022's code fix makes internal, with every hidden type that contains it. A
    /// file-local type has no such fix.
    /// </summary>
    public static INamedTypeSymbol? Hidden(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array: return Hidden(array.ElementType);
            case INamedTypeSymbol named:
                for (var t = named; t != null; t = t.ContainingType)
                    if (t.IsFileLocal) return null;
                if (!IsVisible(named)) return named;
                foreach (var argument in named.TypeArguments)
                    if (Hidden(argument) is { } hidden) return hidden;
                return null;
            default: return null;
        }
    }

    /// <summary>An override, a virtual or abstract method, or a member or implementation of an interface: its signature is shared.</summary>
    public static bool SharesSignature(IMethodSymbol method)
    {
        if (method.IsOverride || method.IsVirtual || method.IsAbstract || method.ExplicitInterfaceImplementations.Length > 0
            || method.ContainingType.TypeKind == TypeKind.Interface) return true;
        foreach (var contract in method.ContainingType.AllInterfaces)
            foreach (var member in contract.GetMembers(method.Name))
                if (SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method))
                    return true;
        return false;
    }

    /// <summary>
    /// The first parameter or return type of a [ForEach] method that generated code outside its type cannot name (a
    /// private nested component), or null. A runner's own type must be nameable too: its entry point extends it.
    /// </summary>
    public static ITypeSymbol? Unnameable(IMethodSymbol method)
    {
        if (IsRunner(method) && !IsVisible(method.ContainingType)) return method.ContainingType;
        for (var t = method.ContainingType; t != null; t = t.ContainingType)
            if (t.IsFileLocal) return t; // a system too: the runtime finds its code by a name the compiler mangles
        if (!method.ReturnsVoid && !IsNameable(method.ReturnType)) return method.ReturnType;
        return method.Parameters.Select(p => p.Type).FirstOrDefault(t => !IsNameable(t));
    }

    /// <summary>The type parameters of <paramref name="type"/> and its containing types, outermost first (the runtime's <c>!0</c>, <c>!1</c>, ...).</summary>
    public static List<ITypeParameterSymbol> ChainTypeParameters(INamedTypeSymbol type)
    {
        var all = new List<ITypeParameterSymbol>();
        for (var t = type; t != null; t = t.ContainingType) all.InsertRange(0, t.TypeParameters);
        return all;
    }

    /// <summary>The type arguments of <paramref name="type"/> and its containing types, outermost first.</summary>
    public static List<ITypeSymbol> ChainTypeArguments(INamedTypeSymbol type)
    {
        var all = new List<ITypeSymbol>();
        for (var t = type; t != null; t = t.ContainingType) all.InsertRange(0, t.TypeArguments);
        return all;
    }

    /// <summary>
    /// A generated file's name for a type: its metadata name (<c>Ns.Outer+Inner`1</c>, unique per type of a compilation,
    /// where a dotted display name is not: <c>N.A.B</c> is a namespace's B or A's nested B) and the name's hash.
    /// </summary>
    public static string HintName(INamedTypeSymbol type)
    {
        string name = type.MetadataName;
        for (var t = type.ContainingType; t != null; t = t.ContainingType) name = t.MetadataName + "+" + name;
        if (!type.ContainingNamespace.IsGlobalNamespace) name = type.ContainingNamespace.ToDisplayString() + "." + name;
        // Roslyn compares hint names ignoring case (Hp and HP would be one file): the exact name's hash tells them apart.
        return name.Replace("@", "") + "." + Code.Fnv(name).ToString("x8");
    }

    /// <summary>
    /// A generated class's name stem for a type of its namespace: <c>Outer__Inner</c> for nested types, <c>Regen_1</c>
    /// for generic ones (a single underscore would make <c>A.B</c> and <c>A_B</c> one name).
    /// </summary>
    public static string FlatName(INamedTypeSymbol type)
    {
        string Level(INamedTypeSymbol t) => t.Arity == 0 ? t.Name : $"{t.Name}_{t.Arity}";
        string name = Level(type);
        for (var t = type.ContainingType; t != null; t = t.ContainingType) name = Level(t) + "__" + name;
        return name;
    }

    /// <summary><c>&lt;T, U&gt;</c>, or "" without type parameters.</summary>
    public static string TypeParameterList(IReadOnlyCollection<ITypeParameterSymbol> parameters) =>
        parameters.Count == 0 ? "" : "<" + string.Join(", ", parameters.Select(p => p.Name)) + ">";

    /// <summary>The <c>where</c> clauses of <paramref name="parameters"/>, each with a leading space ("" without any).</summary>
    public static string Constraints(IEnumerable<ITypeParameterSymbol> parameters)
    {
        var clauses = new StringBuilder();
        foreach (var p in parameters)
        {
            var parts = new List<string>();
            if (p.HasUnmanagedTypeConstraint) parts.Add("unmanaged");
            else if (p.HasValueTypeConstraint) parts.Add("struct");
            else if (p.HasReferenceTypeConstraint) parts.Add("class");
            else if (p.HasNotNullConstraint) parts.Add("notnull");
            parts.AddRange(p.ConstraintTypes.Select(t => t.ToDisplayString(Fqn)));
            if (p.HasConstructorConstraint && !p.HasValueTypeConstraint) parts.Add("new()");
            if (p.AllowsRefLikeType) parts.Add("allows ref struct");
            if (parts.Count > 0) clauses.Append($" where {p.Name} : {string.Join(", ", parts)}");
        }

        return clauses.ToString();
    }

    public enum ParamKind
    {
        Invalid,
        Component,
        Singleton,
        Entity,
        World,
        /// <summary><c>Span&lt;T&gt;</c> (written) or <c>ReadOnlySpan&lt;T&gt;</c> (read) of a component: a batch method.</summary>
        ComponentSpan,
        /// <summary><c>EntitySpan</c>: the entities of a batch method's run of rows.</summary>
        Entities,
        /// <summary>
        /// <c>Maybe&lt;T&gt;</c>: a component the entity may lack (looked up per entity, a write when present unless the
        /// method only reads it); <c>ReadOnlyMaybe&lt;T&gt;</c>: the same, always a read.
        /// </summary>
        Optional,
        /// <summary><c>Mut&lt;T&gt;</c>: a queried component written only where the method writes it (stamped per write).</summary>
        Mutable,
        /// <summary>
        /// A value the caller passes to the entry point of a [ForEach] method outside a system (<c>ref int dead</c>, a
        /// configuration): any type but a component, a singleton or the framework's own, by value, <c>ref</c> or <c>in</c>.
        /// </summary>
        Context,
    }

    /// <summary>
    /// <see cref="Classify(IParameterSymbol)"/> for a method of <paramref name="outside"/> a system: there, what is
    /// not a component, a singleton or the framework's own is a <see cref="ParamKind.Context"/> the caller passes.
    /// </summary>
    public static ParamKind Classify(IParameterSymbol p, bool outside)
    {
        var kind = Classify(p);
        if (kind != ParamKind.Invalid || !outside || p.RefKind is not (RefKind.None or RefKind.Ref or RefKind.In)) return kind;
        var element = SpanElement(p.Type) ?? MaybeElement(p.Type) ?? MutElement(p.Type) ?? p.Type;
        if (IsA(element, Component) || IsA(element, Singleton)) return ParamKind.Invalid; // a component by value
        // A span or other ref struct goes into the job by value: a job holds no ref field to a ref struct.
        if (p.Type.IsRefLikeType && p.RefKind != RefKind.None) return ParamKind.Invalid;
        return Named(p.Type, Entity) || Named(p.Type, EntitySpan) || Named(p.Type, World) ? ParamKind.Invalid : ParamKind.Context;
    }

    /// <summary>
    /// A [ForEach] method outside a system: static, in a type that is no system. The generator writes its job and an
    /// entry point taking the world (and the caller's context parameters), a static extension member of the type.
    /// </summary>
    public static bool IsRunner(IMethodSymbol method) => !IsSystem(method.ContainingType);

    /// <summary>
    /// The entry point of a [ForEach] method outside a system would have the signature of a method of its type with
    /// the same name (the method itself or an overload), or of the entry point of a [ForEach] overload: the World,
    /// the tick with [Changed&lt;T&gt;] / [Removed&lt;T&gt;], then the context parameters.
    /// </summary>
    public static bool EntryPointCollides(IMethodSymbol method)
    {
        var expected = EntryPoint(method);
        var members = new List<IMethodSymbol>(); // the type's and its base types' (an inherited one wins over an extension)
        for (var t = method.ContainingType; t != null; t = t.BaseType) members.AddRange(t.GetMembers(method.Name).OfType<IMethodSymbol>());
        return members.Any(m =>
            !m.GetAttributes().Any(IsGeneratorMark) // not the entry point itself
            && (m.Parameters.Select(p => (p.Type.ToDisplayString(), p.RefKind)).SequenceEqual(expected)
                || !SymbolEqualityComparer.Default.Equals(m, method) && IsForEach(m) && EntryPoint(m).SequenceEqual(expected)));
    }

    private static List<(string, RefKind)> EntryPoint(IMethodSymbol method)
    {
        var parameters = new List<(string, RefKind)> { (World, RefKind.None) };
        if (TracksSince(method)) parameters.Add(("ulong", RefKind.None));
        if (Slices(method) > 1) parameters.Add(("int", RefKind.None));
        foreach (var p in method.Parameters)
            if (Classify(p, true) == ParamKind.Context) parameters.Add((p.Type.ToDisplayString(), p.RefKind));
        return parameters;
    }

    /// <summary>The method has [Changed&lt;T&gt;] or [Removed&lt;T&gt;] filters: outside a system its entry point takes the tick.</summary>
    public static bool TracksSince(IMethodSymbol method) => AttributeFilters(method).Any(f =>
        f.Kind is FilterKind.Changed or FilterKind.Removed or FilterKind.ChangedAny or FilterKind.RemovedAny);

    /// <summary>"ChangedAny" or "RemovedAny" for a <c>[ChangedAny&lt;T1..Tn&gt;]</c> / <c>[RemovedAny&lt;T1..Tn&gt;]</c> attribute class, else null.</summary>
    public static string? AnyFilter(INamedTypeSymbol attribute) =>
        attribute is { Arity: >= 2, Name: "ChangedAnyAttribute" or "RemovedAnyAttribute" } && Named(attribute.ContainingNamespace, "EasyEcs")
            ? attribute.Name.Substring(0, attribute.Name.Length - "Attribute".Length)
            : null;

    /// <summary>What a filter of a [ForEach] method (or of its system group) does with its component.</summary>
    public enum FilterKind { With, Without, WithAny, Changed, Removed, ChangedAny, RemovedAny }

    /// <summary>
    /// The filter attributes of <paramref name="symbol"/> (a [ForEach] method or a system group), one entry per filtered
    /// type (<c>[ChangedAny&lt;A, B&gt;]</c> gives two), in declaration order: what the generator builds a query filter
    /// from and the analyzer checks, so both read the same list.
    /// </summary>
    public static IEnumerable<(FilterKind Kind, ITypeSymbol Type, AttributeData Attribute)> AttributeFilters(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { IsGenericType: true } cls) continue;
            if (AnyFilter(cls) is { } any)
            {
                foreach (var argument in cls.TypeArguments)
                    yield return (any == "ChangedAny" ? FilterKind.ChangedAny : FilterKind.RemovedAny, argument, attribute);
                continue;
            }

            var from = cls.ConstructedFrom;
            FilterKind? kind = Named(from, WithAttribute) ? FilterKind.With
                : Named(from, WithoutAttribute) ? FilterKind.Without
                : Named(from, WithAnyAttribute) ? FilterKind.WithAny
                : Named(from, ChangedAttribute) ? FilterKind.Changed
                : Named(from, RemovedAttribute) ? FilterKind.Removed
                : null;
            if (kind != null) yield return (kind.Value, cls.TypeArguments[0], attribute);
        }
    }

    /// <summary>
    /// The filters a system's group adds to each of its [ForEach] methods: the group's filter attributes, then its
    /// <c>ISystemGroupWith&lt;T&gt;</c> / <c>ISystemGroupWithout&lt;T&gt;</c> (as With / Without, no attribute). None outside a system.
    /// </summary>
    public static IEnumerable<(FilterKind Kind, ITypeSymbol Type, AttributeData? Attribute, ITypeSymbol Group)> GroupFilters(IMethodSymbol method)
    {
        if (IsRunner(method) || GroupOf(method.ContainingType) is not { } group) yield break;
        foreach (var (kind, type, attribute) in AttributeFilters(group)) yield return (kind, type, attribute, group);
        foreach (var (with, type) in GroupInterfaceFilters(group)) yield return (with ? FilterKind.With : FilterKind.Without, type, null, group);
    }

    /// <summary>A filter as its declaration reads: <c>[With&lt;P&gt;]</c>, <c>ISystemGroupWith&lt;P&gt;</c>.</summary>
    public static string FilterText(FilterKind kind, ITypeSymbol type, bool attribute) =>
        attribute ? $"[{kind}<{type.Name}>]" : $"{(kind == FilterKind.With ? "ISystemGroupWith" : "ISystemGroupWithout")}<{type.Name}>";

    /// <summary>
    /// What a [ForEach] method's entities must have (its queried components, [With&lt;T&gt;], [Changed&lt;T&gt;], its group's
    /// [With&lt;T&gt;] and ISystemGroupWith&lt;T&gt;) and must lack ([Without&lt;T&gt;], [Removed&lt;T&gt;], its group's
    /// [Without&lt;T&gt;] and ISystemGroupWithout&lt;T&gt;), each with how its source reads (<c>parameter 'p'</c>,
    /// <c>[With&lt;P&gt;] on its group G</c>) and where it is declared (a parameter or the method's own attribute; null
    /// for a group's). EECS016, EECS026 and EECS049 read it.
    /// </summary>
    public static List<Requirement> Requirements(IMethodSymbol method, System.Threading.CancellationToken token)
    {
        var all = new List<Requirement>();
        foreach (var (kind, type, attribute) in AttributeFilters(method))
            if (kind is FilterKind.With or FilterKind.Changed or FilterKind.Without or FilterKind.Removed && type.TypeKind != TypeKind.Error)
                all.Add(new Requirement(kind, type, FilterText(kind, type, true), attribute.ApplicationSyntaxReference?.GetSyntax(token).GetLocation(), false));
        foreach (var p in method.Parameters)
            if (Classify(p) is ParamKind.Component or ParamKind.ComponentSpan or ParamKind.Mutable && QueriedComponent(p) is { TypeKind: not TypeKind.Error } component)
                all.Add(new Requirement(FilterKind.With, component, $"parameter '{p.Name}'", p.Locations.FirstOrDefault(), true));
        foreach (var (kind, type, attribute, group) in GroupFilters(method))
            if (kind is FilterKind.With or FilterKind.Changed or FilterKind.Without or FilterKind.Removed && type.TypeKind != TypeKind.Error)
                all.Add(new Requirement(kind, type, FilterText(kind, type, attribute != null) + " on its group " + group.Name, null, false));
        return all;
    }

    /// <summary>A requirement of a [ForEach] method (<see cref="Requirements"/>).</summary>
    public sealed record Requirement(FilterKind Kind, ITypeSymbol Type, string Why, Location? Where, bool Parameter)
    {
        /// <summary>The entities need the component (a parameter, With, Changed); else they need to lack it.</summary>
        public bool Has => Kind is FilterKind.With or FilterKind.Changed;
    }

    /// <summary>The N of [ForEach(Slices = N)] (1 without it).</summary>
    public static int Slices(IMethodSymbol method) => ForEachArgument(method, "Slices", 1);

    /// <summary>[ForEach(ChunkRows = N)]: the rows a thread of a parallel pass takes at a time (0: chosen by the runtime).</summary>
    public static int ChunkRows(IMethodSymbol method) => ForEachArgument(method, "ChunkRows", 0);

    private static int ForEachArgument(IMethodSymbol method, string name, int otherwise)
    {
        foreach (var attribute in method.GetAttributes())
            if (Named(attribute.AttributeClass, ForEachAttribute))
                foreach (var named in attribute.NamedArguments)
                    if (named.Key == name && named.Value.Value is int value) return value;
        return otherwise;
    }

    /// <summary>Why a method's [ForEach(ChunkRows = N)] is invalid (EECS054), or null: N below 0, or set on a method that does not run in parallel.</summary>
    public static string? ChunkRowsProblem(IMethodSymbol method)
    {
        int rows = ChunkRows(method);
        if (rows < 0) return $"ChunkRows = {rows} must be 0 (chosen by the runtime) or more";
        if (rows > 0 && !IsParallel(method)) return "the method does not run in parallel (Parallel = true), so it has no chunks";
        return null;
    }

    /// <summary>
    /// Why a method's [ForEach(Slices = N)] is invalid (EECS035), or null: N below 1, or slices of a method that selects
    /// rows changed since its system last ran (the rows of the other slices would lose those changes).
    /// </summary>
    public static string? SlicesProblem(IMethodSymbol method)
    {
        int slices = Slices(method);
        if (slices < 1) return $"Slices = {slices} must be 1 or more";
        if (slices > 1 && TracksSince(method))
            return "it selects rows by change tick ([Changed<T>] / [Removed<T>]), and a slice's run would move the system's last run past the changes of the other slices' rows";
        return null;
    }

    public static ParamKind Classify(IParameterSymbol p)
    {
        if (p.RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter)
            return !p.Type.IsValueType ? ParamKind.Invalid
                : IsA(p.Type, Component) ? ParamKind.Component
                : IsA(p.Type, Singleton) ? ParamKind.Singleton
                : ParamKind.Invalid;
        if (p.RefKind != RefKind.None) return ParamKind.Invalid;
        if (SpanElement(p.Type) is { } element)
            return element.IsValueType && IsA(element, Component) ? ParamKind.ComponentSpan : ParamKind.Invalid;
        if (MaybeElement(p.Type) is { } optional)
            return optional.IsValueType && IsA(optional, Component) ? ParamKind.Optional : ParamKind.Invalid;
        if (MutElement(p.Type) is { } mutable)
            return mutable.IsValueType && IsA(mutable, Component) ? ParamKind.Mutable : ParamKind.Invalid;
        return Named(p.Type, Entity) ? ParamKind.Entity
            : Named(p.Type, EntitySpan) ? ParamKind.Entities
            : Named(p.Type, World) ? ParamKind.World
            : ParamKind.Invalid;
    }

    /// <summary>A component or singleton struct taken by value: <c>in</c> (read) or <c>ref</c> (write) makes it a valid parameter.</summary>
    public static bool ByValueData(IParameterSymbol p) =>
        p.RefKind == RefKind.None && p.Type.IsValueType && (IsA(p.Type, Component) || IsA(p.Type, Singleton));

    /// <summary>Element type of a <c>Span&lt;T&gt;</c> / <c>ReadOnlySpan&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? SpanElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named &&
        (Named(named.ConstructedFrom, "System.Span<T>") || Named(named.ConstructedFrom, "System.ReadOnlySpan<T>"))
            ? named.TypeArguments[0]
            : null;

    /// <summary>Component type of a <c>Maybe&lt;T&gt;</c> or a <c>ReadOnlyMaybe&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? MaybeElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named
        && (Named(named.ConstructedFrom, "EasyEcs.Maybe<T>") || Named(named.ConstructedFrom, "EasyEcs.ReadOnlyMaybe<T>"))
            ? named.TypeArguments[0]
            : null;

    /// <summary>A <c>ReadOnlyMaybe&lt;T&gt;</c>: an optional component the method can only read.</summary>
    public static bool IsReadOnlyMaybe(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.ReadOnlyMaybe<T>");

    /// <summary>Component type of a <c>Mut&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? MutElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.Mut<T>")
            ? named.TypeArguments[0]
            : null;

    /// <summary>The queried component of a parameter: a <c>ref</c> / <c>in</c> component, a span's element or a <c>Mut</c>'s; else null.</summary>
    public static ITypeSymbol? QueriedComponent(IParameterSymbol p) => Classify(p) switch
    {
        ParamKind.Component => p.Type,
        ParamKind.ComponentSpan => SpanElement(p.Type),
        ParamKind.Mutable => MutElement(p.Type),
        _ => null,
    };

    /// <summary>Whether a [ForEach] method can be generated (all errors are reported by the analyzer).</summary>
    public static bool IsValidForEach(IMethodSymbol method)
    {
        var type = method.ContainingType;
        bool outside = IsRunner(method);
        if (outside && (!method.IsStatic || EntryPointCollides(method))) return false;
        if (method.IsGenericMethod && !outside || Unnameable(method) != null) return false; // a system's method gets no type arguments
        if (SlicesProblem(method) != null) return false;
        int components = 0;
        foreach (var p in method.Parameters)
        {
            var kind = Classify(p, outside);
            if (kind == ParamKind.Invalid) return false;
            if (kind is ParamKind.Component or ParamKind.ComponentSpan or ParamKind.Mutable) components++;
        }

        return ComponentCountValid(method, components) && !MixesBatch(method);
    }

    /// <summary>
    /// A batch method (component spans) takes only spans, its <c>EntitySpan</c> and singletons: per-entity parameters
    /// (ref/in components, Maybe, Mut, Entity, World) have no meaning for a run of rows, and the World could change rows under the
    /// spans. An <c>EntitySpan</c> belongs to a batch method only.
    /// </summary>
    public static bool MixesBatch(IMethodSymbol method)
    {
        bool spans = false, perEntity = false, entities = false;
        foreach (var p in method.Parameters)
        {
            var kind = Classify(p);
            spans |= kind == ParamKind.ComponentSpan;
            entities |= kind == ParamKind.Entities;
            perEntity |= kind is ParamKind.Component or ParamKind.Entity or ParamKind.World or ParamKind.Optional or ParamKind.Mutable;
        }

        return spans ? perEntity : entities;
    }

    /// <summary>A batch [ForEach] method: it takes component spans.</summary>
    public static bool IsBatch(IMethodSymbol method) => method.Parameters.Any(p => Classify(p) == ParamKind.ComponentSpan);

    /// <summary>
    /// 1..9 component (or component span) parameters, or none when a [With&lt;T&gt;] or [Changed&lt;T&gt;] filter names the
    /// queried component (a method that only needs the entity, e.g. [With&lt;Dead&gt;] static void Remove(Entity e)).
    /// </summary>
    public static bool ComponentCountValid(IMethodSymbol method, int components) =>
        components is >= 1 and <= MaxComponents || components == 0 && HasWith(method);

    public static bool HasWith(IMethodSymbol method) => AttributeFilters(method).Any(f => f.Kind is FilterKind.With or FilterKind.Changed);

    /// <summary>
    /// The method cannot make structural changes: it is static, takes no <c>World</c>, and uses its <c>Entity</c>
    /// parameter (if any) only to read (<see cref="ReadOnlyEntityMembers"/>). Its job gets INoStructuralChanges.
    /// </summary>
    public static bool IsStable(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model) =>
        method.IsStatic && StableIfStatic(method, syntax, model);

    /// <summary>
    /// <see cref="IsStable"/> for the method found from its symbol (its first declaration, in <paramref name="model"/>'s
    /// tree): false for a method declared elsewhere.
    /// </summary>
    public static bool IsStable(IMethodSymbol method, SemanticModel model) =>
        method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax syntax
        && syntax.SyntaxTree == model.SyntaxTree && IsStable(method, syntax, model);

    /// <summary>
    /// What <see cref="IsStable"/> asks of the parameters: no <c>World</c>, an <c>Entity</c> used only to read. An
    /// instance method that passes is stable once it is made static (EECS010 offers that).
    /// </summary>
    public static bool StableIfStatic(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        (syntax, model) = WithBody(method, syntax, model);
        return !method.Parameters.Any(p => Classify(p) == ParamKind.World)
            && method.Parameters.Where(p => Classify(p) == ParamKind.Entity).All(p => OnlyReads(syntax, p.Name, model));
    }

    /// <summary>
    /// The declaration holding the method's body: a partial method's implementing part (maybe in another file) when
    /// <paramref name="syntax"/> is its bodiless definition.
    /// </summary>
    public static (MethodDeclarationSyntax, SemanticModel) WithBody(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        if (syntax.Body != null || syntax.ExpressionBody != null
            || method.PartialImplementationPart?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax body)
            return (syntax, model);
        return (body, body.SyntaxTree == syntax.SyntaxTree ? model : model.Compilation.GetSemanticModel(body.SyntaxTree));
    }

    /// <summary>The <c>Parallel</c> named argument of the method's [ForEach] attribute.</summary>
    public static bool IsParallel(IMethodSymbol method) => method.GetAttributes()
        .Where(a => Named(a.AttributeClass, ForEachAttribute))
        .SelectMany(a => a.NamedArguments)
        .Any(n => n.Key == "Parallel" && n.Value.Value is true);

    /// <summary>
    /// Why a [ForEach(Parallel = true)] method cannot run on several threads, or null: it must be stable (no structural
    /// change), per entity (not a batch) and write no singleton (each row's run would race on it).
    /// </summary>
    public static string? ParallelProblem(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        if (!IsStable(method, syntax, model)) return "it can change structure (make it static, without World, and use its Entity only to read)";
        if (IsBatch(method)) return "batch methods run on one thread";
        if (method.Parameters.Any(p => Classify(p) == ParamKind.Singleton && p.RefKind == RefKind.Ref))
            return "it writes a singleton, which every chunk would race on (take it as 'in')";
        if (IsRunner(method) && method.Parameters.Any(p => Classify(p, true) == ParamKind.Context && p.RefKind == RefKind.Ref))
            return "it takes a context parameter by 'ref', which every chunk would race on (pass it by value or 'in')";
        if (IsRunner(method) && method.Parameters.Any(p => Classify(p, true) == ParamKind.Context && p.Type.IsRefLikeType))
            return "it takes a span or other ref struct, which a job on other threads cannot hold";
        return null;
    }

    /// <summary>
    /// The members of <c>Entity</c> and <c>World</c> that change the world's structure (or copy it, or run its systems, or
    /// run a job that may: <c>entity.Run</c>, whose pass bookkeeping is the owner's):
    /// they enter the owner's structural gate, so they belong to the world's owner thread and to code that may change
    /// structure.
    /// </summary>
    private static readonly HashSet<string> StructuralEntityMembers =
        ["Set", "SetIfChanged", "Remove", "Destroy", "GetOrAdd", "Clone", "Disable", "Enable", "Link", "Unlink", "Run"];

    private static readonly HashSet<string> StructuralWorldMembers =
    [
        "CreateEntity", "CreateEntities", "Instantiate", "DestroyEntities", "DestroyAll", "EnsureEntityCapacity", "AddSingleton", "RemoveSingleton",
        "AddSystem", "RemoveSystem", "Update", "Capture", "Restore", "Dispose", "SetAll", "RemoveAll", "DisableAll", "EnableAll",
        "Save", "SaveWithout", "Load", "LoadKeeping", "SaveEntities", "SaveEntitiesWith", "SaveEntitiesWithout", "LoadEntities", "LoadEntitiesInto", "MoveEntities", "GroupBy", "RemoveTypes", "Reserve", "TrimExcess",
    ];

    /// <summary>
    /// Whether <paramref name="method"/> is one of the structural members of Entity or World, or a generated
    /// <c>Set…</c> / <c>GetOrAdd…</c> / <c>Remove…</c> member (ComponentGenerator) built on them.
    /// </summary>
    public static bool IsStructural(IMethodSymbol method)
    {
        var type = method.ContainingType;
        return Named(type, Entity) ? StructuralEntityMembers.Contains(method.Name)
            : Named(type, World) ? StructuralWorldMembers.Contains(method.Name)
            : IsGenerated(method) && IsGeneratedWriter(method);
    }

    /// <summary>The handle or world a call works on: the instance, or the receiver of an extension member.</summary>
    public static IOperation? Receiver(IInvocationOperation call) =>
        call.Instance ?? (call.TargetMethod.IsExtensionMethod && call.Arguments.Length > 0 ? call.Arguments[0].Value : null);

    /// <summary>Whether a call works on a World (its own member or a generated one), not on an entity.</summary>
    public static bool OnWorld(IInvocationOperation call) =>
        Named(Receiver(call)?.Type ?? call.TargetMethod.ContainingType, World);

    /// <summary>The tool name ComponentGenerator writes on its classes (<c>[GeneratedCode]</c>).</summary>
    public const string GeneratorName = "EasyEcs.Generator";

    /// <summary>
    /// A member ComponentGenerator wrote: its class carries <c>[GeneratedCode("EasyEcs.Generator", …)]</c>, in source or
    /// in a referenced assembly.
    /// </summary>
    public static bool IsGenerated(ISymbol member)
    {
        var type = member.ContainingType;
        while (type?.ContainingType != null) type = type.ContainingType; // an extension block's member: up to its class
        return type != null && type.GetAttributes().Any(IsGeneratorMark);
    }

    private static bool IsGeneratorMark(AttributeData a) =>
        Named(a.AttributeClass, "System.CodeDom.Compiler.GeneratedCodeAttribute")
        && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value as string == GeneratorName;

    /// <summary>
    /// Whether a member ComponentGenerator wrote changes structure: its writers (<c>SetX</c>, <c>RemoveX</c>,
    /// <c>GetOrAddX</c>, <c>DisableX</c>, <c>EnableX</c>) are the methods of its extension blocks, its reads the
    /// properties, <c>TryGetX</c> and <c>WriteX</c> (a value write, like <c>Get</c>). Decided by the symbol, not the name: a component named <c>Settings</c> is read
    /// through the property <c>entity.Settings</c>.
    /// </summary>
    public static bool IsGeneratedWriter(ISymbol member) =>
        member is IMethodSymbol { MethodKind: MethodKind.Ordinary, ContainingType.IsExtension: true } method
        && !method.Name.StartsWith("TryGet") && !method.Name.StartsWith("Write") && !method.Name.StartsWith("Links")
        && !method.Name.StartsWith("In"); // InZone(group): a QueryFilter's, no entity's

    /// <summary>
    /// Whether <paramref name="access"/> reads through a member ComponentGenerator wrote. The analyzer resolves it; the
    /// generator cannot (another generator's output is not in its compilation), so there the name decides: a
    /// component's name (<c>e.Position</c>), <c>Has</c> / <c>TryGet</c> / <c>Write</c> + a component, <c>Is</c> + a
    /// tag, <c>Is</c> + a component + <c>Disabled</c>, or a buffer element's name, <c>Has</c> / <c>Write</c> / <c>Links</c> + it.
    /// </summary>
    private static bool GeneratedRead(SemanticModel model, MemberAccessExpressionSyntax access, string member)
    {
        if (model.GetSymbolInfo(access).Symbol is { } symbol) return IsGenerated(symbol) && !IsGeneratedWriter(symbol);
        if (member.Length > "IsDisabled".Length && member.StartsWith("Is") && member.EndsWith("Disabled"))
            member = member.Substring(2, member.Length - 2 - "Disabled".Length);
        foreach (var (prefix, tag) in new[] { ("", false), ("Has", false), ("TryGet", false), ("Write", false), ("Links", false), ("Is", true) })
        {
            if (member.Length <= prefix.Length || !member.StartsWith(prefix)) continue;
            string type = member.Substring(prefix.Length);
            // A component (with fields, or a tag for Is), or for the name, Has and Write a buffer's element (a struct
            // that is neither component nor singleton).
            if (model.LookupNamespacesAndTypes(access.SpanStart, name: type).OfType<INamedTypeSymbol>().Any(t =>
                    t.TypeKind == TypeKind.Struct && (Implements(t, Component)
                        ? IsTag(t) == tag
                        : !tag && prefix != "TryGet" && !Implements(t, Singleton))))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The thread-starting calls (Task.Run, Task.ContinueWith, TaskFactory.StartNew, Parallel.*, ThreadPool.*, new Thread /
    /// Task / Timer) that run a delegate argument on another thread.
    /// </summary>
    public static bool StartsThread(IMethodSymbol method)
    {
        // By name first (most calls are of other types), then the full name.
        if (method.ContainingType is not { Name: "Task" or "TaskFactory" or "Parallel" or "ThreadPool" or "Thread" or "Timer" } type) return false;
        // ContinueWith runs its continuation on the thread pool when the task completes
        if (Named(type, "System.Threading.Tasks.Task")) return method.Name is "Run" or "ContinueWith" || method.MethodKind == MethodKind.Constructor;
        if (Named(type, "System.Threading.Tasks.Task<TResult>")) return method.Name == "ContinueWith" || method.MethodKind == MethodKind.Constructor;
        if (Named(type, "System.Threading.Tasks.TaskFactory") || Named(type, "System.Threading.Tasks.TaskFactory<TResult>")) return method.Name == "StartNew";
        if (Named(type, "System.Threading.Tasks.Parallel")) return true;
        if (Named(type, "System.Threading.ThreadPool"))
            return method.Name is "QueueUserWorkItem" or "UnsafeQueueUserWorkItem" or "RegisterWaitForSingleObject" or "UnsafeRegisterWaitForSingleObject";
        return (Named(type, "System.Threading.Thread") || Named(type, "System.Threading.Timer")) && method.MethodKind == MethodKind.Constructor;
    }

    /// <summary>Entity members that cannot change the world (no structural change, no access to the World).</summary>
    private static readonly HashSet<string> ReadOnlyEntityMembers =
        ["Id", "Version", "IsAlive", "Has", "Get", "Read", "TryGet", "TryGetRef", "IsDisabled"];

    /// <summary>
    /// Entity members that read the handle and its entity's record only, no component: <c>Get</c> writes, <c>Has</c>
    /// reads a table another system may change in the same stage.
    /// </summary>
    private static readonly HashSet<string> IdentityEntityMembers = ["Id", "Version", "IsAlive"];

    /// <summary>
    /// Whether every use of parameter <paramref name="name"/> in the method body is a read-only member access
    /// (<c>entity.Id</c>, <c>entity.Has&lt;T&gt;()</c>, ...) or an identity use (<see cref="IsIdentityUse"/>). Syntax
    /// first and conservative: any other use (passing the entity on, <c>entity.World</c>, a structural change) answers false.
    /// </summary>
    private static bool OnlyReads(MethodDeclarationSyntax method, string name, SemanticModel model) =>
        EveryUse(method, name, use => use.Parent is MemberAccessExpressionSyntax access && access.Expression == use
            && (ReadOnlyEntityMembers.Contains(MemberName(access)) || GeneratedRead(model, access, MemberName(access)))
            || IsIdentityUse(model, use));

    /// <summary>
    /// Whether the method uses its <c>Entity</c> parameters only as identities: <c>Id</c>, <c>Version</c>,
    /// <c>IsAlive</c>, <c>==</c> / <c>!=</c>, or converted to an <c>EntityId</c>. Such a method reaches no component
    /// through them, so its system is scheduled by its other parameters instead of running alone. Conservative: any
    /// other use (a method call on the entity, passing the handle on) answers false.
    /// </summary>
    public static bool OnlyIdentifies(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        (syntax, model) = WithBody(method, syntax, model);
        foreach (var p in method.Parameters)
            if (Classify(p) == ParamKind.Entity && !EveryUse(syntax, p.Name, use => IsIdentityUse(model, use)))
                return false;
        return true;
    }

    /// <summary>
    /// An identity use of an entity handle: <c>e.Id</c>, <c>e.Version</c>, <c>e.IsAlive</c>, an operand of Entity's or
    /// EntityId's <c>==</c> / <c>!=</c>, or the handle converted to an <c>EntityId</c> (its implicit conversion: a
    /// copy of the id, passed or stored).
    /// </summary>
    private static bool IsIdentityUse(SemanticModel model, IdentifierNameSyntax use)
    {
        if (use.Parent is MemberAccessExpressionSyntax access && access.Expression == use)
            return access.Name is IdentifierNameSyntax && IdentityEntityMembers.Contains(MemberName(access)); // properties: never called
        if (use.Parent is BinaryExpressionSyntax binary && binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
            return model.GetSymbolInfo(binary).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator, ContainingType: { } op }
                && (Named(op, Entity) || Named(op, EntityId));
        var type = model.GetTypeInfo(use);
        return Named(type.Type, Entity) && Named(type.ConvertedType, EntityId);
    }

    private static string MemberName(MemberAccessExpressionSyntax access) =>
        access.Name is GenericNameSyntax generic ? generic.Identifier.ValueText : access.Name.Identifier.ValueText;

    /// <summary>Whether <paramref name="test"/> holds for every use of <paramref name="name"/> in the method's body (false without one).</summary>
    private static bool EveryUse(MethodDeclarationSyntax method, string name, System.Func<IdentifierNameSyntax, bool> test)
    {
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return false;
        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (identifier.Identifier.ValueText == name && !test(identifier))
                return false;
        return true;
    }

    /// <summary>
    /// Whether the method only reads through <c>Maybe</c> parameter <paramref name="name"/>: every use is
    /// <c>name.HasValue</c> or a read of <c>name.Value</c> (or of a field of it). Syntax only and conservative: an
    /// assignment, increment, <c>ref</c>, a call on the value or passing the Maybe on counts as a write.
    /// </summary>
    public static bool OnlyReadsValue(MethodDeclarationSyntax method, string name)
    {
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return false;
        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != name) continue;
            if (identifier.Parent is not MemberAccessExpressionSyntax access || access.Expression != identifier) return false;
            if (access.Name.Identifier.ValueText == "HasValue") continue;
            if (access.Name.Identifier.ValueText != "Value") return false;
            SyntaxNode top = access; // name.Value.Field[i].Field: the outermost field or element read, unparenthesized
            while (top.Parent is MemberAccessExpressionSyntax { Expression: var e } && e == top
                   || top.Parent is ElementAccessExpressionSyntax { Expression: var x } && x == top
                   || top.Parent is ParenthesizedExpressionSyntax)
                top = top.Parent;
            switch (top.Parent)
            {
                case ArgumentSyntax { Parent: TupleExpressionSyntax }: // (t.Value.V, p.V) = ...: conservative
                case AssignmentExpressionSyntax assignment when assignment.Left == top:
                case PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax: // ++, -- (and !, - on a read: conservative)
                case RefExpressionSyntax:
                case ArgumentSyntax { RefKindKeyword.ValueText: "ref" or "out" }:
                case InvocationExpressionSyntax:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Methods that change the collection they are called on (BCL lists, sets, maps, queues, stacks, linked lists,
    /// arrays). A call to one of them through an 'in' component writes data the parameter promised to only read.
    /// </summary>
    private static readonly HashSet<string> Mutators =
    [
        "Add", "AddRange", "AddFirst", "AddLast", "AddOrUpdate", "GetOrAdd", "TryAdd", "Insert", "InsertRange",
        "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "RemoveFirst", "RemoveLast", "RemoveWhere", "TryRemove",
        "Clear", "Push", "Pop", "TryPop", "Enqueue", "Dequeue", "TryDequeue", "Sort", "Reverse", "Fill", "SetValue",
        "UnionWith", "IntersectWith", "ExceptWith", "SymmetricExceptWith", "EnsureCapacity", "TrimExcess",
    ];

    /// <summary>
    /// The indexed component a write (or a ref local) reaches by ref: a query item's <c>ComponentN</c>, or a <c>ref</c>
    /// / <c>Span</c> parameter of a job's <c>Execute</c> (IForEach, IForEachEntity, IForEachBatch); null otherwise. Refs
    /// from Entity calls are reported at the call, [ForEach] parameters at the parameter.
    /// </summary>
    public static ITypeSymbol? IndexedRoot(IOperation? target)
    {
        while (true)
        {
            switch (target)
            {
                case IFieldReferenceOperation f: target = f.Instance; break;
                case IConversionOperation c: target = c.Operand; break;
                case IPropertyReferenceOperation { Property.IsIndexer: true } indexer: target = indexer.Instance; break; // span[i]
                case IPropertyReferenceOperation { Property: { ReturnsByRef: true } property } when IsItemComponent(property):
                    return IsIndexed(property.Type) ? property.Type : null;
                case IParameterReferenceOperation { Parameter: var p } when IsJobParameter(p):
                    var type = p.RefKind == RefKind.Ref ? p.Type : SpanElement(p.Type);
                    return type != null && IsIndexed(type) ? type : null;
                default: return null;
            }
        }
    }

    /// <summary>A writable parameter (<c>ref</c> or <c>Span</c>) of a job's Execute (a type implementing EasyEcs.IForEach*).</summary>
    private static bool IsJobParameter(IParameterSymbol p) =>
        (p.RefKind == RefKind.Ref || Named(p.Type.OriginalDefinition, "System.Span<T>"))
        && p.ContainingSymbol is IMethodSymbol { Name: "Execute", ContainingType: { } job }
        && job.AllInterfaces.Any(IsJob);

    /// <summary>
    /// The [ForEach] parameter a write reaches through a reference held by a read-only parameter: an 'in' component or
    /// singleton, or a <c>ReadOnlySpan&lt;T&gt;</c> of a component. <paramref name="target"/> is the written location (an assignment
    /// target) or the receiver of a mutating call; null when the write goes anywhere else.
    /// </summary>
    public static IParameterSymbol? ReadOnlyRoot(IOperation? target)
    {
        // The compiler rejects writes to the struct itself, so any write rooted at a read-only component that
        // compiles goes through a reference (a class field, a collection) the component holds.
        while (true)
        {
            switch (target)
            {
                case IFieldReferenceOperation f: target = f.Instance; break;
                case IPropertyReferenceOperation pr: target = pr.Instance; break;
                case IArrayElementReferenceOperation a: target = a.ArrayReference; break;
                case IConversionOperation c: target = c.Operand; break;
                case IParameterReferenceOperation { Parameter: var p }:
                    return p.ContainingSymbol is IMethodSymbol m && IsForEach(m)
                        && (p.RefKind is RefKind.In or RefKind.RefReadOnlyParameter
                            && Classify(p) is ParamKind.Component or ParamKind.Singleton
                            || p.RefKind == RefKind.None && Classify(p) == ParamKind.ComponentSpan
                            && p.Type.Name == "ReadOnlySpan") ? p : null;
                default: return null;
            }
        }
    }

    /// <summary>The receiver of a call that changes a collection (see <see cref="Mutators"/>), else null.</summary>
    public static IOperation? MutatedReceiver(IInvocationOperation call) =>
        call.Instance is { Type: { IsReferenceType: true } type } instance && Mutators.Contains(call.TargetMethod.Name) && !Immutable(type)
            ? instance : null;

    /// <summary>
    /// Types whose "mutators" return a new value (<c>string.Remove</c>, <c>ImmutableList.Add</c>), and the world itself
    /// (its RemoveAll is a structural change, EECS013's business).
    /// </summary>
    private static bool Immutable(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_String || Named(type, World)
        || Named(type.ContainingNamespace, "System.Collections.Immutable") || Named(type.ContainingNamespace, "System.Collections.Frozen");

    public static bool IsForEach(IMethodSymbol method) =>
        method.GetAttributes().Any(a => Named(a.AttributeClass, ForEachAttribute));
}
