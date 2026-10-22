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
            sb.Append($"            {world}.{method.ForEach}<{method.ComponentTypes}, {job}>(ref {local}{filter}{method.Chunks});\n");
            EmitReductions(sb, method, world, local, "            ");
            sb.Append("        }\n\n");

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
        if (method.NeedsWorld) fields.Add($"World = {world}");
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            if (p.Kind == Symbols.ParamKind.Singleton)
                fields.Add(method.Parallel ? $"s{i} = {world}.SingletonRef<{p.Type}>()" : $"s{i} = ref {world}.SingletonRef<{p.Type}>().Value");
            else if (p.Kind == Symbols.ParamKind.Context)
                fields.Add(p.RefKind.Length == 0 || method.Parallel ? $"x{i} = {Escape(p.Name)}" : $"x{i} = ref {Escape(p.Name)}");
            else if (p.Kind == Symbols.ParamKind.Reduction)
                fields.Add($"a{i} = new global::EasyEcs.Reduce<{p.Type}> {{ Value = {p.Type}.Identity }}"); // every partial result starts there
            else if (p.Kind == Symbols.ParamKind.Emit) // on one thread the singleton's list itself; in parallel the target of the chunks' lists
                fields.Add(method.Parallel ? $"o{i} = global::EasyEcs.Emitted<{p.Type}>.Target({world})"
                    : $"e{i} = ref global::EasyEcs.Emitted<{p.Type}>.Target({world}).Value.Items");
            else if (p is { Kind: Symbols.ParamKind.Lookup, Key.Length: > 0 }) // its index, built before the pass
                fields.Add($"k{i} = global::EasyEcs.LookupExtensions.Prepared<{p.Type}, {p.Key}>({world})");
        }

        return fields.Count == 0 ? "()" : $" {{ {string.Join(", ", fields)} }}";
    }

    /// <summary>
    /// A pass's reductions (<c>Reduce&lt;T&gt;</c>), combined into the job by the pass: each replaces its singleton
    /// (added when missing), once per pass.
    /// </summary>
    private static void EmitReductions(StringBuilder sb, ForEachModel method, string world, string job, string indent)
    {
        for (int i = 0; i < method.Parameters.Length; i++)
            if (method.Parameters[i] is { Kind: Symbols.ParamKind.Reduction } p)
                sb.Append($"{indent}{world}.GetOrAddSingleton<{p.Type}>() = {job}.a{i}.Value;\n");
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
            sb.Append($"            world.{method.ForEach}<{method.ComponentTypes}, {method.Name}Job{i}>(ref job{i}{filter}{method.Chunks});\n");
            EmitReductions(sb, method, "world", $"job{i}", "            ");
        }

        sb.Append("        }\n\n");
        // How each method runs (SystemInfo.Loops): one array for the type, made once.
        sb.Append($"        private static readonly string[] __loops = [{string.Join(", ", model.Methods.Select(m => Code.Literal(m.Describe())))}];\n\n");
        sb.Append("        protected override string[] Loops => __loops;\n\n");
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

        foreach (var p in model.Methods.SelectMany(m => m.Parameters).Where(p => p.Kind is not (Symbols.ParamKind.Entity or Symbols.ParamKind.Scratch))
                     .Select(p => (p.Kind, p.Type, p.RefKind, p.Key)).Distinct())
        {
            if (p is { Kind: Symbols.ParamKind.Lookup, Key.Length: > 0 }) // a read, and its index built before a parallel stage
            {
                sb.Append($"            access.Index<{p.Type}, {p.Key}>();\n");
                continue;
            }

            if (p.Kind == Symbols.ParamKind.Emit) // appends to Emitted<T>, once per pass
            {
                sb.Append($"            access.WriteSingleton<global::EasyEcs.Emitted<{p.Type}>>();\n");
                continue;
            }

            string kind = p.Kind is Symbols.ParamKind.Singleton or Symbols.ParamKind.Reduction ? "Singleton" : "";
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
        bool runs = method.Runs || method.Simd != null; // per row of a run: components and handles at the row's offset
        // A batch job copies the caller's ref values into locals for its run (written back after it): through the
        // ref field, every row would load and store memory, a dependency chain of ~2.5 ns per row. No finally: Execute
        // would no longer inline, so a method that throws loses its changes of the run in progress.
        bool[] local = method.Parameters.Select(p => method.IsBatch && (p.Kind == Symbols.ParamKind.Context && p.RefKind == "ref" || p.Kind == Symbols.ParamKind.Reduction)).ToArray();
        string Field(int i) => method.Parameters[i].Kind == Symbols.ParamKind.Reduction ? $"a{i}" : $"x{i}";
        var args = string.Join(", ", method.Parameters.Select((p, i) => p.Kind switch
        {
            Symbols.ParamKind.Component when runs => $"{p.RefKind} global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++index}, i)",
            Symbols.ParamKind.Entity when runs => "entities.At(i)", // i < rows, the run's length: no bounds check
            Symbols.ParamKind.Optional when runs => $"m{i}[(int)i]",
            Symbols.ParamKind.Mutable when runs => $"new global::EasyEcs.Mut<{p.Type}>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++index}, i), in entities, (int)i)",
            Symbols.ParamKind.Component => $"{p.RefKind} c{++index}",
            Symbols.ParamKind.Mutable => $"new global::EasyEcs.Mut<{p.Type}>(ref c{++index}, entity)",
            Symbols.ParamKind.ComponentSpan => $"c{++index}", // Span<T> converts to ReadOnlySpan<T> for read-only parameters
            Symbols.ParamKind.Singleton => method.Parallel ? $"{p.RefKind} s{i}.Value" : $"{p.RefKind} s{i}",
            Symbols.ParamKind.Entity => "entity",
            Symbols.ParamKind.Entities => "entities",
            Symbols.ParamKind.Optional => $"global::EasyEcs.Maybe<{p.Type}>.{(p.RefKind == "ref" ? "ForWrite" : "ForRead")}(entity)",
            Symbols.ParamKind.Reduction => $"ref {(local[i] ? "l" : "a")}{i}", // a run's local: registers (Reduce<T> holds no ref)
            // This thread's box, resolved once per chunk (per pass on one thread): the job's copy keeps it.
            Symbols.ParamKind.Scratch => $"new global::EasyEcs.Scratch<{p.Type}>(ref (t{i} ??= global::EasyEcs.Scratch<{p.Type}>.Of<{job}>()).Value)",
            Symbols.ParamKind.Lookup => $"new global::EasyEcs.Lookup<{p.Type}>(World, {(p.Key.Length > 0 ? $"k{i}" : "null")})",
            Symbols.ParamKind.Emit => $"new global::EasyEcs.Emit<{p.Type}>(ref e{i})",
            Symbols.ParamKind.Context when local[i] => $"ref l{i}",
            Symbols.ParamKind.Context => $"{(p.RefKind.Length == 0 ? "" : p.RefKind + " ")}x{i}",
            _ => "World",
        }).Prepend(method.IsStatic ? "null" : "System"));

        // The JIT specializes World.ForEach on this struct and inlines Execute and the user's method into the loop.
        string iface = "I" + method.Loop;
        // A batch job has no structural path at all; ForEachBatchParallel asks for the marker all the same, as every parallel loop.
        bool stable = method.Stable && (!method.IsBatch || method.Parallel);
        string marker = stable ? ", global::EasyEcs.INoStructuralChanges" : "";
        // A ref struct holds each singleton as a ref field, a register in the row loop: a SingletonRef's box would be
        // null-checked on every row. Parallel jobs are copied into work items on the heap, so they hold SingletonRefs.
        string kind = method.Parallel ? "struct" : "ref struct";
        sb.Append($"    internal {kind} {job} : global::EasyEcs.{iface}<{method.ComponentTypes}>{marker}{method.Constraints}\n    {{\n");
        if (!method.IsStatic) sb.Append("        public object System;\n");
        // Only when the method takes the World: an unused field would still be copied into the row loop, where it
        // holds a callee-saved register a job that calls out (a structural change) needs for its loop state.
        if (method.NeedsWorld) sb.Append("        public global::EasyEcs.World World;\n");
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
            else if (p.Kind == Symbols.ParamKind.Reduction) // the partial result: of the pass, or of a chunk's copy
                sb.Append($"        public global::EasyEcs.Reduce<{p.Type}> a{i};\n");
            else if (p.Kind == Symbols.ParamKind.Scratch) // this thread's box (null until the first row of a chunk)
                sb.Append($"        public global::System.Runtime.CompilerServices.StrongBox<{p.Type}> t{i};\n");
            else if (p is { Kind: Symbols.ParamKind.Lookup, Key.Length: > 0 }) // the component's index, built
                sb.Append($"        public object k{i};\n");
            else if (p.Kind == Symbols.ParamKind.Emit) // the singleton's list, or a chunk's (from the pool) and where it goes
                sb.Append(method.Parallel
                    ? $"        public global::EasyEcs.SingletonRef<global::EasyEcs.Emitted<{p.Type}>> o{i};\n        public global::EasyEcs.DynamicBuffer<{p.Type}> e{i};\n"
                    : $"        public ref global::EasyEcs.DynamicBuffer<{p.Type}> e{i};\n");
        }
        if (sb[sb.Length - 2] != '{') sb.Append('\n'); // a blank line after the fields, if any
        // A wide component's permute tables and layout check (Kernel.Wide.cs): statics, built once per job type.
        if (method.Simd is { Statics.Length: > 0 } statics)
        {
            foreach (var line in statics.Statics.Split('\n')) sb.Append(line.Length > 0 ? "        " + line : "").Append('\n');
        }
        string call = $"__ForEach{owner.TypeParameters}.{method.Name}_{number}{method.TypeParameters}({args});";
        string first = method.IsBatch ? "global::EasyEcs.EntitySpan entities, " : method.TakesEntity ? "global::EasyEcs.Entity entity, " : "";
        string each = method.IsBatch ? "global::System.Span<{0}> c{1}" : "ref {0} c{1}";
        // A vector loop also takes the words of 64 rows of which some pass (IForEachMasked): their passing rows as a
        // lane mask, the other lanes stored back as loaded.
        // A parallel one runs as a batch job over each chunk's runs of rows (ForEachBatchParallel): no masked form.
        for (int pass = 0; pass < (method.Simd != null && !method.Parallel ? 2 : 1); pass++)
        {
        bool masked = pass == 1;
        if (masked) sb.Append('\n');
        sb.Append("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
        sb.Append($"        public void Execute({first}{(masked ? "ulong pass, " : "")}{List(components.Length, i => string.Format(each, components[i - 1], i))})\n");
        if (!runs && !local.Contains(true))
            sb.Append($"            => {call}\n");
        else
        {
            sb.Append("        {\n");
            const string indent = "            ";
            for (int i = 0; i < method.Parameters.Length; i++)
                if (local[i]) sb.Append($"            var l{i} = {Field(i)};\n");
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
                if (method.Simd is { Flat: { } flat } && !masked) EmitFlatLoop(sb, flat, method.Simd, components, indent);
                // Narrow lanes have no lane masks of their own (a Vector512<byte> is a whole word): the passing rows of a
                // masked word run the method.
                // So do a wide component's (its write-back stores whole vectors of rows).
                else if (method.Simd is { Size: < 4 } or { Wide: not null } && masked) sb.Append($"{indent}nint i = 0;\n");
                else if (method.Simd is { } simd) EmitVectorLoop(sb, simd, components, indent, call, masked);
                sb.Append(method.Simd == null
                    ? $"{indent}for (nint i = 0; i < rows; i++) // nint: no sign extension per row\n{indent}    {call}\n"
                    : masked
                        ? $"{indent}for (; i < rows; i++) // the rows after the last whole vector that pass: the method itself\n{indent}    if (((pass >> (int)i) & 1) != 0) {call}\n"
                        : $"{indent}for (; i < rows; i++) // the rows after the last whole vector: the method itself\n{indent}    {call}\n");
            }
            else sb.Append($"{indent}{call}\n");
            for (int i = 0; i < method.Parameters.Length; i++)
                if (local[i]) sb.Append($"            {Field(i)} = l{i};\n");
            sb.Append("        }\n");
        }
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
        string declaring = method.Simd != null ? "IForEachBatch" : iface; // IForEachMasked declares no masks of its own
        sb.Append($"\n        static int global::EasyEcs.{declaring}<{method.ComponentTypes}>.WriteMask => {writes};\n");
        if (own != 0) sb.Append($"        static int global::EasyEcs.{declaring}<{method.ComponentTypes}>.SelfStamped => {own};\n");
        var reductions = Enumerable.Range(0, method.Parameters.Length).Where(i => method.Parameters[i].Kind == Symbols.ParamKind.Reduction).ToArray();
        var emits = Enumerable.Range(0, method.Parameters.Length).Where(i => method.Parameters[i].Kind == Symbols.ParamKind.Emit).ToArray();
        if (method.Parallel && reductions.Length + emits.Length > 0)
        {
            // The chunks' partial results and records, combined in chunk order (TSelf is this job: the runtime passes its own TJob).
            const string unsafeType = "global::System.Runtime.CompilerServices.Unsafe";
            sb.Append($"        static bool global::EasyEcs.{iface}<{method.ComponentTypes}>.Merges => true;\n\n");
            sb.Append($"        static void global::EasyEcs.{iface}<{method.ComponentTypes}>.Merge<TSelf>(ref TSelf job, in TSelf chunk)\n        {{\n");
            sb.Append($"            ref var into = ref {unsafeType}.As<TSelf, {job}>(ref job);\n");
            sb.Append($"            ref var part = ref {unsafeType}.As<TSelf, {job}>(ref {unsafeType}.AsRef(in chunk));\n");
            foreach (int i in reductions)
                sb.Append($"            into.a{i}.Value = {method.Parameters[i].Type}.Combine(into.a{i}.Value, part.a{i}.Value);\n");
            foreach (int i in emits)
                sb.Append($"            global::EasyEcs.Emit<{method.Parameters[i].Type}>.Join(into.o{i}, ref part.e{i});\n");
            sb.Append("        }\n");
        }

        sb.Append("    }\n\n");

        if (!method.HasFilter) return;
        var chain = method.With.Select(t => $".With<{t}>()").Concat(method.Without.Select(t => $".Without<{t}>()"))
            .Concat(method.WithAny.Select(t => $".WithAny<{t}>()")).Concat(method.Changed.Select(t => $".Changed<{t}>(0)")).Concat(method.Removed.Select(t => $".Removed<{t}>(0)"))
            .Concat(AnyCalls("ChangedAny", method.ChangedAny)).Concat(AnyCalls("RemovedAny", method.RemovedAny));
        // Attribute type arguments cannot name type parameters (CS8968): the filter is one field, even for a generic method.
        sb.Append($"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Filter{number} = global::EasyEcs.QueryFilter.Create(){string.Concat(chain)};\n\n");
    }

    /// <summary>
    /// The vector loops of a method <see cref="Kernel"/> translated: each component's lane field as a ref of the lane
    /// type, the uniform values broadcast once per run, then one vector of rows per iteration: Vector512 where the
    /// hardware runs it (AVX-512), then Vector&lt;T&gt; for the rows left (or all of them); the rows left after that
    /// run the method. Without hardware acceleration (the JIT would emulate the vectors), every row runs the method.
    /// The two loops are one text: the Vector512 one names Vector512 where the other names Vector.
    /// </summary>
    private static void EmitVectorLoop(StringBuilder sb, Kernel.Code simd, string[] components, string indent, string call, bool masked)
    {
        for (int k = 1; k <= components.Length; k++)
            sb.Append($"{indent}ref {simd.Columns[k - 1]} e{k} = ref global::System.Runtime.CompilerServices.Unsafe.As<{components[k - 1]}, {simd.Columns[k - 1]}>(ref r{k});\n");
        sb.Append($"{indent}nint i = 0;\n");
        var loop = new StringBuilder();
        // Kernel.V stands for the vector type in both loops' text, and the tokens below for what their forms name apart.
        string vector = $"{Kernel.V}<{simd.Columns[0]}>"; // every column has lanes of one size: one count
        // Components of several fields need the shuffles of Interleaved; without them, every row runs the method.
        string interleaved = simd.Interleaves ? $" && global::EasyEcs.Interleaved.{Accelerated}" : "";
        // A wide component's permutes are AVX-512's, and its layout the generator's (checked once per job type).
        if (simd.Wide != null) interleaved += $" && global::System.Runtime.Intrinsics.X86.Avx512F.IsSupported && {simd.Wide}";
        loop.Append($"{indent}if ({Kernel.V}.IsHardwareAccelerated{interleaved} && rows - i >= {vector}.Count)\n{indent}{{\n");
        string inner = indent + "    ", body = inner + "    ";
        foreach (var line in simd.Setup.Split('\n')) if (line.Length > 0) loop.Append(inner).Append(line).Append('\n');
        loop.Append($"{inner}for (nint last = rows - {vector}.Count; i <= last; i += {vector}.Count)\n{inner}{{\n");
        if (masked)
        {
            // The rows of this vector that pass, one lane each: a vector with none is skipped.
            loop.Append($"{body}var __pass = global::EasyEcs.Interleaved.Lanes{simd.Size * 8}{Kernel.LanesForm}(pass, (int)i);\n");
            loop.Append($"{body}if (__pass == {Kernel.V}<{simd.Mask}>.Zero) continue;\n");
        }
        foreach (var line in (masked ? simd.MaskedBody : simd.Body).Split('\n')) if (line.Length > 0) loop.Append(body).Append(line).Append('\n');
        if (simd.Bails)
        {
            // A lane would throw (Math.Clamp with min above max): nothing is stored yet, so the method itself runs the
            // rows of this vector (that pass), and throws as written. i walks them, and is set back for the loop's own step.
            // The reductions' lanes go back to before this vector: the method adds those rows itself.
            string row = masked ? $"if (((pass >> (int)i) & 1) != 0) {call}" : call;
            loop.Append($"{body}if (__bail)\n{body}{{\n");
            foreach (var line in simd.Restore.Split('\n')) if (line.Length > 0) loop.Append(body).Append("    ").Append(line).Append('\n');
            loop.Append($"{body}    for (nint end = i + {vector}.Count; i < end; i++)\n{body}        {row}\n");
            loop.Append($"{body}    i -= {vector}.Count;\n{body}    continue;\n{body}}}\n");
        }
        foreach (var line in (masked ? simd.MaskedStores : simd.Stores).Split('\n')) if (line.Length > 0) loop.Append(body).Append(line).Append('\n');
        loop.Append($"{inner}}}\n");
        foreach (var line in simd.Fold.Split('\n')) if (line.Length > 0) loop.Append(inner).Append(line).Append('\n');
        loop.Append($"{indent}}}\n");
        // Only the tokens are replaced: setup lines name user types (in Vector3 wind, in Vector<float> bias) as written.
        // Vector512 names the same operations alike, except the square root (Vector.SquareRoot, Vector512.Sqrt).
        var text = new StringBuilder(loop.ToString());
        text.Replace(Kernel.Visited, masked ? "pass" : "ulong.MaxValue");
        sb.Append(text.ToString().Replace(Kernel.SquareRoot, Vector512 + ".Sqrt(").Replace(Kernel.V, Vector512)
            .Replace(Accelerated, "IsAccelerated512").Replace(Kernel.LanesForm, "In512"));
        if (simd.Wide == null) sb.Append(text.Replace(Kernel.V, Vector).Replace(Accelerated, "IsAccelerated").Replace(Kernel.LanesForm, ""));
    }

    /// <summary>
    /// The loop of a method that treats each field alike (<see cref="Kernel.FlatCode"/>): the columns as flat arrays of
    /// <c>rows * fields</c> lanes, no shuffles. The vector loops end at a multiple of <c>Count * fields</c> lanes, whole
    /// rows, so <c>i</c> becomes a row again for the rows left, which run the method.
    /// </summary>
    private static void EmitFlatLoop(StringBuilder sb, Kernel.FlatCode flat, Kernel.Code simd, string[] components, string indent)
    {
        for (int k = 1; k <= components.Length; k++)
            sb.Append($"{indent}ref {simd.Columns[k - 1]} e{k} = ref global::System.Runtime.CompilerServices.Unsafe.As<{components[k - 1]}, {simd.Columns[k - 1]}>(ref r{k});\n");
        sb.Append($"{indent}nint i = 0, lanes = rows * {flat.Fields}; // i counts lanes: {flat.Fields} per row\n");
        var loop = new StringBuilder();
        string count = $"{Kernel.V}<{simd.Columns[0]}>.Count";
        string chunk = $"{count} * {flat.Fields}"; // whole vectors of whole rows
        loop.Append($"{indent}if ({Kernel.V}.IsHardwareAccelerated && lanes - i >= {chunk})\n{indent}{{\n");
        string inner = indent + "    ", body = inner + "    ";
        foreach (var line in flat.Setup.Split('\n')) if (line.Length > 0) loop.Append(inner).Append(line).Append('\n');
        loop.Append($"{inner}for (nint end = lanes - (lanes - i) % ({chunk}); i < end; i += {count})\n{inner}{{\n");
        foreach (var line in (flat.Body + flat.Stores).Split('\n')) if (line.Length > 0) loop.Append(body).Append(line).Append('\n');
        loop.Append($"{inner}}}\n{indent}}}\n");
        var text = new StringBuilder(loop.ToString());
        sb.Append(text.ToString().Replace(Kernel.SquareRoot, Vector512 + ".Sqrt(").Replace(Kernel.V, Vector512));
        sb.Append(text.Replace(Kernel.V, Vector));
        sb.Append($"{indent}i /= {flat.Fields}; // whole rows: the row the rows left start at\n");
    }

    // A token of EmitVectorLoop's text (U+0001 occurs in no C# token outside a literal): Interleaved.IsAccelerated /
    // IsAccelerated512.
    private const string Accelerated = "\u0001Accelerated";

    private const string Vector = "global::System.Numerics.Vector", Vector512 = "global::System.Runtime.Intrinsics.Vector512";

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
    /// <param name="Key">A Lookup's index key type when its index is built before the pass (<see cref="Symbols.LookupKey"/>), else "".</param>
    private sealed record ParamModel(Symbols.ParamKind Kind, string Type, string RefKind, string Name, string Declared, bool Tracked = false, string Key = "");

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

        /// <summary>[ForEach(Parallel = true, ChunkRows = N)]: the rows a thread takes at a time (0: chosen by the runtime).</summary>
        public int ChunkRows { get; init; }

        /// <summary>The chunkRows argument of a parallel call that sets it, else nothing.</summary>
        public string Chunks => Parallel && ChunkRows > 0 ? $", chunkRows: {ChunkRows}" : "";

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

        /// <summary>The job holds the World: the method takes it, or a Lookup reads through it.</summary>
        public bool NeedsWorld => Parameters.Any(p => p.Kind is Symbols.ParamKind.World or Symbols.ParamKind.Lookup);

        /// <summary>The World loop (and, with an I, the job interface) the method runs as.</summary>
        public string Loop => Simd != null && !Parallel ? "ForEachMasked" : IsBatch ? "ForEachBatch" : TakesEntity ? "ForEachEntity" : "ForEach";

        /// <summary>Takes component spans, or runs per row inside runs of rows: World.ForEachBatch.</summary>
        public bool IsBatch => Parameters.Any(p => p.Kind == Symbols.ParamKind.ComponentSpan) || Runs || Simd != null;

        /// <summary>The method's vector loop (<see cref="Kernel"/>), or null: it runs one row at a time.</summary>
        public Kernel.Code? Simd { get; init; }

        /// <summary>Why it runs one row at a time (Kernel.Vectorize's reason, EECS055's), when it does.</summary>
        public string Scalar { get; init; } = "";

        /// <summary>
        /// One line on the loop the method got (SystemInfo.Loops): "vector" with its forms (flat, interleaved fields,
        /// masked words, parallel chunks, a cold branch), or "scalar" and why.
        /// </summary>
        public string Describe()
        {
            if (Simd is not { } simd) return $"{Name}: scalar ({(Scalar.Length > 0 ? Scalar : "not a vector kernel")})";
            var parts = new List<string> { $"vector (Vector512 or Vector<{simd.Columns[0]}>)" };
            if (simd.Flat != null) parts.Add("flat");
            else if (simd.Interleaves) parts.Add("interleaved fields");
            if (simd.Wide != null) parts[0] = "vector (Vector512 only)";
            if (simd.Wide != null) parts.Add("wide component (permutes)");
            if (Parallel) parts.Add("in parallel chunks");
            else parts.Add(simd.Size < 4 || simd.Wide != null ? "masked words scalar" : "masked words vector");
            if (simd.Cold != null) parts.Add($"cold branch ({simd.Cold})");
            return $"{Name}: {string.Join(", ", parts)}";
        }

        /// <summary>
        /// Changes no structure (on one thread) and takes a Maybe, a Mut, a Reduce or a caller's value by ref: its job walks
        /// runs of rows, finding each optional column once per run (a presence bit per row), stamping a Mut's writes by row
        /// (no entity lookup) and keeping partial results and the caller's values in locals for the run. In parallel, a
        /// method that reduces (and takes no Maybe or Mut) runs the same way, a chunk's runs at a time
        /// (ForEachBatchParallel): its partial results stay in registers instead of a load and a store per row.
        /// </summary>
        public bool Runs => Stable && !Parameters.Any(p => p.Kind == Symbols.ParamKind.ComponentSpan) && (Parallel
            ? Parameters.Any(p => p.Kind == Symbols.ParamKind.Reduction) && !Parameters.Any(p => p.Kind is Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable)
            : Parameters.Any(p => p.Kind is Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable or Symbols.ParamKind.Reduction || p is { Kind: Symbols.ParamKind.Context, RefKind: "ref" }));

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
                        Symbols.MaybeElement(p.Type)!.ToDisplayString(Symbols.Fqn), Symbols.IsReadOnlyMaybe(p.Type) || Symbols.OnlyReadsValue(Symbols.WithBody(method, syntax, model).Item1, p.Name) ? "in" : "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MaybeElement(p.Type)!, Symbols.TrackChanges)),
                    Symbols.ParamKind.Mutable => new ParamModel(Symbols.ParamKind.Mutable, // a write: the scheduler sees its writer
                        Symbols.MutElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MutElement(p.Type)!, Symbols.TrackChanges)),
                    Symbols.ParamKind.Reduction => new ParamModel(Symbols.ParamKind.Reduction, // a write of the singleton, once per pass
                        Symbols.ReduceElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared),
                    Symbols.ParamKind.Scratch => new ParamModel(Symbols.ParamKind.Scratch, Symbols.ScratchElement(p.Type)!.ToDisplayString(Symbols.Fqn), "", p.Name, declared),
                    Symbols.ParamKind.Emit => new ParamModel(Symbols.ParamKind.Emit, // a write of Emitted<T>, once per pass
                        Symbols.EmitElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared),
                    Symbols.ParamKind.Lookup => new ParamModel(Symbols.ParamKind.Lookup, Symbols.LookupElement(p.Type)!.ToDisplayString(Symbols.Fqn), "in", p.Name, declared,
                        Key: Symbols.LookupKey(p, method, Symbols.WithBody(method, syntax, model).Item1)?.ToDisplayString(Symbols.Fqn) ?? ""),
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
                    && method.TypeParameters.SelectMany(t => t.ConstraintTypes).All(Symbols.IsPublic)) {
                    Slices = Symbols.Slices(method), ChunkRows = Symbols.ChunkRows(method), Identifies = Symbols.OnlyIdentifies(method, syntax, model),
                    // Kernel.Vectorize decides alone (static, stable, its body; in parallel too): the analyzer reports its reasons.
                    Simd = Kernel.Vectorize(method, syntax, model, parallel ? static (i, kind) => kind switch
                    {
                        Symbols.ParamKind.Singleton => $"s{i}.Value", // a parallel job's SingletonRef
                        Symbols.ParamKind.Reduction => $"l{i}",
                        _ => $"x{i}",
                    } : static (i, kind) => kind switch
                    {
                        Symbols.ParamKind.Singleton => $"s{i}",
                        Symbols.ParamKind.Reduction => $"l{i}", // the run's partial result, a local of the batch job
                        _ => $"x{i}",
                    }, ct, out var scalar),
                    Scalar = scalar ?? "",
                };
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
