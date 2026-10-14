using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// [Query] methods -> struct job + filter + Run{Method}(Context) + IExecuteSystem.OnExecute.
/// The model is plain equatable data so incremental caching works (no symbols kept across stages).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class QueryGenerator : IIncrementalGenerator
{
    private const string QueryAttribute = "EasyEcs.Core.Systems.QueryAttribute";
    private const int MaxComponents = 9;

    private static readonly DiagnosticDescriptor NotPartial = new("EECS001", "System must be partial",
        "Class '{0}' declares [Query] methods and must be partial (including containing types)", "EasyEcs",
        DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor BadParameter = new("EECS002", "Invalid query parameter",
        "Parameter '{0}' of query '{1}' must be 'ref'/'in' IComponent struct, 'int entityId', 'EntityRef' or 'Context'",
        "EasyEcs", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor ComponentCount = new("EECS003", "Invalid component count",
        "Query '{0}' must take between 1 and 9 components", "EasyEcs", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor GenericSystem = new("EECS004", "Generic systems not supported",
        "Query '{0}' is declared in a generic type or is generic itself", "EasyEcs", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var queries = context.SyntaxProvider.ForAttributeWithMetadataName(QueryAttribute,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, _) => QueryModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode));

        var perClass = queries.Collect().SelectMany(static (all, _) =>
            all.GroupBy(q => q.Owner).Select(g => new ClassModel(g.Key, g.OrderBy(q => q.Order).ToImmutableArray())));

        context.RegisterSourceOutput(perClass, static (spc, cls) => Emit(spc, cls));
    }

    private static void Emit(SourceProductionContext spc, ClassModel cls)
    {
        bool ok = true;
        foreach (var q in cls.Queries)
        {
            foreach (var d in q.Diagnostics)
            {
                spc.ReportDiagnostic(d.ToDiagnostic());
                ok = false;
            }
        }

        if (!ok) return;

        var owner = cls.Owner;
        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing Cysharp.Threading.Tasks;\nusing EasyEcs.Core;\nusing EasyEcs.Core.Systems;\n\n");
        if (owner.Namespace.Length > 0) sb.Append("namespace ").Append(owner.Namespace).Append(";\n\n");

        foreach (var outer in owner.Containers) sb.Append($"partial {outer}\n{{\n");

        string executeInterface = owner.DeclaresOnExecute ? "" : " : IExecuteSystem";
        sb.Append($"partial class {owner.Name}{executeInterface}\n{{\n");

        foreach (var q in cls.Queries) EmitQuery(sb, owner, q);

        if (!owner.DeclaresOnExecute)
        {
            sb.Append("    /// <summary>Runs every [Query] of this system in declaration order.</summary>\n");
            sb.Append("    public UniTask OnExecute(Context context)\n    {\n");
            foreach (var q in cls.Queries) sb.Append($"        Run{q.Name}(context);\n");
            sb.Append("        return UniTask.CompletedTask;\n    }\n");
        }

        sb.Append("}\n");
        foreach (var _ in owner.Containers) sb.Append("}\n");

        spc.AddSource($"{owner.HintName}.Queries.g.cs", sb.ToString());
    }

    private static void EmitQuery(StringBuilder sb, OwnerModel owner, QueryModel q)
    {
        var components = q.Parameters.Where(p => p.Kind == ParamKind.Component).ToArray();
        string types = string.Join(", ", components.Select(c => c.Type));
        string job = $"__{q.Name}Job";

        // Job: forwards each entity to the user's method; the JIT inlines it into ForEach.
        sb.Append($"    private struct {job} : IForEach<{types}>\n    {{\n");
        if (!q.IsStatic) sb.Append($"        public {owner.Name} System;\n");
        sb.Append("        public Context Context;\n\n");
        sb.Append("        [MethodImpl(MethodImplOptions.AggressiveInlining)]\n");
        sb.Append($"        public void Execute(int entityId, {List(components.Length, i => $"ref {components[i - 1].Type} c{i}")})\n");

        int index = 0;
        var args = q.Parameters.Select(p => p.Kind switch
        {
            ParamKind.Component => $"{p.RefKind} c{++index}",
            ParamKind.EntityId => "entityId",
            ParamKind.EntityRef => "Context.GetEntityById(entityId)",
            _ => "Context",
        });
        string target = q.IsStatic ? owner.Name : "System";
        sb.Append($"            => {target}.{q.Name}({string.Join(", ", args)});\n    }}\n\n");

        string filterArg = "";
        if (q.With.Length + q.Without.Length > 0)
        {
            string filter = $"__{q.Name}Filter";
            filterArg = $", in {filter}";
            var chain = q.With.Select(t => $".With<{t}>()").Concat(q.Without.Select(t => $".Without<{t}>()"));
            sb.Append($"    private static readonly QueryFilter {filter} = QueryFilter.Create(){string.Concat(chain)};\n\n");
        }

        sb.Append($"    /// <summary>Run the <see cref=\"{q.Name}\"/> query over <paramref name=\"context\"/>.</summary>\n");
        sb.Append($"    protected void Run{q.Name}(Context context)\n    {{\n");
        sb.Append($"        var job = new {job} {{ {(q.IsStatic ? "" : "System = this, ")}Context = context }};\n");
        sb.Append($"        context.ForEach<{types}, {job}>(ref job{filterArg});\n    }}\n\n");
    }

    // ---------------- models ----------------

    private enum ParamKind
    {
        Component,
        EntityId,
        EntityRef,
        Context,
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

    private sealed record OwnerModel(string Namespace, string Name, EquatableArray<string> Containers, bool DeclaresOnExecute,
        string HintName);

    private sealed record ClassModel(OwnerModel Owner, ImmutableArray<QueryModel> Queries);

    private sealed record QueryModel(OwnerModel Owner, string Name, bool IsStatic, int Order,
        EquatableArray<ParamModel> Parameters, EquatableArray<string> With, EquatableArray<string> Without,
        EquatableArray<DiagnosticModel> Diagnostics)
    {
        private static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat;

        public static QueryModel Create(IMethodSymbol method, MethodDeclarationSyntax syntax)
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

            bool declaresOnExecute = type.GetMembers("OnExecute").Any(m => m is IMethodSymbol { Parameters.Length: 1 });
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            string hint = type.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_');
            var owner = new OwnerModel(ns, type.Name, new EquatableArray<string>(containers.ToArray()), declaresOnExecute, hint);

            return new QueryModel(owner, method.Name, method.IsStatic, syntax.SpanStart,
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
                "EasyEcs.Core.Context" => new ParamModel(ParamKind.Context, typeName, ""),
                _ => null,
            };
        }
    }
}
