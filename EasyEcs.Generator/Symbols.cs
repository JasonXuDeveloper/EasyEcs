using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
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
    public const string WithAnyAttribute = "EasyEcs.WithAnyAttribute<T>";
    public const string ChangedAttribute = "EasyEcs.ChangedAttribute<T>";
    public const string EcsSystem = "EasyEcs.EcsSystem";
    public const string Component = "EasyEcs.IComponent";
    public const string Singleton = "EasyEcs.ISingletonComponent";
    public const string Entity = "EasyEcs.Entity";
    public const string World = "EasyEcs.World";
    public const string DynamicBuffer = "EasyEcs.DynamicBuffer<T>";
    public const int MaxComponents = 9;

    public static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>The element type when <paramref name="type"/> is a <c>DynamicBuffer&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? BufferElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named &&
        named.OriginalDefinition.ToDisplayString() == DynamicBuffer
            ? named.TypeArguments[0]
            : null;

    /// <summary>Whether <paramref name="value"/> is <c>new DynamicBuffer&lt;T&gt;()</c> or <c>default</c> (no list).</summary>
    public static bool IsNewBuffer(IOperation? value)
    {
        while (value is IConversionOperation conversion) value = conversion.Operand;
        return value is IDefaultValueOperation or IObjectCreationOperation { Arguments.Length: 0, Initializer: null };
    }

    public static bool IsSystem(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (b.ToDisplayString() == EcsSystem) return true;
        return false;
    }

    public static bool Implements(ITypeSymbol type, string interfaceName) =>
        type.AllInterfaces.Any(i => i.ToDisplayString() == interfaceName);

    /// <summary>The type and every containing type are declared partial.</summary>
    public static bool IsPartialChain(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (!t.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(m => m.Text == "partial")))
                return false;
        return true;
    }

    public static bool IsGenericChain(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (t.IsGenericType) return true;
        return false;
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
    }

    public static ParamKind Classify(IParameterSymbol p)
    {
        if (p.RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter)
            return !p.Type.IsValueType ? ParamKind.Invalid
                : Implements(p.Type, Component) ? ParamKind.Component
                : Implements(p.Type, Singleton) ? ParamKind.Singleton
                : ParamKind.Invalid;
        if (p.RefKind != RefKind.None) return ParamKind.Invalid;
        if (SpanElement(p.Type) is { } element)
            return element.IsValueType && Implements(element, Component) ? ParamKind.ComponentSpan : ParamKind.Invalid;
        return p.Type.ToDisplayString() switch
        {
            Entity => ParamKind.Entity,
            World => ParamKind.World,
            _ => ParamKind.Invalid,
        };
    }

    /// <summary>Element type of a <c>Span&lt;T&gt;</c> / <c>ReadOnlySpan&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? SpanElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named &&
        named.ConstructedFrom.ToDisplayString() is "System.Span<T>" or "System.ReadOnlySpan<T>"
            ? named.TypeArguments[0]
            : null;

    /// <summary>Whether a [ForEach] method can be generated (all errors are reported by the analyzer).</summary>
    public static bool IsValidForEach(IMethodSymbol method)
    {
        var type = method.ContainingType;
        if (!IsSystem(type) || !IsPartialChain(type) || IsGenericChain(type) || method.IsGenericMethod) return false;
        int components = 0;
        foreach (var p in method.Parameters)
        {
            var kind = Classify(p);
            if (kind == ParamKind.Invalid) return false;
            if (kind is ParamKind.Component or ParamKind.ComponentSpan) components++;
        }

        return ComponentCountValid(method, components) && !MixesBatch(method);
    }

    /// <summary>
    /// A batch method (component spans) takes only spans and singletons: per-entity parameters (ref/in components,
    /// Entity, World) have no meaning for a run of rows, and the World could change rows under the spans.
    /// </summary>
    public static bool MixesBatch(IMethodSymbol method)
    {
        bool spans = false, perEntity = false;
        foreach (var p in method.Parameters)
        {
            var kind = Classify(p);
            spans |= kind == ParamKind.ComponentSpan;
            perEntity |= kind is ParamKind.Component or ParamKind.Entity or ParamKind.World;
        }

        return spans && perEntity;
    }

    /// <summary>
    /// 1..9 component (or component span) parameters, or none when a [With&lt;T&gt;] or [Changed&lt;T&gt;] filter names the
    /// queried component (a method that only needs the entity, e.g. [With&lt;Dead&gt;] static void Remove(Entity e)).
    /// </summary>
    public static bool ComponentCountValid(IMethodSymbol method, int components) =>
        components is >= 1 and <= MaxComponents || components == 0 && HasWith(method);

    public static bool HasWith(IMethodSymbol method) => method.GetAttributes().Any(a =>
        a.AttributeClass is { IsGenericType: true } cls &&
        cls.ConstructedFrom.ToDisplayString() is WithAttribute or ChangedAttribute);

    /// <summary>
    /// The method cannot make structural changes: it is static, takes no <c>World</c>, and uses its <c>Entity</c>
    /// parameter (if any) only to read (<see cref="ReadOnlyEntityMembers"/>). Its job gets INoStructuralChanges.
    /// </summary>
    public static bool IsStable(IMethodSymbol method, MethodDeclarationSyntax syntax) =>
        method.IsStatic && !method.Parameters.Any(p => Classify(p) == ParamKind.World)
        && method.Parameters.Where(p => Classify(p) == ParamKind.Entity).All(p => OnlyReads(syntax, p.Name));

    /// <summary>The <c>Parallel</c> named argument of the method's [ForEach] attribute.</summary>
    public static bool IsParallel(IMethodSymbol method) => method.GetAttributes()
        .Where(a => a.AttributeClass?.ToDisplayString() == ForEachAttribute)
        .SelectMany(a => a.NamedArguments)
        .Any(n => n.Key == "Parallel" && n.Value.Value is true);

    /// <summary>
    /// Why a [ForEach(Parallel = true)] method cannot run on several threads, or null: it must be stable (no structural
    /// change), per entity (not a batch) and write no singleton (each row's run would race on it).
    /// </summary>
    public static string? ParallelProblem(IMethodSymbol method, MethodDeclarationSyntax syntax)
    {
        if (!IsStable(method, syntax)) return "it can change structure (make it static, without World, and use its Entity only to read)";
        if (method.Parameters.Any(p => Classify(p) == ParamKind.ComponentSpan)) return "batch methods run on one thread";
        if (method.Parameters.Any(p => Classify(p) == ParamKind.Singleton && p.RefKind == RefKind.Ref))
            return "it writes a singleton, which every chunk would race on (take it as 'in')";
        return null;
    }

    /// <summary>
    /// The members of <c>Entity</c> and <c>World</c> that change the world's structure (or copy it, or run its systems):
    /// they enter the owner's structural gate, so they belong to the world's owner thread and to code that may change
    /// structure.
    /// </summary>
    private static readonly HashSet<string> StructuralEntityMembers = ["Add", "Set", "Remove", "Destroy", "GetOrAdd"];

    private static readonly HashSet<string> StructuralWorldMembers =
    [
        "CreateEntity", "CreateEntities", "DestroyEntities", "EnsureEntityCapacity", "AddSingleton", "RemoveSingleton",
        "AddSystem", "RemoveSystem", "Update", "Capture", "Restore", "Dispose",
    ];

    /// <summary>Whether <paramref name="method"/> is one of the structural members of Entity or World.</summary>
    public static bool IsStructural(IMethodSymbol method)
    {
        string? type = method.ContainingType?.ToDisplayString();
        return type == Entity ? StructuralEntityMembers.Contains(method.Name)
            : type == World && StructuralWorldMembers.Contains(method.Name);
    }

    /// <summary>
    /// The thread-starting calls (Task.Run, TaskFactory.StartNew, Parallel.*, ThreadPool.*, new Thread / Task / Timer)
    /// that run a delegate argument on another thread.
    /// </summary>
    public static bool StartsThread(IMethodSymbol method)
    {
        string type = method.ContainingType?.ToDisplayString() ?? "";
        return type switch
        {
            "System.Threading.Tasks.Task" => method.Name is "Run" || method.MethodKind == MethodKind.Constructor,
            "System.Threading.Tasks.Task<TResult>" => method.MethodKind == MethodKind.Constructor,
            "System.Threading.Tasks.TaskFactory" or "System.Threading.Tasks.TaskFactory<TResult>" => method.Name == "StartNew",
            "System.Threading.Tasks.Parallel" => true,
            "System.Threading.ThreadPool" => method.Name is "QueueUserWorkItem" or "UnsafeQueueUserWorkItem"
                or "RegisterWaitForSingleObject" or "UnsafeRegisterWaitForSingleObject",
            "System.Threading.Thread" or "System.Threading.Timer" => method.MethodKind == MethodKind.Constructor,
            _ => false,
        };
    }

    /// <summary>Entity members that cannot change the world (no structural change, no access to the World).</summary>
    private static readonly HashSet<string> ReadOnlyEntityMembers =
        ["Id", "Version", "IsAlive", "Has", "Get", "Read", "TryGet", "TryGetRef"];

    /// <summary>
    /// Whether every use of parameter <paramref name="name"/> in the method body is a read-only member access
    /// (<c>entity.Id</c>, <c>entity.Has&lt;T&gt;()</c>, ...). Syntax only and conservative: any other use (passing the
    /// entity on, <c>entity.World</c>, a structural change) answers false.
    /// </summary>
    private static bool OnlyReads(MethodDeclarationSyntax method, string name)
    {
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return false;
        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != name) continue;
            if (identifier.Parent is not MemberAccessExpressionSyntax access || access.Expression != identifier) return false;
            var member = access.Name is GenericNameSyntax generic ? generic.Identifier.ValueText : access.Name.Identifier.ValueText;
            if (!ReadOnlyEntityMembers.Contains(member)) return false;
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
        call.Instance is { Type.IsReferenceType: true } instance && Mutators.Contains(call.TargetMethod.Name) ? instance : null;

    public static bool IsForEach(IMethodSymbol method) =>
        method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ForEachAttribute);
}
