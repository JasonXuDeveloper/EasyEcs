using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EasyEcs.Generator;

/// <summary>
/// Generates named members for every component and singleton declared in the compilation (C# 14 extension members),
/// so the common calls read like fields and methods of the entity or the world:
/// <list type="bullet">
/// <item>component <c>Position</c>: <c>entity.Position</c> (<c>ref</c>, <c>Get</c>), <c>HasPosition</c>,
/// <c>TryGetPosition(out p)</c>, <c>GetOrAddPosition()</c> and <c>GetOrAddPosition(x, y)</c> (added with that value if missing; not for indexed components), <c>SetPosition(x, y)</c> (one overload per public constructor, or the public fields),
/// <c>RemovePosition()</c>. A component the world watches (<c>ITracked</c>, <c>IIndexed&lt;TKey&gt;</c>) gets a
/// <c>ref readonly</c> property (<c>Read</c>): reading it never counts as a write, and writes are <c>Set…</c> calls; a
/// tracked one that is neither indexed nor grouped also gets <c>WriteHealth()</c> (<c>ref</c>, <c>Get</c>: stamped as
/// changed); a watched one also gets <c>SetHealthIfChanged(…)</c> (<c>SetIfChanged</c>: an equal value is not written).</item>
/// <item>buffer element <c>Loot</c> (a struct of this compilation some code names in <c>DynamicBuffer&lt;Loot&gt;</c>):
/// <c>entity.Loot</c> (<c>ref readonly DynamicBuffer&lt;Loot&gt;</c>), <c>HasLoot</c>, <c>WriteLoot()</c>,
/// <c>GetOrAddLoot()</c>, <c>RemoveLoot()</c> (a relation's links, indexed, get no <c>ref</c> members: they are
/// written with <c>Link</c> / <c>Unlink</c>).</item>
/// <item>tag <c>Dead</c> (no fields): <c>entity.IsDead</c>, <c>SetDead()</c>, <c>RemoveDead()</c>.</item>
/// <item>the same writers on a <c>Prefab</c>, returning it for chaining (<c>new Prefab().SetHealth(100).SetNpc()</c>),
/// with <c>prefab.Health</c> (read-only), <c>HasHealth</c> and <c>IsDead</c>.</item>
/// <item>indexed component <c>Zone : IIndexed&lt;int&gt;</c>: <c>world.ZoneIndex</c> (<c>Index&lt;Zone, int&gt;()</c>).</item>
/// <item>each <c>world.Query&lt;Position, Velocity&gt;()</c> the code calls: <c>item.Position</c>, <c>item.Velocity</c> on its
/// items (<c>Component1</c>, <c>Component2</c> by name; <c>ref readonly</c> for an indexed component).</item>
/// <item>singleton <c>Clock</c>: <c>world.Clock</c> (<c>ref</c>, <c>GetSingleton</c>), <c>HasClock</c>,
/// <c>GetOrAddClock()</c> (<c>ref</c>, not for tags), <c>SetClock(…)</c> (adds it if missing), <c>RemoveClock()</c>.</item>
/// </list>
/// Writes are methods, not property setters: C# rejects a setter on a handle that is not a variable (a <c>foreach</c>
/// variable, a call's result). Generic types, types nested in types others cannot see, member names the receiver
/// already has and names another struct of the namespace is offered too are skipped (<see cref="Shape.Members"/>). The
/// members are public, their class as visible as the type. Nothing is generated below C# 14.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var components = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null } type
                    && type is StructDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.ValueText: "struct" },
                static (ctx, ct) => ComponentModel.Create(ctx.Node, ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node, ct), ctx.SemanticModel.Compilation, ct))
            .Where(static m => m is not null)
            .WithTrackingName("Components");

        // From the parse options, not the compilation: a new compilation (every edit) does not re-run it.
        var supported = context.ParseOptionsProvider
            .Select(static (o, _) => o is CSharpParseOptions { LanguageVersion: var version } && (int)version >= 1400)
            .WithTrackingName("ExtensionMembers");

        context.RegisterSourceOutput(components.Combine(supported), static (spc, pair) =>
        {
            if (pair.Right) spc.AddSource($"{pair.Left!.HintName}.Component.g.cs", SourceText.From(Emit(pair.Left!), Encoding.UTF8));
        });

        // DynamicBuffer<T> names, bound once: the buffer's named members (one file per element type of this compilation,
        // however many places name it, each cached on its own) and its registration for saves.
        var bufferUses = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is GenericNameSyntax { Identifier.ValueText: "DynamicBuffer", TypeArgumentList.Arguments.Count: 1 },
                static (ctx, ct) => BufferUse.Create(ctx.SemanticModel, (GenericNameSyntax)ctx.Node, ct))
            .Where(static b => b is not null)
            .Select(static (b, _) => b!)
            .WithTrackingName("BufferUses");
        var elements = bufferUses
            .Where(static b => b.Members is not null)
            .Select(static (b, _) => b.Members!)
            .Collect()
            .SelectMany(static (all, _) => all.Distinct())
            .WithTrackingName("BufferElements");
        context.RegisterSourceOutput(elements.Combine(supported), static (spc, pair) =>
        {
            if (pair.Right) spc.AddSource($"{pair.Left.HintName}.Buffer.g.cs", SourceText.From(EmitBuffer(pair.Left), Encoding.UTF8));
        });

        // Query<T1..Tn> and Read<T1..Tn> calls: each shape's items get named refs (item.Position for item.Component1),
        // one file per shape however many loops use it, each cached on its own.
        var shapes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is GenericNameSyntax { Identifier.ValueText: "Query" or "Read", TypeArgumentList.Arguments.Count: > 0 and <= 9 },
                static (ctx, ct) => QueryShape.Create(ctx.SemanticModel, (GenericNameSyntax)ctx.Node, ct))
            .Where(static q => q is not null)
            .Select(static (q, _) => q!)
            .Collect()
            .SelectMany(static (all, _) => all.Distinct())
            .WithTrackingName("QueryShapes");
        context.RegisterSourceOutput(shapes.Combine(supported), static (spc, pair) =>
        {
            if (pair.Right) spc.AddSource($"EasyEcs.QueryItem.{pair.Left.Hash}.g.cs", SourceText.From(EmitQueryItem(pair.Left), Encoding.UTF8));
        });

        // Saves name types: every component, singleton and DynamicBuffer<T> of the compilation is registered when the
        // module loads (World.Load resolves names through TypeRegistry). C# 14, like the extension members.
        // A unique component also tells worlds how to create its index (they create it with their first table holding it).
        var saved = components
            .SelectMany(static (m, _) => m!.Unique
                ? new[]
                {
                    new SavedType("Component<" + m.Type + ">()", m.Type, m.EntityIds, m.Layout),
                    new SavedType((m.Direct ? "DirectIndex<" : "UniqueIndex<") + m.Type + ", " + m.IndexedKey + ">()", m.Type, new EquatableArray<string>([]), ""),
                }
                : [new SavedType((m.Singleton ? "Singleton" : "Component") + "<" + m.Type + ">()", m.Type, m.EntityIds, m.Singleton ? "" : m.Layout)])
            .WithTrackingName("SavedComponents");
        // The setup a type declares for every world ([GroupBy] on its grouped member, [WhenTargetDestroyed]): registered with it.
        var groupBy = context.SyntaxProvider.ForAttributeWithMetadataName(Symbols.GroupByAttribute,
                static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax or ParameterSyntax,
                static (ctx, _) => Setup(ctx.TargetSymbol is IParameterSymbol p ? p.ContainingSymbol.ContainingType : ctx.TargetSymbol.ContainingType,
                    ctx.SemanticModel.Compilation, ctx.Attributes[0]))
            .Where(static t => t is not null).Select(static (t, _) => t!).WithTrackingName("GroupBy");
        var targetPolicies = context.SyntaxProvider.ForAttributeWithMetadataName(Symbols.WhenTargetDestroyedAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => Setup(ctx.TargetSymbol, ctx.SemanticModel.Compilation, ctx.Attributes[0]))
            .Where(static t => t is not null).Select(static (t, _) => t!).WithTrackingName("TargetPolicies");
        var notSaved = context.SyntaxProvider.ForAttributeWithMetadataName(Symbols.NotSavedAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => Setup(ctx.TargetSymbol, ctx.SemanticModel.Compilation, ctx.Attributes[0]))
            .Where(static t => t is not null).Select(static (t, _) => t!).WithTrackingName("NotSaved");
        // [OrderBy] on a member: the order every world keeps the type's tables in (read through Parts, as the part is).
        var orders = context.SyntaxProvider.ForAttributeWithMetadataName(Parts.OrderBy,
                static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax or ParameterSyntax,
                static (ctx, _) => Order(ctx.TargetSymbol is IParameterSymbol p ? p.ContainingSymbol.ContainingType : ctx.TargetSymbol.ContainingType,
                    ctx.SemanticModel.Compilation))
            .Where(static t => t is not null).Select(static (t, _) => t!).WithTrackingName("OrderBy");
        var buffers = bufferUses
            .Where(static b => b.Saved is not null)
            .Select(static (b, _) => b.Saved!)
            .WithTrackingName("SavedBuffers");
        // Every assembly but EasyEcs's own (which registers its types by hand): a component, a singleton and a buffer
        // all name EasyEcs, whose TypeRegistry ships with this generator. The assembly name, not a type lookup: this
        // re-runs on every edit.
        var initializers = context.CompilationProvider
            .Select(static (c, _) => c.AssemblyName != "EasyEcs.Core")
            .Combine(supported)
            .Select(static (pair, _) => pair.Left && pair.Right)
            .WithTrackingName("ModuleInitializers");
        // Only the module initializer reaches it: implementation output.
        var setups = groupBy.Collect().Combine(targetPolicies.Collect()).Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(notSaved.Collect()).Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(orders.Collect()).Select(static (pair, _) => pair.Left.AddRange(pair.Right));
        context.RegisterImplementationSourceOutput(saved.Collect().Combine(buffers.Collect()).Combine(setups).Combine(initializers), static (spc, pair) =>
        {
            var types = pair.Left.Left.Left.Concat(pair.Left.Left.Right).Concat(pair.Left.Right).Distinct().OrderBy(static t => t.Call, System.StringComparer.Ordinal).ToArray();
            if (pair.Right && types.Length > 0) spc.AddSource("EasyEcs.TypeRegistry.g.cs", SourceText.From(EmitTypeRegistry(types), Encoding.UTF8));
        });
    }

    /// <summary>
    /// A <c>DynamicBuffer&lt;T&gt;</c> the code names: its element's named members (<see cref="BufferModel"/>, null when
    /// none are generated) and its registration for saves (null unless closed and accessible here).
    /// </summary>
    private sealed record BufferUse(BufferModel? Members, SavedType? Saved)
    {
        public static BufferUse? Create(SemanticModel model, GenericNameSyntax name, CancellationToken token)
        {
            if (Symbols.TypeNamed(model, name) is not INamedTypeSymbol { IsGenericType: true } buffer
                || !Symbols.Named(buffer.OriginalDefinition, Symbols.DynamicBuffer))
                return null;
            var use = new BufferUse(BufferModel.Create(model, buffer), Registration(model, buffer));
            return use.Members is null && use.Saved is null ? null : use;
        }

        private static SavedType? Registration(SemanticModel model, INamedTypeSymbol buffer)
        {
            if (buffer.TypeArguments[0] is not INamedTypeSymbol element || Symbols.ContainsTypeParameter(element)
                || !model.Compilation.IsSymbolAccessibleWithin(element, model.Compilation.Assembly) || !Symbols.IsNameable(element))
                return null;
            return new SavedType("Component<" + buffer.ToDisplayString(Symbols.Fqn) + ">()", element.ToDisplayString(Symbols.Fqn),
                new EquatableArray<string>(Symbols.EntityIdPaths(element, model.Compilation)),
                // An element of another assembly: its private fields are invisible here, its own module records the hash.
                Symbols.LayoutHash(element) is not { } layout ? ""
                : SymbolEqualityComparer.Default.Equals(element.ContainingAssembly, model.Compilation.Assembly) ? layout : "0");
        }
    }

    /// <summary>
    /// The registration of the world setup <paramref name="attribute"/> declares on <paramref name="symbol"/>
    /// (<c>GroupBy&lt;T&gt;()</c>, <c>WhenTargetDestroyed&lt;T&gt;(policy)</c>, <c>RelationWhenTargetDestroyed&lt;T&gt;(policy)</c>,
    /// <c>NotSaved&lt;T&gt;()</c>),
    /// or null when the type cannot take it (EECS068 reports it) or the module initializer cannot name it.
    /// </summary>
    private static SavedType? Setup(ISymbol symbol, Compilation compilation, AttributeData attribute)
    {
        if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct } type || Symbols.ContainsTypeParameter(type)
            || !compilation.IsSymbolAccessibleWithin(type, compilation.Assembly) || !Symbols.IsNameable(type)
            || Symbols.SetupProblem(type, attribute) != null)
            return null;
        string name = type.ToDisplayString(Symbols.Fqn);
        if (Symbols.Named(attribute.AttributeClass, Symbols.GroupByAttribute))
            return new SavedType($"GroupBy<{name}>()", name, new EquatableArray<string>([]), "");
        if (Symbols.Named(attribute.AttributeClass, Symbols.NotSavedAttribute))
            return new SavedType($"NotSaved<{name}>()", name, new EquatableArray<string>([]), "");
        string policy = $"(global::EasyEcs.TargetPolicy){attribute.ConstructorArguments[0].Value}";
        return new SavedType($"{(Symbols.Implements(type, Symbols.Relation) ? "RelationWhenTargetDestroyed" : "WhenTargetDestroyed")}<{name}>({policy})",
            name, new EquatableArray<string>([]), "");
    }

    /// <summary>
    /// The registration of the order <c>[OrderBy]</c> declares on <paramref name="type"/>
    /// (<c>OrderBy&lt;T, TKey&gt;(axes, interval, margin)</c>), or null when the part is not written (EECS104 reports
    /// why) or the module initializer cannot name the type.
    /// </summary>
    private static SavedType? Order(INamedTypeSymbol? type, Compilation compilation)
    {
        if (type is not { TypeKind: TypeKind.Struct } || Symbols.ContainsTypeParameter(type)
            || !compilation.IsSymbolAccessibleWithin(type, compilation.Assembly) || !Symbols.IsNameable(type)
            || Parts.Of(type) is not { WritesOrder: true, OrderOf: { } order })
            return null;
        string name = type.ToDisplayString(Symbols.Fqn);
        string margin = order.Margin.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
        return new SavedType($"OrderBy<{name}, {order.Type}>({order.Axes}, {order.Interval}, {margin})", name, new EquatableArray<string>([]), "");
    }

    /// <summary>
    /// A registration of the module initializer: <paramref name="Call"/> (<c>Component&lt;T&gt;</c> or
    /// <c>Singleton&lt;T&gt;</c>), and the <c>EntityId</c> fields of <paramref name="Type"/> (the component, or a
    /// buffer's element) a migration rewrites (<see cref="Symbols.EntityIdPaths"/>), and its layout hash when it is
    /// unmanaged (<see cref="Symbols.LayoutHash"/>: its rows are written canonically, empty otherwise).
    /// </summary>
    private sealed record SavedType(string Call, string Type, EquatableArray<string> EntityIds, string Layout);

    private static string EmitTypeRegistry(SavedType[] types)
    {
        var sb = new StringBuilder("// <auto-generated/>\n");
        sb.Append($"{Code.GeneratedCode(Symbols.GeneratorName)}\n");
        sb.Append("internal static class __EasyEcsTypeRegistry\n{\n");
        sb.Append("    /// <summary>Makes this module's components and singletons loadable by type id (World.Load), and tells\n");
        sb.Append("    /// World.LoadEntities where their EntityId fields are.</summary>\n");
        sb.Append($"    {Code.ModuleInitializer}\n    internal static void Register()\n    {{\n");
        foreach (var type in types) sb.Append($"        global::EasyEcs.TypeRegistry.{type.Call};\n");
        foreach (var type in types.Where(static t => t.Layout.Length > 0).Select(static t => (t.Type, t.Layout)).Distinct())
            sb.Append($"        global::EasyEcs.TypeRegistry.Raw<{type.Type}>({type.Layout});\n");

        // A field accessor per (type, field) a path steps through: private fields and auto-property backing fields too.
        var accessors = new Dictionary<string, string>();
        var fields = new StringBuilder();
        string Accessor(string step)
        {
            if (accessors.TryGetValue(step, out var name)) return name;
            var parts = step.Split('\t');
            accessors[step] = name = "Field" + accessors.Count;
            fields.Append($"    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = \"{parts[1]}\")]\n");
            fields.Append($"    private static extern ref {parts[2]} {name}(ref {parts[0]} value);\n");
            return name;
        }

        foreach (var type in types.Where(static t => t.EntityIds.Length > 0).Select(static t => (t.Type, t.EntityIds)).Distinct())
        {
            var lambdas = type.EntityIds.Select(path =>
            {
                string reference = "ref value";
                foreach (var step in path.Split('\n')) reference = $"ref {Accessor(step)}({reference})";
                return $"static (ref value) => {reference}";
            });
            sb.Append($"        global::EasyEcs.TypeRegistry.EntityIds<{type.Type}>({string.Join(", ", lambdas)});\n");
        }

        sb.Append("    }\n");
        if (fields.Length > 0) sb.Append('\n').Append(fields);
        sb.Append("}\n");
        return sb.ToString();
    }

    internal static readonly string Version = typeof(ComponentGenerator).Assembly.GetName().Version?.ToString() ?? "";

    private const string Inline =
        "        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n";

    // The members are public even in an internal class: an internal extension property is not usable from a friend
    // assembly (InternalsVisibleTo), and the class's accessibility already decides who sees them.
    private static void Member(StringBuilder sb, string doc, string code) =>
        sb.Append($"        /// <summary>{doc}</summary>\n{Inline}        public {code}\n");

    /// <summary>A property: the attribute goes on its getter.</summary>
    private static void Property(StringBuilder sb, string doc, string declaration, string body) =>
        sb.Append($"        /// <summary>{doc}</summary>\n        public {declaration}\n        {{\n    {Inline}            get => {body};\n        }}\n");

    /// <summary>
    /// A generated class and its first extension block, on <paramref name="receiver"/> (a full type name):
    /// <paramref name="summary"/> (or none), the tool name (<see cref="Symbols.IsGenerated"/> looks for
    /// <see cref="Symbols.GeneratorName"/>), its accessibility and name.
    /// </summary>
    private static void Open(StringBuilder sb, string? summary, string tool, string access, string name, string receiver)
    {
        if (summary != null) sb.Append($"/// <summary>{summary}</summary>\n");
        sb.Append($"{Code.GeneratedCode(tool)}\n");
        sb.Append($"{access} static class {name}\n{{\n");
        sb.Append($"    extension({receiver} @this)\n    {{\n");
    }

    /// <summary>A type of EasyEcs as generated code names it.</summary>
    private static string Ecs(string name) => "global::EasyEcs." + name;

    /// <summary>Another extension block of the class, on EasyEcs's <paramref name="receiver"/>.</summary>
    private static void Extension(StringBuilder sb, string receiver) =>
        sb.Append($"\n    extension({Ecs(receiver)} @this)\n    {{\n");

    private static string Emit(ComponentModel m)
    {
        var sb = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (m.Namespace.Length > 0) sb.Append($"namespace {m.Namespace};\n\n");
        string a = m.IsPublic ? "public" : "internal";
        string t = m.Type, n = m.Name;
        var s = m.Shape;
        bool Free(string receiver, string name) => m.Members.Contains(receiver + "." + name);

        if (s.Kind == Kind.Singleton)
        {
            Open(sb, $"The named members of <see cref=\"{t}\"/> (generated).", Symbols.GeneratorName, a, m.Path + Classes.Members, Ecs(World));
            if (Free(World, n))
                Property(sb, $"The world's <see cref=\"{t}\"/> by <c>ref</c> (<c>GetSingleton</c>); throws if it was not added.",
                    $"ref {t} {Code.Escape(n)}", $"ref @this.GetSingleton<{t}>()");
            if (Free(World, "Has" + n))
                Property(sb, $"Whether the world has a <see cref=\"{t}\"/> (<c>HasSingleton</c>).", $"bool Has{n}", $"@this.HasSingleton<{t}>()");
            if (Free(World, "GetOrAdd" + n))
                Member(sb, $"The world's <see cref=\"{t}\"/> by <c>ref</c>, added (default) first if missing (<c>GetOrAddSingleton</c>).",
                    $"ref {t} GetOrAdd{n}() => ref @this.GetOrAddSingleton<{t}>();");
            if (Free(World, "GetOrAdd" + n))
                foreach (var setter in m.Setters)
                    if (setter.Parameters.Length > 0)
                        Member(sb, $"The world's <see cref=\"{t}\"/> by <c>ref</c>, added with this value first if missing (<c>GetOrAddSingleton</c>); a world that has it keeps its own.",
                            $"ref {t} GetOrAdd{n}({setter.Parameters}) => ref @this.GetOrAddSingleton({setter.Value});");
            if (Free(World, "Set" + n))
                foreach (var setter in m.Setters)
                    Member(sb, $"Sets the world's <see cref=\"{t}\"/>, adding it if missing.",
                        $"void Set{n}({setter.Parameters}) => @this.GetOrAddSingleton<{t}>() = {setter.Value};");
            if (Free(World, "Remove" + n))
                Member(sb, $"Removes the world's <see cref=\"{t}\"/> (<c>RemoveSingleton</c>); false if it was not added.",
                    $"bool Remove{n}() => @this.RemoveSingleton<{t}>();");
            return sb.Append("    }\n}\n").ToString();
        }

        Open(sb, $"The named members of <see cref=\"{t}\"/> (generated).", Symbols.GeneratorName, a, m.Path + Classes.Members, Ecs(Entity));
        if (Free(Entity, "Is" + n)) Property(sb, $"Whether the entity has the tag <see cref=\"{t}\"/>.", $"bool Is{n}", $"@this.Has<{t}>()");
        if (Free(Entity, n))
            Property(sb, s.Watched
                    ? $"The entity's <see cref=\"{t}\"/>, read-only (<c>Read</c>: never a write); write it with <c>Set</c>, so the change is stamped and indexed. Throws if missing."
                    : $"The entity's <see cref=\"{t}\"/> by <c>ref</c> (<c>Get</c>); throws if missing.",
                $"ref {(s.Watched ? "readonly " : "")}{t} {Code.Escape(n)}", $"ref @this.{(s.Watched ? "Read" : "Get")}<{t}>()");
        if (Free(Entity, "Has" + n)) Property(sb, $"Whether the entity has a <see cref=\"{t}\"/>.", $"bool Has{n}", $"@this.Has<{t}>()");
        if (Free(Entity, "Write" + n))
            Member(sb, $"The entity's <see cref=\"{t}\"/> by <c>ref</c> for writing (<c>Get</c>): stamped as changed. Throws if missing.",
                $"ref {t} Write{n}() => ref @this.Get<{t}>();");
        if (Free(Entity, "TryGet" + n))
            Member(sb, $"A copy of the entity's <see cref=\"{t}\"/> if it has one (not a write).",
                $"bool TryGet{n}(out {t} value) => @this.TryGet(out value);");
        if (Free(Entity, "GetOrAdd" + n))
            Member(sb, $"The entity's <see cref=\"{t}\"/> by <c>ref</c>, added (default) first if missing (<c>GetOrAdd</c>).",
                $"ref {t} GetOrAdd{n}() => ref @this.GetOrAdd<{t}>();");
        if (Free(Entity, "GetOrAdd" + n))
            foreach (var setter in m.Setters)
                if (setter.Parameters.Length > 0)
                    Member(sb, $"The entity's <see cref=\"{t}\"/> by <c>ref</c>, added with this value first if missing (<c>GetOrAdd</c>); an entity that has it keeps its own.",
                        $"ref {t} GetOrAdd{n}({setter.Parameters}) => ref @this.GetOrAdd({setter.Value});");
        if (Free(Entity, "Disable" + n))
            Member(sb, $"Switches the entity's <see cref=\"{t}\"/> off, keeping its value (<c>Disable</c>); false if it has none enabled.",
                $"bool Disable{n}() => @this.Disable<{t}>();");
        if (Free(Entity, "Enable" + n))
            Member(sb, $"Switches the entity's disabled <see cref=\"{t}\"/> back on with its kept value (<c>Enable</c>); false if none is disabled.",
                $"bool Enable{n}() => @this.Enable<{t}>();");
        if (Free(Entity, "Is" + n + "Disabled"))
            Property(sb, $"Whether the entity holds a disabled <see cref=\"{t}\"/> (<c>IsDisabled</c>); <c>Has{n}</c> is whether it is enabled.",
                $"bool Is{n}Disabled", $"@this.IsDisabled<{t}>()");
        if (Free(Entity, "Set" + n))
            foreach (var setter in m.Setters)
                Member(sb, $"Adds or overwrites the entity's <see cref=\"{t}\"/> (<c>Set</c>); returns the entity.",
                    $"global::EasyEcs.Entity Set{n}({setter.Parameters}) => @this.Set({setter.Value});");
        if (Free(Entity, "Set" + n + "IfChanged"))
            foreach (var setter in m.Setters)
                Member(sb, $"Sets the entity's <see cref=\"{t}\"/> unless it already has this value (<c>SetIfChanged</c>): true when it set it; an equal value is not written, stamped or indexed.",
                    $"bool Set{n}IfChanged({setter.Parameters}) => @this.SetIfChanged({setter.Value});");
        if (Free(Entity, "Remove" + n))
            Member(sb, $"Removes the entity's <see cref=\"{t}\"/> if it has one; returns the entity.",
                $"global::EasyEcs.Entity Remove{n}() => @this.Remove<{t}>();");
        sb.Append("    }\n");

        if (Free(Filter, "In" + n))
        {
            Extension(sb, Filter);
            Member(sb, $"Also requires <see cref=\"{t}\"/> group <paramref name=\"group\"/> (<c>InGroup&lt;{n}&gt;</c>): in a world that groups by it, the walk visits that group's tables only.",
                $"global::EasyEcs.QueryFilter In{n}(int group) => @this.InGroup<{t}>(group);");
            sb.Append("    }\n");
        }

        if (Free(World, n + "Index"))
        {
            Extension(sb, World);
            Property(sb, $"The world's index of <see cref=\"{t}\"/> (<c>Index&lt;{n}, …&gt;()</c>); keep it in a local or field in loops.",
                $"global::EasyEcs.ValueIndex<{t}, {m.IndexedKey}> {n}Index", $"@this.Index<{t}, {m.IndexedKey}>()");
            sb.Append("    }\n");
        }

        // A prefab belongs to no world: its writers change no world's structure, so their class carries another
        // tool name than the one Symbols.IsGenerated looks for. A recipe never holds the world's own tag.
        if (!s.Writes) return sb.Append("}\n").ToString();
        sb.Append("}\n\n");
        Open(sb, $"The named members of <see cref=\"{t}\"/> on a <c>Prefab</c> (generated).", Symbols.GeneratorName + ".Prefab", a,
            m.Path + Classes.Prefab, Ecs(Prefab));
        if (Free(Prefab, "Is" + n)) Property(sb, $"Whether the prefab has the tag <see cref=\"{t}\"/>.", $"bool Is{n}", $"@this.Has<{t}>()");
        if (Free(Prefab, n))
            Property(sb, $"The prefab's <see cref=\"{t}\"/>, read-only (<c>Read</c>); change it with <c>Set</c>. Throws if missing.",
                $"ref readonly {t} {Code.Escape(n)}", $"ref @this.Read<{t}>()");
        if (Free(Prefab, "Has" + n)) Property(sb, $"Whether the prefab has a <see cref=\"{t}\"/>.", $"bool Has{n}", $"@this.Has<{t}>()");
        if (Free(Prefab, "Set" + n))
            foreach (var setter in m.Setters)
                Member(sb, $"Adds <see cref=\"{t}\"/> to the recipe or replaces its value; returns the prefab.",
                    $"global::EasyEcs.Prefab Set{n}({setter.Parameters}) => @this.Set({setter.Value});");
        if (Free(Prefab, "Remove" + n))
            Member(sb, $"Drops <see cref=\"{t}\"/> from the recipe if it has it; returns the prefab.",
                $"global::EasyEcs.Prefab Remove{n}() => @this.Remove<{t}>();");
        return sb.Append("    }\n}\n").ToString();
    }

    private static string EmitBuffer(BufferModel m)
    {
        var sb = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (m.Namespace.Length > 0) sb.Append($"namespace {m.Namespace};\n\n");
        string a = m.IsPublic ? "public" : "internal", e = m.Element, n = m.Name, t = $"global::EasyEcs.DynamicBuffer<{e}>";
        bool Free(string receiver, string name) => m.Members.Contains(receiver + "." + name);

        Open(sb, null, Symbols.GeneratorName, a, m.Path + Classes.Buffer, Ecs(Entity));
        if (Free(Entity, n))
            Property(sb, $"The entity's buffer of <see cref=\"{e}\"/>, read-only (<c>Read</c>: never a write); write it through <c>{(m.Relation ? "Link / Unlink" : $"Write{n}()")}</c>. Throws if missing.",
                $"ref readonly {t} {Code.Escape(n)}", $"ref @this.Read<{t}>()");
        if (Free(Entity, "Has" + n))
            Property(sb, $"Whether the entity has a buffer of <see cref=\"{e}\"/>.", $"bool Has{n}", $"@this.Has<{t}>()");
        if (Free(Entity, "Write" + n)) // links are indexed: written with Link / Unlink
            Member(sb, $"The entity's buffer of <see cref=\"{e}\"/> by <c>ref</c> for writing (<c>Get</c>): stamped as changed. Throws if missing.",
                $"ref {t} Write{n}() => ref @this.Get<{t}>();");
        if (Free(Entity, "GetOrAdd" + n))
            Member(sb, $"The entity's buffer of <see cref=\"{e}\"/> by <c>ref</c>, added (empty) first if missing (<c>GetOrAdd</c>).",
                $"ref {t} GetOrAdd{n}() => ref @this.GetOrAdd<{t}>();");
        if (Free(Entity, "Remove" + n))
            Member(sb, $"Removes the entity's buffer of <see cref=\"{e}\"/> if it has one; returns the entity.",
                $"global::EasyEcs.Entity Remove{n}() => @this.Remove<{t}>();");
        if (Free(Entity, "Link" + n))
            foreach (var setter in m.Setters)
                Member(sb, $"Links the entity with a <see cref=\"{e}\"/> (<c>Link</c>: the buffer, added if missing, and the index); returns the entity.",
                    $"global::EasyEcs.Entity Link{n}({setter.Parameters}) => @this.Link({setter.Value});");
        if (Free(Entity, "Unlink" + n))
            Member(sb, $"Drops the entity's <see cref=\"{e}\"/> links to <paramref name=\"target\"/> (<c>Unlink</c>; <c>Remove{n}()</c> drops the whole buffer); returns how many went.",
                $"int Unlink{n}(global::EasyEcs.EntityId target) => @this.Unlink<{e}>(target);");
        if (Free(Entity, "Links" + n))
            Member(sb, $"Whether the entity links to <paramref name=\"target\"/> with a <see cref=\"{e}\"/> (<c>Links</c>).",
                $"bool Links{n}(global::EasyEcs.EntityId target) => @this.Links<{e}>(target);");
        sb.Append("    }\n");

        if (Free(World, n + "Index"))
        {
            Extension(sb, World);
            Property(sb, $"The world's index of <see cref=\"{e}\"/> links by target (<c>Relations&lt;{n}&gt;()</c>); keep it in a local or field in loops.",
                $"global::EasyEcs.RelationIndex<{e}> {n}Index", $"@this.Relations<{e}>()");
            sb.Append("    }\n");
        }

        // A recipe's links would name the prefab's own rows: a relation is added to the instances. A prefab belongs to
        // no world: another tool name, as for components.
        if (m.Relation) return sb.Append("}\n").ToString();
        sb.Append("}\n\n");
        Open(sb, null, Symbols.GeneratorName + ".Prefab", a, m.Path + Classes.PrefabBuffer, Ecs(Prefab));
        if (Free(Prefab, n))
            Property(sb, $"The prefab's buffer of <see cref=\"{e}\"/>, read-only (<c>Read</c>); change it with <c>Set{n}</c>. Throws if missing.",
                $"ref readonly {t} {Code.Escape(n)}", $"ref @this.Read<{t}>()");
        if (Free(Prefab, "Has" + n))
            Property(sb, $"Whether the prefab has a buffer of <see cref=\"{e}\"/>.", $"bool Has{n}", $"@this.Has<{t}>()");
        if (Free(Prefab, "Set" + n))
            Member(sb, $"Gives the recipe a buffer of these <see cref=\"{e}\"/> (<c>SetBuffer</c>), replacing the one it has: every instance gets a list of its own. Returns the prefab.",
                $"global::EasyEcs.Prefab Set{n}(params global::System.ReadOnlySpan<{e}> items) => @this.SetBuffer(items);");
        if (Free(Prefab, "Remove" + n))
            Member(sb, $"Drops the recipe's buffer of <see cref=\"{e}\"/> if it has one; returns the prefab.",
                $"global::EasyEcs.Prefab Remove{n}() => @this.Remove<{t}>();");
        return sb.Append("    }\n}\n").ToString();
    }

    /// <summary>
    /// The suffixes of the generated classes, after the type's flat name (<c>HealthEcsExtensions</c>). Each begins with
    /// the one <c>Ecs</c> marker, so none ends another: <c>Loot</c>'s and <c>LootPrefab</c>'s classes never share a name.
    /// </summary>
    internal static class Classes
    {
        public const string Members = "EcsExtensions", Prefab = "EcsPrefabExtensions", Buffer = "EcsBufferExtensions",
            PrefabBuffer = "EcsPrefabBufferExtensions";
    }

    // The receivers of generated members, as Names qualifies them (and as EasyEcs names its types).
    private const string Entity = "Entity", World = "World", Prefab = "Prefab", Filter = "QueryFilter";

    /// <summary>What a type's named members are: a component's, a singleton's, a buffer element's or a relation's.</summary>
    private enum Kind { Component, Singleton, Element, Relation }

    /// <summary>
    /// What decides which named members a type gets, symbol-free: its kind and how the world treats a component
    /// (<paramref name="Watched"/>: tracked, indexed or grouped, read through <c>Read</c>; <paramref name="Writable"/>:
    /// tracked only, so it gets <c>WriteX()</c>; <paramref name="Disables"/>: neither a cleanup component nor EasyEcs's
    /// own; <paramref name="Writes"/>: not the world's own tag).
    /// </summary>
    private sealed record Shape(Kind Kind, bool Tag, bool Watched, bool Writable, bool Grouped, bool Indexed, bool Disables,
        bool Writes, bool Comparable)
    {
        /// <summary>
        /// The shape of a type that gets named members (a struct, or an enum some <c>DynamicBuffer</c> may hold: any
        /// struct or enum counts), or null. <paramref name="core"/>: EasyEcs's assembly, whose own components are never
        /// disabled.
        /// </summary>
        public static Shape? Of(INamedTypeSymbol type, IAssemblySymbol core)
        {
            if (type.TypeKind is not (TypeKind.Struct or TypeKind.Enum) || !Symbols.IsExtensible(type, out _)) return null;
            bool singleton = Symbols.Implements(type, Symbols.Singleton);
            if (!singleton && !Symbols.Implements(type, Symbols.Component))
                return new Shape(Symbols.Implements(type, "EasyEcs.IRelation") ? Kind.Relation : Kind.Element,
                    false, false, false, false, false, false, true, false);
            bool tag = Symbols.IsTag(type);
            bool indexed = Symbols.IndexedKey(type, out _) != null, grouped = Symbols.IsGrouped(type);
            bool tracked = Symbols.Implements(type, Symbols.TrackChanges);
            bool own = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, core);
            return new Shape(singleton ? Kind.Singleton : Kind.Component, tag, tracked || indexed || grouped,
                tracked && !indexed && !grouped, grouped, indexed,
                // A cleanup component and ChildOf are never disabled (EECS048's rule), nor EasyEcs's other own
                // components; EasyEcs's own tag (Retired) is the world's to add and remove: it is only read.
                Symbols.IsDisableable(type) && !own, !(own && tag),
                Symbols.IsComparable(type)); // what SetIfChanged compares (EECS040's rule)
        }

        /// <summary>
        /// Every member a type of this shape named <paramref name="n"/> is offered, as its receiver and name
        /// (<c>(Entity, HasHealth)</c>, <c>(World, ZoneIndex)</c>): what its file emits unless the name is taken.
        /// </summary>
        public List<(string Receiver, string Name)> Names(string n)
        {
            var names = new List<(string, string)>(24);
            void Add(string receiver, string name) => names.Add((receiver, name));
            switch (Kind)
            {
                case Kind.Singleton:
                    if (!Tag) Add(World, n);
                    Add(World, "Has" + n);
                    if (!Tag) Add(World, "GetOrAdd" + n);
                    Add(World, "Set" + n);
                    Add(World, "Remove" + n);
                    break;
                case Kind.Element or Kind.Relation:
                    Add(Entity, n);
                    Add(Entity, "Has" + n);
                    if (Kind == Kind.Element) // a relation's links are indexed: written with Link / Unlink
                    {
                        Add(Entity, "Write" + n);
                        Add(Entity, "GetOrAdd" + n);
                    }

                    Add(Entity, "Remove" + n);
                    if (Kind == Kind.Relation)
                    {
                        Add(Entity, "Link" + n);
                        Add(Entity, "Unlink" + n);
                        Add(Entity, "Links" + n);
                        Add(World, n + "Index");
                        break;
                    }

                    Add(Prefab, n);
                    Add(Prefab, "Has" + n);
                    Add(Prefab, "Set" + n);
                    Add(Prefab, "Remove" + n);
                    break;
                default:
                    if (Tag) Add(Entity, "Is" + n);
                    else
                    {
                        Add(Entity, n);
                        Add(Entity, "Has" + n);
                        if (Writable) Add(Entity, "Write" + n);
                        Add(Entity, "TryGet" + n);
                        if (!Indexed && !Grouped) Add(Entity, "GetOrAdd" + n);
                        if (Disables)
                        {
                            Add(Entity, "Disable" + n);
                            Add(Entity, "Enable" + n);
                            Add(Entity, "Is" + n + "Disabled");
                        }
                    }

                    if (Writes) Add(Entity, "Set" + n);
                    // A watched component's writer that skips equal values: no stamp, no index update.
                    if (Watched && !Tag && Comparable) Add(Entity, "Set" + n + "IfChanged");
                    if (Writes) Add(Entity, "Remove" + n);
                    if (Grouped) Add(Filter, "In" + n);
                    if (Indexed) Add(World, n + "Index");
                    if (!Writes) break;
                    if (Tag) Add(Prefab, "Is" + n);
                    else
                    {
                        Add(Prefab, n);
                        Add(Prefab, "Has" + n);
                    }

                    Add(Prefab, "Set" + n);
                    Add(Prefab, "Remove" + n);
                    break;
            }

            return names;
        }

        /// <summary>
        /// The members a type of this shape gets: those <see cref="Names"/> offers, less the names its receivers already
        /// have (<c>Entity.Id</c>, <c>Prefab.SetBuffer</c>) and those another struct or enum of its namespace (at any depth) is
        /// offered too: both would emit them, ambiguous wherever the namespace is imported, so both give them up
        /// (<c>Shield</c>'s <c>IsShieldDisabled</c> and the tag <c>ShieldDisabled</c>'s). A nested type named like another
        /// struct of its namespace gets none at all; the namesake it yields to keeps its own. Only types that get members
        /// count: a private or file-local struct takes nothing from another (<c>Tests.Census</c> and <c>Server.Census</c>).
        /// </summary>
        public string[] Members(INamedTypeSymbol type, INamespaceSymbol easyEcs, Compilation compilation)
        {
            // Every component of a namespace asks on every edit: the namespace's types are indexed by name once per
            // compilation, and only the types whose name some pattern cuts out of one of ours are looked at.
            var names = Names(type.Name);
            var types = TypesOf(type.ContainingNamespace);
            var core = easyEcs.ContainingAssembly;
            bool Offered(INamedTypeSymbol other) => !SymbolEqualityComparer.Default.Equals(other, type)
                && compilation.IsSymbolAccessibleWithin(other, compilation.Assembly) && Of(other, core) != null;
            if (type.ContainingType != null && types.TryGetValue(type.Name, out var namesakes))
                foreach (var other in namesakes)
                    if (Offered(other)) return [];

            var members = new List<string>(names.Count);
            string? last = null;
            INamedTypeSymbol? receiver = null;
            foreach (var name in names)
            {
                if (OfferedToAnother(name)) continue;
                if (name.Receiver != last) receiver = easyEcs.GetTypeMembers(last = name.Receiver).FirstOrDefault();
                if (receiver != null && receiver.GetMembers(name.Name).Length == 0) members.Add(name.Receiver + "." + name.Name);
            }

            return members.ToArray();

            // Another struct or enum of the namespace is offered this name too: it is <prefix><its name><suffix>.
            bool OfferedToAnother((string Receiver, string Name) name)
            {
                foreach (var (on, prefix, suffix) in Patterns)
                {
                    if (on != name.Receiver || name.Name.Length <= prefix.Length + suffix.Length
                        || !name.Name.StartsWith(prefix, System.StringComparison.Ordinal) || !name.Name.EndsWith(suffix, System.StringComparison.Ordinal))
                        continue;
                    string other = name.Name.Substring(prefix.Length, name.Name.Length - prefix.Length - suffix.Length);
                    if (other == type.Name || !types.TryGetValue(other, out var candidates)) continue; // a namesake: handled above
                    foreach (var candidate in candidates)
                        if (Offered(candidate) && Of(candidate, core)!.Names(other).Contains(name)) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Every way a name is built from a type's name, as (receiver, prefix, suffix): <see cref="Names"/> of every
        /// shape, computed once.
        /// </summary>
        private static readonly (string Receiver, string Prefix, string Suffix)[] Patterns = AllPatterns();

        private static (string, string, string)[] AllPatterns()
        {
            const string marker = "\u0001";
            var all = new HashSet<(string, string, string)>();
            foreach (var kind in new[] { Kind.Component, Kind.Singleton, Kind.Element, Kind.Relation })
                for (int f = 0; f < 1 << 8; f++)
                {
                    bool Bit(int i) => (f & (1 << i)) != 0;
                    var shape = new Shape(kind, Bit(0), Bit(1), Bit(2), Bit(3), Bit(4), Bit(5), Bit(6), Bit(7));
                    foreach (var (receiver, name) in shape.Names(marker))
                    {
                        int at = name.IndexOf(marker, System.StringComparison.Ordinal);
                        all.Add((receiver, name.Substring(0, at), name.Substring(at + 1)));
                    }
                }

            return all.ToArray();
        }

        /// <summary>
        /// The structs and enums of <paramref name="ns"/> at any depth, by name: built once per namespace of a
        /// compilation (its symbols live as long as it does).
        /// </summary>
        private static Dictionary<string, List<INamedTypeSymbol>> TypesOf(INamespaceSymbol ns) => Namespaces.GetValue(ns, static ns =>
        {
            var byName = new Dictionary<string, List<INamedTypeSymbol>>(System.StringComparer.Ordinal);
            var pending = new Stack<INamedTypeSymbol>(((INamespaceSymbol)ns).GetTypeMembers());
            while (pending.Count > 0)
            {
                var type = pending.Pop();
                foreach (var nested in type.GetTypeMembers()) pending.Push(nested);
                if (type.TypeKind is not (TypeKind.Struct or TypeKind.Enum)) continue;
                if (!byName.TryGetValue(type.Name, out var list)) byName[type.Name] = list = new List<INamedTypeSymbol>(1);
                list.Add(type);
            }

            return byName;
        });

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<INamespaceSymbol, Dictionary<string, List<INamedTypeSymbol>>> Namespaces = new();
    }

    /// <summary>
    /// The element of a <c>DynamicBuffer&lt;T&gt;</c>: a non-generic struct or an enum declared in this compilation,
    /// visible to it and not itself a component or singleton (those have members of their own). Symbol-free.
    /// <paramref name="Members"/>: the members it gets (<see cref="Shape.Members"/>).
    /// </summary>
    private sealed record BufferModel(string Namespace, string Element, string Name, string Path, string HintName, bool IsPublic,
        bool Relation, EquatableArray<Setter> Setters, EquatableArray<string> Members)
    {
        public static BufferModel? Create(SemanticModel model, INamedTypeSymbol buffer)
        {
            if (buffer.TypeArguments[0] is not INamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Enum } type
                || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, model.Compilation.Assembly)
                || !Symbols.IsExtensible(type, out bool isPublic)
                || Shape.Of(type, buffer.ContainingAssembly) is not { Kind: Kind.Element or Kind.Relation } shape)
                return null;
            string fqn = type.ToDisplayString(Symbols.Fqn);
            bool relation = shape.Kind == Kind.Relation;
            var setters = relation // a link is added from what builds it: LinkFriend(target) for Friend(EntityId Target)
                ? ComponentModel.SettersOf(type, fqn, type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray())
                : [];
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            return new BufferModel(ns, fqn, type.Name, Symbols.FlatName(type), Symbols.HintName(type), isPublic, relation,
                new EquatableArray<Setter>(setters), new EquatableArray<string>(shape.Members(type, buffer.ContainingNamespace, model.Compilation)));
        }
    }

    /// <summary>
    /// The components of a <c>world.Query&lt;T1..Tn&gt;()</c> or <c>world.Read&lt;T1..Tn&gt;()</c> call (closed, nameable
    /// from a generated file of this assembly): what its items' named members return. <paramref name="Hash"/> names the
    /// file and the class; <paramref name="Read"/>: the items of a Read, whose members are all read-only.
    /// </summary>
    private sealed record QueryShape(string Hash, string Item, bool Read, EquatableArray<QueryMember> Members)
    {
        public static QueryShape? Create(SemanticModel model, GenericNameSyntax name, CancellationToken token)
        {
            if (Arguments(model, name, token) is not { } types) return null;
            bool read = name.Identifier.ValueText == "Read";
            var compilation = model.Compilation;
            foreach (var t in types)
                if (t is not INamedTypeSymbol named || Symbols.ContainsTypeParameter(named) || !Symbols.IsNameable(named)
                    || !compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
                    return null;
            // A name two components share, one the item already has (Entity, Component1..n) or a generic type's
            // (DynamicBuffer<Loot>) stays unnamed.
            var members = new List<QueryMember>();
            for (int i = 0; i < types.Length; i++)
            {
                string member = types[i].Name;
                if (types[i] is INamedTypeSymbol { IsGenericType: true } || types.Count(t => t.Name == member) > 1 || member == "Entity"
                    || member.StartsWith("Component", System.StringComparison.Ordinal))
                    continue;
                members.Add(new QueryMember(Code.Escape(member), types[i].ToDisplayString(Symbols.Fqn), i + 1, Symbols.IsIndexed(types[i])));
            }

            if (members.Count == 0) return null;
            string item = (read ? "global::EasyEcs.ReadItem<" : "global::EasyEcs.QueryItem<") + string.Join(", ", types.Select(t => t.ToDisplayString(Symbols.Fqn))) + ">";
            string hash = Code.Fnv(item).ToString("x8");
            // A referenced assembly this one sees the internals of (InternalsVisibleTo) that uses the same shape has the
            // class already: a second would make every member ambiguous.
            foreach (var existing in compilation.GetTypesByMetadataName("EasyEcs." + ClassOf(hash)))
                if (!SymbolEqualityComparer.Default.Equals(existing.ContainingAssembly, compilation.Assembly)
                    && compilation.IsSymbolAccessibleWithin(existing, compilation.Assembly))
                    return null;
            return new QueryShape(hash, item, read, new EquatableArray<QueryMember>(members.ToArray()));
        }

        /// <summary>
        /// The components of <c>world.Query&lt;…&gt;()</c> / <c>world.Read&lt;…&gt;()</c>, or null for another call. Only
        /// the syntax and the type arguments are read, each type bound alone (<see cref="Symbols.TypeNamed"/>): binding
        /// the call would bind the body around it and run its nullable analysis, for every call of every file on every
        /// edit. Unbound, the receiver is not known: a call named so on a receiver (<c>x.Query&lt;…&gt;()</c>,
        /// <c>x?.Read&lt;…&gt;()</c>) with struct components only is taken for a World's (an item's members for a shape no
        /// loop uses are harmless), except a <c>Read</c> of one component whose result is used as a value of it (a member
        /// read after it, taken by <c>ref</c>): <c>entity.Read&lt;T&gt;()</c>.
        /// </summary>
        private static ITypeSymbol[]? Arguments(SemanticModel model, GenericNameSyntax name, CancellationToken token)
        {
            var arguments = name.TypeArgumentList.Arguments;
            if (name.Parent is not (MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax } or MemberBindingExpressionSyntax { Parent: InvocationExpressionSyntax })
                || name.Parent is MemberAccessExpressionSyntax access && access.Name != name
                || name.Identifier.ValueText == "Read" && arguments.Count == 1 && ReadsAValue((InvocationExpressionSyntax)name.Parent.Parent!))
                return null;
            var types = new ITypeSymbol[arguments.Count];
            for (int i = 0; i < types.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                // World.Query's constraints: a struct component each.
                if (Symbols.TypeNamed(model, arguments[i]) is not { IsValueType: true } type || !Symbols.Implements(type, Symbols.Component))
                    return null;
                types[i] = type;
            }

            return types;
        }

        /// <summary>
        /// Whether the result of a call is used as a component's value: a member read after it (<c>e.Read&lt;P&gt;().V</c>,
        /// but not <c>GetEnumerator</c>), or taken by <c>ref</c>. A World's <c>Read</c> is walked, kept or passed instead.
        /// </summary>
        private static bool ReadsAValue(InvocationExpressionSyntax call)
        {
            SyntaxNode use = call;
            while (use.Parent is ParenthesizedExpressionSyntax) use = use.Parent;
            return use.Parent is MemberAccessExpressionSyntax member && member.Expression == use && member.Name.Identifier.ValueText != "GetEnumerator"
                || use.Parent is RefExpressionSyntax
                || use.Parent is ConditionalAccessExpressionSyntax conditional && conditional.Expression == use;
        }

        /// <summary>The name of the class holding a shape's members.</summary>
        public static string ClassOf(string hash) => "QueryItemExtensions_" + hash;
    }

    /// <summary>A named member of a query item: <c>item.{Name}</c> is <c>item.Component{Position}</c>.</summary>
    private sealed record QueryMember(string Name, string Type, int Position, bool ReadOnly);

    private static string EmitQueryItem(QueryShape q)
    {
        var sb = new StringBuilder("// <auto-generated/>\n#nullable enable\nnamespace EasyEcs;\n\n");
        Open(sb, null, Symbols.GeneratorName, "internal", QueryShape.ClassOf(q.Hash), q.Item);
        foreach (var m in q.Members)
            if (q.Read || m.ReadOnly)
                Property(sb, $"The row's <see cref=\"{m.Type}\"/> (<c>Component{m.Position}</c>), read-only: {(q.Read ? "a <c>Read</c> stamps nothing" : "it is indexed, so it is written with <c>Set</c>")}.",
                    $"ref readonly {m.Type} {m.Name}", $"ref @this.Component{m.Position}");
            else
                Property(sb, $"The row's <see cref=\"{m.Type}\"/> (<c>Component{m.Position}</c>), by <c>ref</c>.",
                    $"ref {m.Type} {m.Name}", $"ref @this.Component{m.Position}");
        return sb.Append("    }\n}\n").ToString();
    }

    /// <summary>One way to build the value: the generated method's parameters and the expression it stores.</summary>
    private sealed record Setter(string Parameters, string Value);

    /// <summary>
    /// One component or singleton, symbol-free so the pipeline caches between edits. <paramref name="Members"/>: the
    /// members it gets (<see cref="Shape.Members"/>).
    /// </summary>
    private sealed record ComponentModel(
        string Namespace, string Type, string Name, string Path, string HintName, bool IsPublic, Shape Shape, string IndexedKey,
        bool Unique, bool Direct, EquatableArray<Setter> Setters, EquatableArray<string> Members, EquatableArray<string> EntityIds, string Layout)
    {
        private static readonly SymbolDisplayFormat TypeName = Symbols.Fqn
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        public bool Singleton => Shape.Kind == Kind.Singleton;

        public static ComponentModel? Create(SyntaxNode node, ISymbol? symbol, Compilation compilation, CancellationToken token)
        {
            if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } type) return null;
            // A partial type with a base list on several parts: only the first such part emits (one file per type).
            var first = type.DeclaringSyntaxReferences.First(r => r.GetSyntax(token) is TypeDeclarationSyntax { BaseList: not null });
            if (first.SyntaxTree != node.SyntaxTree || first.Span != node.Span) return null;
            if (!Symbols.Implements(type, Symbols.Singleton) && !Symbols.Implements(type, Symbols.Component)
                || !Symbols.IsExtensible(type, out bool isPublic))
                return null;

            // EasyEcs, found next to the interface the type implements.
            var easyEcs = type.AllInterfaces.FirstOrDefault(i => Symbols.Named(i, Symbols.Component) || Symbols.Named(i, Symbols.Singleton))?.ContainingNamespace;
            if (easyEcs == null || easyEcs.GetTypeMembers(Entity).Length == 0 || easyEcs.GetTypeMembers(World).Length == 0) return null;
            // EasyEcs's own components: the public ones (ChildOf, Retired) get their members in EasyEcs itself, so every
            // assembly referencing it has them; the internal ones get nothing.
            bool core = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, easyEcs.ContainingAssembly);
            if (core && !isPublic || Shape.Of(type, easyEcs.ContainingAssembly) is not { } shape) return null;
            token.ThrowIfCancellationRequested();

            string fqn = type.ToDisplayString(Symbols.Fqn);
            var setters = SettersOf(type, fqn, type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray());
            var key = Symbols.IndexedKey(type, out bool unique);
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            return new ComponentModel(ns, fqn, type.Name, Symbols.FlatName(type), Symbols.HintName(type), isPublic, shape,
                key?.ToDisplayString(Symbols.Fqn) ?? "",
                unique && key != null && !Symbols.Named(key, Symbols.EntityId) && key is not ITypeParameterSymbol,
                type.AllInterfaces.Any(i => Symbols.Named(i.OriginalDefinition, Symbols.DirectIndexed)),
                new EquatableArray<Setter>(setters), new EquatableArray<string>(shape.Members(type, easyEcs, compilation)),
                new EquatableArray<string>(shape.Kind == Kind.Singleton ? [] : Symbols.EntityIdPaths(type, compilation)),
                Symbols.LayoutHash(type) ?? "");
        }

        /// <summary>
        /// One setter per public constructor with parameters (none taken by ref or out, none a pointer, each setting the
        /// type's required members); without such a constructor, one taking every field when all are safe to name and
        /// can be set: public writable fields, and auto-properties with a public <c>set</c> or <c>init</c> (set by the
        /// property's name). None for a tag.
        /// </summary>
        internal static Setter[] SettersOf(INamedTypeSymbol type, string fqn, IFieldSymbol[] fields)
        {
            var ctors = type.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0)
                .ToArray();
            bool required = type.GetMembers().Any(m => m is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true });
            if (ctors.Length > 0)
                return ctors.Where(c => c.Parameters.All(p => p.RefKind is RefKind.None or RefKind.In && !Unsafe(p.Type))
                        && (!required || c.GetAttributes().Any(a => Symbols.Named(a.AttributeClass, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))))
                    .Select(c => new Setter(
                        string.Join(", ", c.Parameters.Select(p => $"{(p.IsParams ? "params " : p.RefKind == RefKind.In ? "in " : "")}{p.Type.ToDisplayString(TypeName)} {ParameterName(p.Name)}{Default(p)}")),
                        $"new {fqn}({string.Join(", ", c.Parameters.Select(p => ParameterName(p.Name)))})"))
                    .ToArray();
            if (fields.Length == 0) return [new Setter("", $"default({fqn})")];

            // What an object initializer sets for each field: the field, or the auto-property it backs.
            var settable = new (string Name, ITypeSymbol Type)[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.IsFixedSizeBuffer || Unsafe(f.Type)) return [];
                if (f.AssociatedSymbol is IPropertySymbol { DeclaredAccessibility: Accessibility.Public, IsIndexer: false,
                        SetMethod.DeclaredAccessibility: Accessibility.Public } property)
                    settable[i] = (property.Name, property.Type);
                else if (f.DeclaredAccessibility == Accessibility.Public && !f.IsReadOnly && !f.IsImplicitlyDeclared)
                    settable[i] = (f.Name, f.Type);
                else return [];
            }

            var names = settable.Select(s => ParameterName(char.ToLowerInvariant(s.Name[0]) + s.Name.Substring(1))).ToArray();
            if (names.Distinct().Count() < names.Length) return []; // X and x: one parameter name for both
            return [new Setter(
                string.Join(", ", settable.Select((s, i) => $"{s.Type.ToDisplayString(TypeName)} {names[i]}")),
                $"new {fqn} {{ {string.Join(", ", settable.Select((s, i) => $"{Code.Escape(s.Name)} = {names[i]}"))} }}")];
        }

        /// <summary>A parameter's name in a generated writer: escaped, and never the receiver's (<c>@this</c>).</summary>
        private static string ParameterName(string name) => name == "this" ? "_this" : Code.Escape(name);

        private static bool Unsafe(ITypeSymbol type) => type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;

        /// <summary>
        /// A parameter's default value as C# source (Roslyn's display drops literal suffixes and enum type names: 1.5f
        /// would read 1.5, Color.Blue would read Blue).
        /// </summary>
        private static string Default(IParameterSymbol p)
        {
            if (!p.HasExplicitDefaultValue) return "";
            object? value = p.ExplicitDefaultValue;
            if (value is null) return p.Type.IsValueType && p.Type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T ? " = default" : " = null";
            var inner = p.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : p.Type;
            if (inner.TypeKind == TypeKind.Enum) return $" = ({inner.ToDisplayString(TypeName)})({SymbolDisplay.FormatPrimitive(value, false, false)})";
            return " = " + value switch
            {
                float f when float.IsNaN(f) => "float.NaN",
                float f when float.IsInfinity(f) => f > 0 ? "float.PositiveInfinity" : "float.NegativeInfinity",
                double d when double.IsNaN(d) => "double.NaN",
                double d when double.IsInfinity(d) => d > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity",
                float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
                decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
                long l => l + "L",
                ulong u => u + "UL",
                uint u => u + "U",
                _ => SymbolDisplay.FormatPrimitive(value, true, false),
            };
        }
    }
}
