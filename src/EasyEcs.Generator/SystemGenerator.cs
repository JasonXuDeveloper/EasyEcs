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
/// Generates the plumbing of systems (<c>[EcsSystem]</c> ref structs, no partial needed): per system a code struct
/// (<c>ISystemCode</c>) with one struct job per <c>[ForEach]</c> method, the calls of its hooks and the data its methods
/// read and write (the parallel scheduler batches systems by it); per assembly one system set (<c>ISystemSet</c>,
/// <c>__EasyEcsSystems</c>) whose switches call every system's code directly, registered by a module initializer.
/// [ForEach] methods of other types get entry points.
/// Incremental-generator rules followed: <c>ForAttributeWithMetadataName</c> / cheap syntax predicates as entry points,
/// models are equatable records of strings (no symbols, syntax or locations survive the transform), invalid input is
/// skipped silently (EasyEcsAnalyzer reports it), and every stage has a tracking name for cache tests. The set's model
/// holds the systems' names only, so an edit inside a system's body never regenerates it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class SystemGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(Symbols.ForEachAttribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => ForEachModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel, ct))
            .Where(static m => m is not null)
            .WithTrackingName("ForEachMethods");

        // [EcsSystem] ref structs: their hooks and [UpdateInterval(n, Phase = p)], declarations only.
        var systems = context.SyntaxProvider
            .ForAttributeWithMetadataName(Symbols.EcsSystemAttribute,
                static (node, _) => node is StructDeclarationSyntax,
                static (ctx, _) => SystemModel.Create((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static m => m is not null)
            .WithTrackingName("Systems");

        // [ForEachRecord] methods: passes over the records of Emitted<T>, in the same models as [ForEach] methods.
        var records = context.SyntaxProvider
            .ForAttributeWithMetadataName(Symbols.ForEachRecordAttribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => ForEachModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel, ct))
            .Where(static m => m is not null)
            .WithTrackingName("RecordMethods");

        // Group per type: Collect re-runs this cheap step on any change, but each grouped model is equatable, so
        // RegisterSourceOutput only re-emits types whose methods or declaration actually changed.
        var updates = methods.Collect().Combine(records.Collect()).Combine(systems.Collect())
            .SelectMany(static (pair, _) => UpdateModel.Group(pair.Left.Left.AddRange(pair.Left.Right)!, pair.Right!))
            .WithTrackingName("SystemUpdates");
        // A system's code is reached only through the set's module initializer: implementation output, which IDEs skip
        // while typing. Entry points of other types are API the user's code calls.
        // Its vector loops too: they read the methods' bodies and their helpers' (SystemGenerator.Kernels.cs).
        var systemUpdates = WithLoops(context, updates.Where(static m => m.Owner.IsSystem));
        context.RegisterImplementationSourceOutput(systemUpdates, static (spc, model) => Add(spc, model));
        context.RegisterSourceOutput(updates.Where(static m => !m.Owner.IsSystem), static (spc, model) => Add(spc, model));

        // The assembly's system set: its non-generic systems and the closed generic systems it names (its own generic
        // systems' and those of referenced assemblies' public ones), registered without runtime code generation.
        var closed = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => SetEntry.IsCandidate(node), static (ctx, _) => SetEntry.Closed(ctx))
            .Where(static e => e is not null)
            .WithTrackingName("ClosedSystems")
            .Collect();
        var set = systems.Where(static s => s!.Owner.TypeParameters.Length == 0)
            .Select(static (s, _) => new SetEntry(s!.Owner.FullName, CodeName(s.Owner)))
            .Collect().Combine(closed)
            .Select(static (pair, _) => new EquatableArray<SetEntry>(pair.Left.Concat(pair.Right!).Distinct()
                .OrderBy(e => e!.System, System.StringComparer.Ordinal).ToArray()!))
            .WithTrackingName("SystemSet");
        // Pairs of systems that may share one walk (SystemGenerator.Fusion.cs): their code, and the set's declarations.
        var fusions = FusionPairs(context, systemUpdates);
        context.RegisterImplementationSourceOutput(fusions, static (spc, pair) => AddFusion(spc, pair));
        var declared = fusions.Select(static (p, _) => new SetFusion(p.First.Owner.FullName, p.Second.Owner.FullName, p.FullCodeName))
            .Collect().WithTrackingName("SetFusions");
        context.RegisterImplementationSourceOutput(set.Combine(declared), static (spc, input) =>
        {
            if (input.Left.Length > 0) spc.AddSource("__EasyEcsSystems.g.cs", SourceText.From(EmitSet(input.Left, input.Right), Encoding.UTF8));
        });

        // [ForEntity] methods: entry points that run the method's job on one entity (entity.Run), in a system too.
        var ones = context.SyntaxProvider
            .ForAttributeWithMetadataName(Symbols.ForEntityAttribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => ForEachModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel, ct))
            .Where(static m => m is not null)
            .WithTrackingName("ForEntityMethods");
        var entityTypes = ones.Collect()
            .SelectMany(static (all, _) => UpdateModel.Group(all!, [], entryPoints: true).Select(m => m with { One = true }))
            .WithTrackingName("ForEntityTypes");
        context.RegisterSourceOutput(entityTypes, static (spc, model) => Add(spc, model));

        // world.ForEach(static (ref P p) => ...): each call intercepted by the job of its lambda (SystemGenerator.Lambdas.cs).
        InitializeLambdas(context);
    }

    private static void Add(SourceProductionContext context, UpdateModel model)
    {
        var owner = model.Owner;
        context.AddSource($"{owner.HintName}.{(model.One ? "ForEntity" : owner.IsSystem ? "System" : "ForEach")}.g.cs", SourceText.From(Emit(model), Encoding.UTF8));
        if (model.One || !owner.IsSystem || !owner.IsPublic || owner.TypeParameters.Length == 0) return;
        // Assembly attributes cannot follow the namespace of the code's file.
        string ns = owner.Namespace.Length > 0 ? owner.Namespace + "." : "";
        string open = $"global::{ns}{CodeTypeName(owner)}<{new string(',', owner.Arity - 1)}>";
        context.AddSource($"{owner.HintName}.SystemCode.g.cs", SourceText.From(
            $"{Header}\n[assembly: global::EasyEcs.GenericSystemCode(typeof({owner.Definition}), typeof({open}))]\n", Encoding.UTF8));
    }

    // ---------------- emit ----------------

    /// <summary>
    /// One file per type with [ForEach] methods or [EcsSystem], outside the type (which needs no partial): a file-local
    /// class holds an UnsafeAccessor per method (private methods too), its struct job and its filter. A system gets its
    /// code struct (<see cref="EmitSystemCode"/>), which the set calls; any other type gets entry points, static
    /// extension members of the type.
    /// </summary>
    private static string Emit(UpdateModel model)
    {
        var owner = model.Owner;
        var sb = new StringBuilder(Header).Append('\n');
        if (owner.Namespace.Length > 0) sb.Append("namespace ").Append(owner.Namespace).Append(";\n\n");
        if (model.One || !owner.IsSystem) EmitEntryPoints(sb, model, model.One);

        // Generic over the owner's type parameters (a generic system or runner type): one instantiation per closed type.
        sb.Append($"file static class __ForEach{owner.TypeParameters}{owner.Constraints}\n{{\n");
        for (int i = 0; i < model.Methods.Length; i++)
        {
            EmitAccessor(sb, owner, model.Methods[i], i);
            EmitJob(sb, owner, model.Methods[i], i);
        }

        bool system = owner.IsSystem && !model.One;
        if (system)
        {
            foreach (var hook in model.Hooks) EmitHookAccessor(sb, owner, hook);
            EmitFusedJobs(sb, model);
        }

        sb.Append("}\n");
        if (system) EmitSystemCode(sb, model);
        return sb.ToString();
    }

    /// <summary>The code struct of a system (<c>__Movement_SystemCode</c>), public for a public generic system, whose closed types other assemblies' sets name.</summary>
    private static string CodeTypeName(OwnerModel owner) => $"__{owner.FlatName}_SystemCode";

    /// <summary>The code struct as a set names it, closed over the system's own type parameters.</summary>
    private static string CodeName(OwnerModel owner) =>
        $"global::{(owner.Namespace.Length > 0 ? owner.Namespace + "." : "")}{CodeTypeName(owner)}{owner.TypeParameters}";

    /// <summary>
    /// The assembly's system set: its systems numbered in name order, a module initializer that registers it, and one
    /// switch per hook whose cases call each system's code directly (a direct call the JIT may inline).
    /// </summary>
    private static string EmitSet(EquatableArray<SetEntry> entries, ImmutableArray<SetFusion> fusions)
    {
        var sb = new StringBuilder(Header).Append('\n');
        const string world = "global::EasyEcs.World", handle = "global::EasyEcs.SystemHandle";
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append("file readonly struct __EasyEcsSystems : global::EasyEcs.ISystemSet\n{\n");
        sb.Append($"    {Code.ModuleInitializer}\n    internal static void Register() => global::EasyEcs.SystemCode.Register<__EasyEcsSystems>();\n\n");
        sb.Append("    public static void Declare(ref global::EasyEcs.SystemDeclarations systems)\n    {\n");
        foreach (var entry in entries) sb.Append($"        systems.Add<{entry.System}, {entry.Code}>();\n");
        // Pairs of systems that run as one walk when the schedule puts them next to each other (by their numbers).
        var known = fusions.Where(f => entries.Any(e => e.System == f.First) && entries.Any(e => e.System == f.Second)).ToArray();
        int Number(string system) { for (int i = 0; ; i++) if (entries[i].System == system) return i; }
        foreach (var fusion in known) sb.Append($"        systems.Fuse({Number(fusion.First)}, {Number(fusion.Second)}); // {fusion.Code}\n");
        sb.Append("    }\n");
        if (known.Length > 0)
        {
            sb.Append($"\n    public static bool RunFused({world} world, int fusion, {handle} first, {handle} second, ulong secondTick)\n    {{\n        switch (fusion)\n        {{\n");
            for (int i = 0; i < known.Length; i++)
                sb.Append($"            case {i}: return {known[i].Code}.Update(world, first, second, secondTick);\n");
            sb.Append("        }\n\n        return false;\n    }\n");
        }
        foreach (var hook in new[] { "Update", "Create", "Destroy" })
        {
            sb.Append($"\n    public static void {hook}({world} world, int system, {handle} handle)\n    {{\n        switch (system)\n        {{\n");
            for (int i = 0; i < entries.Length; i++)
                sb.Append($"            case {i}: {entries[i].Code}.{hook}(world, handle); return;\n");
            sb.Append("        }\n    }\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The method itself, reached from outside its type: compiled to a direct call, inlined like one. Generated names
    /// end in the method's index (<c>Heal_0</c>, <c>HealJob0</c>, <c>HealFilter0</c>): overloads and methods named like a
    /// generated member (<c>Code</c>) cannot collide, and profilers still show the method's name.
    /// </summary>
    private static void EmitAccessor(StringBuilder sb, OwnerModel owner, ForEachModel method, int index)
    {
        string parameters = string.Concat(method.Parameters.Select(p => $", {p.Declared} {Escape(p.Name)}"));
        // The instance parameter must not take the name of one of the method's own parameters (CS0100).
        string self = "owner";
        while (method.Parameters.Any(p => p.Name == self)) self = "_" + self;
        // A stencil over a tile is compiled as a root of its own, so the JIT spends its inlining budget (a multiple of
        // the root's size) on the method's loop, its samples and the helpers they call: inlined into this small
        // accessor (or the job), a method of that size uses up the budget and calls its samples. The accessor runs
        // unoptimized (NoOptimization: no inlining in it, one call per entity), and is never inlined itself.
        // Otherwise a Scratch method that loops or calls runs in its own frame (ForEachModel.OutOfLine); the method
        // still inlines into the accessor, so the call per row is the only cost.
        string impl = method.Parameters.Any(p => p.Kind == Symbols.ParamKind.Tile)
            ? ", global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining | global::System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)"
            : method.OutOfLine
                ? ", global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)"
                : "";
        sb.Append($"    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = \"{method.Name}\"){impl}]\n");
        sb.Append($"    internal static extern {method.Returns} {method.Name}_{index}{method.TypeParameters}({owner.AccessorParameter} {self}{parameters}){method.Constraints};\n\n");
    }

    /// <summary>
    /// The entry points of a type that is not a system, static extension members of the type. [ForEach] methods run
    /// the job over the world (taking the world, the change tick for [Changed&lt;T&gt;] / [Removed&lt;T&gt;], the slice of
    /// a sliced method); [ForEntity] methods (<paramref name="perEntity"/>) run it on the entity they take with
    /// <c>entity.Run</c> and return whether it ran. Both take the method's context parameters.
    /// </summary>
    private static void EmitEntryPoints(StringBuilder sb, UpdateModel model, bool perEntity)
    {
        var owner = model.Owner;
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"{(owner.IsPublic ? "public" : "internal")} static class {owner.FlatName}{(perEntity ? "ForEntity" : "ForEach")}\n{{\n");
        sb.Append($"    extension{owner.TypeParameters}({owner.FullName}){owner.Constraints}\n    {{\n");
        for (int m = 0; m < model.Methods.Length; m++)
        {
            var method = model.Methods[m];
            var contexts = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Context).ToArray();
            var names = new HashSet<string>(contexts.Select(p => p.Name).Concat(method.TypeParameterNames));
            // The entity keeps the name the method gives it (Buy(Entity shop, ...) -> Buy(shop, ...)).
            string entity = perEntity ? Free(method.Parameters.FirstOrDefault(p => p.Kind == Symbols.ParamKind.Entity)?.Name ?? "entity") : "";
            if (perEntity) names.Add(entity);
            string world = Free("world"), since = Free("since"), slice = Free("slice"), local = Free("job");
            bool sliced = !perEntity && method.Slices > 1;
            var signature = new List<string> { perEntity ? $"global::EasyEcs.Entity {Escape(entity)}" : $"global::EasyEcs.World {world}" };
            if (method.Since) signature.Add($"ulong {since}");
            if (sliced) signature.Add($"int {slice}");
            signature.AddRange(contexts.Select(p => $"{p.Declared} {Escape(p.Name)}"));

            string forEach = "__ForEach" + owner.TypeParameters, job = $"{forEach}.{method.Name}Job{m}{method.TypeParameters}";
            // A pass takes its filter field, even an empty one (NoFilter); entity.Run without a filter tests none.
            string filter = method.Since
                ? perEntity ? $", in {forEach}.{method.Name}Filter{m}, {since}" : $", {forEach}.{method.Name}Filter{m}.Since({since})" // the static filter and the tick: no copy per call
                : method.HasFilter || !perEntity ? $", in {forEach}.{method.Name}Filter{m}" : "";
            if (sliced) filter = method.Sliced($"{forEach}.{method.Name}Filter{m}", slice);
            if (perEntity)
            {
                string which = method.Since ? $" and its filtered components changed (or went) at or after tick <paramref name=\"{since}\"/>"
                    : method.HasFilter ? " and passes its filters" : "";
                sb.Append($"        /// <summary>Runs <c>{method.Name}</c> on <paramref name=\"{entity}\"/> when it is alive, has the method's components{which}: true when it ran. Owner thread only.</summary>\n");
            }
            else if (method.Record.Length > 0)
                sb.Append($"        /// <summary>Runs <c>{method.Name}</c> on each record of <paramref name=\"{world}\"/>'s Emitted, in the order they were emitted, then clears them. Owner thread only.</summary>\n");
            else
            {
                string which = method.Since ? $" whose filtered components changed (or went) at or after tick <paramref name=\"{since}\"/>"
                    : sliced ? $" in slice <paramref name=\"{slice}\"/> (0 to {method.Slices - 1}) of each matching table's rows" : "";
                sb.Append($"        /// <summary>Runs <c>{method.Name}</c> for every matching entity of <paramref name=\"{world}\"/>{which}. Owner thread only.</summary>\n");
            }

            sb.Append($"        {Code.GeneratedCode(Symbols.GeneratorName)}\n");
            sb.Append($"        {(method.PublicEntry ? "public" : "internal")} static {(perEntity ? "bool" : "void")} {Escape(method.Name)}{method.TypeParameters}({string.Join(", ", signature)}){method.Constraints}\n        {{\n");
            // The world, for singletons, lookups and a World parameter: a default handle has none (Run returns false for it).
            if (perEntity && (method.NeedsWorld || method.Parameters.Any(p => p.Kind == Symbols.ParamKind.Singleton)))
                sb.Append($"            if ({Escape(entity)}.World is not {{ }} {world}) return false;\n");
            if (perEntity)
            {
                sb.Append($"            var {local} = new {job}{Fields(method, world)};\n");
                sb.Append($"            return {Escape(entity)}.Run<{method.ComponentTypes}, {job}>(ref {local}{filter});\n");
            }
            else EmitPass(sb, method, forEach, m, world, local, filter, filter, "            ");

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
    /// One pass of method <paramref name="i"/>: its job made and run over the world, its reductions and [Consume]
    /// after it. A vector loop that marks rows or emits records (<see cref="Kernel.Code.VectorTail"/>) runs only where its
    /// vectors are hardware (the job's <c>Vectorizes</c>, a JIT-time constant), its marked rows changed when the pass
    /// ends; elsewhere the method's own job (<c>Job{i}Rows</c>) runs it row by row.
    /// </summary>
    private static void EmitPass(StringBuilder sb, ForEachModel method, string forEach, int i, string world, string local, string filter, string whole, string indent)
    {
        if (method.Pair != null)
        {
            EmitPairPass(sb, method, forEach, i, world, local, indent); // SystemGenerator.Pairs.cs
            return;
        }

        string job = $"{forEach}.{method.Name}Job{i}{method.TypeParameters}";
        if (method.Record.Length > 0) // [ForEachRecord]: the records in order, then cleared
        {
            sb.Append($"{indent}var {local} = new {job}{Fields(method, world)};\n");
            sb.Append($"{indent}global::EasyEcs.Emitted<{method.Record}>.Consume({world}, ref {local});\n");
            return;
        }

        void Pass(ForEachModel m, string name, string at)
        {
            sb.Append($"{at}var {local} = new {name}{Fields(m, world)};\n");
            sb.Append($"{at}{world}.{m.ForEach}<{m.ComponentTypes}, {name}>(ref {local}{filter}{m.Chunks});\n");
            EmitReductions(sb, m, world, local, at);
            EmitConsume(sb, m, world, whole, at);
        }

        if (method.HasChaseTwin)
        {
            // Past the last-level cache (any of its lookups' tables), the twin prefetching the chases; else today's job.
            sb.Append($"{indent}if ({ChaseGate([method], world)})\n{indent}{{\n");
            Pass(method with { IsChaseTwin = true }, $"{forEach}.{method.Name}Job{i}Chases{method.TypeParameters}", indent + "    ");
            sb.Append($"{indent}}}\n{indent}else\n{indent}{{\n");
            Pass(method, job, indent + "    ");
            sb.Append($"{indent}}}\n");
            return;
        }

        if (method.Simd is not { VectorTail: true } simd)
        {
            Pass(method, job, indent);
            return;
        }

        sb.Append($"{indent}if ({job}.Vectorizes)\n{indent}{{\n");
        Pass(method, job, indent + "    ");
        foreach (var write in simd.StructuralChanges)
        {
            string kind = write.Split(' ')[0], type = write.Length > kind.Length ? write.Substring(kind.Length + 1) : "";
            sb.Append($"{indent}    {local}.d0.{kind switch { "Set" => $"SetAll({local}.v0)", "Destroy" => "DestroyAll()", _ => $"{kind}All<{type}>()" }}; // the rows the pass marked\n");
        }

        sb.Append($"{indent}}}\n{indent}else\n{indent}{{\n");
        Pass(method with { Simd = null }, $"{forEach}.{method.Name}Job{i}Rows{method.TypeParameters}", indent + "    ");
        sb.Append($"{indent}}}\n");
    }

    /// <summary>
    /// [Consume] in bulk: the pass made no structural change, so the rows that pass its filter (<paramref name="filter"/>,
    /// as the pass's call takes it) when it ends are exactly the rows it visited. One DestroyAll destroys them.
    /// </summary>
    private static void EmitConsume(StringBuilder sb, ForEachModel method, string world, string filter, string indent)
    {
        if (!method.ConsumesInBulk) return;
        sb.Append($"{indent}{world}.DestroyAll<{method.ComponentTypes}>({(filter.Length == 0 ? "" : filter.Substring(2))}); // [Consume]: the rows the pass visited\n");
    }

    /// <summary>
    /// The job's initializer: the system (instance methods), the world, singletons resolved once per pass, and the
    /// caller's values (by reference, unless a parallel job copies them for its threads).
    /// </summary>
    private static string Fields(ForEachModel method, string world)
    {
        var fields = new List<string>();
        if (method.NeedsWorld) fields.Add($"World = {world}");
        for (int i = 0; i < method.Parameters.Length; i++)
            if (JobField(method, i, world) is { Initializer: { } initializer }) fields.Add(initializer);
        if (method.Simd is { StructuralChanges.Length: > 0 }) fields.Add($"d0 = global::EasyEcs.MarkedRows.Rent({world})");
        return fields.Count == 0 ? "()" : $" {{ {string.Join(", ", fields)} }}";
    }

    /// <summary>
    /// The job's field for parameter <paramref name="i"/>, if it has one: its declaration (lines) and what the job's
    /// initializer sets it to (null when the job fills it itself). Singletons are resolved once per pass, not per
    /// entity; the caller's values are held by reference unless a parallel job copies them for its threads.
    /// </summary>
    private static (string Declaration, string? Initializer)? JobField(ForEachModel method, int i, string world)
    {
        var p = method.Parameters[i];
        switch (p.Kind)
        {
            case Symbols.ParamKind.Singleton:
                return method.Parallel
                    ? ($"public global::EasyEcs.SingletonRef<{p.Type}> s{i};", $"s{i} = {world}.SingletonRef<{p.Type}>()")
                    : ($"public ref {p.Type} s{i};", $"s{i} = ref {world}.SingletonRef<{p.Type}>().Value");
            case Symbols.ParamKind.Context:
                return p.RefKind.Length == 0 || method.Parallel
                    ? ($"public {p.Type} x{i};", $"x{i} = {Escape(p.Name)}")
                    : ($"public ref {(p.RefKind == "in" ? "readonly " : "")}{p.Type} x{i};", $"x{i} = ref {Escape(p.Name)}");
            case Symbols.ParamKind.Reduction: // the partial result, of the pass or of a chunk's copy; every one starts at Identity
                return ($"public global::EasyEcs.Reduce<{p.Type}> a{i};", $"a{i} = new global::EasyEcs.Reduce<{p.Type}> {{ Value = {p.Type}.Identity }}");
            case Symbols.ParamKind.Scratch: // this thread's box (null until the first row of a chunk)
                return ($"public global::System.Runtime.CompilerServices.StrongBox<{p.Type}> t{i};", null);
            case Symbols.ParamKind.Lookup when p.Key.Length > 0: // its index, built before the pass
                return ($"public object k{i};", $"k{i} = global::EasyEcs.LookupExtensions.Prepared<{p.Type}, {p.Key}>({world})");
            case Symbols.ParamKind.Lookup when method.Prefetches(i): // whether its chases are past the last-level cache, once per pass
                return ($"public bool f{i};", $"f{i} = global::EasyEcs.Lookup<{p.Type}>.Prefetches({world})");
            // The grid's index, built before the pass, and the entity's neighbors: a field, not a local, which the
            // caller's zero-initialized locals would clear on every row before TileNeighbors.Of clears it again.
            case Symbols.ParamKind.Tile when method.FirstOfGrid(i):
                return ($"public object k{i};\npublic global::EasyEcs.TileNeighbors n{i};", $"k{i} = global::EasyEcs.LookupExtensions.Prepared<{p.Grid}, {p.Key}>({world})");
            case Symbols.ParamKind.Emit: // on one thread the singleton's list itself; in parallel the target of the chunks' lists (from the pool)
                return method.Parallel
                    ? ($"public global::EasyEcs.SingletonRef<global::EasyEcs.Emitted<{p.Type}>> o{i};\npublic global::EasyEcs.DynamicBuffer<{p.Type}> e{i};",
                        $"o{i} = global::EasyEcs.Emitted<{p.Type}>.Joined({world})")
                    : ($"public ref global::EasyEcs.DynamicBuffer<{p.Type}> e{i};", $"e{i} = ref global::EasyEcs.Emitted<{p.Type}>.Joined({world}).Value.Items");
            default:
                return null;
        }
    }

    /// <summary>
    /// A pass's reductions (<c>Reduce&lt;T&gt;</c>), combined into the job by the pass: each replaces its singleton
    /// (added when missing), once per pass, or for an [Accumulate] singleton is combined into it, the world's value
    /// first (added at Identity when missing).
    /// </summary>
    private static void EmitReductions(StringBuilder sb, ForEachModel method, string world, string job, string indent)
    {
        for (int i = 0; i < method.Parameters.Length; i++)
            if (method.Parameters[i] is { Kind: Symbols.ParamKind.Reduction } p)
            {
                if (!p.Accumulates)
                {
                    sb.Append($"{indent}{world}.GetOrAddSingleton<{p.Type}>() = {job}.a{i}.Value;\n");
                    continue;
                }

                string total = job.Replace(".", "_") + "_total" + i;
                sb.Append($"{indent}ref var {total} = ref {world}.GetOrAddSingleton({p.Type}.Identity); // [Accumulate]: the total, then this pass\n");
                sb.Append($"{indent}{total} = {p.Type}.Combine({total}, {job}.a{i}.Value);\n");
            }
    }

    /// <summary>
    /// A hook method of the system, reached from outside its type (an UnsafeAccessor by the system's type: a private
    /// method of a ref struct has no other way in).
    /// </summary>
    private static void EmitHookAccessor(StringBuilder sb, OwnerModel owner, HookModel hook)
    {
        string parameters = string.Concat(hook.Arguments.Select((a, i) => a == 'W' ? $", global::EasyEcs.World world{i}" : $", global::EasyEcs.SystemHandle system{i}"));
        sb.Append($"    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = \"{hook.Name}\")]\n");
        sb.Append($"    internal static extern void {hook.Name}_{hook.Kind}({owner.AccessorParameter} owner{parameters});\n\n");
    }

    /// <summary>
    /// The system's code struct (<c>ISystemCode</c>), static members only, called directly by the assembly's set:
    /// Update runs the OnUpdate method, then the [ForEach] methods in declaration order; Create and Destroy run the
    /// other hooks; Access declares what the methods read and write; Interval and Phase are its [UpdateInterval].
    /// </summary>
    private static void EmitSystemCode(StringBuilder sb, UpdateModel model)
    {
        var owner = model.Owner;
        const string world = "global::EasyEcs.World", handle = "global::EasyEcs.SystemHandle";
        string forEach = "__ForEach" + owner.TypeParameters;
        sb.Append("\n[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"{(owner.IsPublic && owner.TypeParameters.Length > 0 ? "public" : "internal")} readonly struct {CodeTypeName(owner)}{owner.TypeParameters} : global::EasyEcs.ISystemCode{owner.Constraints}\n{{\n");
        if (model.Interval > 0)
            sb.Append($"    public static int Interval => {model.Interval};\n\n    public static int Phase => {model.Phase};\n\n");
        sb.Append($"    public static void Update({world} world, {handle} system)\n    {{\n");
        EmitHookCall(sb, model, "Update");
        var groups = FusionGroups(model);
        for (int i = 0; i < model.Methods.Length; i++)
        {
            if (groups[i] > 1) // methods that share one walk (SystemGenerator.Fusion.cs)
            {
                EmitFusedCall(sb, model, i, groups[i]);
                i += groups[i] - 1;
                continue;
            }

            EmitMethodPass(sb, model, i);
        }

        sb.Append("    }\n\n");
        foreach (var hook in new[] { "Create", "Destroy" })
        {
            sb.Append($"    public static void {hook}({world} world, {handle} system)\n    {{\n");
            EmitHookCall(sb, model, hook);
            sb.Append("    }\n\n");
        }

        // How each method runs (SystemInfo.Loops): one array for the type, made once. A vector loop the hardware lacks
        // (AVX-512's permutes, ARM64's table lookups, AVX2's gathers) says so on the machine that runs it.
        static string Line(ForEachModel m, string note) => RunsVector(m.Simd) is { } runs
            ? $"{Code.Literal(m.Describe())} + ({runs} ? \"\" : \"; rows on this hardware\"){(note.Length > 0 ? " + " + Code.Literal(note) : "")}"
            : Code.Literal(m.Describe() + note);
        sb.Append($"    private static readonly string[] __loops = [{string.Join(", ", model.Methods.Select((m, i) => Line(m, FusionNote(model, groups, i))))}];\n\n");
        sb.Append("    public static string[] Loops => __loops;\n\n");
        EmitAccess(sb, model);
        sb.Append("}\n");
    }

    /// <summary>The pass of method <paramref name="i"/> of the system's Update, on its own (no other method in its walk).</summary>
    private static void EmitMethodPass(StringBuilder sb, UpdateModel model, int i)
    {
        string forEach = "__ForEach" + model.Owner.TypeParameters;
        {
            var method = model.Methods[i];
            string job = $"{forEach}.{method.Name}Job{i}", filterName = $"{forEach}.{method.Name}Filter{i}";
            // [Changed<T>] / [Removed<T>]: rows written (or that lost T) since this system last ran.
            string filter = method.Since ? $", {filterName}.Since(system.LastRunTick + 1)" : $", in {filterName}";
            string whole = filter;
            if (method.Slices > 1) filter = method.Sliced(filterName, $"(int)(system.RunCount % {method.Slices})"); // this run's slice
            // WorldOptions.MeasureSystems: the entities the pass matches as it starts (SystemStats.LastEntities); one
            // field test per pass otherwise, nothing in the row loop. Count takes no slice: a sliced pass reports its share.
            string count = $"world.Count<{method.ComponentTypes}>({(whole.Length == 0 ? "" : whole.Substring(2))})";
            if (method.Slices > 1) count = $"{count} / {method.Slices}";
            if (method.Record.Length > 0) // the records it handles
                count = $"(world.HasSingleton<global::EasyEcs.Emitted<{method.Record}>>() ? world.GetSingleton<global::EasyEcs.Emitted<{method.Record}>>().Items.Count : 0)";
            sb.Append($"        if (global::EasyEcs.SystemCode.Measuring(world)) global::EasyEcs.SystemCode.Visit(system, {count});\n");
            EmitPass(sb, method, forEach, i, "world", $"job{i}", filter, whole, "        ");
        }
    }

    /// <summary>The call of the system's <paramref name="kind"/> hook, if it has one: its parameters in its order.</summary>
    private static void EmitHookCall(StringBuilder sb, UpdateModel model, string kind)
    {
        foreach (var hook in model.Hooks)
            if (hook.Kind == kind)
                sb.Append($"        __ForEach{model.Owner.TypeParameters}.{hook.Name}_{kind}(default{string.Concat(hook.Arguments.Select(a => a == 'W' ? ", world" : ", system"))});\n");
    }

    /// <summary>
    /// <c>ref</c> parameters are writes, <c>in</c> parameters reads. Anything that can reach the world beyond its
    /// parameters (EntitySpan / World parameters, an Entity parameter used for more than its identity, [Consume], a pair
    /// method, an OnUpdate method) runs alone.
    /// </summary>
    private static void EmitAccess(StringBuilder sb, UpdateModel model)
    {
        sb.Append("    public static void Access(ref global::EasyEcs.SystemAccess access)\n    {\n");
        bool alone = model.Hooks.Any(h => h.Kind == "Update") || model.Methods.Any(m => m.Consume // [Consume] destroys
            || m.Parameters.Any(p => p.Kind is Symbols.ParamKind.Entities or Symbols.ParamKind.World
                || p.Kind == Symbols.ParamKind.Entity && !m.Identifies
                || p.Kind == Symbols.ParamKind.Neighbor)); // a pair pass sorts its tables on its first run and keeps per-world pair state
        if (alone)
        {
            sb.Append("        access.RunAlone();\n    }\n");
            return;
        }

        // One line per declaration: a type taken twice (a parameter and a [Changed<T>] filter, a lookup and a tile of one
        // grid) is declared once.
        var lines = new List<string>();
        void Declare(string line) { if (!lines.Contains(line)) lines.Add(line); }

        // A view's fields: writes and reads of its components.
        foreach (var field in model.Methods.SelectMany(m => m.Parameters).Where(p => p.Kind == Symbols.ParamKind.View).SelectMany(p => p.Fields))
            Declare($"{(field.Access == "in" ? "Read" : "Write")}<{field.Type}>()");

        foreach (var p in model.Methods.SelectMany(m => m.Parameters).Where(p => p.Kind is not (Symbols.ParamKind.Entity or Symbols.ParamKind.Scratch or Symbols.ParamKind.View))
                     .Select(p => (p.Kind, p.Type, p.RefKind, p.Key, p.Grid)).Distinct())
        {
            if (p is { Kind: Symbols.ParamKind.Lookup, Key.Length: > 0 }) // a read, and its index built before a parallel stage
            {
                Declare($"Index<{p.Type}, {p.Key}>()");
                continue;
            }
            if (p.Kind == Symbols.ParamKind.Tile) // reads of the cells and the grid component, the grid's index built before
            {
                Declare($"Index<{p.Grid}, {p.Key}>()");
                Declare($"Read<{p.Type}>()");
                continue;
            }

            if (p.Kind == Symbols.ParamKind.Record) // reads and clears Emitted<T>
            {
                Declare($"WriteSingleton<global::EasyEcs.Emitted<{p.Type}>>()");
                continue;
            }

            if (p.Kind == Symbols.ParamKind.Emit) // appends to Emitted<T>, once per pass
            {
                Declare($"WriteSingleton<global::EasyEcs.Emitted<{p.Type}>>()");
                continue;
            }

            string kind = p.Kind is Symbols.ParamKind.Singleton or Symbols.ParamKind.Reduction ? "Singleton" : "";
            Declare($"{(p.RefKind == "ref" ? "Write" : "Read")}{kind}<{p.Type}>()");
        }

        // [Changed<T>], [Removed<T>] and the ChangedAny / RemovedAny sets read T's change ticks, which writers and
        // removers of T stamp.
        foreach (var type in model.Methods.SelectMany(m => m.Changed.Concat(m.Removed).Concat(m.ChangedAny).Concat(m.RemovedAny)).Distinct())
            Declare($"Read<{type}>()");

        foreach (var line in lines) sb.Append("        access.").Append(line).Append(";\n");
        sb.Append("    }\n");
    }

    /// <summary>
    /// The row loop of the twin job of a method that chases other entities (<see cref="ForEachModel.Chases"/>), run only
    /// past the last-level cache (its pass's call site decides), group prefetching (AMAC): each chase's dependent loads
    /// one stage per k rows (k = <see cref="ForEachModel.ChaseDistance"/>), the first stage furthest ahead, since each
    /// address is known only once the stage before brought its line in: an entity's record 2k rows ahead, then its row k
    /// ahead (for each element of a buffer of links too); an index probe's slot or bucket, then its entry. First the
    /// first stage of the first rows; the last rows, and every row of a
    /// run too short to prefetch, run as before. A prefetch never faults and changes nothing: a dead or recycled target, or
    /// a key no entity has, only wastes it.
    /// </summary>
    private static void EmitChases(StringBuilder sb, ForEachModel method, string indent, string call)
    {
        const int k = ForEachModel.ChaseDistance;
        const int span = 2 * k;
        string At(Chase c, string row)
        {
            int column = 1 + method.Parameters.Take(c.Component).Count(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.Mutable);
            return $"global::System.Runtime.CompilerServices.Unsafe.Add(ref r{column}, {row})";
        }
        string Lookup(Chase c) => $"new global::EasyEcs.Lookup<{method.Parameters[c.Lookup].Type}>(World, null)";
        // Stage s (1-based) of chase c at row `row`, or null when the chase has no such stage.
        string? Stage(Chase c, int s, string row) => c.Kind switch
        {
            ChaseKind.Probe => $"global::EasyEcs.LookupExtensions.PrefetchKey<{method.Parameters[c.Lookup].Type}, {method.Parameters[c.Lookup].Key}>(k{c.Lookup}, {At(c, row)}{c.Id}, {s});",
            _ when s == 2 && !c.Row => null, // Has: the record alone
            ChaseKind.Buffer => $"foreach (ref readonly var __link in {At(c, row)}.AsReadOnlySpan()) {Lookup(c)}.{(s == 1 ? "Prefetch" : "PrefetchRow")}(__link{c.Id});",
            _ => $"{Lookup(c)}.{(s == 1 ? "Prefetch" : "PrefetchRow")}({At(c, row)}{c.Id});",
        };
        sb.Append($"{indent}nint i = 0;\n");
        sb.Append($"{indent}if (rows > {span}) // the chases {k} and {span} rows ahead\n{indent}{{\n");
        sb.Append($"{indent}    for (nint j = 0; j < {span}; j++)\n{indent}    {{\n");
        foreach (var c in method.Chases) sb.Append($"{indent}        {Stage(c, 1, "j")}\n");
        sb.Append($"{indent}    }}\n");
        sb.Append($"{indent}    for (nint last = rows - {span}; i < last; i++)\n{indent}    {{\n");
        foreach (var c in method.Chases)
            for (int s = 1; s <= 2; s++)
                if (Stage(c, s, $"i + {(3 - s) * k}") is { } line)
                    sb.Append($"{indent}        {line}\n");
        sb.Append($"{indent}        {call}\n{indent}    }}\n{indent}}}\n");
        sb.Append($"{indent}for (; i < rows; i++)\n{indent}    {call}\n");
    }

    private static void EmitJob(StringBuilder sb, OwnerModel owner, ForEachModel method, int number, string suffix = "")
    {
        if (method.Pair != null)
        {
            EmitPairJob(sb, owner, method, number); // SystemGenerator.Pairs.cs
            return;
        }
        if (method.Record.Length > 0)
        {
            EmitRecordJob(sb, owner, method, number);
            return;
        }

        // A vector loop that must take every row has the method's own job beside it, for hardware without its vectors.
        if (suffix.Length == 0 && method.Simd is { VectorTail: true }) EmitJob(sb, owner, method with { Simd = null }, number, "Rows");
        // A method chasing other entities has a twin prefetching them, for the passes past the last-level cache.
        if (suffix.Length == 0 && method.HasChaseTwin) EmitJob(sb, owner, method with { IsChaseTwin = true }, number, "Chases");
        string job = $"{method.Name}Job{number}{suffix}{method.TypeParameters}";
        var components = method.Query.ToArray();
        int index = 0;
        bool runs = method.Runs || method.Simd != null; // per row of a run: components and handles at the row's offset
        // A batch job copies the caller's ref values into locals for its run (written back after it): through the
        // ref field, every row would load and store memory, a dependency chain of ~2.5 ns per row. No finally: Execute
        // would no longer inline, so a method that throws loses its changes of the run in progress.
        bool[] local = method.Parameters.Select(p => method.IsBatch && (p.Kind == Symbols.ParamKind.Context && p.RefKind == "ref" || p.Kind == Symbols.ParamKind.Reduction)).ToArray();
        string Field(int i) => method.Parameters[i].Kind == Symbols.ParamKind.Reduction ? $"a{i}" : $"x{i}";
        // A view's fields take the columns after the component parameters' (ForEachModel.Query), in parameter order.
        int viewed = method.Parameters.Count(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable);
        // A Mut<T> field stamps the rows it writes: through the run's change-tick column (v{i}_{j}), or the entity.
        string View(ParamModel p, int at) => $"new {p.Type} {{ {string.Join(", ", p.Fields.Select((f, j) => (f.Access, runs) switch
        {
            ("mut", true) => $"{f.Name} = new global::EasyEcs.Mut<{f.Type}>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++viewed}, i), in v{at}_{j}, (int)i)",
            ("mut", false) => $"{f.Name} = new global::EasyEcs.Mut<{f.Type}>(ref c{++viewed}, entity)",
            (_, true) => $"{f.Name} = ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++viewed}, i)",
            _ => $"{f.Name} = ref c{++viewed}",
        }))} }}";
        var args = string.Join(", ", method.Parameters.Select((p, i) => p.Kind switch
        {
            Symbols.ParamKind.Component when runs => $"{p.RefKind} global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++index}, i)",
            Symbols.ParamKind.Entity when runs => "entities.At(i)", // i < rows, the run's length: no bounds check
            Symbols.ParamKind.Optional when runs => $"m{i}[(int)i]",
            Symbols.ParamKind.Mutable when runs => $"new global::EasyEcs.Mut<{p.Type}>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r{++index}, i), in u{i}, (int)i)",
            Symbols.ParamKind.Component => $"{p.RefKind} c{++index}",
            Symbols.ParamKind.Mutable => $"new global::EasyEcs.Mut<{p.Type}>(ref c{++index}, entity)",
            Symbols.ParamKind.ComponentSpan => $"c{++index}", // Span<T> converts to ReadOnlySpan<T> for read-only parameters
            Symbols.ParamKind.Singleton => method.Parallel ? $"{p.RefKind} s{i}.Value" : $"{p.RefKind} s{i}",
            Symbols.ParamKind.Entity => "entity",
            Symbols.ParamKind.Entities => "entities",
            Symbols.ParamKind.Optional => $"global::EasyEcs.Maybe<{p.Type}>.{(p.RefKind == "ref" ? "ForWrite" : "ForRead")}(entity)",
            Symbols.ParamKind.Reduction => $"ref {(local[i] ? "l" : "a")}{i}", // a run's local: registers (Reduce<T> holds no ref)
            // This thread's box, resolved once per chunk (per pass on one thread): the job's copy keeps it.
            // Keyed by the method's own job, so its twins (Rows, Chases) find the same box: the gate never changes what the method sees.
            Symbols.ParamKind.Scratch => $"new global::EasyEcs.Scratch<{p.Type}>(ref (t{i} ??= global::EasyEcs.Scratch<{p.Type}>.Of<{method.Name}Job{number}{method.TypeParameters}>()).Value)",
            Symbols.ParamKind.Lookup => $"new global::EasyEcs.Lookup<{p.Type}>(World, {(p.Key.Length > 0 ? $"k{i}" : "null")})",
            Symbols.ParamKind.Tile => $"new global::EasyEcs.Tile<{p.Cell}, {p.Grid}>({(runs ? "entities[(int)i]" : "entity")}, in n{method.GridOf(i)})",
            Symbols.ParamKind.Emit => $"new global::EasyEcs.Emit<{p.Type}>(ref this.e{i})", // this: a vector loop's column refs are e1, e2...
            Symbols.ParamKind.View => View(p, i),
            Symbols.ParamKind.Context when local[i] => $"ref l{i}",
            Symbols.ParamKind.Context => $"{(p.RefKind.Length == 0 ? "" : p.RefKind + " ")}x{i}",
            _ => "World",
        }).Prepend(owner.AccessorArgument));

        // The JIT specializes World.ForEach on this struct and inlines Execute and the user's method into the loop (only
        // Execute for a method that runs out of line: ForEachModel.OutOfLine).
        string iface = "I" + method.Loop;
        // A batch job has no structural path at all; ForEachBatchParallel asks for the marker all the same, as every parallel loop.
        bool stable = method.Stable && (!method.IsBatch || method.Parallel);
        string marker = stable ? ", global::EasyEcs.INoStructuralChanges" : "";
        // A ref struct holds each singleton as a ref field, a register in the row loop: a SingletonRef's box would be
        // null-checked on every row. Parallel jobs are copied into work items on the heap, so they hold SingletonRefs.
        string kind = method.Parallel ? "struct" : "ref struct";
        sb.Append($"    internal {kind} {job} : global::EasyEcs.{iface}<{method.ComponentTypes}>{marker}{method.Constraints}\n    {{\n");
        // Only when the method takes the World: an unused field would still be copied into the row loop, where it
        // holds a callee-saved register a job that calls out (a structural change) needs for its loop state.
        if (method.NeedsWorld) sb.Append("        public global::EasyEcs.World World;\n");
        for (int i = 0; i < method.Parameters.Length; i++)
            if (JobField(method, i, "") is { } field)
                foreach (var line in field.Declaration.Split('\n')) sb.Append("        ").Append(line).Append('\n');
        if (method.Simd is { StructuralChanges.Length: > 0 } marking)
        {
            sb.Append("        public global::EasyEcs.MarkedRows d0; // the rows the change is applied to after the pass\n");
            if (marking.StructuralChanges[0].StartsWith("Set ", System.StringComparison.Ordinal)) sb.Append($"        public {marking.StructuralChanges[0].Substring(4)} v0; // the value it sets\n");
        }
        if (sb[sb.Length - 2] != '{') sb.Append('\n'); // a blank line after the fields, if any
        if (method.Simd is { VectorTail: true } every)
            sb.Append($"        internal static bool Vectorizes => global::System.Numerics.Vector.IsHardwareAccelerated{(every.Interleaves ? " && global::EasyEcs.Interleaved.IsAccelerated" : "")};\n\n");
        // A wide component's permute tables and layout check (Kernel.Wide.cs): statics, built once per job type.
        if (method.Simd is { Statics.Length: > 0 } statics)
        {
            foreach (var line in statics.Statics.Split('\n')) sb.Append(line.Length > 0 ? "        " + line : "").Append('\n');
        }
        string call = $"__ForEach{owner.TypeParameters}.{method.Name}_{number}{method.TypeParameters}({args});";
        string first = method.IsBatch ? "global::EasyEcs.EntitySpan entities, " : method.TakesEntity ? "global::EasyEcs.Entity entity, " : "";
        string each = method.IsBatch ? "global::System.Span<{0}> c{1}" : "ref {0} c{1}";
        // A grid's neighbors, found once per entity for every Tile of the grid the method takes.
        string grids = string.Concat(Enumerable.Range(0, method.Parameters.Length).Where(method.FirstOfGrid).Select(i =>
            $"global::EasyEcs.TileNeighbors.Of<{method.Parameters[i].Grid}, {method.Parameters[i].Key}>(k{i}, {(runs ? "entities[(int)i]" : "entity")}, out n{i}); "));
        if (grids.Length > 0) call = runs ? $"{{ {grids}{call} }}" : grids + call;
        // A vector loop reading other entities sends the vectors it cannot resolve, and those its backoff skips, to the
        // method (Kernel.RunRows): one call of a row loop of its own (__Rows) per span, so the vector loop's code holds no
        // row loop of its own for them (D131, the form measured). Locals of the run (a reduction, a context, a Maybe's or
        // a Mut's column, a grid's neighbors) keep the rows in place.
        string? rows = method.Simd is { Gathers: true } && runs && !local.Contains(true) && grids.Length == 0
            && method.Parameters.All(p => p.Kind is not (Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable or Symbols.ParamKind.View))
            ? $"__Rows(entities, {{0}}, {List(components.Length, k => $"ref r{k}")}, i, __end);" : null;
        // A vector loop also takes the words of 64 rows of which some pass (IForEachMasked): their passing rows as a
        // lane mask, the other lanes stored back as loaded.
        // A parallel one runs as a batch job over each chunk's runs of rows (ForEachBatchParallel): no masked form.
        var loops = new StringBuilder(); // the loops Execute calls when it is split (EmitLoops)
        for (int pass = 0; pass < (method.Simd != null && !method.Parallel ? 2 : 1); pass++)
        {
        bool masked = pass == 1;
        if (masked) sb.Append('\n');
        sb.Append("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
        sb.Append($"        public void Execute({first}{(masked ? "ulong pass, " : "")}{List(components.Length, i => string.Format(each, components[i - 1], i))})\n");
        if (!runs && !local.Contains(true) && grids.Length == 0 && !method.ConsumesEach)
            sb.Append($"            => {call}\n");
        else
        {
            sb.Append("        {\n");
            int start = sb.Length;
            const string indent = "            ";
            // The run's locals, which a loop moved out of Execute takes by reference (EmitLoops).
            var scope = new List<Scoped>();
            for (int i = 0; i < method.Parameters.Length; i++)
                if (local[i])
                {
                    sb.Append($"            var l{i} = {Field(i)};\n");
                    var p = method.Parameters[i];
                    scope.Add(new($"l{i}", $"ref {(p.Kind == Symbols.ParamKind.Reduction ? $"global::EasyEcs.Reduce<{p.Type}>" : p.Type)} l{i}", $"ref l{i}"));
                }
            for (int i = 0; i < method.Parameters.Length; i++)
                if (runs && method.Parameters[i] is { Kind: Symbols.ParamKind.Optional } p)
                {
                    sb.Append($"{indent}var m{i} = new global::EasyEcs.MaybeColumn<{p.Type}>(in entities, {(p.RefKind == "ref" && p.Tracked ? "true" : "false")});\n");
                    scope.Add(new($"m{i}", $"in global::EasyEcs.MaybeColumn<{p.Type}> m{i}", $"in m{i}"));
                }
            // A Mut's change-tick column, looked up once per run: a write stamps its row with no lookup.
            for (int i = 0; i < method.Parameters.Length; i++)
                if (runs && method.Parameters[i] is { Kind: Symbols.ParamKind.Mutable } p)
                {
                    sb.Append($"{indent}var u{i} = global::EasyEcs.MutColumn.Of<{p.Type}>(in entities);\n");
                    scope.Add(new($"u{i}", $"in global::EasyEcs.MutColumn u{i}", $"in u{i}"));
                }
            for (int i = 0; i < method.Parameters.Length; i++)
                if (runs && method.Parameters[i] is { Kind: Symbols.ParamKind.View } view)
                    for (int j = 0; j < view.Fields.Length; j++)
                        if (view.Fields[j].Access == "mut")
                        {
                            sb.Append($"{indent}var v{i}_{j} = global::EasyEcs.MutColumn.Of<{view.Fields[j].Type}>(in entities);\n");
                            scope.Add(new($"v{i}_{j}", $"in global::EasyEcs.MutColumn v{i}_{j}", $"in v{i}_{j}"));
                        }
            if (runs)
            {
                // The columns' refs and the row count in locals: the spans are passed in memory, and a caller's value
                // passed by ref is a store the JIT cannot prove misses them, so it would reload them on every row.
                for (int k = 1; k <= components.Length; k++)
                    sb.Append($"{indent}ref {components[k - 1]} r{k} = ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(c{k});\n");
                sb.Append($"{indent}nint rows = {(components.Length > 0 ? "c1.Length" : "entities.Length")};\n");
                // Narrow lanes have no lane masks of their own (a Vector512<byte> is a whole word): the passing rows of a
                // masked word run the method.
                // So do a wide component's (its write-back stores whole vectors of rows).
                if (method.Simd is { Size: < 4 } or { Wide: not null } && masked) sb.Append($"{indent}nint i = 0;\n");
                else if (method.Simd is { } simd) EmitLoops(sb, loops, simd, components, indent, call, masked, rows, scope, start);
                if (method.Simd is { VectorTail: true }) { } // the tail took the last rows
                else if (method.IsChaseTwin) EmitChases(sb, method, indent, call);
                else sb.Append(method.Simd == null
                    ? $"{indent}for (nint i = 0; i < rows; i++) // nint: no sign extension per row\n{indent}    {call}\n"
                    : masked
                        ? $"{indent}for (; i < rows; i++) // the rows after the last whole vector that pass: the method itself\n{indent}    if (((pass >> (int)i) & 1) != 0) {call}\n"
                        : $"{indent}for (; i < rows; i++) // the rows after the last whole vector: the method itself\n{indent}    {call}\n");
            }
            else sb.Append($"{indent}{call}\n");
            // [Consume] of a method that may change structure: the entity goes after its row, a hole the pass closes.
            if (method.ConsumesEach) sb.Append($"{indent}entity.Destroy();\n");
            for (int i = 0; i < method.Parameters.Length; i++)
                if (local[i]) sb.Append($"            {Field(i)} = l{i};\n");
            sb.Append("        }\n");
        }
        }
        sb.Append(loops);
        if (rows != null)
        {
            // The rows [i, end) of a run that pass, one by one (Kernel.RunRows), out of the vector loop.
            sb.Append("\n        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n");
            sb.Append($"        private void __Rows(global::EasyEcs.EntitySpan entities, ulong pass, {List(components.Length, k => $"ref {components[k - 1]} r{k}")}, nint i, nint end)\n");
            sb.Append($"        {{\n            for (; i < end; i++)\n                if (((pass >> (int)i) & 1) != 0) {call}\n        }}\n");
        }
        // Its ref parameters (Span<T> for a batch, Mut<T>): carried over when the method moves the entity it visits,
        // and stamped when the component tracks changes: by the loop, every row it visits, except a Mut's, which
        // stamps the rows it writes. The queried [With<T>] component of a method without one is read.
        var queried = method.Parameters.Where(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable).ToArray();
        int writes = 0, own = 0;
        for (int k = 0; k < components.Length; k++)
        {
            if (queried.Any(p => p.Type == components[k] && p.RefKind == "ref")
                || method.Parameters.Any(p => p.Kind == Symbols.ParamKind.View && p.Fields.Any(f => f.Type == components[k] && f.Access != "in")))
                writes |= 1 << k;
            if (queried.Any(p => p.Type == components[k] && p.Kind == Symbols.ParamKind.Mutable)
                || method.Parameters.Any(p => p.Kind == Symbols.ParamKind.View && p.Fields.Any(f => f.Type == components[k] && f.Access == "mut")))
                own |= 1 << k;
        }
        string declaring = method.Simd != null ? "IForEachBatch" : iface; // IForEachMasked declares no masks of its own
        sb.Append($"\n        static int global::EasyEcs.{declaring}<{method.ComponentTypes}>.WriteMask => {writes};\n");
        sb.Append($"\n        static bool global::EasyEcs.{declaring}<{method.ComponentTypes}>.OneQuery => true; // the method's filter is a constant\n");
        if (own != 0) sb.Append($"        static int global::EasyEcs.{declaring}<{method.ComponentTypes}>.SelfStamped => {own};\n");
        // Tracked components written through a Maybe: the parallel pass opens their blocks as it opens its own columns'.
        var optional = method.Parallel
            ? method.Parameters.Where(p => p is { Kind: Symbols.ParamKind.Optional, RefKind: "ref", Tracked: true }).Select(p => p.Type).Distinct().ToArray()
            : [];
        if (optional.Length > 0)
        {
            sb.Append($"        private static readonly global::EasyEcs.ComponentSet __optionalWrites = global::EasyEcs.ComponentSet.Create(){string.Concat(optional.Select(t => $".With<{t}>()"))};\n");
            sb.Append($"        static global::EasyEcs.ComponentSet global::EasyEcs.{declaring}<{method.ComponentTypes}>.OptionalWrites => __optionalWrites;\n");
        }
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
            MergeLines(sb, method, "into", "part");
            sb.Append("        }\n");
        }

        sb.Append("    }\n\n");

        if (suffix.Length > 0) return; // the filter is the vector job's
        EmitFilter(sb, method, number);
    }

    /// <summary>The method's filter, a field of the file's class (<c>{Name}Filter{number}</c>).</summary>
    private static void EmitFilter(StringBuilder sb, ForEachModel method, int number)
    {
        if (!method.HasFilter)
        {
            // A pass without a filter takes this empty one by reference: the optional default argument would be a
            // 192-byte temporary the caller zeroes on every call (twice with the count of MeasureSystems).
            if (!method.One) sb.Append($"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Filter{number} = default;\n\n");
            return;
        }

        // Attribute type arguments cannot name type parameters (CS8968): the filter is one field, even for a generic method.
        sb.Append($"    internal static readonly global::EasyEcs.QueryFilter {method.Name}Filter{number} = {FilterChain(method)};\n\n");
    }

    /// <summary>
    /// The job of a [ForEachRecord] method (an <c>IForEachRecord&lt;T&gt;</c> ref struct, run by
    /// <c>Emitted&lt;T&gt;.Consume</c>): its singletons resolved once per pass as ref fields, its lookups and emits as a
    /// [ForEach] job's on one thread, the method called on each record.
    /// </summary>
    private static void EmitRecordJob(StringBuilder sb, OwnerModel owner, ForEachModel method, int number)
    {
        string job = $"{method.Name}Job{number}{method.TypeParameters}";
        sb.Append($"    internal ref struct {job} : global::EasyEcs.IForEachRecord<{method.Record}>\n    {{\n");
        if (method.NeedsWorld) sb.Append("        public global::EasyEcs.World World;\n");
        for (int i = 0; i < method.Parameters.Length; i++)
            if (JobField(method, i, "") is { } field)
                foreach (var line in field.Declaration.Split('\n')) sb.Append("        ").Append(line).Append('\n');
        if (sb[sb.Length - 2] != '{') sb.Append('\n');
        var args = string.Join(", ", method.Parameters.Select((p, i) => p.Kind switch
        {
            Symbols.ParamKind.Record => "in record",
            Symbols.ParamKind.Singleton => $"{p.RefKind} s{i}",
            Symbols.ParamKind.Lookup => $"new global::EasyEcs.Lookup<{p.Type}>(World, {(p.Key.Length > 0 ? $"k{i}" : "null")})",
            Symbols.ParamKind.Emit => $"new global::EasyEcs.Emit<{p.Type}>(ref this.e{i})",
            Symbols.ParamKind.Context => $"{(p.RefKind.Length == 0 ? "" : p.RefKind + " ")}x{i}",
            _ => "World",
        }).Prepend(owner.AccessorArgument));
        sb.Append("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
        sb.Append($"        public void Execute(in {method.Record} record) => __ForEach{owner.TypeParameters}.{method.Name}_{number}({args});\n");
        sb.Append("    }\n\n");
    }

    /// <summary>The filter of <paramref name="method"/> as the expression that builds it.</summary>
    private static string FilterChain(ForEachModel method) => "global::EasyEcs.QueryFilter.Create()" + string.Concat(
        method.With.Select(t => $".With<{t}>()").Concat(method.Without.Select(t => $".Without<{t}>()"))
            .Concat(AnyCalls("WithAny", method.WithAny, "()")).Concat(method.Changed.Select(t => $".Changed<{t}>(0)")).Concat(method.Removed.Select(t => $".Removed<{t}>(0)"))
            .Concat(AnyCalls("ChangedAny", method.ChangedAny, "(0)")).Concat(AnyCalls("RemovedAny", method.RemovedAny, "(0)")));

    // A token of VectorLoops' text (U+0001 occurs in no C# token outside a literal): Interleaved.IsAccelerated /
    // IsAccelerated512.
    private const string Accelerated = "\u0001Accelerated";

    private const string Vector = "global::System.Numerics.Vector", Vector512 = "global::System.Runtime.Intrinsics.Vector512";

    /// <summary>
    /// A WithAny / ChangedAny / RemovedAny set as calls of at most 9 components each (calls join one set): a set of one
    /// (several attributes naming the same type) names it twice.
    /// </summary>
    private static IEnumerable<string> AnyCalls(string kind, EquatableArray<string> types, string arguments)
    {
        const int max = 9;
        for (int at = 0; at < types.Length; at += max)
        {
            var call = types.Skip(at).Take(max).ToList();
            if (call.Count == 1) call.Add(call[0]);
            yield return $".{kind}<{string.Join(", ", call)}>{arguments}";
        }
    }

    // ---------------- models (equatable, symbol-free) ----------------

    /// <param name="FullName">The type as C# names it (<c>global::Ns.Outer.Inner</c>).</param>
    /// <param name="MetadataName">The type as the runtime names it (<c>Ns.Outer+Inner</c>), for UnsafeAccessorType.</param>
    /// <param name="IsVisible">Generated code can name the type (it and its containing types are internal or public).</param>
    /// <param name="IsSystem">An [EcsSystem] ref struct the generator writes code for (<see cref="Symbols.GeneratesSystem"/>).</param>
    /// <param name="AccessorType">MetadataName closed over the generated class's type parameters (<c>Ns.Regen`1[[!0]]</c>).</param>
    /// <param name="TypeParameters">The type parameters of the type and its containing types (<c>&lt;T&gt;</c>), or "".</param>
    /// <param name="Definition">The generic definition as <c>typeof</c> names it (<c>global::Ns.Regen&lt;&gt;</c>), or "".</param>
    private sealed record OwnerModel(string Namespace, string FullName, string MetadataName, string FlatName, string HintName,
        bool IsVisible, bool IsPublic, bool IsSystem, string AccessorType, string TypeParameters,
        string Constraints, string Definition, int Arity)
    {
        /// <summary>
        /// The owner parameter of an UnsafeAccessor of the type's static methods: a system by its type (UnsafeAccessorType
        /// takes no value type), any other type by name (private types too).
        /// </summary>
        public string AccessorParameter => IsSystem ? FullName : $"[global::System.Runtime.CompilerServices.UnsafeAccessorType(\"{AccessorType}\")] object";

        /// <summary>The value an accessor call passes for the owner (ignored by a static method).</summary>
        public string AccessorArgument => IsSystem ? "default" : "null";

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
            bool isPublic = true;
            for (var t = type; t != null; t = t.ContainingType) isPublic &= t.DeclaredAccessibility == Accessibility.Public;
            return new OwnerModel(ns, type.ToDisplayString(Symbols.Fqn), metadata, Symbols.FlatName(type), Symbols.HintName(type),
                Symbols.IsVisible(type), isPublic, Symbols.GeneratesSystem(type), accessor,
                Symbols.TypeParameterList(typeParameters), Symbols.Constraints(typeParameters), definition, typeParameters.Count);
        }
    }

    /// <param name="Type">The component, singleton or value type (a span's or Maybe's element).</param>
    /// <param name="RefKind">"ref" or "in" ("" for a context parameter passed by value).</param>
    /// <param name="Declared">The parameter as declared, with its modifier (<c>in global::Health</c>), for the accessor.</param>
    /// <param name="Tracked">A Maybe's or Mut's element tracks changes (a write through it is stamped).</param>
    /// <param name="Key">A Lookup's index key type when its index is built before the pass (<see cref="Symbols.LookupKey"/>), else "".</param>
    /// <param name="Cell">A Tile's cell type, else "".</param>
    /// <param name="Grid">A Tile's tile component (its key is <paramref name="Key"/>), else "".</param>
    /// <param name="Fields">A view's component fields, in declaration order (empty for any other parameter).</param>
    /// <param name="Accumulates">A Reduce&lt;T&gt; of a singleton declared [Accumulate]: the pass's result is combined into the world's value.</param>
    private sealed record ParamModel(Symbols.ParamKind Kind, string Type, string RefKind, string Name, string Declared, bool Tracked = false, string Key = "",
        string Cell = "", string Grid = "", EquatableArray<ViewFieldModel> Fields = default, bool Accumulates = false);

    /// <summary>A view's component field: its name, its component, and "ref" (written) or "in" (read).</summary>
    /// <summary>A view's field: its name, component, and how the job sets it: "ref", "in" (ref readonly) or "mut" (a Mut&lt;T&gt;).</summary>
    private sealed record ViewFieldModel(string Name, string Type, string Access);

    /// <summary>A view parameter (of a [ForEach] method or a lambda pass): its type and its fields as the job sets them.</summary>
    private static ParamModel ViewParameter(IParameterSymbol p, string declared) =>
        new(Symbols.ParamKind.View, p.Type.ToDisplayString(Symbols.Fqn), "", p.Name, declared,
            Fields: new EquatableArray<ViewFieldModel>(Views.Fields((INamedTypeSymbol)p.Type)
                .Select(f => new ViewFieldModel(Escape(f.Symbol.Name), f.Component.ToDisplayString(Symbols.Fqn), f.Mut ? "mut" : f.Writes ? "ref" : "in")).ToArray()));

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

        /// <summary>[ForEntity]: the job runs on one entity (entity.Run), from an entry point that takes it.</summary>
        public bool One { get; init; }

        /// <summary>[Consume]: the entities the pass visits are destroyed.</summary>
        public bool Consume { get; init; }

        /// <summary>[ForEachRecord]: the record type it handles (its first parameter's), else "".</summary>
        public string Record { get; init; } = "";

        /// <summary>
        /// [Consume] after the pass, with one DestroyAll of its filter: a method that makes no structural change (or a
        /// batch method, which cannot) leaves the set of matching rows as it found it, so those rows are the ones it visited.
        /// </summary>
        public bool ConsumesInBulk => Consume && (Stable || IsBatch);

        /// <summary>[Consume] of a method that may change structure: each entity is destroyed after its row.</summary>
        public bool ConsumesEach => Consume && !ConsumesInBulk;

        /// <summary>The filter argument of a sliced method: its filter (or none) cut to slice <paramref name="slice"/>.</summary>
        public string Sliced(string filter, string slice) =>
            $", {filter}.Slice({slice}, {Slices})";

        /// <summary>The queried component types, as type arguments of World.ForEach / IForEach.</summary>
        public string ComponentTypes => string.Join(", ", Query);

        /// <summary>Runs per entity with its handle: it takes the Entity, a Maybe looked up through it, a Mut that stamps through it, or a Tile of it.</summary>
        public bool TakesEntity => One || ConsumesEach || Parameters.Any(p => p.Kind is Symbols.ParamKind.Entity or Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable or Symbols.ParamKind.Tile)
            || Stamps;

        /// <summary>A view parameter with a Mut&lt;T&gt; field: it stamps the rows it writes, as a Mut parameter does.</summary>
        private bool Stamps => Parameters.Any(p => p.Kind == Symbols.ParamKind.View && p.Fields.Any(f => f.Access == "mut"));

        public bool TakesWorld => Parameters.Any(p => p.Kind == Symbols.ParamKind.World);

        /// <summary>The first Tile parameter of its grid (whose neighbors and index its fields hold): parameter <paramref name="i"/> is one.</summary>
        public bool FirstOfGrid(int i) => Parameters[i].Kind == Symbols.ParamKind.Tile && GridOf(i) == i;

        /// <summary>The first Tile parameter of the grid of Tile parameter <paramref name="i"/>.</summary>
        public int GridOf(int i)
        {
            for (int j = 0; j < i; j++)
                if (Parameters[j].Kind == Symbols.ParamKind.Tile && Parameters[j].Grid == Parameters[i].Grid) return j;
            return i;
        }

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
        /// A pair method ([Neighbor] parameters): its job runs per pair of rows within the cutoff its body tests
        /// (SystemGenerator.Pairs.cs), with its own vector loop; null for any other method (and for a system's method
        /// until its implementation stage puts it in: <see cref="LoopModel"/>).
        /// </summary>
        public PairModel? Pair { get; init; }

        /// <summary>
        /// Its reads of other entities at ids its rows hold (<see cref="Generator.Chases"/>). Without a
        /// vector loop, a pass past the last-level cache runs its twin job (<see cref="IsChaseTwin"/>), which walks
        /// runs of rows and prefetches them <see cref="ChaseDistance"/> rows ahead; any other pass runs today's job.
        /// </summary>
        public EquatableArray<Chase> Chases { get; init; } = new([]);

        /// <summary>
        /// Has a twin job prefetching its chases: no vector loop and some chase (<see cref="Chases"/>). In parallel the twin
        /// is a batch job over each chunk's runs of rows (ForEachBatchParallel, the same chunks as ForEachParallel's), so
        /// only a method a parallel batch job can run (no Maybe, Mut or view Mut, as <see cref="Runs"/>).
        /// </summary>
        public bool HasChaseTwin => Simd == null && !One && Chases.Length > 0 && (!Parallel || ParallelRuns);

        /// <summary>A parallel job can run it over runs of rows: it takes no Maybe, Mut or view Mut (stamped through the entity).</summary>
        private bool ParallelRuns => !Parameters.Any(p => p.Kind is Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable) && !Stamps;

        /// <summary>
        /// This is the twin (<c>Job{i}Chases</c>): a job over runs of rows prefetching the chases, run only by a pass
        /// whose call site found it past the last-level cache (<c>Lookup&lt;T&gt;.Prefetches</c>, once per pass).
        /// </summary>
        public bool IsChaseTwin { get; init; }

        /// <summary>
        /// How many rows ahead of the row it runs the row form prefetches a chase's row (its record twice as far: the
        /// record must be in cache before the row's address is known): 8, the best distance of the screen
        /// (LookupChaseDistanceBenchmark at 1M and 16M entities).
        /// </summary>
        public const int ChaseDistance = 8;

        /// <summary>
        /// The vector loop prefetches for Lookup parameter <paramref name="i"/> (<see cref="Kernel.Code.Prefetched"/>): the
        /// job's field <c>f{i}</c> says, once per pass, whether the pass is past the last-level cache
        /// (<c>Lookup&lt;T&gt;.Prefetches</c>).
        /// </summary>
        public bool Prefetches(int i) => Simd is { } simd && simd.Prefetched.Contains(i);

        /// <summary>
        /// One line on the loop the method got (SystemInfo.Loops): "vector" with its forms (flat, interleaved fields,
        /// masked words, parallel chunks, a cold branch), or "scalar" and why.
        /// </summary>
        public string Describe()
        {
            if (Pair != null) return Pair.Describe(Name);
            if (Record.Length > 0) return $"{Name}: the records of Emitted<{Record.Replace("global::", "")}>, in order, then cleared";
            if (Simd is not { } simd) return $"{Name}: scalar ({(Scalar.Length > 0 ? Scalar : "not a vector kernel")}){(HasChaseTwin ? ", lookups prefetched past the last-level cache" : "")}";
            var parts = new List<string> { $"vector (Vector512 or Vector<{simd.Lane}>)" };
            if (simd.Flat != null) parts.Add("flat");
            else if (simd.Interleaves) parts.Add("interleaved fields");
            if (simd.Wide != null)
            {
                parts[0] = simd.Wide512 && simd.Wide128 ? $"vector (Vector512 or Vector<{simd.Lane}> of 128 bits)" : simd.Wide512 ? "vector (Vector512 only)" : $"vector (Vector<{simd.Lane}> of 128 bits only)";
                parts.Add(simd.Wide512 && simd.Wide128 ? "wide component (permutes, table lookups)" : simd.Wide512 ? "wide component (permutes)" : "wide component (table lookups)");
            }
            if (simd.Gathers) parts.Add("gathers (Lookup<T>)");
            if (simd.Prefetched.Length > 0) parts.Add("lookups prefetched past the last-level cache");
            if (Parallel) parts.Add("in parallel chunks");
            else parts.Add(simd.Size < 4 || simd.Wide != null ? "masked words scalar" : "masked words vector");
            if (simd.Cold != null) parts.Add($"cold branch ({simd.Cold})");
            foreach (var write in simd.StructuralChanges) parts.Add($"{write.Replace("global::", "")} after the pass, on the rows it marks");
            if (simd.VectorTail) parts.Add("last rows through a copy");
            return $"{Name}: {string.Join(", ", parts)}";
        }

        /// <summary>
        /// Takes a Scratch and its body loops or calls: the row loop calls it instead of inlining it. Profile-guided
        /// inlining takes such a method into the row loop (a Scratch argument makes it look cheap to inline), where its
        /// own loop shares the callee-saved registers with the row loop's refs and the scratch box, runs out of them and
        /// keeps its counters on the stack: 30-40% slower than the same method behind a call. A method without a loop or
        /// a call is too small to spill and pays more for a call than it saves (a counter: 10-50%), so it stays inlined.
        /// </summary>
        public bool OutOfLine { get; init; }

        /// <summary>What pass fusion knows of a system's method (<see cref="Generator.Fusion"/>); null outside a system.</summary>
        public FusionSide? Fusion { get; init; }

        /// <summary>
        /// Changes no structure (on one thread) and takes a Maybe, a Mut, a Reduce or a caller's value by ref: its job walks
        /// runs of rows, finding each optional column once per run (a presence bit per row), stamping a Mut's writes by row
        /// (no entity lookup) and keeping partial results and the caller's values in locals for the run. In parallel, a
        /// method that reduces (and takes no Maybe or Mut) runs the same way, a chunk's runs at a time
        /// (ForEachBatchParallel): its partial results stay in registers instead of a load and a store per row.
        /// </summary>
        public bool Runs => !One && Stable && !Parameters.Any(p => p.Kind == Symbols.ParamKind.ComponentSpan) && (Parallel
            ? (Parameters.Any(p => p.Kind == Symbols.ParamKind.Reduction) || IsChaseTwin) && ParallelRuns
            : Parameters.Any(p => p.Kind is Symbols.ParamKind.Optional or Symbols.ParamKind.Mutable or Symbols.ParamKind.Reduction || p is { Kind: Symbols.ParamKind.Context, RefKind: "ref" }) || Stamps
                || IsChaseTwin); // the rows ahead of the row it runs: their chases prefetched

        public static ForEachModel? Create(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
        {
            if (!Symbols.IsValidForEach(method)) return null;
            ct.ThrowIfCancellationRequested();

            // Spans are modeled as their element type: Span<T> writes T, ReadOnlySpan<T> reads it.
            bool outside = Symbols.IsRunner(method);
            string record = Symbols.IsForEachRecord(method) ? Symbols.RecordType(method)!.ToDisplayString(Symbols.Fqn) : "";
            var parameters = method.Parameters.Select((p, at) =>
            {
                string declared = (p.RefKind switch { RefKind.Ref => "ref ", RefKind.In => "in ", RefKind.RefReadOnlyParameter => "ref readonly ", _ => "" })
                    + p.Type.ToDisplayString(Symbols.Fqn);
                if (at == 0 && record.Length > 0) return new ParamModel(Symbols.ParamKind.Record, record, "in", p.Name, declared);
                return Symbols.Classify(p, outside) switch
                {
                    Symbols.ParamKind.ComponentSpan => new ParamModel(Symbols.ParamKind.ComponentSpan,
                        Symbols.SpanElement(p.Type)!.ToDisplayString(Symbols.Fqn),
                        Symbols.Named(((INamedTypeSymbol)p.Type).ConstructedFrom, "System.Span<T>") ? "ref" : "in", p.Name, declared),
                    Symbols.ParamKind.Optional => new ParamModel(Symbols.ParamKind.Optional, // Maybe<T> writes, ReadOnlyMaybe<T> reads (EECS025 says when a Maybe only reads)
                        Symbols.MaybeElement(p.Type)!.ToDisplayString(Symbols.Fqn), Symbols.IsReadOnlyMaybe(p.Type) ? "in" : "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MaybeElement(p.Type)!, Symbols.TrackChanges)),
                    Symbols.ParamKind.Mutable => new ParamModel(Symbols.ParamKind.Mutable, // a write: the scheduler sees its writer
                        Symbols.MutElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MutElement(p.Type)!, Symbols.TrackChanges)),
                    Symbols.ParamKind.Reduction => new ParamModel(Symbols.ParamKind.Reduction, // a write of the singleton, once per pass
                        Symbols.ReduceElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared, Accumulates: Symbols.Accumulates(Symbols.ReduceElement(p.Type)!)),
                    Symbols.ParamKind.Scratch => new ParamModel(Symbols.ParamKind.Scratch, Symbols.ScratchElement(p.Type)!.ToDisplayString(Symbols.Fqn), "", p.Name, declared),
                    Symbols.ParamKind.Emit => new ParamModel(Symbols.ParamKind.Emit, // a write of Emitted<T>, once per pass
                        Symbols.EmitElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared),
                    Symbols.ParamKind.Lookup => new ParamModel(Symbols.ParamKind.Lookup, Symbols.LookupElement(p.Type)!.ToDisplayString(Symbols.Fqn), "in", p.Name, declared,
                        Key: Symbols.LookupKey(p, Symbols.WithBody(method, syntax, model).Item1 is { } declaration ? (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody : null)?.ToDisplayString(Symbols.Fqn) ?? ""),
                    Symbols.ParamKind.Tile => TileModel(p, declared),
                    Symbols.ParamKind.View => ViewParameter(p, declared),
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

            if (record.Length == 0) foreach (var (kind, type, _) in Symbols.AttributeFilters(method)) Add(kind, type);
            // A Tile reads the entity's cells and tile component: the rows must have both.
            foreach (var p in method.Parameters)
                if (Symbols.TileElements(p.Type) is { } tile && Symbols.Classify(p) == Symbols.ParamKind.Tile)
                {
                    Add(Symbols.FilterKind.With, tile.Grid);
                    if (model.Compilation.GetTypeByMetadataName("EasyEcs.DynamicBuffer`1") is { } buffer) Add(Symbols.FilterKind.With, buffer.Construct(tile.Cell));
                }

            var query = parameters.Where(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable)
                .Select(p => p.Type).ToList();
            // A view's components follow the component parameters' (the columns of its vector loop's desugared method, too).
            query.AddRange(parameters.Where(p => p.Kind == Symbols.ParamKind.View).SelectMany(p => p.Fields.Select(f => f.Type)));
            // No component parameter: the first [With<T>] (else [Changed<T>]) becomes the queried component (passed to the
            // job, not the method).
            if (query.Count == 0 && record.Length == 0)
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
            if (record.Length == 0) foreach (var (kind, type, _, _) in Symbols.GroupFilters(method)) Add(kind, type);
            if (query.Count > 0) with.RemoveAll(query.Contains); // a group's [With<T>] (or a Tile's) the method already queries

            bool stable = Symbols.IsStable(method, syntax, model);
            bool parallel = Symbols.IsParallel(method) && Symbols.ParallelProblem(method, syntax, model) == null;
            bool identifies = Symbols.OnlyIdentifies(method, syntax, model);
                        // Declaration order by member index: a text position would change (and re-emit the file) on any edit above it.
            int order = method.ContainingType.GetMembers().IndexOf(method);
            string? scalar = null; // why the method runs scalar; a [ForEntity] method has no loop to explain
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
                    Slices = Symbols.Slices(method), ChunkRows = Symbols.ChunkRows(method), Identifies = identifies,
                    One = Symbols.IsForEntity(method), Consume = Symbols.IsConsume(method), Record = record,
                    // Kernel.Vectorize decides alone (static, stable, its body; in parallel too): the analyzer reports its reasons.
                    // One entity has no run of rows to vectorize; a system's loop is its implementation's (SystemLoops).
                    Simd = Symbols.IsForEntity(method) || !outside || PairForm.IsPairMethod(method) || record.Length > 0 ? null : VectorLoop(method, syntax, model, parallel, ct, out scalar),
                    Scalar = scalar ?? "",
                    Pair = outside && PairForm.IsPairMethod(method) ? PairModel.Create(method, syntax, model, ct) : null,
                    // Reads of other entities at the row's ids, by EntityId (a keyed lookup finds through its index).
                    Chases = Symbols.IsForEntity(method) || record.Length > 0 ? new([]) : Usable(Generator.Chases.Of(method, syntax, model, ct), parameters),
                    OutOfLine = parameters.Any(p => p.Kind == Symbols.ParamKind.Scratch) && LoopsOrCalls(syntax),
                    Fusion = outside || PairForm.IsPairMethod(method) || record.Length > 0 ? null : FusionSide.Of(method, stable, identifies, parallel), // a pair pass walks windows
                };
        }

        /// <summary>
        /// The chases a twin job can prefetch: a probe's index must be the one the job holds (its Lookup is keyed, so the
        /// index is built before the pass).
        /// </summary>
        public static EquatableArray<Chase> Usable(EquatableArray<Chase> chases, ParamModel[] parameters) =>
            new(chases.Where(c => c.Kind != ChaseKind.Probe || parameters[c.Lookup].Key.Length > 0).ToArray());

        /// <summary>
        /// The vector loop of a [ForEach] method (Kernel.Vectorize, its helpers inlined), with the job fields its uniform
        /// values are read from, or null with the reason it runs one row at a time.
        /// </summary>
        public static Kernel.Code? VectorLoop(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, bool parallel,
            System.Threading.CancellationToken ct, out string? scalar) =>
            Kernel.Vectorize(method, syntax, model, parallel ? static (i, kind) => kind switch
            {
                Symbols.ParamKind.Singleton => $"s{i}.Value", // a parallel job's SingletonRef
                Symbols.ParamKind.Reduction => $"l{i}",
                _ => $"x{i}",
            } : static (i, kind) => kind switch
            {
                Symbols.ParamKind.Singleton => $"s{i}",
                Symbols.ParamKind.Reduction => $"l{i}", // the run's partial result, a local of the batch job
                _ => $"x{i}",
            }, ct, out scalar);

        /// <summary>The body has a loop or a call (syntax only: a call's target is not resolved).</summary>
        private static bool LoopsOrCalls(MethodDeclarationSyntax syntax) =>
            ((SyntaxNode?)syntax.Body ?? syntax.ExpressionBody)?.DescendantNodes().Any(static n => n is ForStatementSyntax
                or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                or InvocationExpressionSyntax) == true;
    }


    private static ParamModel TileModel(IParameterSymbol p, string declared)
    {
        var (cell, grid) = Symbols.TileElements(p.Type)!.Value;
        string cellName = cell.ToDisplayString(Symbols.Fqn);
        return new ParamModel(Symbols.ParamKind.Tile, $"global::EasyEcs.DynamicBuffer<{cellName}>", "in", p.Name, declared,
            Key: Symbols.TileKey(grid)!.ToDisplayString(Symbols.Fqn), Cell: cellName, Grid: grid.ToDisplayString(Symbols.Fqn));
    }

    private sealed record UpdateModel(OwnerModel Owner, EquatableArray<ForEachModel> Methods)
    {
        /// <summary>The type's [ForEntity] methods: entry points only (in a file of their own).</summary>
        public bool One { get; init; }

        /// <summary>The system's [UpdateInterval(n, Phase = p)]: n (0 without one) and p.</summary>
        public int Interval { get; init; }

        public int Phase { get; init; }

        /// <summary>The system's hooks.</summary>
        public EquatableArray<HookModel> Hooks { get; init; } = new([]);

        /// <summary>
        /// One model per type with [ForEach] methods, and per system (with or without methods), its methods in
        /// declaration order. A [ForEach] method of an [EcsSystem] the generator writes no code for (EECS074, EECS022)
        /// is left out; <paramref name="entryPoints"/> ([ForEntity] methods) keeps every owner, a system's too, since
        /// its entry points need no system code.
        /// </summary>
        public static IEnumerable<UpdateModel> Group(ImmutableArray<ForEachModel> methods, ImmutableArray<SystemModel> systems, bool entryPoints = false)
        {
            var owners = new List<OwnerModel>();
            var byOwner = new Dictionary<OwnerModel, List<ForEachModel>>();
            var bySystem = new Dictionary<OwnerModel, SystemModel>();
            foreach (var system in systems)
            {
                if (bySystem.ContainsKey(system.Owner)) continue; // a partial type: one attribute (AllowMultiple = false)
                bySystem[system.Owner] = system;
                byOwner[system.Owner] = [];
                owners.Add(system.Owner);
            }

            foreach (var method in methods)
            {
                if (!byOwner.TryGetValue(method.Owner, out var list))
                {
                    if (method.Owner.IsSystem && !entryPoints) continue; // a valid system always has its model
                    byOwner[method.Owner] = list = [];
                    owners.Add(method.Owner);
                }

                list.Add(method);
            }

            foreach (var owner in owners)
            {
                var model = new UpdateModel(owner, new EquatableArray<ForEachModel>(byOwner[owner].OrderBy(m => m.Order).ToArray()));
                yield return bySystem.TryGetValue(owner, out var s) ? model with { Interval = s.Interval, Phase = s.Phase, Hooks = s.Hooks } : model;
            }
        }
    }

    /// <summary>A hook of a system: its kind ("Create", "Update", "Destroy"), its method and its parameters ('W' the World, 'S' the SystemHandle).</summary>
    private sealed record HookModel(string Kind, string Name, string Arguments);

    /// <summary>
    /// A system the generator writes code for: its valid [UpdateInterval(n, Phase = p)] (an invalid one is EECS083's,
    /// and the system runs every update) and its valid hooks (EECS077 otherwise). Declarations only, no body.
    /// </summary>
    private sealed record SystemModel(OwnerModel Owner, int Interval, int Phase, EquatableArray<HookModel> Hooks)
    {
        public static SystemModel? Create(INamedTypeSymbol type)
        {
            if (!Symbols.GeneratesSystem(type)) return null;
            var (interval, phase) = Symbols.ScheduleProblem(type) == null && Symbols.Schedule(type) is var (i, p, _) ? (i, p) : (0, 0);
            var hooks = new List<HookModel>();
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                if (Symbols.Hook(method) is var kind and not Symbols.HookKind.None && Symbols.HookProblem(method) == null)
                    hooks.Add(new HookModel(kind.ToString(), method.Name,
                        new string(method.Parameters.Select(p => Symbols.Named(p.Type, Symbols.World) ? 'W' : 'S').ToArray())));
            return new SystemModel(OwnerModel.From(type), interval, phase, new EquatableArray<HookModel>(hooks.ToArray()));
        }
    }

    /// <summary>
    /// A system of the assembly's set: its type and its code struct as C# names them. Besides the assembly's own
    /// non-generic systems, a closed type of a generic system named in this assembly in the API that takes a system type
    /// (<c>AddSystem&lt;Regen&lt;Health&gt;&gt;()</c>, <c>RemoveSystem</c>, <c>HasSystem</c>, <c>System</c>,
    /// <c>TryGetSystem</c>, <c>[UpdateBefore&lt;...&gt;]</c> / <c>[UpdateAfter&lt;...&gt;]</c>): of this assembly's
    /// systems, or of a referenced assembly's public one through the code its [GenericSystemCode] names.
    /// </summary>
    /// <summary>A pair of systems the set declares as one walk: both by name, and the code that runs it.</summary>
    private sealed record SetFusion(string First, string Second, string Code);

    private sealed record SetEntry(string System, string Code)
    {
        private static readonly HashSet<string> Takers = ["AddSystem", "RemoveSystem", "HasSystem", "System", "TryGetSystem",
            "UpdateBefore", "UpdateAfter", "UpdateBeforeAttribute", "UpdateAfterAttribute"];

        public static bool IsCandidate(SyntaxNode node)
        {
            if (node is not GenericNameSyntax name) return false;
            SyntaxNode type = name;
            while (type.Parent is QualifiedNameSyntax qualified && qualified.Right == type
                   || type.Parent is AliasQualifiedNameSyntax alias && alias.Name == type)
                type = type.Parent;
            return type.Parent is TypeArgumentListSyntax { Parent: GenericNameSyntax taker } && Takers.Contains(taker.Identifier.ValueText);
        }

        public static SetEntry? Closed(GeneratorSyntaxContext ctx)
        {
            if (Symbols.TypeNamed(ctx.SemanticModel, ctx.Node) is not INamedTypeSymbol { IsGenericType: true } type
                || !Symbols.IsValidSystem(type) || !Symbols.IsVisible(type))
                return null;
            var arguments = Symbols.ChainTypeArguments(type);
            if (arguments.Any(a => !Symbols.IsNameable(a) || Symbols.ContainsTypeParameter(a))) return null;
            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".";
            string code;
            if (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, ctx.SemanticModel.Compilation.Assembly))
                code = $"global::{ns}{CodeTypeName(OwnerModel.From(type.OriginalDefinition))}<{string.Join(", ", arguments.Select(a => a.ToDisplayString(Symbols.Fqn)))}>";
            else if (Symbols.GenericSystemCode(type) is { } open) // another assembly's public system, its code closed here
                code = open.Construct(arguments.ToArray()).ToDisplayString(Symbols.Fqn);
            else return null;
            return new SetEntry(type.ToDisplayString(Symbols.Fqn), code);
        }
    }
}
