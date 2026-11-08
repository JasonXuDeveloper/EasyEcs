using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EasyEcs.Generator;

/// <summary>
/// What a <c>[Prefab]</c> struct declares, in a part of the struct: <c>public static readonly Prefab Prefab</c>, built
/// once from the declaration's defaults (one <c>Prefab.Set</c> per nine components), and the parameterless constructor
/// its field initializers need (unless it declares one). Creation is <c>world.Instantiate(Goblin.Prefab)</c>, with values
/// over the defaults or into a span as <c>Instantiate</c> takes them; a variant is <c>new Prefab(Goblin.Prefab)</c>.
/// Valid declarations only (<see cref="Prefabs.Problem"/>, EECS102).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PrefabGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var prefabs = context.SyntaxProvider
            .ForAttributeWithMetadataName(Prefabs.PrefabAttribute,
                static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, _) => PrefabModel.Create((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static m => m is not null)
            .WithTrackingName("Prefabs");
        context.RegisterSourceOutput(prefabs, static (spc, m) => spc.AddSource($"{m!.HintName}.Prefab.g.cs", SourceText.From(Emit(m), Encoding.UTF8)));
    }

    /// <summary>A field of a declaration: its name, its component, and whether it has an initializer (its default).</summary>
    private sealed record FieldModel(string Name, string Component, bool Initialized);

    /// <param name="Constructor">The part declares the parameterless constructor (the struct declares none).</param>
    private sealed record PrefabModel(string Namespace, EquatableArray<string> Heads, string HintName, string Name,
        EquatableArray<FieldModel> Fields, bool Constructor)
    {
        public static PrefabModel? Create(INamedTypeSymbol type)
        {
            if (Prefabs.Problem(type) != null) return null;
            return new PrefabModel(type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
                new EquatableArray<string>(Parts.Heads(type)), Symbols.HintName(type), type.Name,
                new EquatableArray<FieldModel>(Prefabs.Fields(type).Select(f => new FieldModel(Code.Escape(f.Name),
                    f.Type.ToDisplayString(Symbols.Fqn), Prefabs.Initialized(f))).ToArray()),
                !Prefabs.DeclaresConstructor(type));
        }
    }

    private static string Emit(PrefabModel m)
    {
        var body = new StringBuilder();
        string code = Code.GeneratedCode(Symbols.GeneratorName), name = Code.Escape(m.Name);
        if (m.Constructor)
        {
            // Field initializers need a declared constructor; the fields without one start at default.
            body.Append($"/// <summary>The defaults of a new {m.Name}: each field's initializer, <c>default</c> for a field without one.</summary>\n");
            body.Append($"{code}\npublic {name}()\n{{\n");
            foreach (var f in m.Fields.Where(f => !f.Initialized)) body.Append($"    {f.Name} = default;\n");
            body.Append("}\n\n");
        }

        // Prefab.Set takes up to nine values: one call per nine fields.
        var sets = new StringBuilder();
        for (int i = 0; i < m.Fields.Length; i += Symbols.MaxComponents)
            sets.Append($".Set({string.Join(", ", m.Fields.Skip(i).Take(Symbols.MaxComponents).Select(f => $"in d.{f.Name}"))})");
        body.Append($"/// <summary>\n/// The prefab of a {m.Name}: every component at its default, built once. <c>world.Instantiate({m.Name}.Prefab)</c> creates one\n");
        body.Append($"/// (values given to <c>Instantiate</c> replace the defaults); <c>new Prefab({m.Name}.Prefab)</c> is a variant to change.\n");
        body.Append("/// Shared by every world and thread: change a variant, not this one.\n/// </summary>\n");
        body.Append($"{code}\npublic static readonly global::EasyEcs.Prefab {Prefabs.Member} = __BuildPrefab();\n\n");
        body.Append($"{code}\nprivate static global::EasyEcs.Prefab __BuildPrefab()\n{{\n    var d = new {name}();\n");
        body.Append($"    return new global::EasyEcs.Prefab(){sets};\n}}\n");
        return Code.Part(m.Namespace, m.Heads, "", body.ToString());
    }
}
