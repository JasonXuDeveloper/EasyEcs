using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EasyEcs.Generator;

/// <summary>
/// Generates a property on <c>Entity</c> for every component declared in the compilation (C# 14 extension members):
/// <c>player.Position.X += 1</c> instead of <c>player.Get&lt;Position&gt;().X += 1</c>. A component the world watches
/// (<c>ITrackChanges</c>, <c>IIndexed&lt;TKey&gt;</c>) gets a <c>ref readonly</c> property instead (<c>Read</c>): reading
/// it never counts as a write, and writes go through <c>Set</c>, which stamps the change and keeps the index current. (A
/// property setter is not offered: C# rejects assigning one on a handle that is not a variable, such as a <c>foreach</c>
/// variable.)
/// Tags (no fields), generic components, components nested in types others cannot see, and names that an Entity member
/// already takes get no property. Nothing is generated below C# 14.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var components = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null } type
                    && (type is StructDeclarationSyntax || type is RecordDeclarationSyntax { ClassOrStructKeyword.ValueText: "struct" }),
                static (ctx, ct) => ComponentModel.Create(ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node, ct), ct))
            .Where(static m => m is not null)
            .WithTrackingName("Components");

        // C# 14 brings extension members; the property names of Entity are the names a component cannot take.
        var supported = context.CompilationProvider
            .Select(static (c, _) => c is CSharpCompilation { LanguageVersion: var version } && (int)version >= 1400)
            .WithTrackingName("ExtensionMembers");

        context.RegisterSourceOutput(components.Combine(supported), static (spc, pair) =>
        {
            if (pair.Right) spc.AddSource($"{pair.Left!.HintName}.Component.g.cs", SourceText.From(Emit(pair.Left!), Encoding.UTF8));
        });
    }

    private static string Emit(ComponentModel model)
    {
        var sb = new StringBuilder(Code.Header);
        if (model.Namespace.Length > 0) sb.Append($"namespace {model.Namespace};\n\n");
        string access = model.IsPublic ? "public" : "internal";
        sb.Append($"{access} static class {model.Path}EntityExtensions\n{{\n");
        sb.Append("    extension(global::EasyEcs.Entity entity)\n    {\n");
        string inline = "            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n";
        if (model.Watched)
        {
            sb.Append($"        /// <summary>The entity's <see cref=\"{model.Type}\"/>, read-only (<c>Read</c>: never a write). Write it with <c>Set</c>, so the change is stamped and indexed.</summary>\n");
            sb.Append($"        {access} ref readonly {model.Type} {model.Name}\n        {{\n{inline}            get => ref entity.Read<{model.Type}>();\n        }}\n");
        }
        else
        {
            sb.Append($"        /// <summary>The entity's <see cref=\"{model.Type}\"/> by <c>ref</c> (<c>Get</c>).</summary>\n");
            sb.Append($"        {access} ref {model.Type} {model.Name}\n        {{\n{inline}            get => ref entity.Get<{model.Type}>();\n        }}\n");
        }

        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    /// <summary>One component: its namespace, fully qualified type, property name, class name, visibility, and whether the world watches it.</summary>
    private sealed record ComponentModel(string Namespace, string Type, string Name, string Path, bool IsPublic, bool Watched, string HintName)
    {
        public static ComponentModel? Create(ISymbol? symbol, CancellationToken token)
        {
            if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } type
                || !Symbols.Implements(type, Symbols.Component)
                || !type.GetMembers().Any(m => m is IFieldSymbol { IsStatic: false }))
                return null;

            bool isPublic = true;
            for (var t = type; t != null; t = t.ContainingType)
            {
                if (t.TypeParameters.Length > 0) return null;
                if (t.DeclaredAccessibility == Accessibility.Internal) isPublic = false;
                else if (t.DeclaredAccessibility != Accessibility.Public) return null;
            }

            var entity = type.ContainingAssembly is null ? null : FindEntity(type);
            if (entity == null || entity.GetMembers(type.Name).Length > 0) return null; // an Entity member wins: no property
            token.ThrowIfCancellationRequested();

            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            string fqn = type.ToDisplayString(Symbols.Fqn);
            bool watched = Symbols.Implements(type, Symbols.TrackChanges) || Symbols.IsIndexed(type);
            string hint = fqn.Replace("global::", "").Replace('.', '_');
            string path = type.Name; // Outer_Inner for nested components: one extension class each
            for (var t = type.ContainingType; t != null; t = t.ContainingType) path = t.Name + "_" + path;
            return new ComponentModel(ns, fqn, type.Name, path, isPublic, watched, hint);
        }

        private static INamedTypeSymbol? FindEntity(INamedTypeSymbol component) =>
            component.AllInterfaces.FirstOrDefault(i => i.ToDisplayString() == Symbols.Component)?.ContainingNamespace
                .GetTypeMembers("Entity").FirstOrDefault();
    }
}
