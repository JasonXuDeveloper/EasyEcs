using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// [Each] methods of a system -> one struct job (+ static filter) per method, and an override of
/// SystemBase.RunEach() running them in declaration order (World.Update calls OnUpdate, then RunEach).
/// The model is plain equatable data so incremental caching works (no symbols kept across stages).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EachGenerator : IIncrementalGenerator
{
    private const string EachAttribute = "EasyEcs.Core.Systems.EachAttribute";
    private const int MaxComponents = 9;

    private static readonly DiagnosticDescriptor NotPartial = new("EECS001", "System must be partial",
        "Class '{0}' declares [Each] methods and must be partial (including containing types)", "EasyEcs",
        DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor BadParameter = new("EECS002", "Invalid [Each] parameter",
        "Parameter '{0}' of [Each] method '{1}' must be a 'ref'/'in' IComponent struct, 'int entityId', 'EntityRef' or 'World'",
        "EasyEcs", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor ComponentCount = new("EECS003", "Invalid component count",
        "[Each] method '{0}' must take between 1 and 9 components", "EasyEcs", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor GenericSystem = new("EECS004", "Generic systems not supported",
        "[Each] method '{0}' is declared in a generic type or is generic itself", "EasyEcs", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor NotASystem = new("EECS005", "[Each] outside a system",
        "Class '{0}' declares [Each] methods but does not derive from SystemBase", "EasyEcs", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methods = context.SyntaxProvider.ForAttributeWithMetadataName(EachAttribute,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, _) => EachModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode));

        var perSystem = methods.Collect().SelectMany(static (all, _) =>
            all.GroupBy(m => m.Owner).Select(g => new SystemModel(g.Key, g.OrderBy(m => m.Order).ToImmutableArray())));

        context.RegisterSourceOutput(perSystem, static (spc, system) => Emit(spc, system));
    }

    private static void Emit(SourceProductionContext spc, SystemModel system)
    {
        bool ok = true;
        foreach (var method in system.Methods)
        {
            foreach (var d in method.Diagnostics)
            {
                spc.ReportDiagnostic(d.ToDiagnostic());
                ok = false;
            }
        }

        if (!ok) return;

        var owner = system.Owner;
        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing EasyEcs.Core;\nusing EasyEcs.Core.Systems;\n\n");
        if (owner.Namespace.Length > 0) sb.Append("namespace ").Append(owner.Namespace).Append(";\n\n");

        foreach (var outer in owner.Containers) sb.Append($"partial {outer}\n{{\n");
        sb.Append($"partial class {owner.Name}\n{{\n");

        foreach (var method in system.Methods) EmitMethod(sb, owner, method);

        sb.Append("    /// <summary>Runs this system's [Each] methods in declaration order (called after OnUpdate).</summary>\n");
        sb.Append("    protected override void RunEach()\n    {\n        var world = World;\n");
        foreach (var method in system.Methods) sb.Append($"        Each{method.Name}(world);\n");
        sb.Append("    }\n}\n");
        foreach (var _ in owner.Containers) sb.Append("}\n");

        spc.AddSource($"{owner.HintName}.Each.g.cs", sb.ToString());
    }

    private static void EmitMethod(StringBuilder sb, OwnerModel owner, EachModel method)
    {
        var components = method.Parameters.Where(p => p.Kind == ParamKind.Component).ToArray();
        string types = string.Join(", ", components.Select(c => c.Type));
        string job = $"__{method.Name}Job";

        // Job: forwards each entity to the user's method; the JIT specializes ForEach on it and inlines the call.
        sb.Append($"    private struct {job} : IForEach<{types}>\n    {{\n");
        if (!method.IsStatic) sb.Append($"        public {owner.Name} System;\n");
        sb.Append("        public World World;\n\n");
        sb.Append("        [MethodImpl(MethodImplOptions.AggressiveInlining)]\n");
        sb.Append($"        public void Execute(int entityId, {List(components.Length, i => $"ref {components[i - 1].Type} c{i}")})\n");

        int index = 0;
        var args = method.Parameters.Select(p => p.Kind switch
        {
            ParamKind.Component => $"{p.RefKind} c{++index}",
            ParamKind.EntityId => "entityId",
            ParamKind.EntityRef => "World.GetEntityById(entityId)",
            _ => "World",
        });
        string target = method.IsStatic ? owner.Name : "System";
        sb.Append($"            => {target}.{method.Name}({string.Join(", ", args)});\n    }}\n\n");

        string filterArg = "";
        if (method.With.Length + method.Without.Length > 0)
        {
            string filter = $"__{method.Name}Filter";
            filterArg = $", in {filter}";
            var chain = method.With.Select(t => $".With<{t}>()").Concat(method.Without.Select(t => $".Without<{t}>()"));
            sb.Append($"    private static readonly QueryFilter {filter} = QueryFilter.Create(){string.Concat(chain)};\n\n");
        }

        sb.Append($"    private void Each{method.Name}(World world)\n    {{\n");
        sb.Append($"        var job = new {job} {{ {(method.IsStatic ? "" : "System = this, ")}World = world }};\n");
        sb.Append($"        world.ForEach<{types}, {job}>(ref job{filterArg});\n    }}\n\n");
    }

    // ---------------- models ----------------

    private enum ParamKind
    {
        Component,
        EntityId,
        EntityRef,
        World,
    }

    private sealed record ParamModel(ParamKind Kind, string Type, string RefKind);

    private sealed record DiagnosticModel(DiagnosticDescriptor Descriptor, LocationInfo? Location, string Arg0, string Arg1)
    {
        public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location?.ToLocation(), Arg0, Arg1);
    }

    private sealed record LocationInfo(string Path, Microsoft.CodeAnalysis.Text.TextSpan Span,
        Microsoft.CodeAnalysis.Text.LinePositionSpan Lines)
    {
        public Location ToLocation() => Location.Create(Path, Span, Lines);

        public static LocationInfo? From(Location? location) => location?.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    private sealed record OwnerModel(string Namespace, string Name, EquatableArray<string> Containers, string HintName);

    private sealed record SystemModel(OwnerModel Owner, ImmutableArray<EachModel> Methods);

    private sealed record EachModel(OwnerModel Owner, string Name, bool IsStatic, int Order,
        EquatableArray<ParamModel> Parameters, EquatableArray<string> With, EquatableArray<string> Without,
        EquatableArray<DiagnosticModel> Diagnostics)
    {
        private static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat;

        public static EachModel Create(IMethodSymbol method, MethodDeclarationSyntax syntax)
        {
            var type = method.ContainingType;
            var diagnostics = new List<DiagnosticModel>();
            var location = LocationInfo.From(syntax.Identifier.GetLocation());

            var containers = new List<string>();
            bool allPartial = IsPartial(type);
            for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType)
            {
                containers.Insert(0, $"{(outer.IsValueType ? "struct" : "class")} {outer.Name}");
                allPartial &= IsPartial(outer);
            }

            if (!allPartial)
                diagnostics.Add(new DiagnosticModel(NotPartial, location, type.Name, ""));
            if (method.IsGenericMethod || type.IsGenericType || type.ContainingType?.IsGenericType == true)
                diagnostics.Add(new DiagnosticModel(GenericSystem, location, method.Name, ""));

            bool isSystem = false;
            for (var b = type.BaseType; b != null; b = b.BaseType)
                isSystem |= b.ToDisplayString() == "EasyEcs.Core.Systems.SystemBase";
            if (!isSystem)
                diagnostics.Add(new DiagnosticModel(NotASystem, location, type.Name, ""));

            var parameters = new List<ParamModel>();
            foreach (var p in method.Parameters)
            {
                var model = Classify(p);
                if (model == null)
                    diagnostics.Add(new DiagnosticModel(BadParameter, LocationInfo.From(p.Locations.FirstOrDefault()), p.Name, method.Name));
                else
                    parameters.Add(model);
            }

            int componentCount = parameters.Count(p => p.Kind == ParamKind.Component);
            if (componentCount is < 1 or > MaxComponents)
                diagnostics.Add(new DiagnosticModel(ComponentCount, location, method.Name, ""));

            var with = new List<string>();
            var without = new List<string>();
            foreach (var attribute in method.GetAttributes())
            {
                var cls = attribute.AttributeClass;
                if (cls is not { IsGenericType: true, TypeArguments.Length: 1 }) continue;
                string name = cls.ConstructedFrom.ToDisplayString();
                if (name == "EasyEcs.Core.Systems.WithAttribute<T>") with.Add(cls.TypeArguments[0].ToDisplayString(Fqn));
                if (name == "EasyEcs.Core.Systems.WithoutAttribute<T>") without.Add(cls.TypeArguments[0].ToDisplayString(Fqn));
            }

            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            string hint = type.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_');
            var owner = new OwnerModel(ns, type.Name, new EquatableArray<string>(containers.ToArray()), hint);

            return new EachModel(owner, method.Name, method.IsStatic, syntax.SpanStart,
                new EquatableArray<ParamModel>(parameters.ToArray()), new EquatableArray<string>(with.ToArray()),
                new EquatableArray<string>(without.ToArray()), new EquatableArray<DiagnosticModel>(diagnostics.ToArray()));
        }

        private static bool IsPartial(INamedTypeSymbol type) => type.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax() is TypeDeclarationSyntax t && t.Modifiers.Any(m => m.Text == "partial"));

        private static ParamModel? Classify(IParameterSymbol p)
        {
            string typeName = p.Type.ToDisplayString(Fqn);
            if (p.RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter)
            {
                bool isComponent = p.Type.IsValueType &&
                                   p.Type.AllInterfaces.Any(i => i.ToDisplayString() == "EasyEcs.Core.Components.IComponent");
                return isComponent ? new ParamModel(ParamKind.Component, typeName, p.RefKind == RefKind.Ref ? "ref" : "in") : null;
            }

            if (p.RefKind != RefKind.None) return null;
            return p.Type.ToDisplayString() switch
            {
                "int" => new ParamModel(ParamKind.EntityId, "int", ""),
                "EasyEcs.Core.EntityRef" => new ParamModel(ParamKind.EntityRef, typeName, ""),
                "EasyEcs.Core.World" => new ParamModel(ParamKind.World, typeName, ""),
                _ => null,
            };
        }
    }
}
