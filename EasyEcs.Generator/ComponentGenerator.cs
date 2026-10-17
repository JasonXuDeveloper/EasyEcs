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
/// <c>ref readonly</c> property (<c>Read</c>): reading it never counts as a write, and writes are <c>Set…</c> calls.</item>
/// <item>tag <c>Dead</c> (no fields): <c>entity.IsDead</c>, <c>SetDead()</c>, <c>RemoveDead()</c>.</item>
/// <item>the same writers on a <c>Prefab</c>, returning it for chaining (<c>new Prefab().SetHealth(100).SetNpc()</c>),
/// with <c>prefab.Health</c> (read-only), <c>HasHealth</c> and <c>IsDead</c>.</item>
/// <item>indexed component <c>Zone : IIndexed&lt;int&gt;</c>: <c>world.ZoneIndex</c> (<c>Index&lt;Zone, int&gt;()</c>).</item>
/// <item>singleton <c>Clock</c>: <c>world.Clock</c> (<c>ref</c>, <c>GetSingleton</c>), <c>HasClock</c>,
/// <c>SetClock(…)</c> (adds it if missing), <c>RemoveClock()</c>.</item>
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

        // Saves name types: every component, singleton and DynamicBuffer<T> of the compilation is registered when the
        // module loads (World.Load resolves names through SavedTypes), from C# 9 (module initializers).
        var saved = components
            .Select(static (m, _) => new SavedType((m!.Singleton ? "Singleton" : "Component") + "<" + m.Type + ">", m.Type, m.EntityIds, m.Singleton ? "" : m.Layout))
            .WithTrackingName("SavedComponents");
        var buffers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is GenericNameSyntax { Identifier.ValueText: "DynamicBuffer", TypeArgumentList.Arguments.Count: 1 },
                static (ctx, ct) => SavedBuffer(ctx.SemanticModel, (GenericNameSyntax)ctx.Node, ct))
            .Where(static b => b is not null)
            .Select(static (b, _) => b!)
            .WithTrackingName("SavedBuffers");
        var initializers = context.CompilationProvider
            .Select(static (c, _) => c is CSharpCompilation { LanguageVersion: var version } && (int)version >= 900
                && c.GetTypeByMetadataName("EasyEcs.SavedTypes") is { } savedTypes
                && !SymbolEqualityComparer.Default.Equals(savedTypes.ContainingAssembly, c.Assembly))
            .WithTrackingName("ModuleInitializers");
        context.RegisterSourceOutput(saved.Collect().Combine(buffers.Collect()).Combine(initializers), static (spc, pair) =>
        {
            var types = pair.Left.Left.Concat(pair.Left.Right).Distinct().OrderBy(static t => t.Call, System.StringComparer.Ordinal).ToArray();
            if (pair.Right && types.Length > 0) spc.AddSource("EasyEcs.SavedTypes.g.cs", SourceText.From(EmitSavedTypes(types), Encoding.UTF8));
        });
    }

    /// <summary>The <c>DynamicBuffer&lt;T&gt;</c> a generic name denotes when it is EasyEcs's, closed and accessible here (else null).</summary>
    private static SavedType? SavedBuffer(SemanticModel model, GenericNameSyntax name, CancellationToken token)
    {
        if (model.GetSymbolInfo(name, token).Symbol is not INamedTypeSymbol { IsGenericType: true } type
            || type.OriginalDefinition.ToDisplayString() != Symbols.DynamicBuffer
            || type.TypeArguments[0] is not INamedTypeSymbol element
            || ContainsTypeParameter(element)
            || !model.Compilation.IsSymbolAccessibleWithin(element, model.Compilation.Assembly))
            return null;
        return new SavedType("Component<" + type.ToDisplayString(Symbols.Fqn) + ">", element.ToDisplayString(Symbols.Fqn),
            new EquatableArray<string>(Symbols.EntityIdPaths(element, model.Compilation)),
            // An element of another assembly: its private fields are invisible here, its own module records the hash.
            Symbols.LayoutHash(element) is not { } layout ? ""
            : SymbolEqualityComparer.Default.Equals(element.ContainingAssembly, model.Compilation.Assembly) ? layout : "0");
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
                return $"static (ref {type.Type} value) => {reference}";
            });
            sb.Append($"        global::EasyEcs.SavedTypes.EntityIds<{type.Type}>({string.Join(", ", lambdas)});\n");
        }

        sb.Append("    }\n");
        if (fields.Length > 0) sb.Append('\n').Append(fields);
        sb.Append("}\n");
        return sb.ToString();
    }

    private static readonly string Version = typeof(ComponentGenerator).Assembly.GetName().Version?.ToString() ?? "";

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
                    $"ref {t} {n}", $"ref @this.GetSingleton<{t}>()");
            if (Free("Has" + n))
                Property($"Whether the world has a <see cref=\"{t}\"/> (<c>HasSingleton</c>).", $"bool Has{n}", $"@this.HasSingleton<{t}>()");
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
                        $"ref {(m.Watched ? "readonly " : "")}{t} {n}", $"ref @this.{(m.Watched ? "Read" : "Get")}<{t}>()");
                if (Free("Has" + n)) Property($"Whether the entity has a <see cref=\"{t}\"/>.", $"bool Has{n}", $"@this.Has<{t}>()");
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
                        $"ref readonly {t} {n}", $"ref @this.Read<{t}>()");
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

    /// <summary>One way to build the value: the generated method's parameters and the expression it stores.</summary>
    private sealed record Setter(string Parameters, string Value);

    /// <summary>One component or singleton, symbol-free so the pipeline caches between edits.</summary>
    private sealed record ComponentModel(
        string Namespace, string Type, string Name, string Path, string HintName, bool IsPublic, bool Singleton, bool Tag,
        bool Watched, bool Grouped, bool Cleanup, string IndexKey, bool WorldTaken, EquatableArray<Setter> Setters, EquatableArray<string> Taken,
        EquatableArray<string> EntityIds, string Layout)
    {
        private static readonly SymbolDisplayFormat TypeName = Symbols.Fqn
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private static readonly SymbolDisplayFormat Parameter = TypeName
            .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                                  SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut)
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

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
                if (t.TypeParameters.Length > 0) return null;
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
            var taken = new[] { name, "Has" + name, "Is" + name, "TryGet" + name, "GetOrAdd" + name, "Set" + name, "Remove" + name }
                .Where(n => receiver.GetMembers(n).Length > 0).ToArray();
            var key = type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == Symbols.Indexed)?.TypeArguments[0];
            bool grouped = Symbols.Implements(type, Symbols.Group);

            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            string path = name; // Outer_Inner for nested types: one extension class each
            for (var t = type.ContainingType; t != null; t = t.ContainingType) path = t.Name + "_" + path;
            return new ComponentModel(ns, fqn, name, path, fqn.Replace("global::", "").Replace('.', '_'), isPublic, singleton,
                tag, Symbols.Implements(type, Symbols.TrackChanges) || key != null || grouped, grouped,
                Symbols.Implements(type, Symbols.Cleanup), key?.ToDisplayString(Symbols.Fqn) ?? "",
                world.GetMembers(name + "Index").Length > 0, new EquatableArray<Setter>(setters), new EquatableArray<string>(taken),
                new EquatableArray<string>(singleton ? [] : Symbols.EntityIdPaths(type, compilation)), Symbols.LayoutHash(type) ?? "");
        }

        /// <summary>
        /// One setter per public constructor with parameters (none taken by ref or out); without such a constructor, one
        /// taking every field when all are public and writable (or none for a tag).
        /// </summary>
        private static Setter[] SettersOf(INamedTypeSymbol type, string fqn, IFieldSymbol[] fields)
        {
            var ctors = type.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0)
                .ToArray();
            if (ctors.Length > 0)
                return ctors.Where(c => c.Parameters.All(p => p.RefKind is RefKind.None or RefKind.In))
                    .Select(c => new Setter(
                        string.Join(", ", c.Parameters.Select(p => p.ToDisplayString(Parameter))),
                        $"new {fqn}({string.Join(", ", c.Parameters.Select(p => Escape(p.Name)))})"))
                    .ToArray();
            if (fields.Length == 0) return [new Setter("", $"default({fqn})")];
            if (fields.Any(f => f.DeclaredAccessibility != Accessibility.Public || f.IsReadOnly || f.IsImplicitlyDeclared)) return [];

            var names = fields.Select(f => Escape(char.ToLowerInvariant(f.Name[0]) + f.Name.Substring(1))).ToArray();
            return [new Setter(
                string.Join(", ", fields.Select((f, i) => $"{f.Type.ToDisplayString(TypeName)} {names[i]}")),
                $"new {fqn} {{ {string.Join(", ", fields.Select((f, i) => $"{Escape(f.Name)} = {names[i]}"))} }}")];
        }

        private static string Escape(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        /// <summary>EasyEcs's <c>Entity</c> or <c>World</c>, found next to the interface the type implements.</summary>
        private static INamedTypeSymbol? Core(INamedTypeSymbol type, string name) =>
            type.AllInterfaces.FirstOrDefault(i => i.ToDisplayString() is Symbols.Component or Symbols.Singleton)?.ContainingNamespace
                .GetTypeMembers(name).FirstOrDefault();
    }
}
