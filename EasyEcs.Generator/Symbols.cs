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
    public const string RemovedAttribute = "EasyEcs.RemovedAttribute<T>";
    public const string EcsSystem = "EasyEcs.EcsSystem";
    public const string Component = "EasyEcs.IComponent";
    public const string Singleton = "EasyEcs.ISingletonComponent";
    public const string Entity = "EasyEcs.Entity";
    public const string EntitySpan = "EasyEcs.EntitySpan";
    public const string World = "EasyEcs.World";
    public const string DynamicBuffer = "EasyEcs.DynamicBuffer<T>";
    public const string TrackChanges = "EasyEcs.ITrackChanges";
    public const string Indexed = "EasyEcs.IIndexed<TKey>";
    public const string Group = "EasyEcs.IGroup";
    public const string Cleanup = "EasyEcs.ICleanupComponent";
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

    /// <summary>
    /// A component the world indexes (<c>IIndexed&lt;TKey&gt;</c>, or a <c>DynamicBuffer</c> of <c>IRelation</c> links) or
    /// groups by (<c>IGroup</c>): written only with Set (and AddRelation / RemoveRelation), so its index or table follows.
    /// </summary>
    public static bool IsIndexed(ITypeSymbol type) =>
        type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is Indexed or Group)
        || type is INamedTypeSymbol { IsGenericType: true } buffer && buffer.OriginalDefinition.ToDisplayString() == "EasyEcs.DynamicBuffer<T>"
        && Implements(buffer.TypeArguments[0], "EasyEcs.IRelation");

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
        /// <summary><c>EntitySpan</c>: the entities of a batch method's run of rows.</summary>
        Entities,
        /// <summary><c>Maybe&lt;T&gt;</c>: a component the entity may lack (looked up per entity, a write when present).</summary>
        Optional,
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
        if (MaybeElement(p.Type) is { } optional)
            return optional.IsValueType && Implements(optional, Component) ? ParamKind.Optional : ParamKind.Invalid;
        return p.Type.ToDisplayString() switch
        {
            Entity => ParamKind.Entity,
            EntitySpan => ParamKind.Entities,
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

    /// <summary>Component type of a <c>Maybe&lt;T&gt;</c>, else null.</summary>
    public static ITypeSymbol? MaybeElement(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && named.ConstructedFrom.ToDisplayString() == "EasyEcs.Maybe<T>"
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
    /// A batch method (component spans) takes only spans, its <c>EntitySpan</c> and singletons: per-entity parameters
    /// (ref/in components, Maybe, Entity, World) have no meaning for a run of rows, and the World could change rows under the
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
            perEntity |= kind is ParamKind.Component or ParamKind.Entity or ParamKind.World or ParamKind.Optional;
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

    public static bool HasWith(IMethodSymbol method) => method.GetAttributes().Any(a =>
        a.AttributeClass is { IsGenericType: true } cls &&
        cls.ConstructedFrom.ToDisplayString() is WithAttribute or ChangedAttribute);

    /// <summary>
    /// The method cannot make structural changes: it is static, takes no <c>World</c>, and uses its <c>Entity</c>
    /// parameter (if any) only to read (<see cref="ReadOnlyEntityMembers"/>). Its job gets INoStructuralChanges.
    /// </summary>
    public static bool IsStable(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model) =>
        method.IsStatic && !method.Parameters.Any(p => Classify(p) == ParamKind.World)
        && method.Parameters.Where(p => Classify(p) == ParamKind.Entity).All(p => OnlyReads(syntax, p.Name, model));

    /// <summary>The <c>Parallel</c> named argument of the method's [ForEach] attribute.</summary>
    public static bool IsParallel(IMethodSymbol method) => method.GetAttributes()
        .Where(a => a.AttributeClass?.ToDisplayString() == ForEachAttribute)
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
        return null;
    }

    /// <summary>
    /// The members of <c>Entity</c> and <c>World</c> that change the world's structure (or copy it, or run its systems):
    /// they enter the owner's structural gate, so they belong to the world's owner thread and to code that may change
    /// structure.
    /// </summary>
    private static readonly HashSet<string> StructuralEntityMembers = ["Set", "Remove", "Destroy", "GetOrAdd", "Clone", "Disable", "Enable"];

    private static readonly HashSet<string> StructuralWorldMembers =
    [
        "CreateEntity", "CreateEntities", "Instantiate", "DestroyEntities", "EnsureEntityCapacity", "AddSingleton", "RemoveSingleton",
        "AddSystem", "RemoveSystem", "Update", "Capture", "Restore", "Dispose",
    ];

    /// <summary>
    /// Whether <paramref name="method"/> is one of the structural members of Entity or World, or a generated
    /// <c>Set…</c> / <c>GetOrAdd…</c> / <c>Remove…</c> member (ComponentGenerator) built on them.
    /// </summary>
    public static bool IsStructural(IMethodSymbol method)
    {
        string? type = method.ContainingType?.ToDisplayString();
        return type == Entity ? StructuralEntityMembers.Contains(method.Name)
            : type == World ? StructuralWorldMembers.Contains(method.Name)
            : IsGenerated(method) && IsGeneratedWriter(method.Name);
    }

    /// <summary>The handle or world a call works on: the instance, or the receiver of an extension member.</summary>
    public static IOperation? Receiver(IInvocationOperation call) =>
        call.Instance ?? (call.TargetMethod.IsExtensionMethod && call.Arguments.Length > 0 ? call.Arguments[0].Value : null);

    /// <summary>Whether a call works on a World (its own member or a generated one), not on an entity.</summary>
    public static bool OnWorld(IInvocationOperation call) =>
        (Receiver(call)?.Type ?? call.TargetMethod.ContainingType)?.ToDisplayString() == World;

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
        return type != null && type.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute"
            && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value as string == GeneratorName);
    }

    private static bool IsGeneratedWriter(string name) =>
        name.StartsWith("Set") || name.StartsWith("Remove") || name.StartsWith("GetOrAdd")
        || name.StartsWith("Disable") || name.StartsWith("Enable");

    /// <summary>
    /// Whether <paramref name="access"/> reads through a member ComponentGenerator wrote. The analyzer resolves it; the
    /// generator cannot (another generator's output is not in its compilation), so there the name decides: a
    /// component's name (<c>e.Position</c>), <c>Has</c> / <c>TryGet</c> + a component, <c>Is</c> + a tag, or
    /// <c>Is</c> + a component + <c>Disabled</c>.
    /// </summary>
    private static bool GeneratedRead(SemanticModel model, MemberAccessExpressionSyntax access, string member)
    {
        if (model.GetSymbolInfo(access).Symbol is { } symbol) return IsGenerated(symbol) && !IsGeneratedWriter(symbol.Name);
        if (member.Length > "IsDisabled".Length && member.StartsWith("Is") && member.EndsWith("Disabled"))
            member = member.Substring(2, member.Length - 2 - "Disabled".Length);
        foreach (var (prefix, tag) in new[] { ("", false), ("Has", false), ("TryGet", false), ("Is", true) })
        {
            if (member.Length <= prefix.Length || !member.StartsWith(prefix)) continue;
            string type = member.Substring(prefix.Length);
            if (model.LookupNamespacesAndTypes(access.SpanStart, name: type).OfType<INamedTypeSymbol>().Any(t =>
                    t.TypeKind == TypeKind.Struct && Implements(t, Component)
                    && t.GetMembers().Any(f => f is IFieldSymbol { IsStatic: false }) != tag))
                return true;
        }

        return false;
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
        ["Id", "Version", "IsAlive", "Has", "Get", "Read", "TryGet", "TryGetRef", "IsDisabled"];

    /// <summary>
    /// Whether every use of parameter <paramref name="name"/> in the method body is a read-only member access
    /// (<c>entity.Id</c>, <c>entity.Has&lt;T&gt;()</c>, ...). Syntax only and conservative: any other use (passing the
    /// entity on, <c>entity.World</c>, a structural change) answers false.
    /// </summary>
    private static bool OnlyReads(MethodDeclarationSyntax method, string name, SemanticModel model)
    {
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return false;
        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != name) continue;
            if (identifier.Parent is not MemberAccessExpressionSyntax access || access.Expression != identifier) return false;
            var member = access.Name is GenericNameSyntax generic ? generic.Identifier.ValueText : access.Name.Identifier.ValueText;
            if (!ReadOnlyEntityMembers.Contains(member) && !GeneratedRead(model, access, member)) return false;
        }

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
            SyntaxNode top = access; // name.Value.Field.Field: the outermost field read
            while (top.Parent is MemberAccessExpressionSyntax outer && outer.Expression == top) top = outer;
            switch (top.Parent)
            {
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
                case IPropertyReferenceOperation { Property: { ReturnsByRef: true } property }
                    when property.Name.StartsWith("Component") && property.ContainingType.Name == "QueryItem"
                         && property.ContainingNamespace.ToDisplayString() == "EasyEcs":
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
        (p.RefKind == RefKind.Ref || p.Type.OriginalDefinition.ToDisplayString() == "System.Span<T>")
        && p.ContainingSymbol is IMethodSymbol { Name: "Execute", ContainingType: { } job }
        && job.AllInterfaces.Any(i => i.Name.StartsWith("IForEach") && i.ContainingNamespace.ToDisplayString() == "EasyEcs");

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
