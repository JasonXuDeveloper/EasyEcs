using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// Generates the plumbing of partial <c>EcsSystem</c>s: one struct job per <c>[ForEach]</c> method and a
/// <c>GeneratedUpdate</c> override running them.
/// Incremental-generator rules followed: <c>ForAttributeWithMetadataName</c> / cheap syntax predicates as entry points,
/// models are equatable records of strings (no symbols, syntax or locations survive the transform), invalid input is
/// skipped silently (EasyEcsAnalyzer reports it), and every stage has a tracking name for cache tests.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SystemGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(Symbols.ForEachAttribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => ForEachModel.Create((IMethodSymbol)ctx.TargetSymbol, ctx.TargetNode.SpanStart, ct))
            .Where(static m => m is not null)
            .WithTrackingName("ForEachMethods");

        // Group per system: Collect re-runs this cheap step on any change, but each grouped model is equatable, so
        // RegisterSourceOutput only re-emits systems whose methods actually changed.
        var updates = methods.Collect()
            .SelectMany(static (all, _) => all.GroupBy(m => m!.Owner)
                .Select(g => new UpdateModel(g.Key, new EquatableArray<ForEachModel>(g.OrderBy(m => m!.Order).ToArray()!))))
            .WithTrackingName("SystemUpdates");
        context.RegisterSourceOutput(updates, static (spc, model) =>
            spc.AddSource($"{model.Owner.HintName}.ForEach.g.cs", SourceText.From(EmitUpdate(model), Encoding.UTF8)));
    }

    // ---------------- emit ----------------

    private static string EmitUpdate(UpdateModel model)
    {
        var sb = Open(model.Owner, "using System.Runtime.CompilerServices;\n");
        foreach (var method in model.Methods) EmitJob(sb, model.Owner, method);

        sb.Append("    /// <summary>Runs the [ForEach] methods in declaration order (after OnUpdate).</summary>\n");
        sb.Append("    protected override void GeneratedUpdate()\n    {\n        base.GeneratedUpdate();\n        var world = World;\n");
        foreach (var method in model.Methods)
        {
            string filter = method.With.Length + method.Without.Length > 0 ? $", in __{method.Name}Filter" : "";
            string system = method.IsStatic ? "" : "System = this, ";
            sb.Append($"        var __{method.Name} = new __{method.Name}Job {{ {system}World = world }};\n");
            sb.Append($"        world.ForEach<{method.ComponentTypes}, __{method.Name}Job>(ref __{method.Name}{filter});\n");
        }

        sb.Append("    }\n");
        return Close(sb, model.Owner);
    }

    private static void EmitJob(StringBuilder sb, OwnerModel owner, ForEachModel method)
    {
        string job = $"__{method.Name}Job";
        var components = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Component).ToArray();
        int index = 0;
        var args = string.Join(", ", method.Parameters.Select(p => p.Kind switch
        {
            Symbols.ParamKind.Component => $"{p.RefKind} c{++index}",
            Symbols.ParamKind.Entity => "World.GetEntity(entityId)",
            _ => "World",
        }));

        // The JIT specializes World.ForEach on this struct and inlines Execute and the user's method into the loop.
        sb.Append($"    private struct {job} : global::EasyEcs.IForEach<{method.ComponentTypes}>\n    {{\n");
        if (!method.IsStatic) sb.Append($"        public {owner.Name} System;\n");
        sb.Append("        public global::EasyEcs.World World;\n\n");
        sb.Append("        [MethodImpl(MethodImplOptions.AggressiveInlining)]\n");
        sb.Append($"        public void Execute(int entityId, {List(components.Length, i => $"ref {components[i - 1].Type} c{i}")})\n");
        sb.Append($"            => {(method.IsStatic ? owner.Name : "System")}.{method.Name}({args});\n    }}\n\n");

        if (method.With.Length + method.Without.Length == 0) return;
        var chain = method.With.Select(t => $".With<{t}>()").Concat(method.Without.Select(t => $".Without<{t}>()"));
        sb.Append($"    private static readonly global::EasyEcs.QueryFilter __{method.Name}Filter = global::EasyEcs.QueryFilter.Create(){string.Concat(chain)};\n\n");
    }

    private static StringBuilder Open(OwnerModel owner, string usings)
    {
        var sb = new StringBuilder(Header).Append(usings).Append('\n');
        if (owner.Namespace.Length > 0) sb.Append("namespace ").Append(owner.Namespace).Append(";\n\n");
        foreach (var outer in owner.Containers) sb.Append($"partial {outer}\n{{\n");
        return sb.Append($"partial class {owner.Name}\n{{\n");
    }

    private static string Close(StringBuilder sb, OwnerModel owner)
    {
        sb.Append("}\n");
        foreach (var _ in owner.Containers) sb.Append("}\n");
        return sb.ToString();
    }

    // ---------------- models (equatable, symbol-free) ----------------

    private sealed record OwnerModel(string Namespace, string Name, EquatableArray<string> Containers, string HintName)
    {
        public static OwnerModel From(INamedTypeSymbol type)
        {
            var containers = new List<string>();
            for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType)
                containers.Insert(0, $"{(outer.IsValueType ? "struct" : "class")} {outer.Name}");
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            return new OwnerModel(ns, type.Name, new EquatableArray<string>(containers.ToArray()),
                type.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_'));
        }
    }

    private sealed record ParamModel(Symbols.ParamKind Kind, string Type, string RefKind);

    private sealed record ForEachModel(OwnerModel Owner, string Name, bool IsStatic, int Order, string ComponentTypes,
        EquatableArray<ParamModel> Parameters, EquatableArray<string> With, EquatableArray<string> Without)
    {
        public static ForEachModel? Create(IMethodSymbol method, int order, System.Threading.CancellationToken ct)
        {
            if (!Symbols.IsValidForEach(method)) return null;
            ct.ThrowIfCancellationRequested();

            var parameters = method.Parameters.Select(p => new ParamModel(Symbols.Classify(p), p.Type.ToDisplayString(Symbols.Fqn),
                p.RefKind == RefKind.Ref ? "ref" : "in")).ToArray();
            var with = new List<string>();
            var without = new List<string>();
            foreach (var attribute in method.GetAttributes())
            {
                if (attribute.AttributeClass is not { IsGenericType: true } cls) continue;
                string name = cls.ConstructedFrom.ToDisplayString();
                if (name == Symbols.WithAttribute) with.Add(cls.TypeArguments[0].ToDisplayString(Symbols.Fqn));
                else if (name == Symbols.WithoutAttribute) without.Add(cls.TypeArguments[0].ToDisplayString(Symbols.Fqn));
            }

            string components = string.Join(", ", parameters.Where(p => p.Kind == Symbols.ParamKind.Component).Select(p => p.Type));
            return new ForEachModel(OwnerModel.From(method.ContainingType), method.Name, method.IsStatic, order, components,
                new EquatableArray<ParamModel>(parameters), new EquatableArray<string>(with.ToArray()),
                new EquatableArray<string>(without.ToArray()));
        }
    }

    private sealed record UpdateModel(OwnerModel Owner, EquatableArray<ForEachModel> Methods);
}
