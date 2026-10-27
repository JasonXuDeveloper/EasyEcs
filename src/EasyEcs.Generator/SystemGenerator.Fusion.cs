using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

// Pass fusion of one system's [ForEach] methods (the rule: Fusion.cs). Consecutive methods that fuse run as one job over
// the union of their components: per row (each method in turn on the row) when every method is a per-row job, else per
// block of a run of rows (each method's own loop, vector loops included, over the block in turn), so each row is
// loaded into cache once instead of once per method.
public sealed partial class SystemGenerator
{
    /// <summary>
    /// The rows of a block of a fused batch walk: each method runs over a block before the next one does. 256 rows of a
    /// few 16-byte columns stay in L1 between the methods; a multiple of 64, so the vector loops of a block start
    /// aligned like the run's and only the run's last block has rows left after the last whole vector.
    /// </summary>
    private const int FusedBlock = 256;

    /// <summary>
    /// The methods of <paramref name="model"/> (a system's) cut into groups of consecutive methods that run as one walk:
    /// for each method, the number of methods of the group it starts (1: alone), 0 inside a group. A method joins the
    /// group before it when <see cref="Fusion.Problem(IReadOnlyList{FusionSide}, FusionSide)"/> finds nothing against
    /// any method of the group.
    /// </summary>
    private static int[] FusionGroups(UpdateModel model)
    {
        var methods = model.Methods;
        var groups = new int[methods.Length];
        var group = new List<FusionSide>();
        for (int i = 0, start = 0; i < methods.Length; i++)
        {
            var side = methods[i].Fusion;
            if (i > 0 && side != null && group.Count > 0 && Fusion.Problem(group, side) == null)
            {
                group.Add(side);
                groups[start]++;
                continue;
            }

            start = i;
            groups[i] = 1;
            group.Clear();
            if (side != null) group.Add(side);
        }

        return groups;
    }

    /// <summary>A method's note in SystemInfo.Loops when it shares a walk: the other methods of its walk.</summary>
    private static string FusionNote(UpdateModel model, int[] groups, int index)
    {
        int start = index;
        while (groups[start] == 0) start--;
        if (groups[start] < 2) return "";
        var others = Enumerable.Range(start, groups[start]).Where(i => i != index).Select(i => model.Methods[i].Name);
        return $"; one walk with {string.Join(", ", others)}";
    }

    /// <summary>The fused job's name: its methods' names and its first method's index (<c>Integrate_Move_Fused0</c>).</summary>
    private static string FusedName(UpdateModel model, int start, int count) =>
        string.Join("_", Enumerable.Range(start, count).Select(i => model.Methods[i].Name)) + "_Fused" + start;

    /// <summary>The components of a fused walk: every method's queried components, in order of first use.</summary>
    private static string[] FusedQuery(UpdateModel model, int start, int count) =>
        Enumerable.Range(start, count).SelectMany(i => model.Methods[i].Query).Distinct().ToArray();

    /// <summary>The walk runs the methods per row (every method a per-row job), else per block of rows.</summary>
    /// A walk of two systems (<paramref name="second"/> &gt; 0) runs per block: its blocks are where it notes which system runs.
    /// So does the twin of a walk whose methods chase other entities (<paramref name="chases"/>): its chasing methods
    /// run their twin jobs (batch jobs, <c>Job{i}Chases</c>) over each block, prefetching the rows ahead within it.
    private static bool FusedPerRow(UpdateModel model, int start, int count, int second = 0, bool chases = false) =>
        second == 0 && !chases && Enumerable.Range(start, count).All(i => model.Methods[i].Loop is "ForEach" or "ForEachEntity");

    /// <summary>
    /// Some method of the walk chases other entities (<see cref="ForEachModel.HasChaseTwin"/>): the walk has a twin
    /// (<c>{name}Chases</c>) running their twin jobs, which the call site picks past the L2 (<see cref="ChaseGate"/>).
    /// </summary>
    private static bool FusedChases(UpdateModel model, int start, int count) =>
        Enumerable.Range(start, count).Any(i => model.Methods[i].HasChaseTwin);

    /// <summary>
    /// The call site's gate of a pass (or a walk) chasing other entities: true when any of the lookups its chases read
    /// is past the cache of its form, once per pass. A gate per form (borrowed-ideas.md item 1): the row form's chases of
    /// other entities (at an id a row holds or a buffer's elements, on one thread, in parallel, in a fused walk) pay as
    /// soon as their records and rows leave one core's L2 (<c>Lookup&lt;T&gt;.PrefetchesPastL2</c>); a probe, on one
    /// thread or in parallel, costs more than it hides until its index leaves the last-level cache
    /// (<c>LookupExtensions.Probes</c>).
    /// </summary>
    private static string ChaseGate(IEnumerable<ForEachModel> methods, string world) =>
        string.Join(" || ", methods.SelectMany(m => m.Chases.Select(c => c.Kind == ChaseKind.Probe
                // a probe: past the cache when its index is (the index sized by its representation)
                ? $"global::EasyEcs.LookupExtensions.Probes<{m.Parameters[c.Lookup].Type}, {m.Parameters[c.Lookup].Key}>({world})"
                : $"global::EasyEcs.Lookup<{m.Parameters[c.Lookup].Type}>.PrefetchesPastL2({world})")).Distinct());

    /// <summary>
    /// The walk runs in blocks only from <c>SystemCode.FusedRows</c> rows (a check at run time); fewer rows run each pass
    /// on its own, as if not fused: their rows stay in the cache between the passes, and the blocks would only add their
    /// bookkeeping (FusionBenchmark Movement: 1.04-1.07 at 10,000 rows). Fused at any size: a per-row walk (one call per
    /// row instead of two), a parallel one (one parallel region instead of two), and one where two methods write the same
    /// component (the second store of each row stays in L1: Vitals, 0.60-0.68 at 10,000 rows).
    /// </summary>
    private static bool Sized(UpdateModel model, int start, int count, int second = 0)
    {
        if (FusedPerRow(model, start, count, second) || model.Methods[start].Parallel) return false;
        var written = new HashSet<string>();
        for (int i = start; i < start + count; i++)
            foreach (var type in model.Methods[i].Fusion!.Writes.Distinct())
                if (!written.Add(type)) return false;
        return true;
    }

    /// <summary>The job interface of a fused walk (without its type arguments).</summary>
    private static string FusedInterface(UpdateModel model, int start, int count, int second = 0, bool chases = false)
    {
        var methods = Enumerable.Range(start, count).Select(i => model.Methods[i]).ToArray();
        if (FusedPerRow(model, start, count, second, chases)) return methods.Any(m => m.Loop == "ForEachEntity") ? "IForEachEntity" : "IForEach";
        return !methods[0].Parallel && methods.Any(m => m.Loop == "ForEachMasked") ? "IForEachMasked" : "IForEachBatch";
    }

    /// <summary>
    /// The fused jobs of a system and their filters, in its __ForEach class: per group of methods that share a walk, a
    /// job holding each method's own job (<c>j0</c>, <c>j1</c>, ...) whose Execute runs them in declaration order.
    /// </summary>
    private static void EmitFusedJobs(StringBuilder sb, UpdateModel model)
    {
        var groups = FusionGroups(model);
        for (int start = 0; start < groups.Length; start++)
            if (groups[start] > 1)
            {
                EmitFusedJob(sb, model, start, groups[start]);
                if (FusedChases(model, start, groups[start])) EmitFusedJob(sb, model, start, groups[start], chases: true);
            }
    }

    /// <summary>
    /// The fused job of methods <paramref name="start"/> .. <paramref name="start"/> + <paramref name="count"/> - 1 of
    /// <paramref name="model"/>. A walk of two systems (<paramref name="second"/>: the index of the second system's first
    /// method, 0 for one system) runs per block, notes in an exception filter which system's method threw
    /// (<c>SystemCode.Blame</c>), and stamps the second system's writes itself with its stage's tick (<c>Tick</c>).
    /// The twin of a walk that chases other entities (<paramref name="chases"/>, <c>{name}Chases</c>, with the walk's
    /// filter) holds the twin jobs of its chasing methods and runs per block.
    /// </summary>
    private static void EmitFusedJob(StringBuilder sb, UpdateModel model, int start, int count, int second = 0, bool chases = false)
    {
        const string unsafeType = "global::System.Runtime.CompilerServices.Unsafe", marshal = "global::System.Runtime.InteropServices.MemoryMarshal";
        var owner = model.Owner;
        var methods = Enumerable.Range(start, count).Select(i => chases && model.Methods[i].HasChaseTwin ? model.Methods[i] with { IsChaseTwin = true } : model.Methods[i]).ToArray();
        var first = methods[0];
        string name = FusedName(model, start, count), forEach = "__ForEach" + owner.TypeParameters;
        string walk = chases ? name + "Chases" : name;
        var query = FusedQuery(model, start, count);
        string types = string.Join(", ", query);
        string iface = FusedInterface(model, start, count, second, chases);
        // The second system's writes: stamped by the job with the tick of the second system's stage.
        var later = second == 0 ? [] : methods.Skip(second - start).SelectMany(m => m.Parameters)
            .Where(p => p.RefKind == "ref" && p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan).Select(p => p.Type).Distinct().ToArray();
        bool perRow = iface is "IForEach" or "IForEachEntity", masked = iface == "IForEachMasked", parallel = first.Parallel;
        string marker = perRow || parallel ? ", global::EasyEcs.INoStructuralChanges" : "";
        sb.Append($"    internal {(parallel ? "struct" : "ref struct")} {walk} : global::EasyEcs.{iface}<{types}>{marker}\n    {{\n");
        for (int k = 0; k < count; k++) sb.Append($"        public {methods[k].Name}Job{start + k}{(methods[k].IsChaseTwin ? "Chases" : "")} j{k};\n");
        if (second > 0) sb.Append("        public ulong Tick; // the second system's stage's\n");
        sb.Append('\n');

        // The column of each of a method's components among the walk's (1-based, as the Execute parameters c1..cn).
        int[] Columns(ForEachModel method) => method.Query.Select(t => System.Array.IndexOf(query, t) + 1).ToArray();
        const string inline = "        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n";
        if (perRow)
        {
            bool entity = iface == "IForEachEntity";
            sb.Append(inline);
            sb.Append($"        public void Execute({(entity ? "global::EasyEcs.Entity entity, " : "")}{Code.List(query.Length, i => $"ref {query[i - 1]} c{i}")})\n        {{\n");
            for (int k = 0; k < count; k++)
            {
                var arguments = Columns(methods[k]).Select(c => $"ref c{c}");
                if (methods[k].Loop == "ForEachEntity") arguments = arguments.Prepend("entity");
                sb.Append($"            j{k}.Execute({string.Join(", ", arguments)});\n");
            }

            sb.Append("        }\n");
        }
        else
        {
            string spans = Code.List(query.Length, i => $"global::System.Span<{query[i - 1]}> c{i}");
            // Each method over a block of the run, then the next: a block's rows stay in cache between them.
            sb.Append(inline);
            sb.Append($"        public void Execute(global::EasyEcs.EntitySpan entities, {spans})\n        {{\n");
            if (second > 0) sb.Append("            int system = 0;\n            try\n            {\n");
            sb.Append($"            for (int at = 0, rows = entities.Length; at < rows; at += {FusedBlock})\n            {{\n");
            sb.Append($"                int n = global::System.Math.Min({FusedBlock}, rows - at);\n");
            for (int k = 0; k < count; k++)
            {
                var columns = Columns(methods[k]);
                if (second > 0 && start + k == second) sb.Append("                system = 1;\n");
                if (methods[k].IsBatch)
                    sb.Append($"                j{k}.Execute(entities.Slice(at, n), {string.Join(", ", columns.Select(c => $"c{c}.Slice(at, n)"))});\n");
                else
                {
                    var arguments = columns.Select(c => $"ref {unsafeType}.Add(ref {marshal}.GetReference(c{c}), at + i)");
                    if (methods[k].Loop == "ForEachEntity") arguments = arguments.Prepend("entities.At(at + i)");
                    sb.Append($"                for (nint i = 0; i < n; i++)\n                    j{k}.Execute({string.Join(", ", arguments)});\n");
                }
            }

            foreach (var type in later) sb.Append($"                global::EasyEcs.SystemCode.StampRows<{type}>(in entities, at, n, Tick);\n");
            if (second > 0) sb.Append("                system = 0;\n");
            sb.Append("            }\n");
            if (second > 0) sb.Append("            }\n            catch (global::System.Exception e) when (global::EasyEcs.SystemCode.Blame(e, system))\n            {\n            }\n");
            sb.Append("        }\n");
            if (masked)
            {
                // A word of which some rows pass: each method over the word, a vector loop over its lanes, the others
                // over its passing rows.
                sb.Append('\n').Append(inline);
                sb.Append($"        public void Execute(global::EasyEcs.EntitySpan entities, ulong pass, {spans})\n        {{\n");
                if (second > 0) sb.Append("            int system = 0;\n            try\n            {\n");
                for (int k = 0; k < count; k++)
                {
                    var columns = Columns(methods[k]);
                    if (second > 0 && start + k == second) sb.Append("            system = 1;\n");
                    if (methods[k].Loop == "ForEachMasked")
                        sb.Append($"            j{k}.Execute(entities, pass, {string.Join(", ", columns.Select(c => $"c{c}"))});\n");
                    else if (methods[k].IsBatch)
                    {
                        // Its runs of passing rows.
                        sb.Append("            for (ulong bits = pass; bits != 0;)\n            {\n");
                        sb.Append("                int k = global::System.Numerics.BitOperations.TrailingZeroCount(bits);\n");
                        sb.Append("                int end = k + global::System.Numerics.BitOperations.TrailingZeroCount(~(bits >> k));\n");
                        sb.Append("                bits = end >= 64 ? 0 : bits & (ulong.MaxValue << end);\n");
                        sb.Append($"                j{k}.Execute(entities.Slice(k, end - k), {string.Join(", ", columns.Select(c => $"c{c}.Slice(k, end - k)"))});\n");
                        sb.Append("            }\n");
                    }
                    else
                    {
                        var arguments = columns.Select(c => $"ref {unsafeType}.Add(ref {marshal}.GetReference(c{c}), k)");
                        if (methods[k].Loop == "ForEachEntity") arguments = arguments.Prepend("entities.At(k)");
                        sb.Append("            for (ulong bits = pass; bits != 0; bits &= bits - 1)\n            {\n");
                        sb.Append("                int k = global::System.Numerics.BitOperations.TrailingZeroCount(bits);\n");
                        sb.Append($"                j{k}.Execute({string.Join(", ", arguments)});\n            }}\n");
                    }
                }

                foreach (var type in later) sb.Append($"            global::EasyEcs.SystemCode.StampMasked<{type}>(in entities, pass, Tick);\n");
                if (second > 0) sb.Append("            }\n            catch (global::System.Exception e) when (global::EasyEcs.SystemCode.Blame(e, system))\n            {\n            }\n");
                sb.Append("        }\n");
            }
        }

        // What the loop stamps: the components any method writes; the ones every writer stamps itself (Mut<T>) are left
        // to them, as each method's own job declares.
        int writes = 0, own = 0;
        for (int c = 0; c < query.Length; c++)
        {
            var writers = methods.SelectMany(m => m.Parameters).Where(p => p.Type == query[c] && p.RefKind == "ref"
                && p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable).ToArray();
            if (writers.Length > 0) writes |= 1 << c;
            if (writers.Length > 0 && writers.All(p => p.Kind == Symbols.ParamKind.Mutable) || later.Contains(query[c])) own |= 1 << c;
        }

        string declaring = masked ? "IForEachBatch" : iface;
        sb.Append($"\n        static int global::EasyEcs.{declaring}<{types}>.WriteMask => {writes};\n");
        sb.Append($"\n        static bool global::EasyEcs.{declaring}<{types}>.OneQuery => true; // the methods' filter is a constant\n");
        if (own != 0) sb.Append($"        static int global::EasyEcs.{declaring}<{types}>.SelfStamped => {own};\n");
        var optional = parallel
            ? methods.SelectMany(m => m.Parameters).Where(p => p is { Kind: Symbols.ParamKind.Optional, RefKind: "ref", Tracked: true }).Select(p => p.Type).Distinct().ToArray()
            : [];
        if (optional.Length > 0)
        {
            sb.Append($"        private static readonly global::EasyEcs.ComponentSet __optionalWrites = global::EasyEcs.ComponentSet.Create(){string.Concat(optional.Select(t => $".With<{t}>()"))};\n");
            sb.Append($"        static global::EasyEcs.ComponentSet global::EasyEcs.{declaring}<{types}>.OptionalWrites => __optionalWrites;\n");
        }

        // A parallel walk's chunks keep partial results and records per method: merged per method, in chunk order.
        var merges = new StringBuilder();
        for (int k = 0; k < count; k++)
            if (parallel) MergeLines(merges, methods[k], $"into.j{k}", $"part.j{k}");
        if (merges.Length > 0)
        {
            sb.Append($"        static bool global::EasyEcs.{iface}<{types}>.Merges => true;\n\n");
            sb.Append($"        static void global::EasyEcs.{iface}<{types}>.Merge<TSelf>(ref TSelf job, in TSelf chunk)\n        {{\n");
            sb.Append($"            ref var into = ref {unsafeType}.As<TSelf, {walk}>(ref job);\n");
            sb.Append($"            ref var part = ref {unsafeType}.As<TSelf, {walk}>(ref {unsafeType}.AsRef(in chunk));\n");
            sb.Append(merges).Append("        }\n");
        }

        sb.Append("    }\n\n");
        if (chases) return; // the twin walks with the walk's filter

        // The walk's filter: the first method's (every method's), its required components that the walk does not query
        // as [With<T>].
        var fused = first with { Query = new EquatableArray<string>(query), With = new EquatableArray<string>(first.Query.Concat(first.With).Where(t => !query.Contains(t)).ToArray()) };
        sb.Append($"    internal static readonly global::EasyEcs.QueryFilter {name}Filter = {(fused.HasFilter ? FilterChain(fused) : "default")};\n\n");
    }

    /// <summary>
    /// The lines of a parallel job's Merge that combine method <paramref name="method"/>'s partial results and records
    /// (<c>Reduce&lt;T&gt;</c>, <c>Emit&lt;T&gt;</c>) of chunk <paramref name="part"/> into <paramref name="into"/>.
    /// </summary>
    private static void MergeLines(StringBuilder sb, ForEachModel method, string into, string part)
    {
        for (int i = 0; i < method.Parameters.Length; i++)
            if (method.Parameters[i].Kind == Symbols.ParamKind.Reduction)
                sb.Append($"            {into}.a{i}.Value = {method.Parameters[i].Type}.Combine({into}.a{i}.Value, {part}.a{i}.Value);\n");
        for (int i = 0; i < method.Parameters.Length; i++)
            if (method.Parameters[i].Kind == Symbols.ParamKind.Emit)
                sb.Append($"            global::EasyEcs.Emit<{method.Parameters[i].Type}>.Join({into}.o{i}, ref {part}.e{i});\n");
    }

    /// <summary>
    /// The calls of a system's Update that run a group of methods as one walk: the fused job made of each method's
    /// job, each method's count of visited entities (MeasureSystems), the walk, then each method's reductions.
    /// </summary>
    private static void EmitFusedCall(StringBuilder sb, UpdateModel model, int start, int count)
    {
        if (!FusedChases(model, start, count))
        {
            EmitFusedWalk(sb, model, start, count, chases: false);
            return;
        }

        // Past the cache of its chases' forms (for any lookup they read), the twin walk, whose chasing methods prefetch.
        var twin = new StringBuilder();
        var today = new StringBuilder();
        EmitFusedWalk(twin, model, start, count, chases: true);
        EmitFusedWalk(today, model, start, count, chases: false);
        sb.Append($"        if ({ChaseGate(Enumerable.Range(start, count).Select(i => model.Methods[i]), "world")})\n        {{\n");
        sb.Append(Indented(twin)).Append("        }\n        else\n        {\n").Append(Indented(today)).Append("        }\n");
    }

    /// <summary>Lines of a block one level deeper (4 spaces).</summary>
    private static string Indented(StringBuilder block) =>
        string.Concat(block.ToString().Split('\n').Select(line => line.Length > 0 ? "    " + line + "\n" : ""));

    /// <summary>
    /// One walk of <see cref="EmitFusedCall"/>: today's (where a walk too small to pay for its blocks runs each pass on
    /// its own), or its twin over the chasing methods' twin jobs (<paramref name="chases"/>), which runs only past the
    /// L2 (<see cref="ChaseGate"/>), where any walk is large enough for blocks.
    /// </summary>
    private static void EmitFusedWalk(StringBuilder sb, UpdateModel model, int start, int count, bool chases)
    {
        var owner = model.Owner;
        var first = model.Methods[start];
        string forEach = "__ForEach" + owner.TypeParameters, name = FusedName(model, start, count), local = $"fused{start}";
        string walk = chases ? name + "Chases" : name;
        bool sized = !chases && Sized(model, start, count);
        if (sized)
        {
            sb.Append($"        if (world.Count<{string.Join(", ", FusedQuery(model, start, count))}>(in {forEach}.{name}Filter) < global::EasyEcs.SystemCode.FusedRows) // in cache: each pass on its own\n        {{\n");
            for (int k = 0; k < count; k++) EmitMethodPass(sb, model, start + k);
            sb.Append("        }\n        else\n        {\n");
        }

        var members = Enumerable.Range(0, count).Select(k =>
            $"j{k} = new {forEach}.{model.Methods[start + k].Name}Job{start + k}{(chases && model.Methods[start + k].HasChaseTwin ? "Chases" : "")}{Fields(model.Methods[start + k], "world")}");
        sb.Append($"        var {local} = new {forEach}.{walk} {{ {string.Join(", ", members)} }};\n");
        for (int k = 0; k < count; k++)
        {
            var method = model.Methods[start + k];
            string filterName = $"{forEach}.{method.Name}Filter{start + k}";
            string whole = method.Since ? $", {filterName}.Since(system.LastRunTick + 1)" : $", in {filterName}";
            string visited = $"world.Count<{method.ComponentTypes}>({whole.Substring(2)})";
            if (method.Slices > 1) visited = $"{visited} / {method.Slices}";
            sb.Append($"        if (global::EasyEcs.SystemCode.Measuring(world)) global::EasyEcs.SystemCode.Visit(system, {visited});\n");
        }

        string fusedFilter = $"{forEach}.{name}Filter";
        string filter = first.Since ? $", {fusedFilter}.Since(system.LastRunTick + 1)" : $", in {fusedFilter}";
        if (first.Slices > 1) filter = first.Sliced(fusedFilter, $"(int)(system.RunCount % {first.Slices})");
        string loop = FusedInterface(model, start, count, chases: chases).Substring(1) + (first.Parallel ? "Parallel" : "");
        sb.Append($"        world.{loop}<{string.Join(", ", FusedQuery(model, start, count))}, {forEach}.{walk}>(ref {local}{filter}{first.Chunks}); // one walk: {string.Join(", then ", Enumerable.Range(start, count).Select(i => model.Methods[i].Name))} per {(FusedPerRow(model, start, count, chases: chases) ? "row" : "block of rows")}\n");
        for (int k = 0; k < count; k++) EmitReductions(sb, model.Methods[start + k], "world", $"{local}.j{k}", "        ");
        if (sized) sb.Append("        }\n");
    }

    // ---------------- two systems in one walk ----------------

    /// <summary>
    /// The pairs of systems of the assembly that may share one walk (<see cref="FusionPair"/>): from each system's
    /// generated model and its place in the schedule's declarations (group, orderings, file and position).
    /// </summary>
    private static IncrementalValuesProvider<FusionPair> FusionPairs(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<UpdateModel> updates)
    {
        var places = context.SyntaxProvider
            .ForAttributeWithMetadataName(Symbols.EcsSystemAttribute,
                static (node, _) => node is StructDeclarationSyntax,
                static (ctx, _) => SystemPlace.Create((INamedTypeSymbol)ctx.TargetSymbol, ctx.TargetNode))
            .Where(static p => p is not null)
            .WithTrackingName("SystemPlaces");
        return updates.Where(static m => m.Owner.IsSystem).Collect().Combine(places.Collect())
            .SelectMany(static (pair, _) => FusionPair.Find(pair.Left, pair.Right!))
            .WithTrackingName("SystemFusions");
    }

    /// <summary>
    /// Where a system is declared, for pairing (declarations only): its group, the systems its orderings name, where it
    /// is declared (<see cref="File"/>: its file and its containing type or namespace) and its position there.
    /// </summary>
    private sealed record SystemPlace(string FullName, string Group, EquatableArray<string> Before, EquatableArray<string> After, string File, int Position)
    {
        public static SystemPlace? Create(INamedTypeSymbol type, SyntaxNode node)
        {
            if (!Symbols.GeneratesSystem(type) || type.IsGenericType || type.DeclaringSyntaxReferences.Length > 1) return null; // a partial type: no one place
            var before = new List<string>();
            var after = new List<string>();
            foreach (var attribute in type.GetAttributes())
                if (Symbols.Is(attribute.AttributeClass, Symbols.UpdateBeforeAttribute)) before.Add(attribute.AttributeClass!.TypeArguments[0].ToDisplayString(Symbols.Fqn));
                else if (Symbols.Is(attribute.AttributeClass, Symbols.UpdateAfterAttribute)) after.Add(attribute.AttributeClass!.TypeArguments[0].ToDisplayString(Symbols.Fqn));
            return new SystemPlace(type.ToDisplayString(Symbols.Fqn), Symbols.GroupOf(type)?.ToDisplayString(Symbols.Fqn) ?? "",
                new(before.ToArray()), new(after.ToArray()), node.SyntaxTree.FilePath + "#" + type.ContainingSymbol.ToDisplayString(), MemberIndex(node));
        }

        /// <summary>
        /// The declaration's index among the members of what declares it (a type, a namespace, the file): an edit inside
        /// a body or below it leaves it as it is, so the model stays cached.
        /// </summary>
        private static int MemberIndex(SyntaxNode node) => node.Parent switch
        {
            TypeDeclarationSyntax type => type.Members.IndexOf((MemberDeclarationSyntax)node),
            BaseNamespaceDeclarationSyntax ns => ns.Members.IndexOf((MemberDeclarationSyntax)node),
            CompilationUnitSyntax unit => unit.Members.IndexOf((MemberDeclarationSyntax)node),
            _ => -1,
        };
    }

    /// <summary>
    /// Two systems that may run as one walk: <see cref="First"/>'s methods then <see cref="Second"/>'s, each system's
    /// methods one walk already, the second ordered right after the first ([UpdateAfter] / [UpdateBefore], or declared
    /// next in the same file) in the same group with the same [UpdateInterval], and the methods fusing as one system's
    /// would (<see cref="Fusion"/>). The runtime runs the pair's walk only when the schedule puts the two next to each
    /// other and both run (World.RunFused); otherwise each system runs its own code.
    /// </summary>
    private sealed record FusionPair(UpdateModel First, UpdateModel Second)
    {
        /// <summary>The methods of both systems, the first's then the second's, as one model (numbered in that order).</summary>
        public UpdateModel Both => First with { Methods = new EquatableArray<ForEachModel>(First.Methods.Concat(Second.Methods).ToArray()) };

        /// <summary>The type the system set calls (<c>__Integration__Bounce_Fusion</c>), in the first system's namespace.</summary>
        public string CodeName => $"__{First.Owner.FlatName}__{Second.Owner.FlatName}_Fusion";

        public string FullCodeName => $"global::{(First.Owner.Namespace.Length > 0 ? First.Owner.Namespace + "." : "")}{CodeName}";

        public static IEnumerable<FusionPair> Find(ImmutableArray<UpdateModel> systems, ImmutableArray<SystemPlace> places)
        {
            var byName = new Dictionary<string, UpdateModel>();
            foreach (var system in systems)
                if (Fusable(system)) byName[system.Owner.FullName] = system;
            var placed = places.Where(p => byName.ContainsKey(p.FullName)).ToArray();
            var pairs = new List<FusionPair>();
            foreach (var first in placed)
            foreach (var second in placed)
            {
                if (first == second || first.Group != second.Group || !Ordered(first, second, placed)) continue;
                var a = byName[first.FullName];
                var b = byName[second.FullName];
                if (a.Interval != b.Interval || a.Phase != b.Phase || Problem(a, b) != null) continue;
                if (!pairs.Any(p => p.First.Owner == a.Owner && p.Second.Owner == b.Owner)) pairs.Add(new FusionPair(a, b));
            }

            return pairs.OrderBy(p => p.First.Owner.FullName, System.StringComparer.Ordinal).ThenBy(p => p.Second.Owner.FullName, System.StringComparer.Ordinal);
        }

        /// <summary>
        /// The second runs right after the first: an ordering names the pair, or the second is the next system declared
        /// in the first's file (registered in declaration order, the schedule keeps them next to each other).
        /// </summary>
        private static bool Ordered(SystemPlace first, SystemPlace second, SystemPlace[] places)
        {
            if (second.After.Contains(first.FullName) || first.Before.Contains(second.FullName)) return true;
            if (first.File != second.File || second.Position <= first.Position) return false;
            return !places.Any(p => p.File == first.File && p.Position > first.Position && p.Position < second.Position);
        }

        /// <summary>
        /// A system that can share a walk with another: not generic, no [OnUpdate] (which would run between the two), its
        /// methods one walk of their own, on one thread, every row on every run (no slice) and no change filter (each
        /// system's tick is its own).
        /// </summary>
        private static bool Fusable(UpdateModel system) =>
            system.Owner.TypeParameters.Length == 0 && system.Methods.Length > 0 && !system.Hooks.Any(h => h.Kind == "Update")
            && system.Methods.All(m => m.Fusion != null && !m.Parallel && m.Slices == 1 && !m.Since)
            && FusionGroups(system)[0] == system.Methods.Length;

        /// <summary>
        /// Why <paramref name="second"/>'s methods cannot join <paramref name="first"/>'s walk, or null: the rule of one
        /// system (<see cref="Fusion"/>), and no write the second system's stage would have to stamp through a Mut or a
        /// Maybe (they stamp with the world's tick, the first's).
        /// </summary>
        private static string? Problem(UpdateModel first, UpdateModel second)
        {
            if (second.Methods.Any(m => m.Parameters.Any(p => p.Kind == Symbols.ParamKind.Mutable || p is { Kind: Symbols.ParamKind.Optional, RefKind: "ref" })))
                return "the second system writes through a Mut or a Maybe";
            var group = first.Methods.Select(m => m.Fusion!).ToList();
            foreach (var method in second.Methods)
            {
                if (Fusion.Problem(group, method.Fusion!) is { } problem) return problem;
                group.Add(method.Fusion!);
            }

            return null;
        }
    }

    /// <summary>
    /// The file of a pair of systems: both systems' methods (accessors and jobs, numbered first's then second's), the
    /// walk's job and the code the set calls (<c>Update</c>).
    /// </summary>
    private static void AddFusion(SourceProductionContext context, FusionPair pair)
    {
        var both = pair.Both;
        var first = pair.First.Owner;
        var sb = new StringBuilder(Header).Append('\n');
        if (first.Namespace.Length > 0) sb.Append("namespace ").Append(first.Namespace).Append(";\n\n");
        sb.Append("file static class __ForEach\n{\n");
        for (int i = 0; i < both.Methods.Length; i++)
        {
            EmitAccessor(sb, both.Methods[i].Owner, both.Methods[i], i);
            EmitJob(sb, both.Methods[i].Owner, both.Methods[i], i);
        }

        int second = pair.First.Methods.Length;
        EmitFusedJob(sb, both, 0, both.Methods.Length, second);
        bool chases = FusedChases(both, 0, both.Methods.Length);
        if (chases) EmitFusedJob(sb, both, 0, both.Methods.Length, second, chases: true);
        sb.Append("}\n\n");

        const string world = "global::EasyEcs.World", handle = "global::EasyEcs.SystemHandle";
        string name = FusedName(both, 0, both.Methods.Length);
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"internal static class {pair.CodeName}\n{{\n");
        sb.Append($"    /// <summary>{pair.First.Owner.FlatName}, then {pair.Second.Owner.FlatName}, as one walk: the second's writes stamped at <paramref name=\"tick\"/>; false when it ran nothing (too few rows: SystemCode.FusedRows).</summary>\n");
        sb.Append($"    public static bool Update({world} world, {handle} first, {handle} second, ulong tick)\n    {{\n");
        string query = string.Join(", ", FusedQuery(both, 0, both.Methods.Length));
        void Walk(bool twin, string indent)
        {
            string walk = twin ? name + "Chases" : name, local = twin ? "chases" : "fused"; // one name per scope (CS0136)
            var members = Enumerable.Range(0, both.Methods.Length).Select(k =>
                $"j{k} = new __ForEach.{both.Methods[k].Name}Job{k}{(twin && both.Methods[k].HasChaseTwin ? "Chases" : "")}{Fields(both.Methods[k], "world")}");
            sb.Append($"{indent}var {local} = new __ForEach.{walk} {{ {string.Join(", ", members)}, Tick = tick }};\n");
            string loop = FusedInterface(both, 0, both.Methods.Length, second).Substring(1);
            sb.Append($"{indent}world.{loop}<{query}, __ForEach.{walk}>(ref {local}, in __ForEach.{name}Filter);\n");
            for (int k = 0; k < both.Methods.Length; k++) EmitReductions(sb, both.Methods[k], "world", $"{local}.j{k}", indent);
            sb.Append($"{indent}return true;\n");
        }

        if (chases)
        {
            // Past the cache of the chases' forms (for any lookup they read), the twin walk: its chasing methods prefetch.
            sb.Append($"        if ({ChaseGate(both.Methods, "world")})\n        {{\n");
            Walk(true, "            ");
            sb.Append("        }\n\n");
        }

        if (Sized(both, 0, both.Methods.Length, second))
            sb.Append($"        if (world.Count<{query}>(in __ForEach.{name}Filter) < global::EasyEcs.SystemCode.FusedRows) return false; // in cache: each pass on its own\n");
        Walk(false, "        ");
        sb.Append("    }\n}\n");
        context.AddSource($"{pair.First.Owner.HintName}+{pair.Second.Owner.HintName}.Fusion.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}
