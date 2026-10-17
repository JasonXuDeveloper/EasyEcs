using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// Expands the T1..T9 API families of EasyEcs.Core from one template each, so the hand-written runtime stays a
/// single shared implementation. Only runs when compiling EasyEcs.Core itself: the pipeline depends on nothing but
/// the assembly name (an equatable bool), so it is computed once and cached for every later edit.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ArityGenerator : IIncrementalGenerator
{
    private const int MaxArity = 9;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var isCore = context.CompilationProvider
            .Select(static (c, _) => c.AssemblyName == "EasyEcs.Core")
            .WithTrackingName("IsEasyEcsCore");

        context.RegisterSourceOutput(isCore, static (spc, isCoreAssembly) =>
        {
            if (!isCoreAssembly) return;
            spc.AddSource("Entity.Structural.g.cs", SourceText.From(Structural(), Encoding.UTF8));
            spc.AddSource("World.Queries.g.cs", SourceText.From(Queries(), Encoding.UTF8));
            spc.AddSource("QueryItems.g.cs", SourceText.From(QueryItems(), Encoding.UTF8));
        });
    }

    // ---------------- Entity.Set / Entity.Remove / World.CreateEntity ----------------

    private static string Structural()
    {
        var entity = new StringBuilder(Header).Append(
            "using System;\nusing System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\n\nnamespace EasyEcs;\n\npublic readonly partial struct Entity\n{\n");
        var world = new StringBuilder("public sealed partial class World\n{\n");
        var prefab = new StringBuilder("public sealed partial class Prefab\n{\n");

        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            string cons = Constraints(n, "        ");
            string values = List(n, i => $"in T{i} c{i}");
            string what = n == 1 ? "a component" : n + " components";
            // Dense index of the generic signature: keys the cached multi-component transitions of an archetype.
            string signature = $"TypeIndex<ComponentSignature, ({tps})>.Value";

            if (n > 1)
                entity.Append($$"""
                        /// <summary>Whether the entity has all of <c>{{tps}}</c>; false for a destroyed or default handle. O({{n}}).</summary>
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public bool Has<{{tps}}>()
                    {{cons}}
                            => HasAll([{{List(n, i => $"ComponentType<T{i}>.Bit")}}]);


                    """);

            entity.Append($$"""
                    /// <summary>
                    /// Add or overwrite {{what}} with the given value{{(n == 1 ? "" : "s")}}, with at most one row copy. Returns the entity
                    /// for chaining. O({{n}}) amortized. Owner thread only (see doc/guide/threading.md).
                    /// </summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public Entity Set<{{tps}}>({{values}})
                {{cons}}
                    {
                        SetCore(_world, Id, Version, {{List(n, i => $"in c{i}")}});
                        return this;
                    }

                    // The handle goes by value: a loop calling Set keeps its handle in registers (a call through `this`
                    // would take its address and store it to memory on every row, even when the call is rare).
                    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the loops that call it lean
                    private static void SetCore<{{tps}}>(World world, int id, int version, {{values}})
                {{cons}}
                    {
                        if (world == null) ThrowStale(); // a default handle
                        using (world.Structural())
                        {
                            ref var meta = ref world.Live(new Entity(world, id, version));
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            {{(n == 1 ? "world.AddBit(ref meta, id, b1);" : $"world.AddBits(ref meta, id, [{List(n, i => $"b{i}")}], {signature});")}}
                {{Lines(n, i => $"            World.Row<T{i}>(ref meta, b{i}) = c{i};")}}
                {{Lines(n, i => $"            meta.Archetype.Stamp<T{i}>(meta.Slot, world.ChangeTick);")}}
                {{Lines(n, i => $"            world.Indexed(id, version, in c{i});")}}
                        }
                    }

                    /// <summary>
                    /// Remove {{what}}; missing components are ignored. The entity keeps its row (no move). Returns the entity
                    /// for chaining. O({{n}}). Owner thread only (see doc/guide/threading.md).
                    /// </summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public Entity Remove<{{tps}}>()
                {{cons}}
                    {
                        RemoveCore<{{tps}}>(_world, Id, Version);
                        return this;
                    }


                    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the loops that call it lean; the handle by value (see SetCore)
                    private static void RemoveCore<{{tps}}>(World world, int id, int version)
                {{cons}}
                    {
                        if (world == null) ThrowStale(); // a default handle
                        using (world.Structural())
                        {
                            ref var meta = ref world.Live(new Entity(world, id, version));
                            {{(n == 1 ? "world.RemoveBit(ref meta, ComponentType<T1>.Bit);"
                                : $"world.RemoveBits(ref meta, [{List(n, i => $"ComponentType<T{i}>.Bit")}]);")}}
                        }
                    }


                """);

            prefab.Append($$"""
                    /// <summary>Add {{what}} to the recipe, or replace {{(n == 1 ? "its value" : "their values")}}. Returns this prefab (chain calls). O({{n}}).</summary>
                    public Prefab Set<{{tps}}>({{values}})
                {{cons}}
                    {
                        Row.Set({{List(n, i => $"in c{i}")}});
                        return this;
                    }

                    /// <summary>Drop {{what}} from the recipe; missing components are ignored. Returns this prefab. O({{n}}).</summary>
                    public Prefab Remove<{{tps}}>()
                {{cons}}
                    {
                        Row.Remove<{{tps}}>();
                        return this;
                    }


                """);

            world.Append($$"""
                    /// <summary>
                    /// Create an entity with {{what}} placed directly in its final archetype (no intermediate moves).
                    /// O({{n}}) amortized. Owner thread only (see doc/guide/threading.md).
                    /// </summary>
                    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the loops that call it lean
                    public Entity CreateEntity<{{tps}}>({{values}})
                {{cons}}
                    {
                        using (Structural())
                        {
                            int id = AllocateId();
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            // Straight into the final archetype through an edge cached on the empty archetype (no mask hashing).
                            var target = ({{(n == 1 ? "_emptyArchetype.GetAddEdge(b1) ?? CreateEdge(_emptyArchetype, b1)"
                                : $"_emptyArchetype.GetAddSetEdge({signature}) ?? CreateSetEdge(_emptyArchetype, [{List(n, i => $"b{i}")}], {signature})")}}).Target;
                            int slot = Place(id, target);
                {{Lines(n, i => $"            Unsafe.Add(ref target.Base<T{i}>(b{i}), slot) = c{i};")}}
                {{Lines(n, i => $"            target.Stamp<T{i}>(slot, _changeTick);")}}
                            int version = Meta[id].Version;
                {{Lines(n, i => $"            Indexed(id, version, in c{i});")}}
                            return new Entity(this, id, version);
                        }
                    }

                    /// <summary>
                    /// Create <c>entities.Length</c> entities with {{what}} (the same value{{(n == 1 ? "" : "s")}} for each), placed directly in
                    /// their final archetype, and write their handles to <paramref name="entities"/>. One pass through the
                    /// structural gate and at most one table growth: O(count × {{n}}). Owner thread only (see doc/guide/threading.md).
                    /// </summary>
                    public void CreateEntities<{{tps}}>(Span<Entity> entities, {{values}})
                {{cons}}
                    {
                        using (Structural())
                        {
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            var target = ({{(n == 1 ? "_emptyArchetype.GetAddEdge(b1) ?? CreateEdge(_emptyArchetype, b1)"
                                : $"_emptyArchetype.GetAddSetEdge({signature}) ?? CreateSetEdge(_emptyArchetype, [{List(n, i => $"b{i}")}], {signature})")}}).Target;
                            if (entities.IsEmpty) return;
                            int first = PlaceRows(target, entities); // consecutive rows: each column is filled at once
                {{Lines(n, i => $"            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref target.Base<T{i}>(b{i}), first), entities.Length).Fill(c{i});")}}
                {{Lines(n, i => $"            target.StampRows<T{i}>(first, entities.Length, _changeTick);")}}
                {{Lines(n, i => $"            if (ComponentType<T{i}>.Indexed) foreach (var e in entities) Indexed(e.Id, e.Version, in c{i});")}}
                        }
                    }


                """);
        }

        return entity.Append("}\n\n").Append(world).Append("}\n\n").Append(prefab).Append("}\n").ToString();
    }

    // ---------------- IForEach / ForEach / Query / Count / Any ----------------

    private const string Writes = """
            /// <summary>
            /// Components the job writes through its refs: bit k - 1 for component k (default: all). When the job changes
            /// the archetype of the entity it visits, ForEach carries those components over from the row the refs point
            /// into to the entity's new row after the job returns. Generated jobs set only their <c>ref</c> parameters.
            /// </summary>
            static virtual int Writes => -1;
        """;

    private static string Queries()
    {
        var sb = new StringBuilder(Header).Append("""
            using System;
            using System.Buffers;
            using System.Numerics;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Threading;

            namespace EasyEcs;


            """);

        for (int n = 1; n <= MaxArity; n++)
        {
            sb.Append($$"""
                /// <summary>
                /// Struct callback for <c>World.ForEach</c>. Being a struct-constrained generic, calls are devirtualized and
                /// usually inlined by the JIT: no delegate, no closure, no allocation. <c>[ForEach]</c> methods compile to this.
                /// </summary>
                public interface IForEach<{{TypeParams(n)}}>
                {{Constraints(n, "    ")}}
                {
                    void Execute({{List(n, i => $"ref T{i} c{i}")}});

                {{Writes}}
                }

                /// <summary>
                /// Struct callback for <c>World.ForEachBatch</c>: receives whole runs of rows as spans, one per component, so
                /// the job can process them with SIMD (<c>System.Numerics.Vector</c>, <c>MemoryMarshal.Cast</c>), and their
                /// entities (built only when indexed). <c>[ForEach]</c> methods taking <c>Span&lt;T&gt;</c> /
                /// <c>ReadOnlySpan&lt;T&gt;</c> compile to this.
                /// </summary>
                public interface IForEachBatch<{{TypeParams(n)}}>
                {{Constraints(n, "    ")}}
                {
                    /// <summary>
                    /// One run of consecutive rows of one archetype: element i of <paramref name="entities"/> and of every span
                    /// belongs to the same entity.
                    /// </summary>
                    void Execute(EntitySpan entities, {{List(n, i => $"Span<T{i}> c{i}")}});

                {{Writes}}
                }

                /// <summary>
                /// Like <see cref="IForEach{{{TypeParams(n)}}}"/> but also receives the <see cref="Entity"/> handle, built from the
                /// archetype's entity rows (id and version side by side) without a per-entity lookup. <c>[ForEach]</c> methods taking an
                /// <see cref="Entity"/> compile to this.
                /// </summary>
                public interface IForEachEntity<{{TypeParams(n)}}>
                {{Constraints(n, "    ")}}
                {
                    void Execute(Entity entity, {{List(n, i => $"ref T{i} c{i}")}});

                {{Writes}}
                }


                """);
        }

        sb.Append("public sealed partial class World\n{\n");
        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            string cons = Constraints(n, "        ");
            string bits = List(n, i => $"b{i}");
            sb.Append($$"""
                    /// <summary>Resolve the archetypes matching <c>{{tps}}</c> plus <paramref name="filter"/>.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    private QueryMatch Match<{{tps}}>(in QueryFilter filter, {{List(n, i => $"out ushort b{i}")}})
                {{cons}}
                    {
                {{Lines(n, i => $"        b{i} = ComponentType<T{i}>.Bit;")}}
                        return GetMatchingArchetypes(TypeIndex<ComponentSignature, {{(n == 1 ? "T1" : $"({tps})")}}>.Value, [{{bits}}], in filter);
                    }

                {{ForEach(n, "ForEach", "IForEach", "", "", "the components")}}
                {{ForEach(n, "ForEachEntity", "IForEachEntity", "new Entity(this, id, idp.Version)", "new Entity(this, ids.Id, ids.Version)", "the entity handle")}}
                    /// <summary>
                    /// Run <paramref name="job"/> over every entity having all of <c>{{tps}}</c> (and matching
                    /// <paramref name="filter"/>), one run of consecutive rows at a time: a dense archetype is a single
                    /// call. Allocation-free. The job sees spans into the tables, so structural changes throw while it runs
                    /// (<see cref="InvalidOperationException"/>).
                    /// </summary>
                    public void ForEachBatch<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default)
                {{cons}}
                        where TJob : struct, IForEachBatch<{{tps}}>, allows ref struct
                    {
                        Gate.Freeze(); // the job holds spans into the tables: a structural change throws
                        try
                        {
                            Batches<{{tps}}, TJob>(ref job, in filter);
                        }
                        finally
                        {
                            Gate.Thaw();
                        }
                    }

                    [MethodImpl(MethodImplOptions.NoInlining)] // like ForEach: the job inlines into this loop, not further up
                    private void Batches<{{tps}}, TJob>(ref TJob job, in QueryFilter filter)
                {{cons}}
                        where TJob : struct, IForEachBatch<{{tps}}>, allows ref struct
                    {
                        var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        var archetypes = match.Archetypes;
                        for (int ai = 0; ai < archetypes.Length; ai++)
                        {
                            var archetype = archetypes[ai];
                            var rows = new RowFilter(match.Filters[ai], filter.Tick);
                            int count = archetype.Count;
                            int mode = rows.Mode(archetype);
                            if (archetype.AliveCount == 0 || mode == RowFilter.None) continue;
                            ref EntitySlot slots = ref MemoryMarshal.GetArrayDataReference(archetype.Slots);
                {{Lines(n, i => $"            ref T{i} r{i} = ref archetype.Base<T{i}>(b{i});")}}
                            if (mode == RowFilter.Test)
                            {
                                // Rows without some component: runs of rows whose presence bits pass.
                                for (int start = 0; rows.NextRun(archetype, ref start, count, out int length); start += length)
                                {
                {{StampRows(n, "                    ", "start", "length")}}
                                    job.Execute(new EntitySpan(this, ref Unsafe.Add(ref slots, start), length), {{List(n, i => $"MemoryMarshal.CreateSpan(ref Unsafe.Add(ref r{i}, start), length)")}});
                                }

                                continue;
                            }

                            if (count == archetype.AliveCount)
                            {
                {{StampRows(n, "                ", "0", "count")}}
                                job.Execute(new EntitySpan(this, ref slots, count), {{List(n, i => $"MemoryMarshal.CreateSpan(ref r{i}, count)")}});
                                continue;
                            }

                            for (int start = 0; archetype.NextRun(ref start, count, out int length); start += length)
                            {
                {{StampRows(n, "                ", "start", "length")}}
                                job.Execute(new EntitySpan(this, ref Unsafe.Add(ref slots, start), length), {{List(n, i => $"MemoryMarshal.CreateSpan(ref Unsafe.Add(ref r{i}, start), length)")}});
                            }
                        }
                    }

                    /// <summary>Enumerate entities having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>). Allocation-free.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public QueryEnumerator<{{tps}}> Query<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        return new QueryEnumerator<{{tps}}>(this, match, filter.Tick, {{bits}});
                    }

                    /// <summary>Number of alive entities having all of <c>{{tps}}</c>. O(matching archetypes), plus O(rows / 64) for tables with rows to test.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public int Count<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => Match<{{tps}}>(in filter, {{List(n, i => "out _")}}).CountAlive(filter.Tick);

                    /// <summary>Whether any alive entity has all of <c>{{tps}}</c>. O(matching archetypes), plus O(rows / 64) for tables with rows to test; early exit.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool Any<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => Match<{{tps}}>(in filter, {{List(n, i => "out _")}}).AnyAlive(filter.Tick);

                    /// <summary>
                    /// Destroy every alive entity having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>) and return
                    /// how many. One pass through the structural gate: O(matching archetypes + rows). Owner thread only (see doc/guide/threading.md).
                    /// </summary>
                    public int DestroyEntities<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => DestroyMatching(Match<{{tps}}>(in filter, {{List(n, i => "out _")}}), filter.Tick);


                """);
        }

        return sb.Append("}\n").ToString();
    }

    /// <summary>Stamp row <paramref name="row"/> of every component the job writes (only tracked ones emit code).</summary>
    private static string StampRow(int n, string indent, string row) =>
        Lines(n, i => $"{indent}    if ((TJob.Writes & {1 << (i - 1)}) != 0) archetype.StampVisited<T{i}>({row}, _changeTick);");

    /// <summary>Stamp a run of rows of every component the job writes.</summary>
    private static string StampRows(int n, string indent, string start, string count) =>
        Lines(n, i => $"{indent}    if ((TJob.Writes & {1 << (i - 1)}) != 0) archetype.StampRows<T{i}>({start}, {count}, _changeTick);");

    /// <summary>
    /// Drop the per-row advance of the entity rows when the job does not receive the entity (the step becomes 0): a
    /// ref a loop advances but never reads still takes a register, and the refs the job uses then spill to the stack.
    /// </summary>
    private static string Lean(string rowArg, string code) => rowArg.Length > 0
        ? code
        : System.Text.RegularExpressions.Regex.Replace(code, @"\b(ids|idk|idr) = ref Unsafe\.Add\(ref (ids|idk|idr), 1\)", "$1 = ref Unsafe.Add(ref $1, 0)");

    /// <summary>
    /// The dense row loop stepping one index instead of one ref per column, for three or more columns whose element sizes
    /// are all x64 address scales (1, 2, 4 or 8 bytes; a JIT-time constant), followed by <c>else</c> and the ref loop.
    /// The JIT turns the index into a byte offset plus a counter, two steps per row, which pays only against three or
    /// more refs; with fewer it is slower (and an offset inside the element is then not always folded into the address).
    /// </summary>
    private static string IndexedRows(int n, string rowArg) => $$"""
        if ({{string.Join(" && ", Enumerable.Range(1, n).Select(i => $"Unsafe.SizeOf<T{i}>() is 1 or 2 or 4 or 8"))}})
                {
                    nint rowCount = end - start;
                    for (nint i = 0; i < rowCount; i++)
                        job.Execute({{Args(rowArg, List(n, k => $"ref Unsafe.Add(ref r{k}, i)"))}});
                }
                else
        """;

    /// <summary>The arguments of a job call: the entity (none for <c>IForEach</c>), then the component refs.</summary>
    private static string Args(string entity, string refs) => entity.Length == 0 ? refs : entity + ", " + refs;

    /// <summary>
    /// One ForEach flavor: <paramref name="entityArg"/> is what the job receives before the components (nothing, or an
    /// Entity built from the archetype's entity rows).
    /// </summary>
    private static string ForEach(int n, string name, string job, string entityArg, string rowArg, string receives)
    {
        string tps = TypeParams(n);
        string execute = $"job.Execute({Args(entityArg, List(n, i => $"ref r{i}"))});";
        // The row argument inside a run of rows of a word (idk walks the run).
        string rowArgRun = System.Text.RegularExpressions.Regex.Replace(rowArg, @"\bids\b", "idk");
        string rowArgIndexed = System.Text.RegularExpressions.Regex.Replace(rowArg, @"\bids\b", "Unsafe.Add(ref ids, i)");
        // Row loops move one ref per column by one element per row (strength-reduced by hand): with an int index the
        // JIT sign-extends and rebuilds every address, which nearly doubles the cost of a tight loop.
        string advance = List(n, i => $"r{i} = ref Unsafe.Add(ref r{i}, 1)");
        string startAt = Lines(n, i => $"                ref T{i} r{i} = ref Unsafe.Add(ref archetype.Base<T{i}>(b{i}), start);");
        string bits = List(n, i => $"b{i}");
        string ushorts = List(n, i => $"ushort b{i}");
        return Lean(rowArg, $$"""
                /// <summary>
                /// Run <paramref name="job"/> with {{receives}} for every entity having all of <c>{{tps}}</c> (and matching
                /// <paramref name="filter"/>). The fastest iteration: walks each matching archetype's columns front to back,
                /// O(matching archetypes + rows), allocation-free.
                /// Structural changes inside the job are allowed and take effect immediately: an entity is visited at most
                /// once (entities that enter a matching archetype during the pass are not visited, entities that leave
                /// before their turn are skipped), and when the job changes the archetype of the entity it visits, its
                /// writes through the refs are carried over to the entity's new row (<c>{{job}}.Writes</c>). A job
                /// implementing <see cref="INoStructuralChanges"/> skips that bookkeeping.
                /// </summary>
                // Never inlined: a caller that profile-guided inlining grows to hold this loop runs out of inlining
                // budget for the job, which then becomes a call per entity. Called once per pass, the call is free.
                [MethodImpl(MethodImplOptions.NoInlining)]
                public void {{name}}<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default)
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                {
                    if (typeof(INoStructuralChanges).IsAssignableFrom(typeof(TJob)))
                    {
                        // The job promises no structural change (the type test is a JIT-time constant), so its pass keeps no
                        // bookkeeping; the frozen gate turns a broken promise (a change made in a helper the analyzer does
                        // not follow) into an exception instead of torn rows.
                        Gate.Freeze();
                        try
                        {
                            job = {{name}}Stable<{{tps}}, TJob>(job, in filter);
                        }
                        finally
                        {
                            Gate.Thaw();
                        }

                        return;
                    }

                    var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                    var archetypes = match.Archetypes;

                    // Row counts at the start of the pass: rows appended during the pass are not visited.
                    var filters = match.Filters;
                    ulong since = filter.Tick;
                    // The world's array for this nesting depth, not stackalloc'd: a localloc would keep this method out
                    // of tiered compilation (no OSR, no PGO), and with it the inlining of the job into the loop.
                    int[] counts = PassCounts(archetypes.Length);
                    for (int ai = 0; ai < archetypes.Length; ai++) counts[ai] = archetypes[ai].Count;
                    BeginPass();
                    try // a job that throws still ends the pass, so its tombstones are closed
                    {
                        for (int ai = 0; ai < archetypes.Length; ai++)
                        {
                            var archetype = archetypes[ai];
                            var rows = new RowFilter(filters[ai], since);
                            int count = counts[ai];
                            int mode = rows.Mode(archetype);
                            if (archetype.AliveCount == 0 || mode == RowFilter.None) continue;
                            // The row loops return after a structural change (the next row and the visited entity): the
                            // refs are re-read and the visited entity's writes carried over here, so the loops keep only
                            // their row state live across the job. A table whose rows all pass takes the lean loop.
                            for (int at = 0; at < count && mode != RowFilter.None;)
                            {
                                job = mode == RowFilter.All
                                    ? {{name}}Live<{{tps}}, TJob>(job, archetype, at, count, {{bits}})
                                    : {{name}}LiveTested<{{tps}}, TJob>(job, archetype, rows, at, count, {{bits}});
                                long stop = _stop;
                                if (stop < 0) break; // the rows ran out without a structural change
                                at = count - (int)stop;
                                int moved = (int)(stop >> 32);
                                if (archetype.Slots[at - 1].Id < 0) CarryOver(archetype, at - 1, moved, TJob.Writes, [{{bits}}]);
                                mode = rows.Mode(archetype); // the change may have made rows fail (or pass)
                            }
                        }
                    }
                    finally
                    {
                        EndPass();
                    }
                }

                /// <summary>
                /// Rows [<paramref name="start"/>, <paramref name="count"/>) of a pass that may make structural changes,
                /// while every row passes the query. Returns the job; sets <c>_stop</c> to -1 when the rows ran out, or,
                /// after a structural change, to the rows left after the visited one (low 32 bits; the caller knows
                /// <paramref name="count"/>) and the visited entity (high 32 bits): the caller refreshes and carries over.
                /// </summary>
                [MethodImpl(MethodImplOptions.NoInlining)] // per table or run: kept out of the caller, which profile-guided
                // inlining would otherwise grow until the loop's refs spill to the stack
                private TJob {{name}}Live<{{tps}}, TJob>(TJob job, Archetype archetype, int start, int count, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                {
                    // The loop walks refs up to an end ref: no row index or count stays live across the job (the row
                    // is computed only to stamp a tracked write), so the job's own values keep more registers.
                    int seen = StorageVersion;
                    ref EntitySlot slots = ref MemoryMarshal.GetArrayDataReference(archetype.Slots);
                    ref EntitySlot idp = ref Unsafe.Add(ref slots, start);
                    ref EntitySlot end = ref Unsafe.Add(ref slots, count);
            {{startAt}}
                    for (; Unsafe.IsAddressLessThan(ref idp, ref end); idp = ref Unsafe.Add(ref idp, 1), {{advance}})
                    {
                        int id = idp.Id;
                        if (id < 0) continue; // tombstone

            {{StampRow(n, "            ", "(int)(Unsafe.ByteOffset(ref slots, ref idp) / Unsafe.SizeOf<EntitySlot>())")}}
                        {{execute}}

                        if (seen != StorageVersion)
                        {
                            _stop = (long)id << 32 | (uint)(Unsafe.ByteOffset(ref idp, ref end) / Unsafe.SizeOf<EntitySlot>() - 1);
                            return job;
                        }
                    }

                    _stop = -1;
                    return job;
                }

                /// <summary>
                /// The rows from <paramref name="start"/> of a pass that may make structural changes, testing presence
                /// bits: words that pass whole join one run through the lean loop, the passing rows of any other word run
                /// one by one (<c>{{name}}Bits</c>), a word of failing rows costs one load. Returns like
                /// <c>{{name}}Live</c>.
                /// </summary>
                [MethodImpl(MethodImplOptions.NoInlining)] // per table: kept out of the caller like the lean loop
                private TJob {{name}}LiveTested<{{tps}}, TJob>(TJob job, Archetype archetype, RowFilter rows, int start, int count, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                {
                    // Only finds the rows: the job runs in the two row loops, each a small method of its own that the
                    // JIT inlines the job into with registers to spare (a loop of whole words would pay a call per short
                    // run of a word with failing rows, a loop by bit would walk a full word bit by bit).
                    int words = (count + 63) >> 6;
                    int run = -1; // the first row of the whole words not run yet
                    ulong from = ulong.MaxValue << start; // the first word's rows before start fail (the shift is mod 64)
                    for (int w = start >> 6; w < words; w++, from = ulong.MaxValue)
                    {
                        ulong pass = rows.Word(archetype, w) & from;
                        if (pass == from && (w + 1) << 6 <= count)
                        {
                            if (run < 0) run = (w << 6) + BitOperations.TrailingZeroCount(from);
                            // At most 4 words ahead: a structural change ends the run, and the pass then comes back here
                            // and finds its words again (unbounded, a table of passing rows would be scanned per change).
                            if ((w + 1) - (run >> 6) < 4) continue;
                            job = {{name}}Live<{{tps}}, TJob>(job, archetype, run, (w + 1) << 6, {{bits}});
                            if (Stopped((w + 1) << 6, count)) return job;
                            run = -1;
                            continue;
                        }

                        if (run >= 0)
                        {
                            job = {{name}}Live<{{tps}}, TJob>(job, archetype, run, w << 6, {{bits}});
                            if (Stopped(w << 6, count)) return job;
                            run = -1;
                        }

                        pass &= RowFilter.RowMask(w, count);
                        if (pass == 0) continue;
                        job = {{name}}Bits<{{tps}}, TJob>(job, archetype, w, pass, count, {{bits}});
                        if (_stop >= 0) return job;
                    }

                    if (run >= 0)
                    {
                        job = {{name}}Live<{{tps}}, TJob>(job, archetype, run, words << 6, {{bits}}); // whole words: <= count
                        if (Stopped(words << 6, count)) return job;
                    }

                    _stop = -1;
                    return job;
                }

                /// <summary>
                /// The rows of word <paramref name="w"/> set in <paramref name="pass"/>, one by one, in a pass that may make
                /// structural changes. Returns like <c>{{name}}Live</c> (rows left counted to <paramref name="count"/>).
                /// </summary>
                [MethodImpl(MethodImplOptions.NoInlining)] // per word: the row comes from the bit, no ref per column stays live
                private TJob {{name}}Bits<{{tps}}, TJob>(TJob job, Archetype archetype, int w, ulong pass, int count, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                {
                    int seen = StorageVersion;
                    ref EntitySlot slots = ref MemoryMarshal.GetArrayDataReference(archetype.Slots);
            {{Lines(n, i => $"            ref T{i} c{i} = ref archetype.Base<T{i}>(b{i});")}}
                    for (; pass != 0; pass &= pass - 1)
                    {
                        int row = (w << 6) + BitOperations.TrailingZeroCount(pass);
                        ref EntitySlot idp = ref Unsafe.Add(ref slots, row);
                        int id = idp.Id;
                        if (id < 0) continue; // tombstone
            {{Lines(n, i => $"            ref T{i} r{i} = ref Unsafe.Add(ref c{i}, row);")}}

            {{StampRow(n, "        ", "row")}}
                        {{execute}}

                        if (seen != StorageVersion)
                        {
                            _stop = (long)id << 32 | (uint)(count - 1 - row);
                            return job;
                        }
                    }

                    _stop = -1;
                    return job;
                }

                /// <summary>The pass of a job that makes no structural change, inside the frozen gate.</summary>
                [MethodImpl(MethodImplOptions.NoInlining)] // the pass's loop, apart from the gate's try/finally
                private TJob {{name}}Stable<{{tps}}, TJob>(TJob job, scoped in QueryFilter filter)
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                {
                    var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                    var archetypes = match.Archetypes;
                    // The job cannot change rows or reallocate storage. The row
                    // loops live in small methods of their own, so the JIT keeps their refs in registers. They take the
                    // job by value and return it: its fields are locals there (registers, not reloaded every row), and
                    // a job of up to 16 bytes travels in registers, so no pointer back to it stays live across the
                    // loop. A job that calls out on some rows (a write barrier, a structural change) clobbers the
                    // caller-saved registers, and with one more value held across the call a row ref would spill and
                    // round-trip the stack every row.
                    for (int ai = 0; ai < archetypes.Length; ai++)
                    {
                        var archetype = archetypes[ai];
                        var rows = new RowFilter(match.Filters[ai], filter.Tick);
                        int count = archetype.Count;
                        int mode = rows.Mode(archetype);
                        if (archetype.AliveCount == 0 || mode == RowFilter.None) continue;
                        if (mode == RowFilter.Test)
                            job = {{name}}Filtered<{{tps}}, TJob>(job, archetype, rows, 0, count, {{bits}});
                        else if (count == archetype.AliveCount)
                            job = {{name}}Rows<{{tps}}, TJob>(job, archetype, 0, count, {{bits}}); // dense table
                        else // tombstones: runs of alive rows, found with vectorized scans
                            for (int start = 0; archetype.NextRun(ref start, count, out int length); start += length)
                                job = {{name}}Rows<{{tps}}, TJob>(job, archetype, start, start + length, {{bits}});
                    }

                    return job;
                }

                /// <summary>Rows [<paramref name="start"/>, <paramref name="end"/>) of a stable pass: every row is alive.</summary>
                [MethodImpl(MethodImplOptions.NoInlining)] // per table or run: kept out of the caller, which profile-guided
                // inlining would otherwise grow until the loop's refs spill to the stack
                private TJob {{name}}Rows<{{tps}}, TJob>(TJob job, Archetype archetype, int start, int end, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                {
                    ref EntitySlot ids = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(archetype.Slots), start);
            {{startAt}}
            {{StampRows(n, "        ", "start", "end - start")}}
            {{(n >= 3 ? IndexedRows(n, rowArgIndexed) : "")}}
                    {
                        // The first column's end bounds the loop: no counter to step besides the refs.
                        ref T1 stop = ref Unsafe.Add(ref r1, end - start);
                        for (; Unsafe.IsAddressLessThan(ref r1, ref stop); ids = ref Unsafe.Add(ref ids, 1), {{advance}})
                            job.Execute({{Args(rowArg, List(n, i => $"ref r{i}"))}});
                    }

                    return job;
                }

                /// <summary>
                /// A stable pass over rows [<paramref name="start"/>, <paramref name="count"/>) of a table with rows lacking a
                /// queried component (<paramref name="start"/> is a multiple of 64): the refs walk every row like a dense
                /// table, 64 presence bits per load decide which rows run (a word with none skips 64 rows).
                /// </summary>
                [MethodImpl(MethodImplOptions.NoInlining)] // per table or run: kept out of the caller, which profile-guided
                // inlining would otherwise grow until the loop's refs spill to the stack
                private TJob {{name}}Filtered<{{tps}}, TJob>(TJob job, Archetype archetype, RowFilter rows, int start, int count, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                {
                    bool tombstones = archetype.Count != archetype.AliveCount;
                    ref EntitySlot ids = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(archetype.Slots), start);
            {{startAt}}
                    int words = (count + 63) >> 6;
                    for (int w = start >> 6; w < words; w++)
                    {
                        int next = rows.NextWord(archetype, w, words);
                        if (next != w)
                        {
                            // Whole words without a passing row (unwritten rows of a Changed filter): skip them.
                            int skip = (next - w) << 6;
                            w = next;
                            if (w >= words) break;
                            ids = ref Unsafe.Add(ref ids, skip);
            {{Lines(n, i => $"                    r{i} = ref Unsafe.Add(ref r{i}, skip);")}}
                        }

                        int left = Math.Min(64, count - (w << 6));
                        // Runs of passing rows (a word that passes whole is one run; a few failing rows split a word into
                        // long runs, a sparse word costs one step per run), each walked like a dense table. The run's refs
                        // are locals of the run, so they stay in registers while the word loop's state waits on the stack.
                        ulong bits = rows.Word(archetype, w) & RowFilter.RowMask(w, count);
                        if (tombstones) bits &= AliveRows(ref ids, left); // rare: the run loops then test nothing
            {{Lines(n, i => $"                if ((TJob.Writes & {1 << (i - 1)}) != 0) archetype.StampWord<T{i}>(w, bits, _changeTick);")}}
                        while (bits != 0)
                        {
                            int k = BitOperations.TrailingZeroCount(bits);
                            // The shift fills zeros, so ~ stops the run at the word's end at the latest.
                            int end = k + BitOperations.TrailingZeroCount(~(bits >> k));
                            bits = end >= 64 ? 0 : bits & (ulong.MaxValue << end);
                            ref EntitySlot idk = ref Unsafe.Add(ref ids, k);
            {{Lines(n, i => $"                ref T{i} q{i} = ref Unsafe.Add(ref r{i}, k);")}}
                            for (int run = end - k; run > 0; run--, idk = ref Unsafe.Add(ref idk, 1), {{List(n, i => $"q{i} = ref Unsafe.Add(ref q{i}, 1)")}})
                                job.Execute({{Args(rowArgRun, List(n, i => $"ref q{i}"))}});
                        }

                        ids = ref Unsafe.Add(ref ids, left);
            {{Lines(n, i => $"            r{i} = ref Unsafe.Add(ref r{i}, left);")}}
                    }

                    return job;
                }

                /// <summary>
                /// <see cref="{{name}}{TJob}"/> on several threads: the matching rows are cut into chunks of
                /// <see cref="ParallelChunkRows"/> rows that the calling thread and up to <see cref="WorldOptions.MaxDegreeOfParallelism"/>
                /// - 1 thread-pool threads run at the same time, each with its own copy of <paramref name="job"/> (state
                /// a job keeps in its fields is not merged back). The job must not change structure or write anything
                /// that another row's run reads (a singleton, another entity); nothing else may change the world
                /// during the call. Fewer than two chunks of rows, or a runner already busy, run on the calling thread.
                /// Allocation-free after the first call per job type.
                /// </summary>
                public void {{name}}Parallel<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default)
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, INoStructuralChanges
                {
                    var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                    var work = WorkOf<{{name}}Chunks<{{tps}}, TJob>>();
                    int chunks = work.Cut(this, match, filter.Tick, ref job, {{bits}});
                    Gate.Freeze(); // the chunks may run on several threads: a structural change throws
                    try
                    {
                        if (chunks < 2 || !TryRunChunks(work, chunks))
                            for (int i = 0; i < chunks; i++) work.Run(i);
                    }
                    finally
                    {
                        Gate.Thaw();
                    }

                    work.Release();
                }

                /// <summary>One chunk: rows [<paramref name="start"/>, <paramref name="end"/>) of a table, stable pass.</summary>
                private void {{name}}Chunk<{{tps}}, TJob>(ref TJob job, Archetype archetype, RowFilter rows, int start, int end, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    if (rows.Mode(archetype) == RowFilter.All && archetype.Count == archetype.AliveCount)
                        job = {{name}}Rows<{{tps}}, TJob>(job, archetype, start, end, {{bits}});
                    else
                        job = {{name}}Filtered<{{tps}}, TJob>(job, archetype, rows, start, end, {{bits}});
                }

                /// <summary>The chunks of one <see cref="{{name}}Parallel{TJob}"/> call: (table, first row) pairs.</summary>
                private sealed class {{name}}Chunks<{{tps}}, TJob> : ParallelWork
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    public TJob Job;
                    private World _world;
                    private Archetype[] _tables = [];
                    private RowFilter[] _filters = [];
                    private int[] _starts = [];
            {{Lines(n, i => $"            private ushort _b{i};")}}

                    /// <summary>Cut the matching rows into chunks; returns how many.</summary>
                    public int Cut(World world, QueryMatch match, ulong since, ref TJob job, {{ushorts}})
                    {
                        _world = world;
                        Job = job;
            {{Lines(n, i => $"            _b{i} = b{i};")}}
                        int chunks = 0;
                        var archetypes = match.Archetypes;
                        for (int a = 0; a < archetypes.Length; a++)
                        {
                            var archetype = archetypes[a];
                            var rows = new RowFilter(match.Filters[a], since);
                            if (archetype.AliveCount == 0 || rows.Mode(archetype) == RowFilter.None) continue;
                            for (int start = 0; start < archetype.Count; start += ParallelChunkRows)
                            {
                                if (chunks == _starts.Length)
                                {
                                    int size = Math.Max(8, chunks * 2);
                                    Array.Resize(ref _tables, size);
                                    Array.Resize(ref _filters, size);
                                    Array.Resize(ref _starts, size);
                                }

                                _tables[chunks] = archetype;
                                _filters[chunks] = rows;
                                _starts[chunks++] = start;
                            }
                        }

                        return chunks;
                    }

                    public override void Run(int index)
                    {
                        var archetype = _tables[index];
                        int start = _starts[index];
                        var job = Job;
                        _world.{{name}}Chunk<{{tps}}, TJob>(ref job, archetype, _filters[index], start, Math.Min(start + ParallelChunkRows, archetype.Count), {{List(n, i => $"_b{i}")}});
                    }

                    /// <summary>Drop the references of the last call (tables, filters, the job's own) until the next one.</summary>
                    public void Release()
                    {
                        Array.Clear(_tables);
                        Array.Clear(_filters);
                        Job = default;
                        _world = null;
                    }
                }

            """);
    }

    private static string QueryItems()
    {
        var sb = new StringBuilder(Header).Append("""
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;

            namespace EasyEcs;


            """);

        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            sb.Append($$"""
                /// <summary>
                /// One query result: the entity and refs into its row. Like every ref into ECS storage, the refs are valid
                /// until the next structural change; re-read through <see cref="Entity"/> after one.
                /// </summary>
                public readonly ref struct QueryItem<{{tps}}>
                {{Constraints(n, "    ")}}
                {
                    private readonly World _world;
                    private readonly int _id;
                    private readonly ref int _version;
                {{Lines(n, i => $"    private readonly ref T{i} _c{i};")}}

                    internal QueryItem(World world, int id, ref int version, {{List(n, i => $"ref T{i} c{i}")}})
                    {
                        _world = world;
                        _id = id;
                        _version = ref version;
                {{Lines(n, i => $"        _c{i} = ref c{i};")}}
                    }

                    /// <summary>Handle of the current entity.</summary>
                    public Entity Entity
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        get => new(_world, _id, _version);
                    }

                {{Lines(n, i => $$"""
                    public ref T{{i}} Component{{i}}
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        get => ref _c{{i}};
                    }

                """)}}
                }

                /// <summary>
                /// Allocation-free query enumerator; safe under structural changes during iteration (same visiting rules as
                /// ForEach). A ref struct: it holds refs into the current table, so a loop over it cannot <c>await</c>.
                /// </summary>
                /// <remarks>
                /// Every member the loop calls is inlined and the cold work is static (<see cref="QueryWalk"/>, by value
                /// in and out): the enumerator is never passed by reference, so its fields live in registers.
                /// </remarks>
                public ref struct QueryEnumerator<{{tps}}>
                {{Constraints(n, "    ")}}
                {
                    private readonly World _world;
                    private readonly QueryMatch _match;
                    private readonly ulong _since;
                {{Lines(n, i => $"    private readonly ushort _b{i};")}}
                    private QueryWalk _walk;
                    private int _ticket; // 0: not started; QueryWalk.Ended: ended
                    private ref EntitySlot _rows;
                {{Lines(n, i => $"    private ref T{i} _r{i};")}}
                    private int _id, _storage;
                    // Native-sized: indexing needs no sign extension (movsxd on x64, sbfiz/sxtw on arm64) per row.
                    private nint _slot;
                    private nint _count; // rows of the current table to walk; negative: rows to test (QueryWalk.NextTested)

                    internal QueryEnumerator(World world, QueryMatch match, ulong since, {{List(n, i => $"ushort b{i}")}})
                    {
                        _world = world;
                        _match = match;
                        _since = since;
                {{Lines(n, i => $"        _b{i} = b{i};")}}
                    }

                    public readonly QueryItem<{{tps}}> Current
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        get
                        {
                            nint slot = _slot;
                            // Version is the row's first field: the row's address is the version's.
                            return new(_world, _id, ref Unsafe.As<EntitySlot, int>(ref Unsafe.Add(ref _rows, slot)), {{List(n, i => $"ref Unsafe.Add(ref _r{i}, slot)")}});
                        }
                    }

                    public readonly QueryEnumerator<{{tps}}> GetEnumerator() => this;

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool MoveNext()
                    {
                        if (_storage == _world.StorageVersion) // no structural change since the last row
                        {
                            if (_count >= 0)
                            {
                                while (++_slot < _count)
                                {
                                    int id = Unsafe.Add(ref _rows, _slot).Id; // _slot < _count <= rows
                                    if (id >= 0)
                                    {
                                        _id = id;
                                        goto Found;
                                    }
                                }
                            }
                            else
                            {
                                int slot = _walk.NextTested((int)_slot);
                                if (slot >= 0)
                                {
                                    _slot = slot;
                                    _id = Unsafe.Add(ref _rows, slot).Id;
                                    goto Found;
                                }
                            }
                        }

                        // Another table, a structural change, the start or the end: the walk's cold path.
                        var step = QueryWalk.Step(_walk, _ticket, _world, _match, _since, (int)_slot, _storage);
                        _ticket = step.Ticket;
                        _walk = step.Walk;
                        _rows = ref step.Rows;
                        _slot = step.Slot;
                        _count = step.Count;
                        _storage = step.Storage;
                        var table = step.Table;
                        if (table == null) return false;
                        _id = Unsafe.Add(ref _rows, _slot).Id;
                {{Lines(n, i => $"        _r{i} = ref table.Base<T{i}>(_b{i});")}}

                    Found:
                        // Items hand out refs: a visited row of a tracked component counts as written (a JIT-time
                        // constant: no code for queries without tracked components).
                        if ({{List(n, i => $"default(T{i}) is ITrackChanges", " || ")}}) Stamp(_walk, (int)_slot);
                        return true;
                    }

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    private static void Stamp(QueryWalk walk, int slot)
                    {
                        var archetype = walk.Current;
                        ulong tick = walk.World.ChangeTick;
                        // Rows come in order: the summaries of a word are noted once per word and tick.
                        if (slot >> 6 != walk.StampWord || tick != walk.StampTick)
                        {
                            walk.StampWord = slot >> 6;
                            walk.StampTick = tick;
                {{Lines(n, i => $"            archetype.NoteVisited<T{i}>(slot, tick);")}}
                        }

                {{Lines(n, i => $"        archetype.StampTick<T{i}>(slot, tick);")}}
                    }

                    /// <summary>Ends the walk early (<c>foreach</c> calls it); a walk that reaches the end ends by itself.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public void Dispose()
                    {
                        if (_ticket > 0) QueryWalk.End(_walk, _ticket);
                        _ticket = QueryWalk.Ended;
                    }
                }


                """);
        }

        return sb.ToString();
    }
}
