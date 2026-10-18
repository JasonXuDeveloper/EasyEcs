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
/// <c>TryGetPosition(out p)</c>, <c>GetOrAddPosition()</c> (not for indexed components), <c>SetPosition(x, y)</c> (one overload per public constructor, or the public fields),
/// <c>RemovePosition()</c>. A component the world watches (<c>ITrackChanges</c>, <c>IIndexed&lt;TKey&gt;</c>) gets a
/// <c>ref readonly</c> property (<c>Read</c>): reading it never counts as a write, and writes are <c>Set…</c> calls; a
/// tracked one that is neither indexed nor grouped also gets <c>WriteHealth()</c> (<c>ref</c>, <c>Get</c>: stamped as
/// changed).</item>
/// <item>buffer element <c>Loot</c> (a struct of this compilation some code names in <c>DynamicBuffer&lt;Loot&gt;</c>):
/// <c>entity.Loot</c> (<c>ref readonly DynamicBuffer&lt;Loot&gt;</c>), <c>HasLoot</c>, <c>WriteLoot()</c>,
/// <c>GetOrAddLoot()</c>, <c>RemoveLoot()</c> (a relation's links, indexed, get no <c>ref</c> members: they are
/// written with <c>AddRelation</c> / <c>RemoveRelation</c>).</item>
/// <item>tag <c>Dead</c> (no fields): <c>entity.IsDead</c>, <c>SetDead()</c>, <c>RemoveDead()</c>.</item>
/// <item>the same writers on a <c>Prefab</c>, returning it for chaining (<c>new Prefab().SetHealth(100).SetNpc()</c>),
/// with <c>prefab.Health</c> (read-only), <c>HasHealth</c> and <c>IsDead</c>.</item>
/// <item>indexed component <c>Zone : IIndexed&lt;int&gt;</c>: <c>world.ZoneIndex</c> (<c>Index&lt;Zone, int&gt;()</c>).</item>
/// <item>singleton <c>Clock</c>: <c>world.Clock</c> (<c>ref</c>, <c>GetSingleton</c>), <c>HasClock</c>,
/// <c>GetOrAddClock()</c> (<c>ref</c>, not for tags), <c>SetClock(…)</c> (adds it if missing), <c>RemoveClock()</c>.</item>
/// </list>
/// Writes are methods, not property setters: C# rejects a setter on a handle that is not a variable (a <c>foreach</c>
/// variable, a call's result). Generic types, types nested in types others cannot see and member names the receiver
/// already has are skipped. Nothing is generated below C# 14.
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

        var supported = context.CompilationProvider
            .Select(static (c, _) => c is CSharpCompilation { LanguageVersion: var version } && (int)version >= 1400)
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

        // Saves name types: every component, singleton and DynamicBuffer<T> of the compilation is registered when the
        // module loads (World.Load resolves names through SavedTypes). C# 14, like the extension members.
        var saved = components
            .Select(static (m, _) => new SavedType((m!.Singleton ? "Singleton" : "Component") + "<" + m.Type + ">", m.Type, m.EntityIds, m.Singleton ? "" : m.Layout))
            .WithTrackingName("SavedComponents");
        var buffers = bufferUses
            .Where(static b => b.Saved is not null)
            .Select(static (b, _) => b.Saved!)
            .WithTrackingName("SavedBuffers");
        var initializers = context.CompilationProvider
            .Select(static (c, _) => c is CSharpCompilation { LanguageVersion: var version } && (int)version >= 1400
                && c.GetTypeByMetadataName("EasyEcs.SavedTypes") is { } savedTypes
                && !SymbolEqualityComparer.Default.Equals(savedTypes.ContainingAssembly, c.Assembly))
            .WithTrackingName("ModuleInitializers");
        // Only the module initializer reaches it: implementation output.
        context.RegisterImplementationSourceOutput(saved.Collect().Combine(buffers.Collect()).Combine(initializers), static (spc, pair) =>
        {
            var types = pair.Left.Left.Concat(pair.Left.Right).Distinct().OrderBy(static t => t.Call, System.StringComparer.Ordinal).ToArray();
            if (pair.Right && types.Length > 0) spc.AddSource("EasyEcs.SavedTypes.g.cs", SourceText.From(EmitSavedTypes(types), Encoding.UTF8));
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
            if (model.GetSymbolInfo(name, token).Symbol is not INamedTypeSymbol { IsGenericType: true } buffer
                || buffer.OriginalDefinition.ToDisplayString() != Symbols.DynamicBuffer)
                return null;
            var use = new BufferUse(BufferModel.Create(model, buffer), Registration(model, buffer));
            return use.Members is null && use.Saved is null ? null : use;
        }

        private static SavedType? Registration(SemanticModel model, INamedTypeSymbol buffer)
        {
            if (buffer.TypeArguments[0] is not INamedTypeSymbol element || ContainsTypeParameter(element)
                || !model.Compilation.IsSymbolAccessibleWithin(element, model.Compilation.Assembly) || !Symbols.IsNameable(element))
                return null;
            return new SavedType("Component<" + buffer.ToDisplayString(Symbols.Fqn) + ">", element.ToDisplayString(Symbols.Fqn),
                new EquatableArray<string>(Symbols.EntityIdPaths(element, model.Compilation)),
                // An element of another assembly: its private fields are invisible here, its own module records the hash.
                Symbols.LayoutHash(element) is not { } layout ? ""
                : SymbolEqualityComparer.Default.Equals(element.ContainingAssembly, model.Compilation.Assembly) ? layout : "0");
        }
    }

    /// <summary>
    /// A registration of the module initializer: <paramref name="Call"/> (<c>Component&lt;T&gt;</c> or
    /// <c>Singleton&lt;T&gt;</c>), and the <c>EntityId</c> fields of <paramref name="Type"/> (the component, or a
    /// buffer's element) a migration rewrites (<see cref="Symbols.EntityIdPaths"/>), and its layout hash when it is
    /// unmanaged (<see cref="Symbols.LayoutHash"/>: its rows are written canonically, empty otherwise).
    /// </summary>
    private sealed record SavedType(string Call, string Type, EquatableArray<string> EntityIds, string Layout);

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter) || named.ContainingType is { } outer && ContainsTypeParameter(outer),
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        _ => false,
    };

    private static string EmitSavedTypes(SavedType[] types)
    {
        var sb = new StringBuilder("// <auto-generated/>\n");
        sb.Append($"[global::System.CodeDom.Compiler.GeneratedCode(\"{Symbols.GeneratorName}\", \"{Version}\")]\n");
        sb.Append("internal static class __EasyEcsSavedTypes\n{\n");
        sb.Append("    /// <summary>Makes this module's components and singletons loadable by name (World.Load), and tells\n");
        sb.Append("    /// World.LoadEntities where their EntityId fields are.</summary>\n");
        sb.Append("    [global::System.Runtime.CompilerServices.ModuleInitializer]\n    internal static void Register()\n    {\n");
        foreach (var type in types) sb.Append($"        global::EasyEcs.SavedTypes.{type.Call}();\n");
        foreach (var type in types.Where(static t => t.Layout.Length > 0).Select(static t => (t.Type, t.Layout)).Distinct())
            sb.Append($"        global::EasyEcs.SavedTypes.Raw<{type.Type}>({type.Layout});\n");

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
            sb.Append($"        global::EasyEcs.SavedTypes.EntityIds<{type.Type}>({string.Join(", ", lambdas)});\n");
        }

        sb.Append("    }\n");
        if (fields.Length > 0) sb.Append('\n').Append(fields);
        sb.Append("}\n");
        return sb.ToString();
    }

    internal static readonly string Version = typeof(ComponentGenerator).Assembly.GetName().Version?.ToString() ?? "";

    private const string Inline =
        "        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n";

    private static string Emit(ComponentModel m)
    {
        var sb = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (m.Namespace.Length > 0) sb.Append($"namespace {m.Namespace};\n\n");
        string a = m.IsPublic ? "public" : "internal";
        string t = m.Type, n = m.Name;
        bool Free(string name) => !m.Taken.Contains(name);
        void Member(string doc, string code)
        {
            sb.Append($"        /// <summary>{doc}</summary>\n");
            sb.Append(Inline);
            sb.Append($"        {a} {code}\n");
        }

        // A property: the attribute goes on its getter.
        void Property(string doc, string declaration, string body)
        {
            sb.Append($"        /// <summary>{doc}</summary>\n");
            sb.Append($"        {a} {declaration}\n        {{\n    {Inline}            get => {body};\n        }}\n");
        }

        sb.Append($"[global::System.CodeDom.Compiler.GeneratedCode(\"{Symbols.GeneratorName}\", \"{Version}\")]\n");
        sb.Append($"{a} static class {m.Path}EcsExtensions\n{{\n");
        if (m.Singleton)
        {
            sb.Append("    extension(global::EasyEcs.World @this)\n    {\n");
            if (!m.Tag && Free(n))
                Property($"The world's <see cref=\"{t}\"/> by <c>ref</c> (<c>GetSingleton</c>); throws if it was not added.",
                    $"ref {t} {ComponentModel.Escape(n)}", $"ref @this.GetSingleton<{t}>()");
            if (Free("Has" + n))
                Property($"Whether the world has a <see cref=\"{t}\"/> (<c>HasSingleton</c>).", $"bool Has{n}", $"@this.HasSingleton<{t}>()");
            if (!m.Tag && Free("GetOrAdd" + n))
                Member($"The world's <see cref=\"{t}\"/> by <c>ref</c>, added (default) first if missing (<c>GetOrAddSingleton</c>).",
                    $"ref {t} GetOrAdd{n}() => ref @this.GetOrAddSingleton<{t}>();");
            if (Free("Set" + n))
                foreach (var s in m.Setters)
                    Member($"Sets the world's <see cref=\"{t}\"/>, adding it if missing.",
                        $"void Set{n}({s.Parameters}) => @this.GetOrAddSingleton<{t}>() = {s.Value};");
            if (Free("Remove" + n))
                Member($"Removes the world's <see cref=\"{t}\"/> (<c>RemoveSingleton</c>); false if it was not added.",
                    $"bool Remove{n}() => @this.RemoveSingleton<{t}>();");
            sb.Append("    }\n");
        }
        else
        {
            sb.Append("    extension(global::EasyEcs.Entity @this)\n    {\n");
            if (m.Tag)
            {
                if (Free("Is" + n)) Property($"Whether the entity has the tag <see cref=\"{t}\"/>.", $"bool Is{n}", $"@this.Has<{t}>()");
            }
            else
            {
                if (Free(n))
                    Property(m.Watched
                            ? $"The entity's <see cref=\"{t}\"/>, read-only (<c>Read</c>: never a write); write it with <c>Set</c>, so the change is stamped and indexed. Throws if missing."
                            : $"The entity's <see cref=\"{t}\"/> by <c>ref</c> (<c>Get</c>); throws if missing.",
                        $"ref {(m.Watched ? "readonly " : "")}{t} {ComponentModel.Escape(n)}", $"ref @this.{(m.Watched ? "Read" : "Get")}<{t}>()");
                if (Free("Has" + n)) Property($"Whether the entity has a <see cref=\"{t}\"/>.", $"bool Has{n}", $"@this.Has<{t}>()");
                if (m.Writable && Free("Write" + n))
                    Member($"The entity's <see cref=\"{t}\"/> by <c>ref</c> for writing (<c>Get</c>): stamped as changed. Throws if missing.",
                        $"ref {t} Write{n}() => ref @this.Get<{t}>();");
                if (Free("TryGet" + n))
                    Member($"A copy of the entity's <see cref=\"{t}\"/> if it has one (not a write).",
                        $"bool TryGet{n}(out {t} value) => @this.TryGet(out value);");
                if (m.IndexKey.Length == 0 && !m.Grouped && Free("GetOrAdd" + n))
                    Member($"The entity's <see cref=\"{t}\"/> by <c>ref</c>, added (default) first if missing (<c>GetOrAdd</c>).",
                        $"ref {t} GetOrAdd{n}() => ref @this.GetOrAdd<{t}>();");
                if (!m.Cleanup && Free("Disable" + n)) // a cleanup component is never disabled (see Entity.Disable)
                    Member($"Switches the entity's <see cref=\"{t}\"/> off, keeping its value (<c>Disable</c>); false if it has none enabled.",
                        $"bool Disable{n}() => @this.Disable<{t}>();");
                if (!m.Cleanup && Free("Enable" + n))
                    Member($"Switches the entity's disabled <see cref=\"{t}\"/> back on with its kept value (<c>Enable</c>); false if none is disabled.",
                        $"bool Enable{n}() => @this.Enable<{t}>();");
                if (!m.Cleanup && Free("Is" + n + "Disabled"))
                    Property($"Whether the entity holds a disabled <see cref=\"{t}\"/> (<c>IsDisabled</c>); <c>Has{n}</c> is whether it is enabled.",
                        $"bool Is{n}Disabled", $"@this.IsDisabled<{t}>()");
            }

            if (Free("Set" + n))
                foreach (var s in m.Setters)
                    Member($"Adds or overwrites the entity's <see cref=\"{t}\"/> (<c>Set</c>); returns the entity.",
                        $"global::EasyEcs.Entity Set{n}({s.Parameters}) => @this.Set({s.Value});");
            if (Free("Remove" + n))
                Member($"Removes the entity's <see cref=\"{t}\"/> if it has one; returns the entity.",
                    $"global::EasyEcs.Entity Remove{n}() => @this.Remove<{t}>();");
            sb.Append("    }\n");

            if (m.Grouped)
            {
                sb.Append("\n    extension(global::EasyEcs.QueryFilter @this)\n    {\n");
                Member($"Also requires <see cref=\"{t}\"/> group <paramref name=\"group\"/> (<c>InGroup&lt;{n}&gt;</c>): in a world that groups by it, the walk visits that group's tables only.",
                    $"global::EasyEcs.QueryFilter In{n}(int group) => @this.InGroup<{t}>(group);");
                sb.Append("    }\n");
            }

            if (m.IndexKey.Length > 0 && !m.WorldTaken)
            {
                sb.Append("\n    extension(global::EasyEcs.World @this)\n    {\n");
                Property($"The world's index of <see cref=\"{t}\"/> (<c>Index&lt;{n}, …&gt;()</c>); keep it in a local or field in loops.",
                    $"global::EasyEcs.ValueIndex<{t}, {m.IndexKey}> {n}Index", $"@this.Index<{t}, {m.IndexKey}>()");
                sb.Append("    }\n");
            }

            // A prefab belongs to no world: its writers change no world's structure, so their class carries another
            // tool name than the one Symbols.IsGenerated looks for.
            sb.Append("}\n\n");
            sb.Append($"[global::System.CodeDom.Compiler.GeneratedCode(\"{Symbols.GeneratorName}.Prefab\", \"{Version}\")]\n");
            sb.Append($"{a} static class {m.Path}PrefabExtensions\n{{\n");
            sb.Append("    extension(global::EasyEcs.Prefab @this)\n    {\n");
            if (m.Tag)
            {
                if (Free("Is" + n)) Property($"Whether the prefab has the tag <see cref=\"{t}\"/>.", $"bool Is{n}", $"@this.Has<{t}>()");
            }
            else
            {
                if (Free(n))
                    Property($"The prefab's <see cref=\"{t}\"/>, read-only (<c>Read</c>); change it with <c>Set</c>. Throws if missing.",
                        $"ref readonly {t} {ComponentModel.Escape(n)}", $"ref @this.Read<{t}>()");
                if (Free("Has" + n)) Property($"Whether the prefab has a <see cref=\"{t}\"/>.", $"bool Has{n}", $"@this.Has<{t}>()");
            }

            if (Free("Set" + n))
                foreach (var s in m.Setters)
                    Member($"Adds <see cref=\"{t}\"/> to the recipe or replaces its value; returns the prefab.",
                        $"global::EasyEcs.Prefab Set{n}({s.Parameters}) => @this.Set({s.Value});");
            if (Free("Remove" + n))
                Member($"Drops <see cref=\"{t}\"/> from the recipe if it has it; returns the prefab.",
                    $"global::EasyEcs.Prefab Remove{n}() => @this.Remove<{t}>();");
            sb.Append("    }\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private static string EmitBuffer(BufferModel m)
    {
        var sb = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (m.Namespace.Length > 0) sb.Append($"namespace {m.Namespace};\n\n");
        string a = m.IsPublic ? "public" : "internal", e = m.Element, n = m.Name, t = $"global::EasyEcs.DynamicBuffer<{e}>";
        bool Free(string name) => !m.Taken.Contains(name);
        void Member(string doc, string code) =>
            sb.Append($"        /// <summary>{doc}</summary>\n{Inline}        {a} {code}\n");

        sb.Append($"[global::System.CodeDom.Compiler.GeneratedCode(\"{Symbols.GeneratorName}\", \"{Version}\")]\n");
        sb.Append($"{a} static class {m.Path}BufferExtensions\n{{\n");
        sb.Append("    extension(global::EasyEcs.Entity @this)\n    {\n");
        if (Free(n))
            sb.Append($"        /// <summary>The entity's buffer of <see cref=\"{e}\"/>, read-only (<c>Read</c>: never a write); write it through <c>{(m.Relation ? "AddRelation / RemoveRelation" : $"Write{n}()")}</c>. Throws if missing.</summary>\n" +
                      $"        {a} ref readonly {t} {ComponentModel.Escape(n)}\n        {{\n    {Inline}            get => ref @this.Read<{t}>();\n        }}\n");
        if (Free("Has" + n))
            sb.Append($"        /// <summary>Whether the entity has a buffer of <see cref=\"{e}\"/>.</summary>\n" +
                      $"        {a} bool Has{n}\n        {{\n    {Inline}            get => @this.Has<{t}>();\n        }}\n");
        if (!m.Relation && Free("Write" + n)) // links are indexed: written with AddRelation / RemoveRelation
            Member($"The entity's buffer of <see cref=\"{e}\"/> by <c>ref</c> for writing (<c>Get</c>): stamped as changed. Throws if missing.",
                $"ref {t} Write{n}() => ref @this.Get<{t}>();");
        if (!m.Relation && Free("GetOrAdd" + n))
            Member($"The entity's buffer of <see cref=\"{e}\"/> by <c>ref</c>, added (empty) first if missing (<c>GetOrAdd</c>).",
                $"ref {t} GetOrAdd{n}() => ref @this.GetOrAdd<{t}>();");
        if (Free("Remove" + n))
            Member($"Removes the entity's buffer of <see cref=\"{e}\"/> if it has one; returns the entity.",
                $"global::EasyEcs.Entity Remove{n}() => @this.Remove<{t}>();");
        if (m.Relation)
        {
            if (Free("Add" + n))
                foreach (var s in m.Setters)
                    Member($"Links the entity with a <see cref=\"{e}\"/> (<c>AddRelation</c>: the buffer, added if missing, and the index); returns the entity.",
                        $"global::EasyEcs.Entity Add{n}({s.Parameters}) => @this.AddRelation({s.Value});");
            if (Free("Remove" + n))
                Member($"Drops the entity's <see cref=\"{e}\"/> links to <paramref name=\"target\"/> (<c>RemoveRelation</c>); returns how many went.",
                    $"int Remove{n}(global::EasyEcs.EntityId target) => @this.RemoveRelation<{e}>(target);");
            if (Free("Has" + n))
                Member($"Whether the entity links to <paramref name=\"target\"/> with a <see cref=\"{e}\"/> (<c>HasRelation</c>).",
                    $"bool Links{n}(global::EasyEcs.EntityId target) => @this.HasRelation<{e}>(target);");
        }

        sb.Append("    }\n");
        if (m.Relation && !m.WorldTaken)
        {
            sb.Append("\n    extension(global::EasyEcs.World @this)\n    {\n");
            sb.Append($"        /// <summary>The world's index of <see cref=\"{e}\"/> links by target (<c>Relations&lt;{n}&gt;()</c>); keep it in a local or field in loops.</summary>\n" +
                      $"        {a} global::EasyEcs.RelationIndex<{e}> {n}Index\n        {{\n    {Inline}            get => @this.Relations<{e}>();\n        }}\n");
            sb.Append("    }\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The element of a <c>DynamicBuffer&lt;T&gt;</c>: a non-generic struct declared in this compilation, visible to
    /// it and not itself a component or singleton (those have members of their own). Symbol-free.
    /// </summary>
    private sealed record BufferModel(string Namespace, string Element, string Name, string Path, string HintName, bool IsPublic,
        bool Relation, EquatableArray<Setter> Setters, bool WorldTaken, EquatableArray<string> Taken)
    {
        public static BufferModel? Create(SemanticModel model, INamedTypeSymbol buffer)
        {
            if (buffer.TypeArguments[0] is not INamedTypeSymbol { TypeKind: TypeKind.Struct } type
                || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, model.Compilation.Assembly)
                || Symbols.Implements(type, Symbols.Component) || Symbols.Implements(type, Symbols.Singleton))
                return null;
            bool isPublic = true;
            for (var t = type; t != null; t = t.ContainingType)
            {
                if (t.TypeParameters.Length > 0 || t.IsFileLocal) return null; // a file type: no other file can name it
                if (t.DeclaredAccessibility == Accessibility.Internal) isPublic = false;
                else if (t.DeclaredAccessibility != Accessibility.Public) return null;
            }

            var entity = buffer.ContainingNamespace.GetTypeMembers("Entity").FirstOrDefault();
            var world = buffer.ContainingNamespace.GetTypeMembers("World").FirstOrDefault();
            if (entity == null || world == null) return null;
            string name = type.Name, fqn = type.ToDisplayString(Symbols.Fqn);
            var taken = new[] { name, "Has" + name, "Write" + name, "GetOrAdd" + name, "Remove" + name, "Add" + name }
                .Where(n => entity.GetMembers(n).Length > 0).ToArray();
            bool relation = Symbols.Implements(type, "EasyEcs.IRelation");
            var setters = relation // a link is added from what builds it: AddFriend(target) for Friend(EntityId Target)
                ? ComponentModel.SettersOf(type, fqn, type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray())
                : [];
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            return new BufferModel(ns, fqn, name, Symbols.FlatName(type), Symbols.HintName(type), isPublic,
                relation, new EquatableArray<Setter>(setters), world.GetMembers(name + "Index").Length > 0, new EquatableArray<string>(taken));
        }
    }

    /// <summary>One way to build the value: the generated method's parameters and the expression it stores.</summary>
    private sealed record Setter(string Parameters, string Value);

    /// <summary>One component or singleton, symbol-free so the pipeline caches between edits.</summary>
    private sealed record ComponentModel(
        string Namespace, string Type, string Name, string Path, string HintName, bool IsPublic, bool Singleton, bool Tag,
        bool Watched, bool Writable, bool Grouped, bool Cleanup, string IndexKey, bool WorldTaken, EquatableArray<Setter> Setters, EquatableArray<string> Taken,
        EquatableArray<string> EntityIds, string Layout)
    {
        private static readonly SymbolDisplayFormat TypeName = Symbols.Fqn
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        public static ComponentModel? Create(SyntaxNode node, ISymbol? symbol, Compilation compilation, CancellationToken token)
        {
            if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } type) return null;
            // A partial type with a base list on several parts: only the first such part emits (one file per type).
            var first = type.DeclaringSyntaxReferences.First(r => r.GetSyntax(token) is TypeDeclarationSyntax { BaseList: not null });
            if (first.SyntaxTree != node.SyntaxTree || first.Span != node.Span) return null;
            bool singleton = Symbols.Implements(type, Symbols.Singleton);
            if (!singleton && !Symbols.Implements(type, Symbols.Component)) return null;

            bool isPublic = true;
            for (var t = type; t != null; t = t.ContainingType)
            {
                if (t.TypeParameters.Length > 0 || t.IsFileLocal) return null; // a file type: no other file can name it
                if (t.DeclaredAccessibility == Accessibility.Internal) isPublic = false;
                else if (t.DeclaredAccessibility != Accessibility.Public) return null;
            }

            var entity = Core(type, "Entity");
            var world = Core(type, "World");
            if (entity == null || world == null || SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, entity.ContainingAssembly))
                return null; // EasyEcs's own internal components get nothing
            token.ThrowIfCancellationRequested();

            string fqn = type.ToDisplayString(Symbols.Fqn);
            var fields = type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
            bool tag = fields.Length == 0;
            var setters = SettersOf(type, fqn, fields);
            string name = type.Name;
            var receiver = singleton ? world : entity;
            var members = new[] { name, "Has" + name, "Is" + name, "TryGet" + name, "GetOrAdd" + name, "Set" + name, "Remove" + name, "Write" + name };
            // A nested component named like another component of its namespace: both would get entity.X, ambiguous
            // wherever the namespace is imported, so the nested one gets no members (the generic API still works).
            var taken = type.ContainingType != null && HasNamesake(type.ContainingNamespace, type)
                ? members : members.Where(n => receiver.GetMembers(n).Length > 0).ToArray();
            var key = type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == Symbols.Indexed)?.TypeArguments[0];
            bool grouped = Symbols.Implements(type, Symbols.Group);
            bool tracked = Symbols.Implements(type, Symbols.TrackChanges);

            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            return new ComponentModel(ns, fqn, name, Symbols.FlatName(type), Symbols.HintName(type), isPublic, singleton,
                tag, tracked || key != null || grouped, tracked && key == null && !grouped, grouped,
                Symbols.Implements(type, Symbols.Cleanup), key?.ToDisplayString(Symbols.Fqn) ?? "",
                world.GetMembers(name + "Index").Length > 0, new EquatableArray<Setter>(setters), new EquatableArray<string>(taken),
                new EquatableArray<string>(singleton ? [] : Symbols.EntityIdPaths(type, compilation)), Symbols.LayoutHash(type) ?? "");
        }

        /// <summary>
        /// One setter per public constructor with parameters (none taken by ref or out, none a pointer, each setting the
        /// type's required members); without such a constructor, one taking every field when all are public, writable
        /// and safe to name (or none for a tag).
        /// </summary>
        internal static Setter[] SettersOf(INamedTypeSymbol type, string fqn, IFieldSymbol[] fields)
        {
            var ctors = type.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0)
                .ToArray();
            bool required = type.GetMembers().Any(m => m is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true });
            if (ctors.Length > 0)
                return ctors.Where(c => c.Parameters.All(p => p.RefKind is RefKind.None or RefKind.In && !Unsafe(p.Type))
                        && (!required || c.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")))
                    .Select(c => new Setter(
                        string.Join(", ", c.Parameters.Select(p => $"{(p.IsParams ? "params " : p.RefKind == RefKind.In ? "in " : "")}{p.Type.ToDisplayString(TypeName)} {ParameterName(p.Name)}{Default(p)}")),
                        $"new {fqn}({string.Join(", ", c.Parameters.Select(p => ParameterName(p.Name)))})"))
                    .ToArray();
            if (fields.Length == 0) return [new Setter("", $"default({fqn})")];
            if (fields.Any(f => f.DeclaredAccessibility != Accessibility.Public || f.IsReadOnly || f.IsImplicitlyDeclared
                    || f.IsFixedSizeBuffer || Unsafe(f.Type)))
                return [];

            var names = fields.Select(f => ParameterName(char.ToLowerInvariant(f.Name[0]) + f.Name.Substring(1))).ToArray();
            if (names.Distinct().Count() < names.Length) return []; // X and x: one parameter name for both
            return [new Setter(
                string.Join(", ", fields.Select((f, i) => $"{f.Type.ToDisplayString(TypeName)} {names[i]}")),
                $"new {fqn} {{ {string.Join(", ", fields.Select((f, i) => $"{Escape(f.Name)} = {names[i]}"))} }}")];
        }

        /// <summary>A parameter's name in a generated writer: escaped, and never the receiver's (<c>@this</c>).</summary>
        private static string ParameterName(string name) => name == "this" ? "_this" : Escape(name);

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

        internal static string Escape(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        private static bool HasNamesake(INamespaceSymbol ns, INamedTypeSymbol type)
        {
            var pending = new Stack<INamedTypeSymbol>(ns.GetTypeMembers());
            while (pending.Count > 0)
            {
                var t = pending.Pop();
                if (t.Name == type.Name && !SymbolEqualityComparer.Default.Equals(t, type)
                    && (Symbols.Implements(t, Symbols.Component) || Symbols.Implements(t, Symbols.Singleton)))
                    return true;
                foreach (var nested in t.GetTypeMembers()) pending.Push(nested);
            }

            return false;
        }

        /// <summary>EasyEcs's <c>Entity</c> or <c>World</c>, found next to the interface the type implements.</summary>
        private static INamedTypeSymbol? Core(INamedTypeSymbol type, string name) =>
            type.AllInterfaces.FirstOrDefault(i => i.ToDisplayString() is Symbols.Component or Symbols.Singleton)?.ContainingNamespace
                .GetTypeMembers(name).FirstOrDefault();
    }
}
