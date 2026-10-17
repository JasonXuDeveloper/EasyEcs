using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// Generates the plumbing of partial <c>EcsSystem</c>s: one struct job per <c>[ForEach]</c> method and a
/// <c>GeneratedUpdate</c> override running them, and a <c>GeneratedAccess</c> override declaring the data they read
/// and write (the parallel scheduler batches systems by it).
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
                static (ctx, ct) => ForEachModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel, ct))
            .Where(static m => m is not null)
            .WithTrackingName("ForEachMethods");

        // Group per system: Collect re-runs this cheap step on any change, but each grouped model is equatable, so
        // RegisterSourceOutput only re-emits systems whose methods actually changed.
        var updates = methods.Collect()
            .SelectMany(static (all, _) => all.GroupBy(m => m!.Owner)
                .Select(g => new UpdateModel(g.Key, new EquatableArray<ForEachModel>(g.OrderBy(m => m!.Order).ToArray()!))))
            .WithTrackingName("SystemUpdates");
        context.RegisterSourceOutput(updates, static (spc, model) =>
            spc.AddSource($"{model.Owner.HintName}.ForEach.g.cs",
                SourceText.From(Emit(model), Encoding.UTF8)));
    }

    // ---------------- emit ----------------

    /// <summary>
    /// One file per type with [ForEach] methods, outside the type (which needs no partial): a file-local class holds
    /// an UnsafeAccessor per method (private methods of private types too), its struct job and its filter. A system
    /// type gets a <c>SystemCode</c> that runs the jobs and declares their access, registered by a module initializer;
    /// any other type gets entry points, static extension members of the type.
    /// </summary>
    private static string Emit(UpdateModel model)
    {
        var owner = model.Owner;
        var sb = new StringBuilder(Header).Append("using System.Runtime.CompilerServices;\n\n");
        if (owner.Namespace.Length > 0) sb.Append("namespace ").Append(owner.Namespace).Append(";\n\n");
        if (!owner.IsSystem) EmitRunners(sb, model);

        sb.Append("file static class __ForEach\n{\n");
        foreach (var method in model.Methods)
        {
            EmitAccessor(sb, owner, method);
            EmitJob(sb, method);
        }

        if (owner.IsSystem) EmitSystemCode(sb, model);
        return sb.Append("}\n").ToString();
    }

    /// <summary>The method itself, reached from outside its type: compiled to a direct call, inlined like one.</summary>
    private static void EmitAccessor(StringBuilder sb, OwnerModel owner, ForEachModel method)
    {
        string kind = method.IsStatic ? "StaticMethod" : "Method";
        string parameters = string.Concat(method.Parameters.Select(p => $", {p.Declared} {Escape(p.Name)}"));
        sb.Append($"    [UnsafeAccessor(UnsafeAccessorKind.{kind}, Name = \"{method.Name}\")]\n");
        sb.Append($"    internal static extern {method.Returns} {method.Name}([UnsafeAccessorType(\"{owner.MetadataName}\")] object owner{parameters});\n\n");
    }

    /// <summary>
    /// [ForEach] methods outside a system: each gets an entry point, a static extension member of the type taking the
    /// world (the change tick for [Changed&lt;T&gt;] / [Removed&lt;T&gt;]) and the method's context parameters, which
    /// runs the job once.
    /// </summary>
    private static void EmitRunners(StringBuilder sb, UpdateModel model)
    {
        var owner = model.Owner;
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"{(owner.IsPublic ? "public" : "internal")} static class {owner.FlatName}ForEach\n{{\n");
        sb.Append($"    extension({owner.FullName})\n    {{\n");
        foreach (var method in model.Methods)
        {
            var contexts = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Context).ToArray();
            var names = new HashSet<string>(contexts.Select(p => p.Name));
            string world = Free("world"), since = Free("since");
            var signature = new List<string> { $"global::EasyEcs.World {world}" };
            if (method.Since) signature.Add($"ulong {since}");
            signature.AddRange(contexts.Select(p => $"{p.Declared} {Escape(p.Name)}"));

            string filter = method.Since ? $", __ForEach.{method.Name}Filter.Since({since})" : method.HasFilter ? $", in __ForEach.{method.Name}Filter" : "";
            sb.Append($"        /// <summary>Runs <c>{method.Name}</c> for every matching entity of <paramref name=\"{world}\"/>{(method.Since ? $" whose filtered components changed (or went) at or after tick <paramref name=\"{since}\"/>" : "")}. Owner thread only.</summary>\n");
            sb.Append($"        [global::System.CodeDom.Compiler.GeneratedCode(\"{Symbols.GeneratorName}\", \"{ComponentGenerator.Version}\")]\n");
            sb.Append($"        public static void {method.Name}({string.Join(", ", signature)})\n        {{\n");
            sb.Append($"            var job = new __ForEach.{method.Name}Job{Fields(method, world, null)};\n");
            sb.Append($"            {world}.{method.ForEach}<{method.ComponentTypes}, __ForEach.{method.Name}Job>(ref job{filter});\n        }}\n\n");

            string Free(string name)
            {
                while (names.Contains(name)) name = "_" + name;
                return name;
            }
        }

        sb.Append("    }\n}\n\n");
    }

    /// <summary>
    /// The job's initializer: the system (instance methods), the world, singletons resolved once per pass, and the
    /// caller's values (by reference, unless a parallel job copies them for its threads).
    /// </summary>
    private static string Fields(ForEachModel method, string world, string? system)
    {
        var fields = new List<string>();
        if (system != null && !method.IsStatic) fields.Add($"System = {system}");
        if (method.TakesWorld) fields.Add($"World = {world}");
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            if (p.Kind == Symbols.ParamKind.Singleton)
                fields.Add(method.Parallel ? $"s{i} = {world}.SingletonRef<{p.Type}>()" : $"s{i} = ref {world}.SingletonRef<{p.Type}>().Value");
            else if (p.Kind == Symbols.ParamKind.Context)
                fields.Add(p.RefKind.Length == 0 || method.Parallel ? $"x{i} = {Escape(p.Name)}" : $"x{i} = ref {Escape(p.Name)}");
        }

        return fields.Count == 0 ? "()" : $" {{ {string.Join(", ", fields)} }}";
    }

    private static string Escape(string name) => SyntaxFacts.IsKeywordKind(SyntaxFacts.GetKeywordKind(name)) ? "@" + name : name;

    /// <summary>
    /// The system type's code: Update runs the [ForEach] methods in declaration order (after OnUpdate), Access declares
    /// what they read and write. The runtime runs the code of base types first.
    /// </summary>
    private static void EmitSystemCode(StringBuilder sb, UpdateModel model)
    {
        var owner = model.Owner;
        sb.Append("    private sealed class Code : global::EasyEcs.SystemCode\n    {\n");
        sb.Append("        protected override void Update(global::EasyEcs.EcsSystem system)\n        {\n            var world = system.World;\n");
        foreach (var method in model.Methods)
        {
            // [Changed<T>] / [Removed<T>]: rows written (or that lost T) since this system last ran.
            string filter = method.Since ? $", {method.Name}Filter.Since(system.LastRunTick + 1)" : method.HasFilter ? $", in {method.Name}Filter" : "";
            sb.Append($"            var {method.Name} = new {method.Name}Job{Fields(method, "world", "system")};\n");
            sb.Append($"            world.{method.ForEach}<{method.ComponentTypes}, {method.Name}Job>(ref {method.Name}{filter});\n");
        }

        sb.Append("        }\n\n");
        EmitAccess(sb, model);
        sb.Append("    }\n\n");
        string type = owner.IsVisible ? $"typeof({owner.FullName})" : $"global::System.Type.GetType(\"{owner.MetadataName}\")";
        sb.Append("    [ModuleInitializer]\n");
        sb.Append($"    internal static void Register() => global::EasyEcs.SystemCode.Register({type}, new Code());\n");
    }

    /// <summary>
    /// <c>ref</c> parameters are writes, <c>in</c> parameters reads. Anything that can reach the world beyond its
    /// parameters (Entity / EntitySpan / World parameters, instance methods that see <c>World</c>, an OnUpdate override)
    /// runs alone.
    /// </summary>
    private static void EmitAccess(StringBuilder sb, UpdateModel model)
    {
        sb.Append("        protected override void Access(ref global::EasyEcs.SystemAccess access)\n        {\n");
        bool alone = model.Owner.OverridesOnUpdate || model.Methods.Any(m => !m.IsStatic
            || m.Parameters.Any(p => p.Kind is Symbols.ParamKind.Entity or Symbols.ParamKind.Entities or Symbols.ParamKind.World));
        if (alone)
        {
            sb.Append("            access.RunsAlone();\n        }\n");
            return;
        }

        foreach (var p in model.Methods.SelectMany(m => m.Parameters).Select(p => (p.Kind, p.Type, p.RefKind)).Distinct())
        {
            string kind = p.Kind == Symbols.ParamKind.Singleton ? "Singleton" : "";
            sb.Append($"            access.{(p.RefKind == "ref" ? "Write" : "Read")}{kind}<{p.Type}>();\n");
        }

        // [Changed<T>] and [Removed<T>] read T's change ticks, which writers and removers of T stamp.
        foreach (var type in model.Methods.SelectMany(m => m.Changed.Concat(m.Removed)).Distinct())
            sb.Append($"            access.Read<{type}>();\n");

        sb.Append("        }\n");
    }

    private static void EmitJob(StringBuilder sb, ForEachModel method)
    {
        string job = $"{method.Name}Job";
        var components = method.Query.ToArray();
        int index = 0;
        var args = string.Join(", ", method.Parameters.Select((p, i) => p.Kind switch
        {
            Symbols.ParamKind.Component => $"{p.RefKind} c{++index}",
            Symbols.ParamKind.ComponentSpan => $"c{++index}", // Span<T> converts to ReadOnlySpan<T> for read-only parameters
            Symbols.ParamKind.Singleton => method.Parallel ? $"{p.RefKind} s{i}.Value" : $"{p.RefKind} s{i}",
            Symbols.ParamKind.Entity => "entity",
            Symbols.ParamKind.Entities => "entities",
            Symbols.ParamKind.Optional => $"global::EasyEcs.Maybe<{p.Type}>.{(p.RefKind == "ref" ? "ForWrite" : "ForRead")}(entity)",
            Symbols.ParamKind.Context => $"{(p.RefKind.Length == 0 ? "" : p.RefKind + " ")}x{i}",
            _ => "World",
        }).Prepend(method.IsStatic ? "null" : "System"));

        // The JIT specializes World.ForEach on this struct and inlines Execute and the user's method into the loop.
        string iface = "I" + method.Loop;
        bool stable = method.Stable && !method.IsBatch; // a batch job has no structural path at all
        string marker = stable ? ", global::EasyEcs.INoStructuralChanges" : "";
        // A ref struct holds each singleton as a ref field, a register in the row loop: a SingletonRef's box would be
        // null-checked on every row. Parallel jobs are copied into work items on the heap, so they hold SingletonRefs.
        string kind = method.Parallel ? "struct" : "ref struct";
        sb.Append($"    internal {kind} {job} : global::EasyEcs.{iface}<{method.ComponentTypes}>{marker}\n    {{\n");
        if (!method.IsStatic) sb.Append("        public object System;\n");
        // Only when the method takes the World: an unused field would still be copied into the row loop, where it
        // holds a callee-saved register a job that calls out (a structural change) needs for its loop state.
        if (method.TakesWorld) sb.Append("        public global::EasyEcs.World World;\n");
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            if (p.Kind == Symbols.ParamKind.Singleton) // resolved once per pass, not per entity
                sb.Append(method.Parallel
                    ? $"        public global::EasyEcs.SingletonRef<{p.Type}> s{i};\n"
                    : $"        public ref {p.Type} s{i};\n");
            else if (p.Kind == Symbols.ParamKind.Context) // the caller's value: by reference unless copied for threads
                sb.Append(p.RefKind.Length == 0 || method.Parallel ? $"        public {p.Type} x{i};\n"
                    : $"        public ref {(p.RefKind == "in" ? "readonly " : "")}{p.Type} x{i};\n");
        }
        if (sb[sb.Length - 2] != '{') sb.Append('\n'); // a blank line after the fields, if any
        sb.Append("        [MethodImpl(MethodImplOptions.AggressiveInlining)]\n");
        string first = method.IsBatch ? "global::EasyEcs.EntitySpan entities, " : method.TakesEntity ? "global::EasyEcs.Entity entity, " : "";
        string each = method.IsBatch ? "global::System.Span<{0}> c{1}" : "ref {0} c{1}";
        sb.Append($"        public void Execute({first}{List(components.Length, i => string.Format(each, components[i - 1], i))})\n");
        sb.Append($"            => __ForEach.{method.Name}({args});\n");
        // Its ref parameters (Span<T> for a batch): carried over when the method moves the entity it visits, and
        // stamped when the component tracks changes. The queried [With<T>] component of a method without one is read.
        var queried = method.Parameters.Where(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan).ToArray();
        int writes = 0;
        for (int k = 0; k < components.Length; k++)
            if (queried.Any(p => p.Type == components[k] && p.RefKind == "ref")) writes |= 1 << k;
        sb.Append($"\n        static int global::EasyEcs.{iface}<{method.ComponentTypes}>.Writes => {writes};\n");

        sb.Append("    }\n\n");

        if (!method.HasFilter) return;
        var chain = method.With.Select(t => $".With<{t}>()").Concat(method.Without.Select(t => $".Without<{t}>()"))
            .Concat(method.WithAny.Select(t => $".WithAny<{t}>()")).Concat(method.Changed.Select(t => $".Changed<{t}>(0)")).Concat(method.Removed.Select(t => $".Removed<{t}>(0)"));
        sb.Append($"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Filter = global::EasyEcs.QueryFilter.Create(){string.Concat(chain)};\n\n");
    }

    // ---------------- models (equatable, symbol-free) ----------------

    /// <param name="FullName">The type as C# names it (<c>global::Ns.Outer.Inner</c>).</param>
    /// <param name="MetadataName">The type as the runtime names it (<c>Ns.Outer+Inner</c>), for UnsafeAccessorType.</param>
    /// <param name="IsVisible">Generated code can name the type (it and its containing types are internal or public).</param>
    /// <param name="OverridesOnUpdate">The type or a base type below EcsSystem overrides OnUpdate.</param>
    private sealed record OwnerModel(string Namespace, string FullName, string MetadataName, string FlatName, string HintName,
        bool IsVisible, bool IsPublic, bool OverridesOnUpdate, bool IsSystem)
    {
        public static OwnerModel From(INamedTypeSymbol type)
        {
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            var names = new List<string>();
            for (var t = type; t != null; t = t.ContainingType) names.Insert(0, t.Name);
            bool overridesOnUpdate = false;
            for (var t = type; t != null && t.ToDisplayString() != Symbols.EcsSystem; t = t.BaseType)
                overridesOnUpdate |= t.GetMembers("OnUpdate").Any(m => m is IMethodSymbol { IsOverride: true, Parameters.Length: 0 });
            bool isPublic = true;
            for (var t = type; t != null; t = t.ContainingType) isPublic &= t.DeclaredAccessibility == Accessibility.Public;
            return new OwnerModel(ns, type.ToDisplayString(Symbols.Fqn),
                (ns.Length > 0 ? ns + "." : "") + string.Join("+", names), string.Join("_", names),
                type.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_'),
                Symbols.IsVisible(type), isPublic, overridesOnUpdate, Symbols.IsSystem(type));
        }
    }

    /// <param name="Type">The component, singleton or value type (a span's or Maybe's element).</param>
    /// <param name="RefKind">"ref" or "in" ("" for a context parameter passed by value).</param>
    /// <param name="Declared">The parameter as declared, with its modifier (<c>in global::Health</c>), for the accessor.</param>
    private sealed record ParamModel(Symbols.ParamKind Kind, string Type, string RefKind, string Name, string Declared);

    /// <param name="Stable">
    /// The method cannot make structural changes: it is static, takes no <c>World</c>, and uses its <c>Entity</c>
    /// parameter (if any) only to read (<see cref="ReadOnlyEntityMembers"/>). Its job gets INoStructuralChanges.
    /// </param>
    /// <param name="Parallel">[ForEach(Parallel = true)] on a method that can run on several threads (ForEachParallel).</param>
    private sealed record ForEachModel(OwnerModel Owner, string Name, bool IsStatic, bool Stable, bool Parallel, int Order,
        EquatableArray<string> Query, EquatableArray<ParamModel> Parameters, EquatableArray<string> With,
        EquatableArray<string> Without, EquatableArray<string> WithAny, EquatableArray<string> Changed,
        EquatableArray<string> Removed, string Returns)
    {
        /// <summary>The World method that runs the job.</summary>
        public string ForEach => Loop + (Parallel ? "Parallel" : "");

        public bool HasFilter => With.Length + Without.Length + WithAny.Length + Changed.Length + Removed.Length > 0;

        /// <summary>Filters rows by change tick ([Changed&lt;T&gt;] / [Removed&lt;T&gt;]).</summary>
        public bool Since => Changed.Length + Removed.Length > 0;

        /// <summary>The queried component types, as type arguments of World.ForEach / IForEach.</summary>
        public string ComponentTypes => string.Join(", ", Query);

        /// <summary>Runs per entity with its handle: it takes the Entity, or a Maybe looked up through it.</summary>
        public bool TakesEntity => Parameters.Any(p => p.Kind is Symbols.ParamKind.Entity or Symbols.ParamKind.Optional);

        public bool TakesWorld => Parameters.Any(p => p.Kind == Symbols.ParamKind.World);

        /// <summary>The World loop (and, with an I, the job interface) the method runs as.</summary>
        public string Loop => IsBatch ? "ForEachBatch" : TakesEntity ? "ForEachEntity" : "ForEach";

        /// <summary>Takes component spans: runs over whole runs of rows (World.ForEachBatch).</summary>
        public bool IsBatch => Parameters.Any(p => p.Kind == Symbols.ParamKind.ComponentSpan);

        public static ForEachModel? Create(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
        {
            if (!Symbols.IsValidForEach(method)) return null;
            ct.ThrowIfCancellationRequested();

            // Spans are modeled as their element type: Span<T> writes T, ReadOnlySpan<T> reads it.
            bool outside = Symbols.IsRunner(method);
            var parameters = method.Parameters.Select(p =>
            {
                string declared = (p.RefKind switch { RefKind.Ref => "ref ", RefKind.In => "in ", RefKind.RefReadOnlyParameter => "ref readonly ", _ => "" })
                    + p.Type.ToDisplayString(Symbols.Fqn);
                return Symbols.Classify(p, outside) switch
                {
                    Symbols.ParamKind.ComponentSpan => new ParamModel(Symbols.ParamKind.ComponentSpan,
                        Symbols.SpanElement(p.Type)!.ToDisplayString(Symbols.Fqn),
                        ((INamedTypeSymbol)p.Type).ConstructedFrom.ToDisplayString() == "System.Span<T>" ? "ref" : "in", p.Name, declared),
                    Symbols.ParamKind.Optional => new ParamModel(Symbols.ParamKind.Optional, // a write unless it only reads Value
                        Symbols.MaybeElement(p.Type)!.ToDisplayString(Symbols.Fqn), Symbols.OnlyReadsValue(syntax, p.Name) ? "in" : "ref", p.Name, declared),
                    Symbols.ParamKind.Context => new ParamModel(Symbols.ParamKind.Context, p.Type.ToDisplayString(Symbols.Fqn),
                        p.RefKind switch { RefKind.Ref => "ref", RefKind.In => "in", _ => "" }, p.Name, declared),
                    var kind => new ParamModel(kind, p.Type.ToDisplayString(Symbols.Fqn), p.RefKind == RefKind.Ref ? "ref" : "in", p.Name, declared),
                };
            }).ToArray();
            var with = new List<string>();
            var without = new List<string>();
            var withAny = new List<string>();
            var changed = new List<string>();
            var removed = new List<string>();
            foreach (var attribute in method.GetAttributes())
            {
                if (attribute.AttributeClass is not { IsGenericType: true } cls) continue;
                string name = cls.ConstructedFrom.ToDisplayString();
                if (name == Symbols.WithAttribute) with.Add(cls.TypeArguments[0].ToDisplayString(Symbols.Fqn));
                else if (name == Symbols.WithoutAttribute) without.Add(cls.TypeArguments[0].ToDisplayString(Symbols.Fqn));
                else if (name == Symbols.WithAnyAttribute) withAny.Add(cls.TypeArguments[0].ToDisplayString(Symbols.Fqn));
                else if (name == Symbols.ChangedAttribute) changed.Add(cls.TypeArguments[0].ToDisplayString(Symbols.Fqn));
                else if (name == Symbols.RemovedAttribute) removed.Add(cls.TypeArguments[0].ToDisplayString(Symbols.Fqn));
            }

            var query = parameters.Where(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan)
                .Select(p => p.Type).ToList();
            // No component parameter: the first [With<T>] (else [Changed<T>]) becomes the queried component (passed to the
            // job, not the method).
            if (query.Count == 0)
            {
                if (with.Count == 0) query.Add(changed[0]); // the [Changed<T>] filter keeps its T: it also filters rows
                else
                {
                    query.Add(with[0]);
                    with.RemoveAt(0);
                }
            }
            bool stable = Symbols.IsStable(method, syntax, model);
            bool parallel = Symbols.IsParallel(method) && Symbols.ParallelProblem(method, syntax, model) == null;
            return new ForEachModel(OwnerModel.From(method.ContainingType), method.Name, method.IsStatic, stable, parallel, syntax.SpanStart,
                new EquatableArray<string>(query.ToArray()),
                new EquatableArray<ParamModel>(parameters), new EquatableArray<string>(with.ToArray()),
                new EquatableArray<string>(without.ToArray()), new EquatableArray<string>(withAny.ToArray()),
                new EquatableArray<string>(changed.ToArray()), new EquatableArray<string>(removed.ToArray()),
                method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(Symbols.Fqn));
        }
    }


    private sealed record UpdateModel(OwnerModel Owner, EquatableArray<ForEachModel> Methods);
}
