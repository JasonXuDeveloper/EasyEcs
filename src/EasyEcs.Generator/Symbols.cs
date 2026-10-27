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
    public const string ForEntityAttribute = "EasyEcs.ForEntityAttribute";
    public const string ConsumeAttribute = "EasyEcs.ConsumeAttribute";
    public const string NeighborAttribute = "EasyEcs.NeighborAttribute";
    public const string ForEachRecordAttribute = "EasyEcs.ForEachRecordAttribute";
    public const string UpdateIntervalAttribute = "EasyEcs.UpdateIntervalAttribute";
    public const string WithAttribute = "EasyEcs.WithAttribute<T>";
    public const string WithoutAttribute = "EasyEcs.WithoutAttribute<T>";
    public const string GroupWith = "EasyEcs.ISystemGroupWith<T>";
    public const string GroupWithout = "EasyEcs.ISystemGroupWithout<T>";
    public const string ChangedAttribute = "EasyEcs.ChangedAttribute<T>";
    public const string RemovedAttribute = "EasyEcs.RemovedAttribute<T>";
    public const string EcsSystemAttribute = "EasyEcs.EcsSystemAttribute";
    public const string SystemHandle = "EasyEcs.SystemHandle";
    public const string Component = "EasyEcs.IComponent";
    public const string Singleton = "EasyEcs.ISingletonComponent";
    public const string Entity = "EasyEcs.Entity";
    public const string EntitySpan = "EasyEcs.EntitySpan";
    public const string World = "EasyEcs.World";
    public const string ComponentSet = "EasyEcs.ComponentSet";
    public const string DynamicBuffer = "EasyEcs.DynamicBuffer<T>";
    public const string TrackChanges = "EasyEcs.ITracked";
    public const string Indexed = "EasyEcs.IIndexed<TKey>";
    public const string UniqueIndexed = "EasyEcs.IUniqueIndexed<TKey>";
    public const string DirectIndexed = "EasyEcs.IDirectIndexed<TKey>";
    public const string Group = "EasyEcs.IGroup";
    public const string Relation = "EasyEcs.IRelation";
    public const string GroupByAttribute = "EasyEcs.GroupByAttribute";
    public const string WhenTargetDestroyedAttribute = "EasyEcs.WhenTargetDestroyedAttribute";
    public const string NotSavedAttribute = "EasyEcs.NotSavedAttribute";
    public const string AccumulateAttribute = "EasyEcs.AccumulateAttribute";
    public const string Cleanup = "EasyEcs.ICleanupComponent";
    public const string EntityId = "EasyEcs.EntityId";
    public const string TileShape = "EasyEcs.ITileShape";
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
    /// Whether the instance fields of <paramref name="type"/> come from more than one of its parts, so their order in
    /// memory is not defined. A part without fields (a generated [LookupKey] or [Sum] part, a part of methods) does not
    /// count: the generator sees the type without the generated parts and the analyzer with them, and both must agree.
    /// </summary>
    public static bool FieldsInSeveralParts(INamedTypeSymbol type)
    {
        if (type.DeclaringSyntaxReferences.Length <= 1) return false;
        int parts = 0;
        foreach (var reference in type.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is TypeDeclarationSyntax part && DeclaresFields(part) && ++parts > 1) return true;
        return false;
    }

    private static bool DeclaresFields(TypeDeclarationSyntax part)
    {
        if (part.ParameterList is { Parameters.Count: > 0 }) return true; // primary constructor parameters may become fields
        foreach (var member in part.Members)
            switch (member)
            {
                case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;
                case PropertyDeclarationSyntax { AccessorList: { } accessors } property
                    when !property.Modifiers.Any(SyntaxKind.StaticKeyword) && property.ExpressionBody == null
                         && accessors.Accessors.All(a => a.Body == null && a.ExpressionBody == null):
                    return true; // an auto-property has a backing field
            }
        return false;
    }

    /// <summary>
    /// A position where a type name of <paramref name="node"/>'s member body (or initializer) binds without the member:
    /// inside the braces of the type holding it, or for top-level statements the end of the file (its usings and the
    /// global namespace, not the Main the statements make); -1 outside a member body (a declaration's own types bind
    /// alone already).
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
                case GlobalStatementSyntax { Parent: CompilationUnitSyntax unit }:
                    return unit.EndOfFileToken.SpanStart; // after every member: the file's usings, not the Main body

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

    /// <summary>A system: a type marked [EcsSystem] (valid only as a ref struct, <see cref="SystemProblem"/>).</summary>
    public static bool IsSystem(INamedTypeSymbol? type) =>
        type != null && type.OriginalDefinition.GetAttributes().Any(static a => Named(a.AttributeClass, EcsSystemAttribute));

    /// <summary>
    /// Why an [EcsSystem] type cannot be a system (EECS074), or null: it is no ref struct (a class or a struct could
    /// be instantiated, stored and captured; a system is a name for static functions).
    /// </summary>
    public static string? SystemProblem(INamedTypeSymbol type)
    {
        if (!IsSystem(type)) return null;
        if (type.TypeKind != TypeKind.Struct || !type.IsRefLikeType)
            return type.TypeKind == TypeKind.Struct ? "is a struct, which can be instantiated, stored and boxed: make it a ref struct"
                : $"is {(type.IsRecord ? "a record" : type.TypeKind == TypeKind.Interface ? "an interface" : "a class")}: a system is a ref struct of static members";
        return null;
    }

    /// <summary>A system the generator writes code for: [EcsSystem] on a ref struct.</summary>
    public static bool IsValidSystem(INamedTypeSymbol? type) => IsSystem(type) && SystemProblem(type!) == null;

    /// <summary>
    /// A system the generator writes code for: valid (<see cref="IsValidSystem"/>) and nameable by code outside it, its
    /// code struct and its set's (EECS022 otherwise): UnsafeAccessor reaches a ref struct's private methods only by its
    /// type, never by name.
    /// </summary>
    public static bool GeneratesSystem(INamedTypeSymbol? type) => IsValidSystem(type) && IsVisible(type!);

    /// <summary>The kinds of hook a system method may be: OnCreate, OnUpdate, OnDestroy.</summary>
    public enum HookKind { None, Create, Update, Destroy }

    /// <summary>The hook a method's name makes it in a system, or <see cref="HookKind.None"/> for any other name.</summary>
    public static HookKind HookOfName(string name) => name switch
    {
        "OnCreate" => HookKind.Create,
        "OnUpdate" => HookKind.Update,
        "OnDestroy" => HookKind.Destroy,
        _ => HookKind.None,
    };

    /// <summary>
    /// The hook <paramref name="method"/> is: a method of an [EcsSystem] named <c>OnCreate</c>, <c>OnUpdate</c> or
    /// <c>OnDestroy</c> (the name alone, no attribute), else <see cref="HookKind.None"/>.
    /// </summary>
    public static HookKind Hook(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary && HookOfName(method.Name) is var kind and not HookKind.None && IsSystem(method.ContainingType)
            ? kind
            : HookKind.None;

    /// <summary>
    /// Why hook method <paramref name="method"/> cannot be called (EECS076, or EECS075 for an instance hook), or null: it
    /// is an instance or generic method, does not return void, takes something but one World and one SystemHandle by
    /// value, or is the second method of its system with its name (an overload).
    /// </summary>
    public static string? HookProblem(IMethodSymbol method)
    {
        var kind = Hook(method);
        if (kind == HookKind.None) return null;
        if (!method.IsStatic) return "is an instance method: make it static (a system has no instance)";
        if (method.IsGenericMethod) return "is generic, and nothing gives it type arguments: make the system generic instead";
        if (!method.ReturnsVoid) return "must return void";
        if (!HookParameters(method, out var parameter))
            return $"takes '{parameter}': a hook takes the World, the SystemHandle, both (once each, by value) or neither";

        foreach (var other in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(other, method)) break; // the first one of a name is the hook
            return $"is a second '{method.Name}' of '{method.ContainingType.Name}': one method per hook, so rename this overload";
        }

        return null;
    }

    /// <summary>Whether the method takes what a hook takes: the World, the SystemHandle, both or neither, by value; else the first other parameter.</summary>
    public static bool HookParameters(IMethodSymbol method, out string? parameter)
    {
        bool world = false, handle = false;
        foreach (var p in method.Parameters)
        {
            bool isWorld = Named(p.Type, World), isHandle = Named(p.Type, SystemHandle);
            if (p.RefKind != RefKind.None || !isWorld && !isHandle || isWorld && world || isHandle && handle)
            {
                parameter = p.Name;
                return false;
            }

            world |= isWorld;
            handle |= isHandle;
        }

        parameter = null;
        return true;
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
        IndexedKey(type, out _) != null || IsGrouped(type)
        || type is INamedTypeSymbol { IsGenericType: true } buffer && Named(buffer.OriginalDefinition, "EasyEcs.DynamicBuffer<T>")
        && Implements(buffer.TypeArguments[0], "EasyEcs.IRelation");

    /// <summary>
    /// The key type of indexed component <paramref name="type"/>, or null: by the interfaces it implements
    /// (<c>IUniqueIndexed&lt;TKey&gt;</c> first, then <c>IIndexed&lt;TKey&gt;</c>), else by its <c>[LookupKey]</c> /
    /// <c>[UniqueKey]</c> when the generator writes that key (<see cref="Parts"/>). Every generator asks this, never the
    /// interfaces alone: the interface a generated part adds is not in another generator's compilation.
    /// <paramref name="unique"/>: the key is unique.
    /// </summary>
    public static ITypeSymbol? IndexedKey(ITypeSymbol type, out bool unique)
    {
        if (InterfaceKey(type, out unique) is { } declared) return declared;
        if (type is not INamedTypeSymbol named || !Parts.Declares(named, Parts.Key) && !Parts.Declares(named, Parts.UniqueKey)) return null;
        if (Parts.Of(named) is not { WritesKey: true } parts) return null;
        unique = parts.KeyOf!.Unique;
        return parts.KeyType;
    }

    /// <summary>The key of the index interfaces <paramref name="type"/> implements (<c>IUniqueIndexed&lt;TKey&gt;</c> first), or null.</summary>
    public static ITypeSymbol? InterfaceKey(ITypeSymbol type, out bool unique)
    {
        ITypeSymbol? key = null;
        unique = false;
        foreach (var face in type.AllInterfaces)
        {
            if (Named(face.OriginalDefinition, UniqueIndexed))
            {
                unique = true;
                return face.TypeArguments[0];
            }

            if (key == null && Named(face.OriginalDefinition, Indexed)) key = face.TypeArguments[0];
        }

        return key;
    }

    /// <summary>
    /// Whether the world groups tables by component <paramref name="type"/>: it implements <c>IGroup</c>, or the
    /// generator writes its <c>[Group]</c> (see <see cref="IndexedKey"/>).
    /// </summary>
    public static bool IsGrouped(ITypeSymbol type) =>
        Implements(type, Group) || type is INamedTypeSymbol named && (Parts.Declares(named, Parts.Group) || Parts.Declares(named, Parts.GroupBy))
        && Parts.Of(named) is { WritesGroup: true };

    /// <summary>
    /// Why <paramref name="type"/> cannot take the world setup <paramref name="attribute"/> declares, or null: [GroupBy]
    /// on a grouped component (<see cref="IsGrouped"/>), [WhenTargetDestroyed] on a relation or a component indexed by
    /// an <c>EntityId</c> (<see cref="IndexedKey"/>), [NotSaved] on a component or a singleton, [Accumulate] on a
    /// singleton that combines with itself (<c>IReduction&lt;T&gt;</c>, by attributes or by hand). The generator registers
    /// only what this accepts; EECS068 reports the rest.
    /// </summary>
    public static string? SetupProblem(INamedTypeSymbol type, AttributeData attribute)
    {
        if (Named(attribute.AttributeClass, AccumulateAttribute))
            return Implements(type, Singleton) && (Parts.Of(type) is { Fields.Count: > 0 } || ReducesItself(type)) ? null
                : "is not a singleton that combines with itself: [Accumulate] makes a Reduce<T> pass combine its result into a singleton that implements IReduction<T> (by [Sum], [Min], ... or by hand)";
        if (Named(attribute.AttributeClass, NotSavedAttribute))
            return Implements(type, Component) || Implements(type, Singleton) ? null
                : "is neither a component nor a singleton: [NotSaved] leaves a component or a singleton out of saves";
        if (Named(attribute.AttributeClass, GroupByAttribute))
            return Implements(type, Component) && IsGrouped(type) ? null
                : "is not a grouped component: [GroupBy] splits tables by the member it marks (an int or an enum of int of a partial component) or by the Group of a hand-written IGroup";
        if (Implements(type, Relation)) return null;
        return Implements(type, Component) && IndexedKey(type, out bool unique) is { } key && Named(key, EntityId) && !unique ? null
            : "is neither a relation (IRelation) nor a component indexed by an EntityId: [WhenTargetDestroyed] decides what happens to links to a destroyed entity";
    }

    /// <summary>Whether <paramref name="type"/> is <c>IEquatable</c> of itself: what an index key must be.</summary>
    public static bool IsEquatable(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol parameter)
            return parameter.ConstraintTypes.Any(c => c is INamedTypeSymbol { IsGenericType: true } e && Named(e.OriginalDefinition, "System.IEquatable<T>")
                                                      && SymbolEqualityComparer.Default.Equals(e.TypeArguments[0], type));
        foreach (var face in type.AllInterfaces)
            if (Named(face.OriginalDefinition, "System.IEquatable<T>") && SymbolEqualityComparer.Default.Equals(face.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.None), type.WithNullableAnnotation(NullableAnnotation.None)))
                return true;
        return false;
    }

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
        if (!IsVisible(method.ContainingType)) return method.ContainingType; // a runner's entry point extends it, a system's code names it
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
        /// <c>Maybe&lt;T&gt;</c>: a component the entity may lack, a write (stamped where present, as a <c>ref T</c> is
        /// where visited); <c>ReadOnlyMaybe&lt;T&gt;</c>: the same, a read. The type declares the access, as <c>ref</c> /
        /// <c>in</c> do: EECS025 reports a <c>Maybe&lt;T&gt;</c> the method only reads.
        /// </summary>
        Optional,
        /// <summary><c>Mut&lt;T&gt;</c>: a queried component written only where the method writes it (stamped per write).</summary>
        Mutable,
        /// <summary>
        /// <c>Reduce&lt;T&gt;</c>: a partial result of singleton <c>T</c> per chunk of rows (per pass on one thread), combined
        /// into the singleton when the pass ends: a write of the singleton, once per pass.
        /// </summary>
        Reduction,
        /// <summary><c>Scratch&lt;T&gt;</c>: the method's working memory on the thread running it, kept between rows and passes.</summary>
        Scratch,
        /// <summary><c>Lookup&lt;T&gt;</c>: read-only access to component <c>T</c> of any entity (a read of <c>T</c>).</summary>
        Lookup,
        /// <summary>
        /// <c>Tile&lt;TCell, TTile&gt;</c>: the entity's <c>DynamicBuffer&lt;TCell&gt;</c> with its neighbors' in the grid of
        /// <c>TTile</c> (an <c>ITile&lt;TKey&gt;</c>): a read of both, and of TTile's index.
        /// </summary>
        Tile,
        /// <summary>
        /// <c>Emit&lt;T&gt;</c>: records appended to a list per chunk of rows (per pass on one thread), joined in chunk order
        /// into singleton <c>Emitted&lt;T&gt;</c> when the pass ends: a write of the singleton, once per pass.
        /// </summary>
        Emit,
        /// <summary>
        /// A <c>[View]</c> ref struct, by value: its <c>ref</c> fields are written components of the pass, its
        /// <c>ref readonly</c> fields read ones (<see cref="Views"/>); the job builds it over the row.
        /// </summary>
        View,
        /// <summary>
        /// A value the caller passes to the entry point of a [ForEach] method outside a system (<c>ref int dead</c>, a
        /// configuration): any type but a component, a singleton or the framework's own, by value, <c>ref</c> or <c>in</c>.
        /// </summary>
        Context,
        /// <summary>
        /// <c>[Neighbor] in T</c> (a component of the other entity of a pair, read) or <c>[Neighbor] Entity</c> (the other
        /// entity): the method runs per pair (PairForm).
        /// </summary>
        Neighbor,
        /// The first parameter of a [ForEachRecord] method, <c>in T</c>: the record of <c>Emitted&lt;T&gt;</c> it handles
        /// (<see cref="RecordType"/>). Never returned by <see cref="Classify(IParameterSymbol)"/>: its place makes it one.
        /// </summary>
        Record,
    }

    /// <summary>
    /// <see cref="Classify(IParameterSymbol)"/> for a method of <paramref name="outside"/> a system: there, what is
    /// not a component, a singleton or the framework's own is a <see cref="ParamKind.Context"/> the caller passes.
    /// </summary>
    public static ParamKind Classify(IParameterSymbol p, bool outside)
    {
        var kind = Classify(p);
        if (kind != ParamKind.Invalid || !outside || IsNeighbor(p) ||p.RefKind is not (RefKind.None or RefKind.Ref or RefKind.In)) return kind;
        var element = SpanElement(p.Type) ?? MaybeElement(p.Type) ?? MutElement(p.Type) ?? p.Type;
        if (IsA(element, Component) || IsA(element, Singleton) || ReduceElement(p.Type) != null) return ParamKind.Invalid; // a component by value
        // A span or other ref struct goes into the job by value: a job holds no ref field to a ref struct.
        if (p.Type.IsRefLikeType && p.RefKind != RefKind.None) return ParamKind.Invalid;
        return Named(p.Type, Entity) || Named(p.Type, EntitySpan) || Named(p.Type, World) ? ParamKind.Invalid : ParamKind.Context;
    }

    /// <summary>
    /// A [ForEach] method outside a system: static, in a type that is no system. The generator writes its job and an
    /// entry point taking the world (and the caller's context parameters), a static extension member of the type.
    /// </summary>
    public static bool IsRunner(IMethodSymbol method) => !IsSystem(method.ContainingType) || IsForEntity(method);

    /// <summary>
    /// A [ForEntity] method: static, it runs on one entity through its entry point, a static extension member of its
    /// type (in a system too) that takes the entity, then the tick with [Changed&lt;T&gt;] / [Removed&lt;T&gt;], then
    /// the caller's context parameters, and runs the method's job with <c>entity.Run</c>.
    /// </summary>
    public static bool IsForEntity(IMethodSymbol method) =>
        method.GetAttributes().Any(a => Named(a.AttributeClass, ForEntityAttribute));

    /// <summary>How a diagnostic names a generated job's method: "[ForEach] method 'Move'" or "[ForEntity] method 'Heal'".</summary>
    public static string Describe(IMethodSymbol method) =>
        (IsForEntity(method) ? "[ForEntity] method '" : IsForEachRecord(method) ? "[ForEachRecord] method '" : "[ForEach] method '") + method.Name + "'";

    /// <summary>
    /// A [ForEachRecord] method: static, its first parameter the record (<c>in T</c>) it handles once per record of
    /// <c>Emitted&lt;T&gt;</c>, in order, before the records are cleared; in a system it runs in declaration order with the
    /// [ForEach] methods, elsewhere through an entry point taking the world and the caller's values.
    /// </summary>
    public static bool IsForEachRecord(IMethodSymbol method) =>
        method.GetAttributes().Any(a => Named(a.AttributeClass, ForEachRecordAttribute));

    /// <summary>
    /// The record a [ForEachRecord] method handles: its first parameter's type, taken by <c>in</c> (or <c>ref readonly</c>),
    /// a struct that is no component or singleton and no ref struct; else null.
    /// </summary>
    public static ITypeSymbol? RecordType(IMethodSymbol method) =>
        method.Parameters.Length > 0 && method.Parameters[0] is { RefKind: RefKind.In or RefKind.RefReadOnlyParameter, Type: { IsValueType: true, IsRefLikeType: false } type }
        && type.TypeKind != TypeKind.TypeParameter && !IsA(type, Component) && !IsA(type, Singleton) && !IsFramework(type)
            ? type
            : null;

    /// <summary>A type of EasyEcs itself (Entity, World, Lookup&lt;T&gt;, ...): never a record.</summary>
    private static bool IsFramework(ITypeSymbol type) => Named(type.ContainingNamespace, "EasyEcs");

    /// <summary>
    /// Why a [ForEachRecord] method cannot be generated (EECS108), or null: it also has [ForEach] or [ForEntity], is an
    /// instance or generic method, does not take its record first by <c>in</c>, has a filter or [Consume], or takes what
    /// a pass over records has none of (a component, a Maybe, a Reduce, the Entity, ...) or an <c>Emit&lt;T&gt;</c> of its
    /// own record (cleared unhandled when the pass ends).
    /// </summary>
    public static string? RecordProblem(IMethodSymbol method)
    {
        if (IsForEach(method) || IsForEntity(method))
            return "also has [ForEach] or [ForEntity]: a method handles records, the rows of a pass or one entity";
        if (!method.IsStatic) return "is an instance method: make it static";
        if (method.IsGenericMethod) return "is generic: its pass handles the records of one type";
        if (RecordType(method) is not { } record)
            return "does not take its record first by 'in' ('in T record', where T is a struct that is no component or singleton)";
        if (AttributeFilters(method).Any() || IsConsume(method))
            return "has a filter or [Consume]: a record is no entity, and the pass clears the records it handled";
        bool outside = !IsSystem(method.ContainingType);
        foreach (var p in method.Parameters.Skip(1))
        {
            switch (Classify(p, outside))
            {
                case ParamKind.Singleton or ParamKind.Lookup or ParamKind.World or ParamKind.Context:
                    continue;
                case ParamKind.Emit when !SymbolEqualityComparer.Default.Equals(EmitElement(p.Type), record):
                    continue;
                case ParamKind.Emit:
                    return $"takes '{p.Name}', an Emit<{record.Name}> of its own records: the pass clears Emitted<{record.Name}> when it ends, so they would be cleared unhandled";
            }

            return $"takes '{p.Name}', which a pass over records has none of: it takes the record, singletons, Lookup<T>, Emit<T> of other records, the World{(outside ? " and values the caller passes" : "")}";
        }

        return null;
    }

    /// <summary>
    /// Why a [ForEntity] method cannot be generated (EECS081), or null: it is an instance method, also has [ForEach],
    /// or takes a parameter that has no meaning for one entity (a component span, an EntitySpan, a Reduce, a Scratch).
    /// Its other problems are those of a [ForEach] method outside a system (EECS002, EECS003, EECS021, EECS022).
    /// </summary>
    public static string? ForEntityProblem(IMethodSymbol method)
    {
        if (method.GetAttributes().Any(a => Named(a.AttributeClass, ForEachAttribute)))
            return "also has [ForEach]: a method is either a pass over the world or a run on one entity";
        if (!method.IsStatic) return "is an instance method: make it static (its entry point takes the entity)";
        foreach (var p in method.Parameters)
        {
            string? what = Classify(p) switch
            {
                ParamKind.ComponentSpan => "a component span, which belongs to a batch method",
                ParamKind.Entities => "an EntitySpan, which belongs to a batch method",
                ParamKind.Reduction => "a Reduce, the partial result of a pass",
                ParamKind.Scratch => "a Scratch, the working memory of a pass's thread",
                _ => null,
            };
            if (what != null) return $"takes '{p.Name}', {what}: one entity has none (take the singleton by 'ref', or a value the caller passes)";
        }

        return null;
    }

    /// <summary>The method has [Consume]: its entities are destroyed when the pass that visited them ends.</summary>
    public static bool IsConsume(IMethodSymbol method) =>
        method.GetAttributes().Any(a => Named(a.AttributeClass, ConsumeAttribute));

    /// <summary>Why a method's [Consume] cannot apply (EECS085), or null: it has no [ForEach], or it visits a slice of its rows.</summary>
    public static string? ConsumeProblem(IMethodSymbol method)
    {
        if (!IsConsume(method)) return null;
        if (!method.GetAttributes().Any(a => Named(a.AttributeClass, ForEachAttribute)))
            return "it has no [ForEach]: only a pass has rows to consume";
        if (Slices(method) > 1)
            return "it visits a slice of its rows per run, and destroying them moves the rows of the other slices (events would wait for many runs)";
        return null;
    }

    public const string GenericSystemCodeAttribute = "EasyEcs.GenericSystemCodeAttribute";

    /// <summary>
    /// The public code type (open) that <paramref name="system"/>'s assembly names for the generic system with
    /// [assembly: GenericSystemCode], or null: an assembly built without it, or a system that is not public.
    /// </summary>
    public static INamedTypeSymbol? GenericSystemCode(INamedTypeSymbol system)
    {
        var definition = system.OriginalDefinition;
        foreach (var attribute in system.ContainingAssembly.GetAttributes())
            if (Named(attribute.AttributeClass, GenericSystemCodeAttribute) && attribute.ConstructorArguments.Length == 2
                && attribute.ConstructorArguments[0].Value is INamedTypeSymbol named
                && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, definition)
                && attribute.ConstructorArguments[1].Value is INamedTypeSymbol code)
                return code.OriginalDefinition;
        return null;
    }

    /// <summary>
    /// The [UpdateInterval(n, Phase = p)] of a system type (its own, not a base's), or null without one. Invalid values
    /// are <see cref="ScheduleProblem"/>'s.
    /// </summary>
    public static (int Interval, int Phase, AttributeData Attribute)? Schedule(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (!Named(attribute.AttributeClass, UpdateIntervalAttribute)) continue;
            int interval = attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value ? value : 0, phase = 0;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "Phase" && named.Value.Value is int p) phase = p;
            return (interval, phase, attribute);
        }

        return null;
    }

    /// <summary>
    /// Why a type's [UpdateInterval] cannot apply (EECS083), or null: the type is no system, the interval is below 1,
    /// or the phase is outside 0 to interval - 1.
    /// </summary>
    public static string? ScheduleProblem(INamedTypeSymbol type)
    {
        if (Schedule(type) is not var (interval, phase, _)) return null;
        if (!IsSystem(type)) return "it is no [EcsSystem], so nothing runs it on a schedule";
        if (interval < 1) return $"its interval {interval} must be 1 or more";
        if (phase < 0 || phase >= interval) return $"its phase {phase} must be 0 to {interval - 1} (the interval - 1)";
        return null;
    }

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
                || !SymbolEqualityComparer.Default.Equals(m, method) && (IsGeneratedJob(m) || IsForEachRecord(m)) && EntryPoint(m).SequenceEqual(expected)));
    }

    private static List<(string, RefKind)> EntryPoint(IMethodSymbol method)
    {
        var parameters = new List<(string, RefKind)> { (IsForEntity(method) ? Entity : World, RefKind.None) };
        if (TracksSince(method)) parameters.Add(("ulong", RefKind.None));
        if (Slices(method) > 1) parameters.Add(("int", RefKind.None));
        foreach (var p in method.Parameters.Skip(IsForEachRecord(method) ? 1 : 0)) // a record method's record is no caller's value
            if (Classify(p, true) == ParamKind.Context) parameters.Add((p.Type.ToDisplayString(), p.RefKind));
        return parameters;
    }

    /// <summary>The method has [Changed&lt;T&gt;] or [Removed&lt;T&gt;] filters: outside a system its entry point takes the tick.</summary>
    public static bool TracksSince(IMethodSymbol method) => AttributeFilters(method).Any(f =>
        f.Kind is FilterKind.Changed or FilterKind.Removed or FilterKind.ChangedAny or FilterKind.RemovedAny);

    /// <summary>
    /// The kind of a generated "any of these" attribute class (<c>[WithAny&lt;T1..Tn&gt;]</c>, <c>[ChangedAny&lt;T1..Tn&gt;]</c>,
    /// <c>[RemovedAny&lt;T1..Tn&gt;]</c>), else null.
    /// </summary>
    public static FilterKind? AnyFilter(INamedTypeSymbol attribute) =>
        attribute.Arity < 2 || !Named(attribute.ContainingNamespace, "EasyEcs") ? null
        : attribute.Name switch
        {
            "WithAnyAttribute" => FilterKind.WithAny,
            "ChangedAnyAttribute" => FilterKind.ChangedAny,
            "RemovedAnyAttribute" => FilterKind.RemovedAny,
            _ => null,
        };

    /// <summary>What a filter of a [ForEach] method (or of its system group) does with its component.</summary>
    public enum FilterKind { With, Without, WithAny, Changed, Removed, ChangedAny, RemovedAny }

    /// <summary>
    /// The filter attributes of <paramref name="symbol"/> (a [ForEach] method or a system group), one entry per filtered
    /// type (<c>[WithAny&lt;A, B&gt;]</c> gives two), in declaration order: what the generator builds a query filter
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
                    yield return (any, argument, attribute);
                continue;
            }

            var from = cls.ConstructedFrom;
            FilterKind? kind = Named(from, WithAttribute) ? FilterKind.With
                : Named(from, WithoutAttribute) ? FilterKind.Without
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
    /// for a group's; none with <paramref name="groups"/> false: a lambda pass takes no group's filters). EECS016, EECS026 and
    /// EECS049 read it.
    /// </summary>
    public static List<Requirement> Requirements(IMethodSymbol method, System.Threading.CancellationToken token, bool groups = true)
    {
        var all = new List<Requirement>();
        foreach (var (kind, type, attribute) in AttributeFilters(method))
            if (kind is FilterKind.With or FilterKind.Changed or FilterKind.Without or FilterKind.Removed && type.TypeKind != TypeKind.Error)
                all.Add(new Requirement(kind, type, FilterText(kind, type, true), attribute.ApplicationSyntaxReference?.GetSyntax(token).GetLocation(), false));
        foreach (var p in method.Parameters)
            if (Classify(p) is ParamKind.Component or ParamKind.ComponentSpan or ParamKind.Mutable && QueriedComponent(p) is { TypeKind: not TypeKind.Error } component)
                all.Add(new Requirement(FilterKind.With, component, $"parameter '{p.Name}'", p.Locations.FirstOrDefault(), true));
            else if (Classify(p) == ParamKind.View && Views.IsValid(p.Type))
                foreach (var field in Views.Fields((INamedTypeSymbol)p.Type))
                    all.Add(new Requirement(FilterKind.With, field.Component, $"field '{field.Symbol.Name}' of view '{p.Name}'", p.Locations.FirstOrDefault(), true));
        if (groups)
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

    /// <summary>[ForEach(FusionMode = FusionMode.X)]: 0 Auto, 1 Require, 2 Off (the values of EasyEcs.FusionMode).</summary>
    public static int Fusion(IMethodSymbol method) => ForEachArgument(method, "FusionMode", 0);

    /// <summary>[ForEach(SimdMode = SimdMode.X)]: <see cref="SimdAuto"/>, <see cref="SimdRequire"/> or <see cref="SimdOff"/>.</summary>
    public static int Simd(IMethodSymbol method) => ForEachArgument(method, "SimdMode", SimdAuto);

    /// <summary>The values of EasyEcs.SimdMode.</summary>
    public const int SimdAuto = 0, SimdRequire = 1, SimdOff = 2;

    /// <summary>[ForEach(FloatMode = FloatMode.Fast)]: a vector loop may reorder float sums.</summary>
    public static bool FastFloats(IMethodSymbol method) => ForEachArgument(method, "FloatMode", 0) == 1;

    private static int ForEachArgument(IMethodSymbol method, string name, int otherwise)
    {
        foreach (var attribute in method.GetAttributes())
            if (Named(attribute.AttributeClass, ForEachAttribute))
                foreach (var named in attribute.NamedArguments)
                    if (named.Key == name && named.Value.Value is int value) return value;
        return otherwise;
    }

    /// <summary>
    /// Why <paramref name="reduction"/>, a <c>Reduce&lt;T&gt;</c> parameter of <paramref name="method"/>, would lose its
    /// result (EECS057), or null: the method takes T by <c>ref</c>, or reduces into T in an earlier parameter, or an
    /// earlier [ForEach] method of its system does (the later pass replaces the earlier one's result).
    /// </summary>
    public static string? ReductionProblem(IMethodSymbol method, IParameterSymbol reduction)
    {
        var target = ReduceElement(reduction.Type);
        foreach (var p in method.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(p, reduction)) break;
            if (ReduceElement(p.Type) is { } other && SymbolEqualityComparer.Default.Equals(other, target))
                return $"parameter '{p.Name}' reduces into it too";
        }

        foreach (var p in method.Parameters)
            if (p.RefKind == RefKind.Ref && SymbolEqualityComparer.Default.Equals(p.Type, target))
                return $"parameter '{p.Name}' writes it, and the pass's result replaces what it wrote";
        if (IsRunner(method)) return null; // its methods are separate entry points: the caller runs each on its own
        foreach (var member in method.ContainingType.GetMembers())
        {
            if (SymbolEqualityComparer.Default.Equals(member, method)) break;
            if (member is IMethodSymbol other && IsForEach(other)
                && other.Parameters.Any(p => ReduceElement(p.Type) is { } t && SymbolEqualityComparer.Default.Equals(t, target)))
                return $"[ForEach] method '{other.Name}' of its type reduces into it too, and the later pass replaces its result";
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="lookup"/>, a <c>Lookup&lt;T&gt;</c> or <c>Tile&lt;TCell, TTile&gt;</c> parameter of
    /// <paramref name="method"/>, would read rows of <paramref name="target"/> (one of <see cref="LookedUp"/>) the pass
    /// writes (EECS059), or null: another parameter writes it (<c>ref T</c>, <c>Mut&lt;T&gt;</c>, <c>Span&lt;T&gt;</c>,
    /// or <c>Maybe&lt;T&gt;</c>).
    /// </summary>
    public static string? LookupProblem(IMethodSymbol method, IParameterSymbol lookup, ITypeSymbol target)
    {
        foreach (var p in method.Parameters)
        {
            var kind = Classify(p, IsRunner(method));
            bool writes = kind switch
            {
                ParamKind.Component => p.RefKind == RefKind.Ref,
                ParamKind.Mutable => true,
                ParamKind.ComponentSpan => Named(p.Type.OriginalDefinition, "System.Span<T>"),
                ParamKind.Optional => !IsReadOnlyMaybe(p.Type),
                _ => false,
            };
            var written = kind == ParamKind.Optional ? MaybeElement(p.Type) : QueriedComponent(p);
            if (writes && SymbolEqualityComparer.Default.Equals(written, target))
                return $"the method writes through parameter '{p.Name}'";
            if (kind == ParamKind.View && Views.IsValid(p.Type))
                foreach (var field in Views.Fields((INamedTypeSymbol)p.Type))
                    if (field.Writes && SymbolEqualityComparer.Default.Equals(field.Component, target))
                        return $"the method writes through field '{field.Symbol.Name}' of view '{p.Name}'";
        }

        return null;
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

    /// <summary>An Entity parameter: the row's, or a pair's other entity ([Neighbor] Entity).</summary>
    public static bool IsEntityHandle(IParameterSymbol p) => Classify(p) is ParamKind.Entity || Classify(p) == ParamKind.Neighbor && Named(p.Type, Entity);

    /// <summary>The parameter has [Neighbor]: it belongs to the other entity of a pair (<see cref="ParamKind.Neighbor"/>, or invalid).</summary>
    public static bool IsNeighbor(IParameterSymbol p) => p.GetAttributes().Any(a => Named(a.AttributeClass, NeighborAttribute));

    public static ParamKind Classify(IParameterSymbol p)
    {
        if (IsNeighbor(p)) // the other entity of a pair: read only
            return p.RefKind == RefKind.In && p.Type.IsValueType && IsA(p.Type, Component) || p.RefKind == RefKind.None && Named(p.Type, Entity)
                ? ParamKind.Neighbor : ParamKind.Invalid;
        if (ReduceElement(p.Type) is { } reduced) // by ref only: the pass's partial result, which the method writes
            return p.RefKind == RefKind.Ref && reduced.IsValueType && IsA(reduced, Singleton) ? ParamKind.Reduction : ParamKind.Invalid;
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
        if (ScratchElement(p.Type) != null) return ParamKind.Scratch;
        if (EmitElement(p.Type) is { } emitted) return emitted.IsValueType ? ParamKind.Emit : ParamKind.Invalid;
        if (LookupElement(p.Type) is { } looked)
            return looked.IsValueType && IsA(looked, Component) ? ParamKind.Lookup : ParamKind.Invalid;
        if (TileElements(p.Type) is { } tile)
            return tile.Grid.IsValueType && TileKey(tile.Grid) != null ? ParamKind.Tile : ParamKind.Invalid;
        if (Views.IsView(p.Type)) return ParamKind.View;
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

    /// <summary>Singleton type of a <c>Reduce&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? ReduceElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.Reduce<T>")
            ? named.TypeArguments[0]
            : null;

    /// <summary>Whether <paramref name="type"/> implements <c>IReduction&lt;T&gt;</c> of itself (written by hand: a part's is not visible to other generators).</summary>
    public static bool ReducesItself(ITypeSymbol type)
    {
        foreach (var face in type.AllInterfaces)
            if (Named(face.OriginalDefinition, "EasyEcs.IReduction<T>") && SymbolEqualityComparer.Default.Equals(face.TypeArguments[0], type))
                return true;
        return false;
    }

    /// <summary>
    /// Whether singleton <paramref name="type"/> is declared <c>[Accumulate]</c>: a pass's <c>Reduce&lt;T&gt;</c> combines
    /// its result into the world's value instead of replacing it.
    /// </summary>
    public static bool Accumulates(ITypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
            if (Named(attribute.AttributeClass, AccumulateAttribute)) return true;
        return false;
    }

    /// <summary>Value type of a <c>Scratch&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? ScratchElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.Scratch<T>")
            ? named.TypeArguments[0]
            : null;

    /// <summary>Record type of an <c>Emit&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? EmitElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.Emit<T>")
            ? named.TypeArguments[0]
            : null;

    /// <summary>Record type of an <c>Emitted&lt;T&gt;</c> singleton, else null.</summary>
    public static ITypeSymbol? EmittedElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.Emitted<T>")
            ? named.TypeArguments[0]
            : null;

    /// <summary>
    /// Why <paramref name="emit"/>, an <c>Emit&lt;T&gt;</c> parameter of <paramref name="method"/>, would read or reorder
    /// its records (EECS070), or null: the method takes <c>Emitted&lt;T&gt;</c> (a parallel pass's records reach it only
    /// when the pass ends, a pass on one thread's as they are added), or emits <c>T</c> in an earlier parameter too (on one
    /// thread both append to one list, row by row; in parallel each chunk's lists are joined one after the other).
    /// </summary>
    public static string? EmitProblem(IMethodSymbol method, IParameterSymbol emit)
    {
        var record = EmitElement(emit.Type);
        foreach (var p in method.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(p, emit)) break;
            if (EmitElement(p.Type) is { } other && SymbolEqualityComparer.Default.Equals(other, record))
                return $"parameter '{p.Name}' emits it too, and the order of their records would depend on whether the pass runs in parallel";
        }

        foreach (var p in method.Parameters)
            if (EmittedElement(p.Type) is { } read && SymbolEqualityComparer.Default.Equals(read, record))
                return $"parameter '{p.Name}' reads the records, which the pass is still appending to";
        return null;
    }

    /// <summary>Component type of a <c>Lookup&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? LookupElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.Lookup<T>")
            ? named.TypeArguments[0]
            : null;

    /// <summary>The cell type and the tile component of a <c>Tile&lt;TCell, TTile&gt;</c>, else null.</summary>
    public static (ITypeSymbol Cell, ITypeSymbol Grid)? TileElements(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Named(named.ConstructedFrom, "EasyEcs.Tile<TCell, TTile>")
            ? (named.TypeArguments[0], named.TypeArguments[1])
            : null;

    /// <summary>The key type of a tile component (<c>ITile&lt;TKey&gt;</c>), else null.</summary>
    public static ITypeSymbol? TileKey(ITypeSymbol grid) =>
        grid.AllInterfaces.FirstOrDefault(i => Named(i.OriginalDefinition, "EasyEcs.ITile<TKey>"))?.TypeArguments[0];

    /// <summary>
    /// The components a parameter reads of other entities than its own: a <c>Lookup&lt;T&gt;</c>'s T, a
    /// <c>Tile&lt;TCell, TTile&gt;</c>'s <c>DynamicBuffer&lt;TCell&gt;</c> and TTile; else none.
    /// </summary>
    public static IEnumerable<ITypeSymbol> LookedUp(IParameterSymbol p)
    {
        if (LookupElement(p.Type) is { } looked) yield return looked;
        if (TileElements(p.Type) is not { } tile) yield break;
        // DynamicBuffer is declared next to Tile: a lookup by name there, not a search of every assembly.
        if (p.Type.ContainingNamespace.GetTypeMembers("DynamicBuffer", 1).FirstOrDefault() is { } buffer) yield return buffer.Construct(tile.Cell);
        yield return tile.Grid;
    }

    /// <summary>
    /// The key type of indexed component <paramref name="type"/> (<c>IIndexed&lt;TKey&gt;</c>) when a
    /// <c>Lookup&lt;T&gt;</c> parameter of it must have its index built before the pass, else null: always for a unique
    /// index (it exists already: free), and for another when <paramref name="body"/> (a method's or a lambda pass's) finds
    /// it by key itself (<c>lookup.TryFind</c> / <c>Find</c> / <c>Count</c> on the parameter), since building it costs memory
    /// and upkeep on every write from then on.
    /// </summary>
    public static ITypeSymbol? LookupKey(IParameterSymbol lookup, SyntaxNode? body)
    {
        if (LookupElement(lookup.Type) is not INamedTypeSymbol component || IndexedKey(component, out bool unique) is not { } key) return null;
        if (unique) return key;
        bool byKey = body != null && body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryFind" or "Count" or "Find", Expression: IdentifierNameSyntax target }
            && target.Identifier.ValueText == lookup.Name);
        return byKey ? key : null;
    }

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
        if (IsForEachRecord(method)) // a pass over records: no components, no filter (EECS108 says why not)
            return RecordProblem(method) == null && Unnameable(method) == null && (outside ? !EntryPointCollides(method) : IsValidSystem(type));
        if (IsForEntity(method) ? ForEntityProblem(method) != null : ConsumeProblem(method) != null) return false;
        if (!method.IsStatic || outside && EntryPointCollides(method)) return false;
        if (!outside && !IsValidSystem(type)) return false; // EECS074: no system code
        if (method.IsGenericMethod && !outside || Unnameable(method) != null) return false; // a system's method gets no type arguments
        if (SlicesProblem(method) != null || ChunkRowsProblem(method) != null) return false;
        int components = 0;
        foreach (var p in method.Parameters)
        {
            var kind = Classify(p, outside);
            if (kind == ParamKind.Invalid || kind == ParamKind.View && !Views.IsValid(p.Type)) return false;
            if (kind is ParamKind.Component or ParamKind.ComponentSpan or ParamKind.Mutable) components++;
        }

        return ComponentCountValid(method, components + Views.ComponentCount(method)) && !MixesBatch(method) && Views.Overlap(method) == null;
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
            perEntity |= kind is ParamKind.Component or ParamKind.Entity or ParamKind.World or ParamKind.Optional or ParamKind.Mutable or ParamKind.View;
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
            && method.Parameters.Where(IsEntityHandle).All(p => OnlyReads(syntax, p.Name, model));
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
        return (body, ModelOf(body.SyntaxTree, model));
    }

    /// <summary>
    /// The semantic model of <paramref name="tree"/>: <paramref name="model"/> when it is that tree's, else the
    /// compilation's (a body in another file: a partial method's part, a job's Execute).
    /// </summary>
    public static SemanticModel ModelOf(SyntaxTree tree, SemanticModel model) =>
        tree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(tree);

    /// <summary>The <c>Parallel</c> named argument of the method's [ForEach] attribute.</summary>
    public static bool IsParallel(IMethodSymbol method) => method.GetAttributes()
        .Where(a => Named(a.AttributeClass, ForEachAttribute))
        .SelectMany(a => a.NamedArguments)
        .Any(n => n.Key == "Parallel" && n.Value.Value is true);

    /// <summary>
    /// Why a [ForEach(Parallel = true)] method cannot run on several threads, or null: it must be stable (no structural
    /// change), per entity (not a batch) and write no singleton (each row's run would race on it).
    /// </summary>
    public static string? ParallelProblem(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, bool? stable = null)
    {
        if (!(stable ?? IsStable(method, syntax, model))) return "it can change structure (make it static, without World, and use its Entity only to read)";
        if (IsBatch(method)) return "batch methods run on one thread";
        if (method.Parameters.FirstOrDefault(p => Classify(p) == ParamKind.Singleton && p.RefKind == RefKind.Ref) is { } singleton)
            return $"'ref {singleton.Type.Name} {singleton.Name}' writes a singleton, which every chunk would race on: take it as 'in' to read it; "
                   + "a result per chunk goes into a 'ref Reduce<T>' (sums, minimums, maximums) or an Emit<T> (records), combined when the pass ends";
        if (!IsRunner(method)) return null;
        foreach (var p in method.Parameters)
            if (Classify(p, true) == ParamKind.Context && p.RefKind == RefKind.Ref)
                return $"it takes '{p.Name}' by 'ref', which every chunk would race on: pass it by value or 'in', or collect per-chunk results with a Reduce<T> or an Emit<T>";
            else if (Classify(p, true) == ParamKind.Context && p.Type.IsRefLikeType)
                return $"it takes '{p.Name}', a {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}, which the job the threads share cannot hold (a ref struct lives on one stack): pass an array, or read the data through a Lookup<T> or a singleton";
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

    /// <summary>
    /// A structural change of one entity by one call: what it does (<c>Set</c>, <c>Remove</c>, <c>Disable</c>,
    /// <c>Enable</c>, <c>Destroy</c>, or <c>Other</c> for any other structural call: <c>SetIfChanged</c>, several
    /// components, <c>GetOrAdd</c>, <c>Clone</c>, <c>Link</c>), the component it changes (null for <c>Destroy</c> and
    /// <c>Other</c>), and the call's arguments in source order.
    /// </summary>
    public sealed record EntityChange(string Kind, ITypeSymbol? Component, IOperation[] Arguments, bool Generated);

    /// <summary>
    /// The structural change <paramref name="op"/> makes to <paramref name="entity"/> (a parameter), or null when it is no
    /// call on it that changes structure. Decided by meaning, the same way in the analyzer and the generator: Entity's own
    /// members by symbol (<c>Set&lt;T&gt;(value)</c>, <c>Remove&lt;T&gt;()</c>, <c>Destroy()</c>), the members
    /// ComponentGenerator writes (<c>SetDead()</c>, <c>SetBurning(3)</c>, <c>RemoveX()</c>, <c>DisableX()</c>) by the
    /// component their name ends in: the analyzer binds them (<see cref="IsGeneratedWriter"/>), the generator cannot
    /// (another generator's output is not in its compilation), so both read the name.
    /// </summary>
    public static EntityChange? StructuralChange(IOperation op, IParameterSymbol entity, SemanticModel model)
    {
        if (op.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call) return null;
        IOperation[] arguments;
        switch (op)
        {
            case IInvocationOperation invocation when Receiver(invocation) is IParameterReferenceOperation { Parameter: var p }
                                                      && SymbolEqualityComparer.Default.Equals(p, entity):
                var m = invocation.TargetMethod;
                arguments = invocation.Arguments.Where(a => !a.IsImplicit || a.ArgumentKind != ArgumentKind.DefaultValue)
                    .OrderBy(a => a.Syntax.SpanStart).Select(a => a.Value).ToArray();
                if (Named(m.ContainingType, Entity))
                {
                    if (!StructuralEntityMembers.Contains(m.Name)) return null;
                    return m.Name switch
                    {
                        "Destroy" => new EntityChange("Destroy", null, arguments, false),
                        "Set" when m.TypeArguments.Length == 1 && m.Parameters.Length == 1 => new EntityChange("Set", m.TypeArguments[0], arguments, false),
                        "Remove" or "Disable" or "Enable" when m.TypeArguments.Length == 1 && m.Parameters.Length == 0
                            => new EntityChange(m.Name, m.TypeArguments[0], arguments, false),
                        _ => new EntityChange("Other", null, arguments, false),
                    };
                }

                if (!IsGenerated(m) || !IsGeneratedWriter(m)) return null;
                break;
            case IInvalidOperation invalid when invalid.ChildOperations.FirstOrDefault() is IParameterReferenceOperation { Parameter: var p } receiver
                                                && receiver.Syntax == access.Expression && SymbolEqualityComparer.Default.Equals(p, entity):
                arguments = invalid.ChildOperations.Skip(1).Where(c => c.Syntax.Parent is ArgumentSyntax).OrderBy(c => c.Syntax.SpanStart).ToArray();
                break;
            default:
                return null;
        }

        string name = access.Name.Identifier.ValueText;
        foreach (var kind in new[] { "Set", "Remove", "Disable", "Enable" })
        {
            if (!name.StartsWith(kind, System.StringComparison.Ordinal) || name.Length == kind.Length) continue;
            string type = name.Substring(kind.Length);
            if (model.LookupNamespacesAndTypes(call.SpanStart, name: type).OfType<INamedTypeSymbol>()
                    .FirstOrDefault(t => t.TypeKind == TypeKind.Struct && Implements(t, Component)) is { } component)
                return new EntityChange(kind, component, arguments, true);
        }

        return new EntityChange("Other", null, arguments, true); // SetXIfChanged, GetOrAddX, LinkX...
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
    /// A member an EasyEcs generator wrote: its class carries <c>[GeneratedCode("EasyEcs.Generator", …)]</c>
    /// (ComponentGenerator's), or the member does (SystemGenerator's entry points), in source or in a referenced assembly.
    /// </summary>
    public static bool IsGenerated(ISymbol member)
    {
        if (member.GetAttributes().Any(IsGeneratorMark)) return true;
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
            if (IsEntityHandle(p) && !EveryUse(syntax, p.Name, use => IsIdentityUse(model, use)))
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
                    return p.ContainingSymbol is IMethodSymbol m && IsGeneratedJob(m)
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

    /// <summary>A [ForEach] method: a pass over the world's matching entities (a system's, or a runner's).</summary>
    public static bool IsForEach(IMethodSymbol method) =>
        method.GetAttributes().Any(a => Named(a.AttributeClass, ForEachAttribute));

    /// <summary>
    /// A [ForEach] or a [ForEntity] method: the generator compiles its body into a job, so the rules about its
    /// parameters, filters and what its body costs or changes apply to both.
    /// </summary>
    public static bool IsGeneratedJob(IMethodSymbol method) =>
        method.GetAttributes().Any(a => Named(a.AttributeClass, ForEachAttribute) || Named(a.AttributeClass, ForEntityAttribute));
}
