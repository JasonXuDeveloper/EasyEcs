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
/// Generates the plumbing of <c>EcsSystem</c>s (no partial needed): one struct job per <c>[ForEach]</c> method and a
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
        // Closed types of generic systems this assembly names: their code is registered without runtime code generation.
        var closed = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => ClosedUse.IsCandidate(node), static (ctx, ct) => ClosedUse.Create(ctx, ct))
            .SelectMany(static (uses, _) => uses)
            .WithTrackingName("ClosedSystems")
            .Collect();
        updates = updates.Combine(closed)
            .Select(static (pair, _) => pair.Left with
            {
                Closed = new EquatableArray<ClosedUse>(pair.Right.Where(c => c.Owner == pair.Left.Owner.FullName)
                    .Distinct().OrderBy(c => c.Closed, System.StringComparer.Ordinal).ToArray()),
            })
            .WithTrackingName("SystemCode");
        // A system's code is reached only through its module initializer: implementation output, which IDEs skip while
        // typing. Entry points of other types are API the user's code calls.
        context.RegisterImplementationSourceOutput(updates.Where(static m => m.Owner.IsSystem), static (spc, model) => Add(spc, model));
        context.RegisterSourceOutput(updates.Where(static m => !m.Owner.IsSystem), static (spc, model) => Add(spc, model));
    }

    private static void Add(SourceProductionContext context, UpdateModel model) =>
        context.AddSource($"{model.Owner.HintName}.ForEach.g.cs", SourceText.From(Emit(model), Encoding.UTF8));

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
        var sb = new StringBuilder(Header).Append('\n');
        if (owner.Namespace.Length > 0) sb.Append("namespace ").Append(owner.Namespace).Append(";\n\n");
        if (!owner.IsSystem) EmitEntryPoints(sb, model);

        // Generic over the owner's type parameters (a generic system or runner type): one instantiation per closed type.
        sb.Append($"file static class __ForEach{owner.TypeParameters}{owner.Constraints}\n{{\n");
        for (int i = 0; i < model.Methods.Length; i++)
        {
            EmitAccessor(sb, owner, model.Methods[i], i);
            EmitJob(sb, owner, model.Methods[i], i);
        }

        if (owner.IsSystem) EmitSystemCode(sb, model);
        sb.Append("}\n");
        if (owner.IsSystem && owner.TypeParameters.Length > 0) EmitGenericRegistration(sb, model);
        return sb.ToString();
    }

    /// <summary>
    /// The method itself, reached from outside its type: compiled to a direct call, inlined like one. Generated names
    /// end in the method's index (<c>Heal_0</c>, <c>HealJob0</c>, <c>HealFilter0</c>): overloads and methods named like a
    /// generated member (<c>Code</c>) cannot collide, and profilers still show the method's name.
    /// </summary>
    private static void EmitAccessor(StringBuilder sb, OwnerModel owner, ForEachModel method, int index)
    {
        string kind = method.IsStatic ? "StaticMethod" : "Method";
        string parameters = string.Concat(method.Parameters.Select(p => $", {p.Declared} {Escape(p.Name)}"));
        // The instance parameter must not take the name of one of the method's own parameters (CS0100).
        string self = "owner";
        while (method.Parameters.Any(p => p.Name == self)) self = "_" + self;
        sb.Append($"    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.{kind}, Name = \"{method.Name}\")]\n");
        sb.Append($"    internal static extern {method.Returns} {method.Name}_{index}{method.TypeParameters}([global::System.Runtime.CompilerServices.UnsafeAccessorType(\"{owner.AccessorType}\")] object {self}{parameters}){method.Constraints};\n\n");
    }

    /// <summary>
    /// [ForEach] methods outside a system: each gets an entry point, a static extension member of the type taking the
    /// world (the change tick for [Changed&lt;T&gt;] / [Removed&lt;T&gt;]) and the method's context parameters, which
    /// runs the job once.
    /// </summary>
    private static void EmitEntryPoints(StringBuilder sb, UpdateModel model)
    {
        var owner = model.Owner;
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"{(owner.IsPublic ? "public" : "internal")} static class {owner.FlatName}ForEach\n{{\n");
        sb.Append($"    extension{owner.TypeParameters}({owner.FullName}){owner.Constraints}\n    {{\n");
        for (int m = 0; m < model.Methods.Length; m++)
        {
            var method = model.Methods[m];
            var contexts = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Context).ToArray();
            var names = new HashSet<string>(contexts.Select(p => p.Name).Concat(method.TypeParameterNames));
            string world = Free("world"), since = Free("since"), slice = Free("slice"), local = Free("job");
            var signature = new List<string> { $"global::EasyEcs.World {world}" };
            if (method.Since) signature.Add($"ulong {since}");
            if (method.Slices > 1) signature.Add($"int {slice}");
            signature.AddRange(contexts.Select(p => $"{p.Declared} {Escape(p.Name)}"));

            string forEach = "__ForEach" + owner.TypeParameters, job = $"{forEach}.{method.Name}Job{m}{method.TypeParameters}";
            string filter = method.Since ? $", {forEach}.{method.Name}Filter{m}.Since({since})" : method.HasFilter ? $", in {forEach}.{method.Name}Filter{m}" : "";
            if (method.Slices > 1) filter = method.Sliced($"{forEach}.{method.Name}Filter{m}", slice);
            string which = method.Since ? $" whose filtered components changed (or went) at or after tick <paramref name=\"{since}\"/>"
                : method.Slices > 1 ? $" in slice <paramref name=\"{slice}\"/> (0 to {method.Slices - 1}) of each matching table's rows" : "";
            sb.Append($"        /// <summary>Runs <c>{method.Name}</c> for every matching entity of <paramref name=\"{world}\"/>{which}. Owner thread only.</summary>\n");
            sb.Append($"        {Code.GeneratedCode(Symbols.GeneratorName)}\n");
            sb.Append($"        {(method.PublicEntry ? "public" : "internal")} static void {Escape(method.Name)}{method.TypeParameters}({string.Join(", ", signature)}){method.Constraints}\n        {{\n");
            sb.Append($"            var {local} = new {job}{Fields(method, world, null)};\n");
            sb.Append($"            {world}.{method.ForEach}<{method.ComponentTypes}, {job}>(ref {local}{filter});\n        }}\n\n");

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

    /// <summary>
    /// The system type's code: Update runs the [ForEach] methods in declaration order (after OnUpdate), Access declares
    /// what they read and write. The runtime runs the code of base types first.
    /// </summary>
    private static void EmitSystemCode(StringBuilder sb, UpdateModel model)
    {
        var owner = model.Owner;
        sb.Append("    internal sealed class Code : global::EasyEcs.SystemCode\n    {\n");
        sb.Append("        protected override void Update(global::EasyEcs.EcsSystem system)\n        {\n            var world = system.World;\n");
        for (int i = 0; i < model.Methods.Length; i++)
        {
            var method = model.Methods[i];
            // [Changed<T>] / [Removed<T>]: rows written (or that lost T) since this system last ran.
            string filter = method.Since ? $", {method.Name}Filter{i}.Since(system.LastRunTick + 1)" : method.HasFilter ? $", in {method.Name}Filter{i}" : "";
            string whole = filter;
            if (method.Slices > 1) filter = method.Sliced($"{method.Name}Filter{i}", $"(int)(system.RunCount % {method.Slices})"); // this run's slice
            sb.Append($"            var job{i} = new {method.Name}Job{i}{Fields(method, "world", "system")};\n");
            // WorldOptions.MeasureSystems: the entities the pass matches as it starts (SystemStats.LastEntities); one
            // field test per pass otherwise, nothing in the row loop. Count takes no slice: a sliced pass reports its share.
            string count = $"world.Count<{method.ComponentTypes}>({(whole.Length == 0 ? "" : whole.Substring(2))})";
            if (method.Slices > 1) count = $"{count} / {method.Slices}";
            sb.Append($"            if (Measuring(world)) Visit(system, {count});\n");
            sb.Append($"            world.{method.ForEach}<{method.ComponentTypes}, {method.Name}Job{i}>(ref job{i}{filter});\n");
        }

        sb.Append("        }\n\n");
        EmitAccess(sb, model);
        sb.Append("    }\n");
        if (owner.TypeParameters.Length > 0) return; // registered by EmitGenericRegistration: a module initializer is never generic
        string type = owner.IsVisible ? $"typeof({owner.FullName})" : $"global::System.Type.GetType(\"{owner.MetadataName}\")";
        sb.Append($"\n    {Code.ModuleInitializer}\n");
        sb.Append($"    internal static void Register() => global::EasyEcs.SystemCode.Register({type}, new Code());\n");
    }

    /// <summary>
    /// A generic system's registration: its code's definition, closed at run time for closed types this assembly does
    /// not name, and the code of each closed type it names (no runtime code generation: Native AOT keeps them).
    /// </summary>
    private static void EmitGenericRegistration(StringBuilder sb, UpdateModel model)
    {
        var owner = model.Owner;
        string open = $"__ForEach<{new string(',', owner.Arity - 1)}>.Code";
        string definition = owner.IsVisible ? $"typeof({owner.Definition})" : $"global::System.Type.GetType(\"{owner.MetadataName}\")";
        sb.Append($"\nfile static class __ForEachRegistration\n{{\n    {Code.ModuleInitializer}\n    internal static void Register()\n    {{\n");
        sb.Append($"        global::EasyEcs.SystemCode.RegisterGeneric({definition}, typeof({open}));\n");
        foreach (var use in model.Closed)
            sb.Append($"        global::EasyEcs.SystemCode.Register(typeof({use.Closed}), new __ForEach<{use.Arguments}>.Code());\n");
        sb.Append("    }\n}\n");
    }

    /// <summary>
    /// <c>ref</c> parameters are writes, <c>in</c> parameters reads. Anything that can reach the world beyond its
    /// parameters (EntitySpan / World parameters, an Entity parameter used for more than its identity, instance methods
    /// that see <c>World</c>, an OnUpdate override) runs alone.
    /// </summary>
    private static void EmitAccess(StringBuilder sb, UpdateModel model)
    {
        sb.Append("        protected override void Access(ref global::EasyEcs.SystemAccess access)\n        {\n");
        bool alone = model.Owner.OverridesOnUpdate || model.Methods.Any(m => !m.IsStatic
            || m.Parameters.Any(p => p.Kind is Symbols.ParamKind.Entities or Symbols.ParamKind.World
                || p.Kind == Symbols.ParamKind.Entity && !m.Identifies));
        if (alone)
        {
            sb.Append("            access.RunAlone();\n        }\n");
            return;
        }

        foreach (var p in model.Methods.SelectMany(m => m.Parameters).Where(p => p.Kind != Symbols.ParamKind.Entity)
                     .Select(p => (p.Kind, p.Type, p.RefKind)).Distinct())
        {
            string kind = p.Kind == Symbols.ParamKind.Singleton ? "Singleton" : "";
            sb.Append($"            access.{(p.RefKind == "ref" ? "Write" : "Read")}{kind}<{p.Type}>();\n");
        }

        // [Changed<T>], [Removed<T>] and the ChangedAny / RemovedAny sets read T's change ticks, which writers and
        // removers of T stamp.
        foreach (var type in model.Methods.SelectMany(m => m.Changed.Concat(m.Removed).Concat(m.ChangedAny).Concat(m.RemovedAny)).Distinct())
            sb.Append($"            access.Read<{type}>();\n");

        sb.Append("        }\n");
    }

    private static void EmitJob(StringBuilder sb, OwnerModel owner, ForEachModel method, int number)
    {
        string job = $"{method.Name}Job{number}{method.TypeParameters}";
        var components = method.Query.ToArray();
        int index = 0;
        bool runs = method.Runs; // per row of a run: components and handles at the row's offset
        // A batch job copies the caller's ref values into locals for its run (written back after it): through the
        // ref field, every row would load and store memory, a dependency chain of ~2.5 ns per row. No finally: Execute
        // would no longer inline, so a method that throws loses its changes of the run in progress.
        bool[] local = method.Parameters.Select(p => method.IsBatch && p.Kind == Symbols.ParamKind.Context && p.RefKind == "ref").ToArray();
        var args = string.Join(", ", method.Parameters.Select((p, i) => p.Kind switch
        {
            Symbols.ParamKind.Component when runs => $"{p.RefKind} global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++index}, i)",
            Symbols.ParamKind.Entity when runs => "entities[(int)i]",
            Symbols.ParamKind.Optional when runs => $"m{i}[(int)i]",
            Symbols.ParamKind.Mutable when runs => $"new global::EasyEcs.Mut<{p.Type}>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++index}, i), in entities, (int)i)",
            Symbols.ParamKind.Component => $"{p.RefKind} c{++index}",
            Symbols.ParamKind.Mutable => $"new global::EasyEcs.Mut<{p.Type}>(ref c{++index}, entity)",
            Symbols.ParamKind.ComponentSpan => $"c{++index}", // Span<T> converts to ReadOnlySpan<T> for read-only parameters
            Symbols.ParamKind.Singleton => method.Parallel ? $"{p.RefKind} s{i}.Value" : $"{p.RefKind} s{i}",
            Symbols.ParamKind.Entity => "entity",
            Symbols.ParamKind.Entities => "entities",
            Symbols.ParamKind.Optional => $"global::EasyEcs.Maybe<{p.Type}>.{(p.RefKind == "ref" ? "ForWrite" : "ForRead")}(entity)",
            Symbols.ParamKind.Context when local[i] => $"ref l{i}",
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
        sb.Append($"    internal {kind} {job} : global::EasyEcs.{iface}<{method.ComponentTypes}>{marker}{method.Constraints}\n    {{\n");
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
        sb.Append("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
        string first = method.IsBatch ? "global::EasyEcs.EntitySpan entities, " : method.TakesEntity ? "global::EasyEcs.Entity entity, " : "";
        string each = method.IsBatch ? "global::System.Span<{0}> c{1}" : "ref {0} c{1}";
        sb.Append($"        public void Execute({first}{List(components.Length, i => string.Format(each, components[i - 1], i))})\n");
        string call = $"__ForEach{owner.TypeParameters}.{method.Name}_{number}{method.TypeParameters}({args});";
        if (!runs && !local.Contains(true))
            sb.Append($"            => {call}\n");
        else
        {
            sb.Append("        {\n");
            const string indent = "            ";
            for (int i = 0; i < method.Parameters.Length; i++)
                if (local[i]) sb.Append($"            var l{i} = x{i};\n");
            for (int i = 0; i < method.Parameters.Length; i++)
                if (runs && method.Parameters[i] is { Kind: Symbols.ParamKind.Optional } p)
                    sb.Append($"{indent}var m{i} = new global::EasyEcs.MaybeColumn<{p.Type}>(in entities, {(p.RefKind == "ref" && p.Tracked ? "true" : "false")});\n");
            if (runs)
            {
                // The columns' refs and the row count in locals: the spans are passed in memory, and a caller's value
                // passed by ref is a store the JIT cannot prove misses them, so it would reload them on every row.
                for (int k = 1; k <= components.Length; k++)
                    sb.Append($"{indent}ref {components[k - 1]} r{k} = ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(c{k});\n");
                sb.Append($"{indent}nint rows = {(components.Length > 0 ? "c1.Length" : "entities.Length")};\n");
                sb.Append($"{indent}for (nint i = 0; i < rows; i++) // nint: no sign extension per row\n{indent}    {call}\n");
            }
            else sb.Append($"{indent}{call}\n");
            for (int i = 0; i < method.Parameters.Length; i++)
                if (local[i]) sb.Append($"            x{i} = l{i};\n");
            sb.Append("        }\n");
        }
        // Its ref parameters (Span<T> for a batch, Mut<T>): carried over when the method moves the entity it visits,
        // and stamped when the component tracks changes: by the loop, every row it visits, except a Mut's, which
        // stamps the rows it writes. The queried [With<T>] component of a method without one is read.
        var queried = method.Parameters.Where(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable).ToArray();
        int writes = 0, own = 0;
        for (int k = 0; k < components.Length; k++)
        {
            if (queried.Any(p => p.Type == components[k] && p.RefKind == "ref")) writes |= 1 << k;
            if (queried.Any(p => p.Type == components[k] && p.Kind == Symbols.ParamKind.Mutable)) own |= 1 << k;
        }
        sb.Append($"\n        static int global::EasyEcs.{iface}<{method.ComponentTypes}>.WriteMask => {writes};\n");
        if (own != 0) sb.Append($"        static int global::EasyEcs.{iface}<{method.ComponentTypes}>.SelfStamped => {own};\n");

        sb.Append("    }\n\n");

        if (!method.HasFilter) return;
        var chain = method.With.Select(t => $".With<{t}>()").Concat(method.Without.Select(t => $".Without<{t}>()"))
            .Concat(method.WithAny.Select(t => $".WithAny<{t}>()")).Concat(method.Changed.Select(t => $".Changed<{t}>(0)")).Concat(method.Removed.Select(t => $".Removed<{t}>(0)"))
            .Concat(AnyCalls("ChangedAny", method.ChangedAny)).Concat(AnyCalls("RemovedAny", method.RemovedAny));
        // Attribute type arguments cannot name type parameters (CS8968): the filter is one field, even for a generic method.
        sb.Append($"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Filter{number} = global::EasyEcs.QueryFilter.Create(){string.Concat(chain)};\n\n");
    }

    /// <summary>
    /// A ChangedAny / RemovedAny set as calls of at most 9 components each (calls join one set): a set of one
    /// (several attributes naming the same type) names it twice.
    /// </summary>
    private static IEnumerable<string> AnyCalls(string kind, EquatableArray<string> types)
    {
        const int max = 9;
        for (int at = 0; at < types.Length; at += max)
        {
            var call = types.Skip(at).Take(max).ToList();
            if (call.Count == 1) call.Add(call[0]);
            yield return $".{kind}<{string.Join(", ", call)}>(0)";
        }
    }

    // ---------------- models (equatable, symbol-free) ----------------

    /// <param name="FullName">The type as C# names it (<c>global::Ns.Outer.Inner</c>).</param>
    /// <param name="MetadataName">The type as the runtime names it (<c>Ns.Outer+Inner</c>), for UnsafeAccessorType.</param>
    /// <param name="IsVisible">Generated code can name the type (it and its containing types are internal or public).</param>
    /// <param name="OverridesOnUpdate">The type or a base type below EcsSystem overrides OnUpdate.</param>
    /// <param name="AccessorType">MetadataName closed over the generated class's type parameters (<c>Ns.Regen`1[[!0]]</c>).</param>
    /// <param name="TypeParameters">The type parameters of the type and its containing types (<c>&lt;T&gt;</c>), or "".</param>
    /// <param name="Definition">The generic definition as <c>typeof</c> names it (<c>global::Ns.Regen&lt;&gt;</c>), or "".</param>
    private sealed record OwnerModel(string Namespace, string FullName, string MetadataName, string FlatName, string HintName,
        bool IsVisible, bool IsPublic, bool OverridesOnUpdate, bool IsSystem, string AccessorType, string TypeParameters,
        string Constraints, string Definition, int Arity)
    {
        public static OwnerModel From(INamedTypeSymbol type)
        {
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            var levels = new List<INamedTypeSymbol>();
            for (var t = type; t != null; t = t.ContainingType) levels.Insert(0, t);
            var typeParameters = Symbols.ChainTypeParameters(type);
            string metadata = (ns.Length > 0 ? ns + "." : "") + string.Join("+", levels.Select(t => t.MetadataName));
            string accessor = typeParameters.Count == 0 ? metadata
                : metadata + "[" + string.Join(",", typeParameters.Select((_, i) => $"[!{i}]")) + "]";
            string definition = typeParameters.Count == 0 ? ""
                : "global::" + (ns.Length > 0 ? ns + "." : "") + string.Join(".", levels.Select(t => t.Arity == 0 ? t.Name : $"{t.Name}<{new string(',', t.Arity - 1)}>"));
            bool overridesOnUpdate = false;
            for (var t = type; t != null && !Symbols.Named(t, Symbols.EcsSystem); t = t.BaseType)
                overridesOnUpdate |= t.GetMembers("OnUpdate").Any(m => m is IMethodSymbol { IsOverride: true, Parameters.Length: 0 });
            bool isPublic = true;
            for (var t = type; t != null; t = t.ContainingType) isPublic &= t.DeclaredAccessibility == Accessibility.Public;
            return new OwnerModel(ns, type.ToDisplayString(Symbols.Fqn), metadata, Symbols.FlatName(type), Symbols.HintName(type),
                Symbols.IsVisible(type), isPublic, overridesOnUpdate, Symbols.IsSystem(type), accessor,
                Symbols.TypeParameterList(typeParameters), Symbols.Constraints(typeParameters), definition, typeParameters.Count);
        }
    }

    /// <param name="Type">The component, singleton or value type (a span's or Maybe's element).</param>
    /// <param name="RefKind">"ref" or "in" ("" for a context parameter passed by value).</param>
    /// <param name="Declared">The parameter as declared, with its modifier (<c>in global::Health</c>), for the accessor.</param>
    /// <param name="Tracked">A Maybe's or Mut's element tracks changes (a write through it is stamped).</param>
    private sealed record ParamModel(Symbols.ParamKind Kind, string Type, string RefKind, string Name, string Declared, bool Tracked = false);

    /// <param name="Stable">
    /// The method cannot make structural changes: it is static, takes no <c>World</c>, and uses its <c>Entity</c>
    /// parameter (if any) only to read (<see cref="ReadOnlyEntityMembers"/>). Its job gets INoStructuralChanges.
    /// </param>
    /// <param name="Parallel">[ForEach(Parallel = true)] on a method that can run on several threads (ForEachParallel).</param>
    private sealed record ForEachModel(OwnerModel Owner, string Name, bool IsStatic, bool Stable, bool Parallel, int Order,
        EquatableArray<string> Query, EquatableArray<ParamModel> Parameters, EquatableArray<string> With,
        EquatableArray<string> Without, EquatableArray<string> WithAny, EquatableArray<string> Changed,
        EquatableArray<string> Removed, EquatableArray<string> ChangedAny, EquatableArray<string> RemovedAny, string Returns, string TypeParameters, string Constraints,
        EquatableArray<string> TypeParameterNames, bool PublicEntry)
    {
        /// <summary>The World method that runs the job.</summary>
        public string ForEach => Loop + (Parallel ? "Parallel" : "");

        public bool HasFilter => With.Length + Without.Length + WithAny.Length > 0 || Since;

        /// <summary>Filters rows by change tick ([Changed&lt;T&gt;] / [Removed&lt;T&gt;] / [ChangedAny&lt;…&gt;] / [RemovedAny&lt;…&gt;]).</summary>
        public bool Since => Changed.Length + Removed.Length + ChangedAny.Length + RemovedAny.Length > 0;

        /// <summary>[ForEach(Slices = N)]: visits one of N slices of its rows per run (1: all of them).</summary>
        public int Slices { get; init; } = 1;

        /// <summary>Uses its Entity parameters (if any) only as identities (<see cref="Symbols.OnlyIdentifies"/>).</summary>
        public bool Identifies { get; init; }

        /// <summary>The filter argument of a sliced method: its filter (or none) cut to slice <paramref name="slice"/>.</summary>
        public string Sliced(string filter, string slice) =>
            $", {(HasFilter ? filter : "global::EasyEcs.QueryFilter.Create()")}.Slice({slice}, {Slices})";

        /// <summary>The queried component types, as type arguments of World.ForEach / IForEach.</summary>
        public string ComponentTypes => string.Join(", ", Query);

        /// <summary>Runs per entity with its handle: it takes the Entity, a Maybe looked up through it, or a Mut that stamps through it.</summary>
        public bool TakesEntity => Parameters.Any(p => p.Kind is Symbols.ParamKind.Entity or Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable);

        public bool TakesWorld => Parameters.Any(p => p.Kind == Symbols.ParamKind.World);

        /// <summary>The World loop (and, with an I, the job interface) the method runs as.</summary>
        public string Loop => IsBatch ? "ForEachBatch" : TakesEntity ? "ForEachEntity" : "ForEach";

        /// <summary>Takes component spans, or runs per row inside runs of rows: World.ForEachBatch.</summary>
        public bool IsBatch => Parameters.Any(p => p.Kind == Symbols.ParamKind.ComponentSpan) || Runs;

        /// <summary>
        /// Changes no structure (on one thread) and takes a Maybe, a Mut or a caller's value by ref: its job walks runs of
        /// rows, finding each optional column once per run (a presence bit per row), stamping a Mut's writes by row (no
        /// entity lookup) and keeping the caller's values in locals for the run.
        /// </summary>
        public bool Runs => Stable && !Parallel && !Parameters.Any(p => p.Kind == Symbols.ParamKind.ComponentSpan)
            && Parameters.Any(p => p.Kind is Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable || p is { Kind: Symbols.ParamKind.Context, RefKind: "ref" });

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
                        Symbols.Named(((INamedTypeSymbol)p.Type).ConstructedFrom, "System.Span<T>") ? "ref" : "in", p.Name, declared),
                    Symbols.ParamKind.Optional => new ParamModel(Symbols.ParamKind.Optional, // a write unless it only reads Value
                        Symbols.MaybeElement(p.Type)!.ToDisplayString(Symbols.Fqn), Symbols.OnlyReadsValue(Symbols.WithBody(method, syntax, model).Item1, p.Name) ? "in" : "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MaybeElement(p.Type)!, Symbols.TrackChanges)),
                    Symbols.ParamKind.Mutable => new ParamModel(Symbols.ParamKind.Mutable, // a write: the scheduler sees its writer
                        Symbols.MutElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MutElement(p.Type)!, Symbols.TrackChanges)),
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
            var changedAny = new List<string>();
            var removedAny = new List<string>();
            void Add(Symbols.FilterKind kind, ITypeSymbol filtered)
            {
                // every [ChangedAny<..>] / [RemovedAny<..>] of a method joins its one set
                var list = kind switch
                {
                    Symbols.FilterKind.With => with, Symbols.FilterKind.Without => without, Symbols.FilterKind.WithAny => withAny,
                    Symbols.FilterKind.Changed => changed, Symbols.FilterKind.Removed => removed,
                    Symbols.FilterKind.ChangedAny => changedAny, _ => removedAny,
                };
                string type = filtered.ToDisplayString(Symbols.Fqn);
                if (!list.Contains(type)) list.Add(type);
            }

            foreach (var (kind, type, _) in Symbols.AttributeFilters(method)) Add(kind, type);

            var query = parameters.Where(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable)
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
            // The system group's filters ([Without<Inactive>] public struct Gameplay : ISystemGroup, ISystemGroupWith<T> /
            // ISystemGroupWithout<T>) join every method's (after the queried component is chosen: a group's [With<T>] only filters).
            foreach (var (kind, type, _, _) in Symbols.GroupFilters(method)) Add(kind, type);
            if (query.Count > 0) with.RemoveAll(query.Contains); // a group's [With<T>] the method already queries

            bool stable = Symbols.IsStable(method, syntax, model);
            bool parallel = Symbols.IsParallel(method) && Symbols.ParallelProblem(method, syntax, model) == null;
                        // Declaration order by member index: a text position would change (and re-emit the file) on any edit above it.
            int order = method.ContainingType.GetMembers().IndexOf(method);
            return new ForEachModel(OwnerModel.From(method.ContainingType), method.Name, method.IsStatic, stable, parallel, order,
                new EquatableArray<string>(query.ToArray()),
                new EquatableArray<ParamModel>(parameters), new EquatableArray<string>(with.ToArray()),
                new EquatableArray<string>(without.ToArray()), new EquatableArray<string>(withAny.ToArray()),
                new EquatableArray<string>(changed.ToArray()), new EquatableArray<string>(removed.ToArray()),
                new EquatableArray<string>(changedAny.ToArray()), new EquatableArray<string>(removedAny.ToArray()),
                (method.ReturnsByRef ? "ref " : method.ReturnsByRefReadonly ? "ref readonly " : "")
                    + (method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(Symbols.Fqn)),
                Symbols.TypeParameterList(method.TypeParameters), Symbols.Constraints(method.TypeParameters),
                new EquatableArray<string>(method.TypeParameters.Select(t => t.Name).ToArray()),
                // A public entry point names only public types: its context parameters' and its constraints'.
                method.Parameters.Where(p => Symbols.Classify(p, outside) == Symbols.ParamKind.Context).All(p => Symbols.IsPublic(p.Type))
                    && method.TypeParameters.SelectMany(t => t.ConstraintTypes).All(Symbols.IsPublic)) { Slices = Symbols.Slices(method), Identifies = Symbols.OnlyIdentifies(method, syntax, model) };
        }
    }


    private sealed record UpdateModel(OwnerModel Owner, EquatableArray<ForEachModel> Methods)
    {
        /// <summary>The closed types of a generic system that this assembly names.</summary>
        public EquatableArray<ClosedUse> Closed { get; init; } = new([]);
    }

    /// <summary>
    /// A closed type of a generic system with [ForEach] methods, named in this assembly: <c>AddSystem&lt;Regen&lt;Health&gt;&gt;()</c>,
    /// <c>new Regen&lt;Health&gt;()</c>, a target-typed <c>Regen&lt;Health&gt; r = new()</c> (local, field or property),
    /// <c>typeof(Regen&lt;Health&gt;)</c>, or a base type (<c>class X : Regen&lt;Health&gt;</c>), and each generic base system of it.
    /// </summary>
    /// <param name="Owner">The generic definition as its OwnerModel names it (<c>global::Ns.Regen&lt;T&gt;</c>).</param>
    /// <param name="Closed">The closed type (<c>global::Ns.Regen&lt;global::Health&gt;</c>).</param>
    /// <param name="Arguments">Its type arguments, outermost first (<c>global::Health</c>).</param>
    private sealed record ClosedUse(string Owner, string Closed, string Arguments)
    {
        public static bool IsCandidate(SyntaxNode node)
        {
            if (node is not GenericNameSyntax name) return false;
            SyntaxNode type = name;
            while (type.Parent is QualifiedNameSyntax qualified && qualified.Right == type
                   || type.Parent is AliasQualifiedNameSyntax alias && alias.Name == type || type.Parent is NullableTypeSyntax)
                type = type.Parent;
            return type.Parent is ObjectCreationExpressionSyntax or SimpleBaseTypeSyntax or TypeOfExpressionSyntax
                || type.Parent is TypeArgumentListSyntax { Parent: GenericNameSyntax { Identifier.ValueText: "AddSystem" } }
                // Target-typed: Regen<Health> r = new(); (locals and fields) and Regen<Health> P { get; } = new();
                || type.Parent is VariableDeclarationSyntax { Variables: var variables } && variables.Any(v => v.Initializer?.Value is ImplicitObjectCreationExpressionSyntax)
                || type.Parent is PropertyDeclarationSyntax { Initializer.Value: ImplicitObjectCreationExpressionSyntax };
        }

        public static EquatableArray<ClosedUse> Create(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
        {
            if (Symbols.TypeNamed(ctx.SemanticModel, ctx.Node) is not INamedTypeSymbol { IsGenericType: true } type
                || type.TypeKind != TypeKind.Class || !Symbols.IsSystem(type))
                return new([]);
            var uses = new List<ClosedUse>();
            var assembly = ctx.SemanticModel.Compilation.Assembly;
            for (var t = type; t != null && !Symbols.Named(t, Symbols.EcsSystem); t = t.BaseType)
            {
                var arguments = Symbols.ChainTypeArguments(t);
                if (arguments.Count == 0 || !SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, assembly)
                    || !Symbols.IsVisible(t) || arguments.Any(a => !Symbols.IsNameable(a) || Symbols.ContainsTypeParameter(a))
                    || !t.OriginalDefinition.GetMembers().OfType<IMethodSymbol>().Any(Symbols.IsForEach))
                    continue;
                uses.Add(new ClosedUse(t.OriginalDefinition.ToDisplayString(Symbols.Fqn), t.ToDisplayString(Symbols.Fqn),
                    string.Join(", ", arguments.Select(a => a.ToDisplayString(Symbols.Fqn)))));
            }

            return new(uses.ToArray());
        }
    }
}
