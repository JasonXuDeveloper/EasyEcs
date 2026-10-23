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
            spc.AddSource("Filters.g.cs", SourceText.From(Filters(), Encoding.UTF8));
        });
    }

    // ---------------- ChangedAny / RemovedAny ----------------

    /// <summary>
    /// The "any of these" change filters, 2..9 components: <c>QueryFilter.ChangedAny&lt;T1..Tn&gt;(since)</c> /
    /// <c>RemovedAny</c> and the <c>[ChangedAny&lt;T1..Tn&gt;]</c> / <c>[RemovedAny&lt;T1..Tn&gt;]</c> attributes of
    /// [ForEach] methods (one component is <c>Changed</c> / <c>Removed</c>).
    /// </summary>
    private static string Filters()
    {
        var filter = new StringBuilder(Header).Append(
            "using System;\n\nnamespace EasyEcs;\n\npublic readonly partial struct QueryFilter\n{\n");
        var attributes = new StringBuilder();
        for (int n = 2; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            string cons = Lines(n, i => $"        where T{i} : struct, IComponent, ITracked");
            string bits = List(n, i => $"ComponentType<T{i}>.Bit");
            filter.Append($$"""
                    /// <summary>
                    /// Only rows where at least one of <c>{{tps}}</c> was written at or after change tick
                    /// <paramref name="since"/>: <c>Changed&lt;T1&gt;(since)</c> OR ... OR <c>Changed&lt;T{{n}}&gt;(since)</c>
                    /// (a row has the component it counts for; the others need not be there). Every <c>ChangedAny</c> call
                    /// of a filter joins one set; the set, the <c>Changed</c> and <c>Removed</c> filters, a
                    /// <c>RemovedAny</c> set and the other filters combine with AND. A table none of whose columns was
                    /// written since the tick is skipped whole, and words and blocks of rows no column wrote. A filter has
                    /// one tick (<see cref="Since"/> moves it): keep one per client, run it with each client's tick.
                    /// </summary>
                    /// <exception cref="ArgumentException">An earlier change filter named another tick.</exception>
                    public QueryFilter ChangedAny<{{tps}}>(ulong since)
                {{cons}}
                        => AddingAny(false, since, [{{bits}}]);

                    /// <summary>
                    /// Only rows that lost at least one of <c>{{tps}}</c> at or after change tick <paramref name="since"/>
                    /// and have not got it back: <c>Removed&lt;T1&gt;(since)</c> OR ... OR <c>Removed&lt;T{{n}}&gt;(since)</c>.
                    /// Every <c>RemovedAny</c> call joins one set, combined with the other filters with AND; one tick
                    /// per filter, as <see cref="ChangedAny{{{tps}}}(ulong)"/>.
                    /// </summary>
                    /// <exception cref="ArgumentException">An earlier change filter named another tick.</exception>
                    public QueryFilter RemovedAny<{{tps}}>(ulong since)
                {{cons}}
                        => AddingAny(true, since, [{{bits}}]);


                """);
            string attributeCons = Lines(n, i => $"    where T{i} : struct, IComponent, ITracked");
            attributes.Append($$"""
                /// <summary>
                /// The <see cref="ForEachAttribute"/> method visits only entities where at least one of <c>{{tps}}</c> was
                /// written since its system last ran (<see cref="EcsSystem.LastRunTick"/>; on the first run, every entity
                /// having one): <see cref="QueryFilter.ChangedAny{{{tps}}}(ulong)"/>. Several attributes join one set;
                /// combined with <c>[Changed&lt;T&gt;]</c> and the other filters with AND.
                /// </summary>
                [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
                public sealed class ChangedAnyAttribute<{{tps}}> : Attribute
                {{attributeCons}};

                /// <summary>
                /// The <see cref="ForEachAttribute"/> method visits only entities that lost at least one of <c>{{tps}}</c>
                /// since its system last ran and have not got it back: <see cref="QueryFilter.RemovedAny{{{tps}}}(ulong)"/>.
                /// Several attributes join one set; combined with the other filters with AND.
                /// </summary>
                [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
                public sealed class RemovedAnyAttribute<{{tps}}> : Attribute
                {{attributeCons}};


                """);
        }

        return filter.Append("}\n\n").Append(attributes).ToString();
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
                    /// for chaining. O({{n}}) amortized. Owner thread only (see docs/content/guide/threading.md).
                    /// </summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public Entity Set<{{tps}}>({{values}})
                {{cons}}
                    {
                        SetCore(_world, Index, Version, {{List(n, i => $"in c{i}")}});
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
                {{(n == 1 ? "            world.SetRow(ref meta, id, version, in c1);" : SetRows(n, signature))}}
                        }
                    }

                    /// <summary>
                    /// Remove {{what}}; missing components are ignored. The entity keeps its row (no move). Returns the entity
                    /// for chaining. O({{n}}). Owner thread only (see docs/content/guide/threading.md).
                    /// </summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public Entity Remove<{{tps}}>()
                {{cons}}
                    {
                        RemoveCore<{{tps}}>(_world, Index, Version);
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
                            {{(n == 1 ? "world.RemoveBit<T1>(ref meta);"
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
                    /// Pre-size the table of entities with exactly <c>{{tps}}</c> and the entity storage, so the next
                    /// <paramref name="count"/> <c>CreateEntity</c> / <c>CreateEntities</c> calls with these components never grow
                    /// them: a start-up or a wave of spawns pays its growth once, before the loop. Entities whose groups put them
                    /// in another table (<see cref="GroupBy{T}"/>) grow that one; components added later move entities to other
                    /// tables, which grow on their own. O({{n}}) plus one growth. Owner thread only (see docs/content/guide/threading.md).
                    /// </summary>
                    public void Reserve<{{tps}}>(int count)
                {{cons}}
                    {
                        using (Structural())
                        {
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            var target = ({{(n == 1 ? "_emptyArchetype.GetAddEdge(b1) ?? CreateEdge(_emptyArchetype, b1)"
                                : $"_emptyArchetype.GetAddSetEdge({signature}) ?? CreateSetEdge(_emptyArchetype, [{List(n, i => $"b{i}")}], {signature})")}}).Target;
                            if (count <= 0) return;
                            ReserveIds(count);
                            target.Reserve(target.Count + count);
                            ReserveBeside(target, count);
                            StorageVersion++; // its columns may have moved: a pass in progress refreshes its refs
                        }
                    }

                    /// <summary>
                    /// Create an entity with {{what}} placed directly in its final archetype (no intermediate moves).
                    /// O({{n}}) amortized. Owner thread only (see docs/content/guide/threading.md).
                    /// </summary>
                    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the loops that call it lean
                    public Entity CreateEntity<{{tps}}>({{values}})
                {{cons}}
                    {
                        using (Structural())
                        {
                {{Lines(n, i => $"            Claim(-1, in c{i});")}}
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            // Straight into the final archetype through an edge cached on the empty archetype (no mask hashing).
                            var target = ({{(n == 1 ? "_emptyArchetype.GetAddEdge(b1) ?? CreateEdge(_emptyArchetype, b1)"
                                : $"_emptyArchetype.GetAddSetEdge({signature}) ?? CreateSetEdge(_emptyArchetype, [{List(n, i => $"b{i}")}], {signature})")}}).Target;
                {{Lines(n, i => $"            target = GroupTarget(target, b{i}, in c{i});")}}
                            int id = AllocateId(); // after the tables: creating one may throw (a unique component without an index)
                            int slot = Place(id, target);
                {{Lines(n, i => $"            Unsafe.Add(ref target.Base<T{i}>(b{i}), slot) = c{i};")}}
                {{Lines(n, i => $"            target.Stamp<T{i}>(slot, _changeTick);")}}
                            int version = Meta[id].Version;
                {{Lines(n, i => $"            Written(id, version, in c{i});")}}
                            return new Entity(this, id, version);
                        }
                    }

                    /// <summary>
                    /// <see cref="Instantiate(Prefab)"/> with {{what}} given as well: a copy of every component of
                    /// <paramref name="prefab"/>, then the given value{{(n == 1 ? "" : "s")}} (replacing the prefab's, a disabled one enabled
                    /// as <c>Set</c> does), the entity placed directly in the table of both (no intermediate moves, one row
                    /// copy). Tracked components count as written now, indexes and groups learn the values, a given
                    /// <see cref="ChildOf"/> links the hierarchy. O(components) amortized, allocation-free once the table exists.
                    /// A prefab with members creates them all and sets the components on the entity of the one passed after
                    /// (one row move for it). Owner thread only (see docs/content/guide/threading.md).
                    /// </summary>
                    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the loops that call it lean
                    public Entity Instantiate<{{tps}}>(Prefab prefab, {{values}})
                {{cons}}
                    {
                        if (prefab.IsGroup) return InstantiateGroup(prefab).Set({{List(n, i => $"in c{i}")}});
                        using (Structural())
                        {
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            var target = PrefabTable(prefab, {{(n == 1 ? "TypeIndex<ComponentSignature, T1>.Value" : signature)}}, [{{List(n, i => $"b{i}")}}], out var source, out int from);
                            ClaimPrefab(source, from, 1, [{{List(n, i => $"b{i}")}}]); // before anything changes
                {{Lines(n, i => $"            ClaimAll(1, in c{i});")}}
                {{Lines(n, i => $"            target = GroupTarget(target, b{i}, in c{i});")}}
                            int id = AllocateId();
                            int slot = Place(id, target);
                            target.CloneRows(source, from, slot, 1, _changeTick);
                {{Lines(n, i => $"            Unsafe.Add(ref target.Base<T{i}>(b{i}), slot) = c{i};")}}
                {{Lines(n, i => $"            target.Stamp<T{i}>(slot, _changeTick);")}}
                            int version = MetaRef(id).Version;
                            Instantiated(target, slot, 1, [{{List(n, i => $"b{i}")}}]);
                            return new Entity(this, id, version);
                        }
                    }

                    /// <summary>
                    /// <see cref="Instantiate(Prefab, Span{Entity})"/> with {{what}} given as well (the same value{{(n == 1 ? "" : "s")}} for each
                    /// entity), as the single-entity overload does for one: the entities placed directly in their final table,
                    /// their handles written to <paramref name="entities"/>. One pass through the structural gate and at most one
                    /// table growth: O(count × components). Owner thread only.
                    /// </summary>
                    public void Instantiate<{{tps}}>(Prefab prefab, Span<Entity> entities, {{values}})
                {{cons}}
                    {
                        if (prefab.IsGroup)
                        {
                            for (int i = 0; i < entities.Length; i++) entities[i] = InstantiateGroup(prefab).Set({{List(n, i => $"in c{i}")}});
                            return;
                        }

                        using (Structural())
                        {
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            var target = PrefabTable(prefab, {{(n == 1 ? "TypeIndex<ComponentSignature, T1>.Value" : signature)}}, [{{List(n, i => $"b{i}")}}], out var source, out int from);
                            if (entities.IsEmpty) return;
                            ClaimPrefab(source, from, entities.Length, [{{List(n, i => $"b{i}")}}]); // before anything changes
                {{Lines(n, i => $"            ClaimAll(entities.Length, in c{i});")}}
                {{Lines(n, i => $"            target = GroupTarget(target, b{i}, in c{i});")}}
                            int first = PlaceRows(target, entities); // consecutive rows: each column is filled at once
                            target.CloneRows(source, from, first, entities.Length, _changeTick);
                {{Lines(n, i => $"            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref target.Base<T{i}>(b{i}), first), entities.Length).Fill(c{i});")}}
                {{Lines(n, i => $"            target.StampRows<T{i}>(first, entities.Length, _changeTick);")}}
                            Instantiated(target, first, entities.Length, [{{List(n, i => $"b{i}")}}]);
                        }
                    }

                    /// <summary>
                    /// Create <c>entities.Length</c> entities with {{what}} (the same value{{(n == 1 ? "" : "s")}} for each), placed directly in
                    /// their final archetype, and write their handles to <paramref name="entities"/>. One pass through the
                    /// structural gate and at most one table growth: O(count × {{n}}). Owner thread only (see docs/content/guide/threading.md).
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
                {{Lines(n, i => $"            ClaimAll(entities.Length, in c{i});")}}
                {{Lines(n, i => $"            target = GroupTarget(target, b{i}, in c{i});")}}
                            int first = PlaceRows(target, entities); // consecutive rows: each column is filled at once
                {{Lines(n, i => $"            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref target.Base<T{i}>(b{i}), first), entities.Length).Fill(c{i});")}}
                {{Lines(n, i => $"            target.StampRows<T{i}>(first, entities.Length, _changeTick);")}}
                {{Lines(n, i => $"            if (ComponentType<T{i}>.Indexed) WrittenAll(entities, in c{i});")}}
                        }
                    }


                """);
        }

        return entity.Append("}\n\n").Append(world).Append("}\n\n").Append(prefab).Append("}\n").ToString();
    }

    // ---------------- IForEach / ForEach / Query / Count / Any ----------------

    private const string JobMasks = """
            /// <summary>
            /// Components the job writes through its refs: bit k - 1 for component k (default: all). When the job changes
            /// the archetype of the entity it visits, ForEach carries those components over from the row the refs point
            /// into to the entity's new row after the job returns. Generated jobs set only their <c>ref</c> parameters.
            /// </summary>
            static virtual int WriteMask => -1;

            /// <summary>
            /// Components among <see cref="WriteMask"/> that the job stamps itself, only in the rows it writes (bits like
            /// <see cref="WriteMask"/>; default none): the loop leaves their rows unstamped. Generated jobs set their
            /// <see cref="Mut{T}"/> parameters.
            /// </summary>
            static virtual int SelfStamped => 0;

            /// <summary>
            /// Tracked components the job writes besides its own (through a <see cref="Maybe{T}"/>; default none): a
            /// parallel pass opens their change-tick blocks with those of <see cref="WriteMask"/>, so chunks splitting a
            /// block share it. Generated parallel jobs set their <see cref="Maybe{T}"/> writes.
            /// </summary>
            static virtual ComponentSet OptionalWrites => default;

            /// <summary>
            /// The job keeps partial results in its fields (<see cref="Reduce{T}"/>; default false): a parallel pass keeps
            /// each chunk's copy of the job and combines them into the caller's job in chunk order (<see cref="Merge{TSelf}"/>).
            /// </summary>
            static virtual bool Merges => false;

            /// <summary>
            /// Combine the partial results <paramref name="chunk"/> holds into <paramref name="job"/> (both of the job's
            /// own type; <see cref="Merges"/>). Generated jobs with <see cref="Reduce{T}"/> parameters implement it.
            /// </summary>
            static virtual void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { }
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
                    /// <summary>One matching entity: its components, by reference (<c>ref</c> writes them in place).</summary>
                    void Execute({{List(n, i => $"ref T{i} c{i}")}});

                {{JobMasks}}
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

                {{JobMasks}}
                }

                /// <summary>
                /// A batch job that also takes a word of 64 rows of which only some pass (a filter that tests presence bits
                /// or change ticks): <c>World.ForEachMasked</c> hands it the whole word with the passing rows as bits, where
                /// <see cref="IForEachBatch{{{TypeParams(n)}}}"/> would get each run of passing rows apart. A vector loop
                /// computes every lane and keeps the passing ones. <c>[ForEach]</c> methods with a vector loop compile to this.
                /// </summary>
                public interface IForEachMasked<{{TypeParams(n)}}> : IForEachBatch<{{TypeParams(n)}}>
                {{Constraints(n, "    ")}}
                {
                    /// <summary>
                    /// Rows of one word: row i (of <paramref name="entities"/> and of every span) passes when bit i of
                    /// <paramref name="rows"/> is set; the job leaves the other rows as they are (it may store their values
                    /// back unchanged). Bit 0 is set, and no bit at or past the spans' length.
                    /// </summary>
                    void Execute(EntitySpan entities, ulong rows, {{List(n, i => $"Span<T{i}> c{i}")}});
                }

                /// <summary>
                /// Like <see cref="IForEach{{{TypeParams(n)}}}"/> but also receives the <see cref="Entity"/> handle, built from the
                /// archetype's entity rows (id and version side by side) without a per-entity lookup. <c>[ForEach]</c> methods taking an
                /// <see cref="Entity"/> compile to this.
                /// </summary>
                public interface IForEachEntity<{{TypeParams(n)}}>
                {{Constraints(n, "    ")}}
                {
                    /// <summary>One matching entity: its handle and its components, by reference.</summary>
                    void Execute(Entity entity, {{List(n, i => $"ref T{i} c{i}")}});

                {{JobMasks}}
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
                    internal QueryMatch Match<{{tps}}>(in QueryFilter filter, {{List(n, i => $"out ushort b{i}")}})
                {{cons}}
                    {
                {{Lines(n, i => $"        b{i} = ComponentType<T{i}>.Bit;")}}
                        return GetMatchingArchetypes(TypeIndex<ComponentSignature, {{(n == 1 ? "T1" : $"({tps})")}}>.Value, [{{bits}}], in filter);
                    }

                {{ForEach(n, "ForEach", "IForEach", "", "", "the components")}}
                {{ForEach(n, "ForEachEntity", "IForEachEntity", "new Entity(this, idp)", "new Entity(this, ids)", "the entity handle")}}
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
                        // The job holds spans into the tables: a structural change throws. The owner's plain freeze while
                        // nothing holds the gate (two locked instructions per pass otherwise); a held gate throws anyway.
                        bool froze = Gate.FreezeAlone();
                        try
                        {
                            Batches<{{tps}}, TJob>(ref job, in filter);
                        }
                        finally
                        {
                            if (froze) Gate.ThawAlone();
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
                            var (first, count) = filter.Rows(ai, archetype.Count); // all rows, or a slice's
                            if (archetype.AliveCount == 0 || rows.Mode(archetype) == RowFilter.None || first >= count) continue;
                            ForEachBatchChunk<{{tps}}, TJob>(ref job, archetype, rows, first, count, {{bits}});
                        }
                    }

                    /// <summary>
                    /// <see cref="ForEachBatch{{"{"}}{{tps}}, TJob{{"}"}}"/> for a job that also takes a word of rows of
                    /// which only some pass: in a table whose rows must be tested, each word whose rows all pass joins
                    /// the run of the words before it, and each word with some passing rows goes to the job whole with
                    /// those rows as bits. Allocation-free.
                    /// </summary>
                    public void ForEachMasked<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default)
                {{cons}}
                        where TJob : struct, IForEachMasked<{{tps}}>, allows ref struct
                    {
                        bool froze = Gate.FreezeAlone(); // as ForEachBatch
                        try
                        {
                            Masked<{{tps}}, TJob>(ref job, in filter);
                        }
                        finally
                        {
                            if (froze) Gate.ThawAlone();
                        }
                    }

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    private void Masked<{{tps}}, TJob>(ref TJob job, in QueryFilter filter)
                {{cons}}
                        where TJob : struct, IForEachMasked<{{tps}}>, allows ref struct
                    {
                        var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        var archetypes = match.Archetypes;
                        for (int ai = 0; ai < archetypes.Length; ai++)
                        {
                            var archetype = archetypes[ai];
                            var rows = new RowFilter(match.Filters[ai], filter.Tick);
                            var (first, count) = filter.Rows(ai, archetype.Count);
                            int mode = rows.Mode(archetype);
                            if (archetype.AliveCount == 0 || mode == RowFilter.None || first >= count) continue;
                            ref EntityId slots = ref MemoryMarshal.GetArrayDataReference(archetype.Slots);
                {{Lines(n, i => $"            ref T{i} r{i} = ref archetype.Base<T{i}>(b{i});")}}
                            if (mode == RowFilter.Test)
                            {
                                // Word by word: whole words make runs, the others go to the job with their passing rows.
                                bool tombstones = archetype.Count != archetype.AliveCount;
                                int words = (count + 63) >> 6, start = first, end = first; // the pending run: start .. end
                                for (int w = first >> 6; w < words; w++)
                                {
                                    int next = rows.NextWord(archetype, w, words); // words no Changed column reaches
                                    if (next != w)
                                    {
                                        w = next - 1;
                                        continue;
                                    }

                                    int at = Math.Max(w << 6, first);
                                    ulong range = RowFilter.RowMask(w, count) & (ulong.MaxValue << at); // shift is mod 64
                                    ulong bits = rows.Word(archetype, w) & range;
                                    if (tombstones && bits != 0) bits &= AliveRows(ref Unsafe.Add(ref slots, w << 6), Math.Min(64, count - (w << 6)));
                                    if (bits == range)
                                    {
                                        if (end != at)
                                        {
                                            if (end > start)
                                            {
                {{BatchRun(n, "                            ", "start", "end - start")}}
                                            }

                                            start = at;
                                        }

                                        end = Math.Min((w + 1) << 6, count);
                                        continue;
                                    }

                                    if (bits == 0)
                                    {
                                        w = rows.NextPassing(archetype, w + 1, words) - 1; // words without a passing row
                                        continue;
                                    }

                                    int low = (w << 6) + BitOperations.TrailingZeroCount(bits), high = (w << 6) + 64 - BitOperations.LeadingZeroCount(bits);
                                    ulong passing = bits >> (low & 63);
                {{StampWord(n, "                    ", "w", "bits")}}
                                    job.Execute(new EntitySpan(this, archetype, low, ref Unsafe.Add(ref slots, low), high - low), passing, {{List(n, i => $"MemoryMarshal.CreateSpan(ref Unsafe.Add(ref r{i}, low), high - low)")}});
                                }

                                if (end > start)
                                {
                {{BatchRun(n, "                    ", "start", "end - start")}}
                                }

                                continue;
                            }

                            ForEachBatchChunk<{{tps}}, TJob>(ref job, archetype, rows, first, count, {{bits}}); // every row passes: runs
                        }
                    }

                {{ParallelLoop(n, "ForEachBatch", "IForEachBatch")}}
                    /// <summary>
                    /// The runs of rows [<paramref name="start"/>, <paramref name="end"/>) of a table, a nonempty range of a table
                    /// some row of which may pass: a table of <c>Batches</c>, a table of <c>Masked</c> whose rows all pass, a chunk
                    /// of a parallel batch pass. A dense range is a single call.
                    /// </summary>
                    private void ForEachBatchChunk<{{tps}}, TJob>(ref TJob job, Archetype archetype, RowFilter rows, int start, int end, {{List(n, i => $"ushort b{i}")}})
                {{cons}}
                        where TJob : struct, IForEachBatch<{{tps}}>, allows ref struct
                    {
                        ref EntityId slots = ref MemoryMarshal.GetArrayDataReference(archetype.Slots);
                {{Lines(n, i => $"        ref T{i} r{i} = ref archetype.Base<T{i}>(b{i});")}}
                        if (rows.Mode(archetype) == RowFilter.Test)
                        {
                            for (int at = start; rows.NextRun(archetype, ref at, end, out int length); at += length)
                            {
                {{BatchRun(n, "                ", "at", "length")}}
                            }

                            return;
                        }

                        if (archetype.Count == archetype.AliveCount)
                        {
                {{BatchRun(n, "            ", "start", "end - start")}}
                            return;
                        }

                        for (int at = start; archetype.NextRun(ref at, end, out int length); at += length)
                        {
                {{BatchRun(n, "            ", "at", "length")}}
                        }
                    }

                    /// <summary>Enumerate entities having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>). Allocation-free.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public QueryEnumerator<{{tps}}> Query<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        filter.Whole(); // a slice is for passes: a query sees every matching row
                        var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        return new QueryEnumerator<{{tps}}>(this, match, filter.Tick, {{bits}});
                    }

                    /// <summary>
                    /// Enumerate entities having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>) to read
                    /// them: the items hand out <c>ref readonly</c> refs and stamp nothing, so a <c>[Changed&lt;T&gt;]</c>
                    /// reader does not see a row as written because it was read (<see cref="Query{{"{"}}{{tps}}{{"}"}}"/> stamps the
                    /// rows of tracked components it visits). Allocation-free.
                    /// </summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public ReadEnumerator<{{tps}}> Read<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        filter.Whole();
                        var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        return new ReadEnumerator<{{tps}}>(this, match, filter.Tick, {{bits}});
                    }

                    /// <summary>Number of alive entities having all of <c>{{tps}}</c>. O(matching archetypes), plus O(rows / 64) for tables with rows to test.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public int Count<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        filter.Whole();
                        return Match<{{tps}}>(in filter, {{List(n, i => "out _")}}).CountAlive(filter.Tick);
                    }

                    /// <summary>Whether any alive entity has all of <c>{{tps}}</c>. O(matching archetypes), plus O(rows / 64) for tables with rows to test; early exit.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool Any<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        filter.Whole();
                        return Match<{{tps}}>(in filter, {{List(n, i => "out _")}}).AnyAlive(filter.Tick);
                    }

                    /// <summary>
                    /// Destroy every alive entity having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>) and return
                    /// how many. One pass through the structural gate: O(matching archetypes + rows). Owner thread only (see docs/content/guide/threading.md).
                    /// </summary>
                    public int DestroyAll<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        filter.Whole();
                        return DestroyMatching(Match<{{tps}}>(in filter, {{List(n, i => "out _")}}), filter.Tick);
                    }

                    /// <summary>
                    /// Write the ids of the alive entities having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>)
                    /// into <paramref name="into"/>, in the order <c>Query</c> and <c>Read</c> visit them, until it is full, and
                    /// return how many: a bounded batch of work (the first requests of a tick, the next chunks to unload) to hand
                    /// to <c>DestroyEntities</c>, <c>SaveEntities</c> or a loop that changes structure, which a query loop may
                    /// not. O(matching archetypes), plus the rows read until <paramref name="into"/> is full (a table whose rows
                    /// all match is one copy). Allocation-free.
                    /// </summary>
                    public int CollectIds<{{tps}}>(Span<EntityId> into, in QueryFilter filter = default)
                {{cons}}
                    {
                        filter.Whole();
                        return Match<{{tps}}>(in filter, {{List(n, i => "out _")}}).CollectAlive(filter.Tick, into);
                    }


                """);
        }

        sb.Append("}\n\npublic readonly partial struct Entity\n{\n");
        for (int n = 1; n <= MaxArity; n++) sb.Append(RunOne(n));
        return sb.Append("}\n").ToString();
    }

    /// <summary>
    /// <c>entity.Run</c>: one entity's job, with the refs a pass gives it (a structural change made by the job moves
    /// the components it writes to the entity's new row, like <c>ForEachEntity</c>).
    /// </summary>
    private static string RunOne(int n)
    {
        string tps = TypeParams(n);
        string refs = Lines(n, i => $"        ref T{i} r{i} = ref At<T{i}>(archetype, slot);\n        if (Unsafe.IsNullRef(ref r{i})) return false;");
        string args = List(n, i => $"ref r{i}");
        string cons = Constraints(n, "        ");
        return $$"""
                /// <summary>
                /// Run <paramref name="job"/> on this entity when it has all of <c>{{tps}}</c>, with the refs
                /// <c>World.ForEachEntity</c> would give it: true when the job ran, false when the entity lacks a
                /// component or is not alive. The components the job writes (<c>IForEachEntity.WriteMask</c>) count as
                /// written; a structural change the job makes takes effect at once, and those writes are carried over to
                /// the entity's new row (also when the job throws). A job implementing <see cref="INoStructuralChanges"/>
                /// skips that bookkeeping. O(1), allocation-free.
                /// </summary>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool Run<{{tps}}, TJob>(ref TJob job)
            {{cons}}
                    where TJob : struct, IForEachEntity<{{tps}}>, allows ref struct
                {
                    var archetype = LocateArchetype(out int slot, false);
                    if (archetype == null) return false;
            {{refs}}
                    RunJob<{{tps}}, TJob>(ref job, archetype, slot, {{args}});
                    return true;
                }

                /// <summary>
                /// <see cref="Run{{{tps}}, TJob}(ref TJob)"/> when the entity also passes <paramref name="filter"/>, tested
                /// as a query tests a row (<c>Without</c>, <c>WithAny</c>, <c>Changed</c>, <c>InGroup</c>): false, without running
                /// the job, when it does not. O(components the filter names).
                /// </summary>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool Run<{{tps}}, TJob>(ref TJob job, in QueryFilter filter)
            {{cons}}
                    where TJob : struct, IForEachEntity<{{tps}}>, allows ref struct
                {
                    filter.Whole();
                    var archetype = LocateArchetype(out int slot, false);
                    if (archetype == null) return false;
                    // The filter first: it rules out an entity (a dead one) before any column is looked up.
                    if (filter.HasOverflow ? !World.RowPasses(_world.Match<{{tps}}>(in filter, {{List(n, i => "out _")}}), archetype, slot, filter.Tick) : !filter.Passes(archetype, slot))
                        return false;
            {{refs}}
                    RunJob<{{tps}}, TJob>(ref job, archetype, slot, {{args}});
                    return true;
                }

                /// <summary>The job of <c>Run</c> on row <paramref name="slot"/>, whose refs are given.</summary>
                private void RunJob<{{tps}}, TJob>(ref TJob job, Archetype archetype, int slot, {{List(n, i => $"ref T{i} r{i}")}})
            {{cons}}
                    where TJob : struct, IForEachEntity<{{tps}}>, allows ref struct
                {
                    var world = _world;
            {{Lines(n, i => $"        if ((TJob.WriteMask & ~TJob.SelfStamped & {1 << (i - 1)}) != 0) archetype.Stamp<T{i}>(slot, world.ChangeTick);")}}
                    if (typeof(INoStructuralChanges).IsAssignableFrom(typeof(TJob))) // a JIT-time constant
                    {
                        // A broken promise throws instead of moving the row under the refs. The owner's plain freeze:
                        // an atomic one would cost more than the rest of the call.
                        bool froze = world.Gate.FreezeAlone();
                        try
                        {
                            job.Execute(this, {{args}});
                        }
                        finally
                        {
                            if (froze) world.Gate.ThawAlone();
                        }

                        return;
                    }

                    // Inside a pass a structural change leaves the row where it is (a tombstone): the refs stay valid.
                    int seen = world.StorageVersion;
                    bool alone = world.BeginPassAlone();
                    try
                    {
                        job.Execute(this, {{args}});
                    }
                    finally
                    {
                        if (seen != world.StorageVersion && archetype.Slots[slot].Index < 0)
                        {
                            // The entity moved (or died): its old row is a tombstone that holds the job's writes, which
                            // go to the new row, typed (a component the job removed is not there).
                            ref var meta = ref world.MetaRef(Index);
                            if (meta.Version == Version && meta.Archetype is { } moved)
                            {
            {{Lines(n, i => $"                    if ((TJob.WriteMask & {1 << (i - 1)}) != 0) CarryTo(moved, meta.Slot, in r{i});")}}
                            }
                        }

                        world.EndPassAlone(alone);
                    }
                }


            """;
    }

    /// <summary>The body of SetCore for several components: one move adds every bit, then each value is written.</summary>
    private static string SetRows(int n, string signature) =>
        Lines(n, i => $"            world.Claim(id, in c{i});") + "\n" +
        Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;") + "\n" +
        $"            world.AddBits(ref meta, id, [{List(n, i => $"b{i}")}], {signature});\n" +
        Lines(n, i => $"            World.Row<T{i}>(ref meta, b{i}) = c{i};") + "\n" +
        Lines(n, i => $"            meta.Archetype.Stamp<T{i}>(meta.Slot, world.ChangeTick);") + "\n" +
        Lines(n, i => $"            world.Written(id, version, in c{i});");

    /// <summary>Stamp row <paramref name="row"/> of every component the job writes (only tracked ones emit code).</summary>
    private static string StampRow(int n, string indent, string row) =>
        Lines(n, i => $"{indent}    if ((TJob.WriteMask & ~TJob.SelfStamped & {1 << (i - 1)}) != 0) archetype.StampVisited<T{i}>({row}, _changeTick);");

    /// <summary>A run of <paramref name="length"/> rows from <paramref name="start"/>: stamped, then the job's batch Execute.</summary>
    private static string BatchRun(int n, string indent, string start, string length) =>
        StampRows(n, indent, start, $"({length})") + "\n" + indent
        + $"    job.Execute(new EntitySpan(this, archetype, {start}, ref Unsafe.Add(ref slots, {start}), {length}), {List(n, i => $"MemoryMarshal.CreateSpan(ref Unsafe.Add(ref r{i}, {start}), {length})")});";

    /// <summary>Stamp the rows <paramref name="rows"/> (bits) of word <paramref name="word"/> of every component the job writes.</summary>
    private static string StampWord(int n, string indent, string word, string rows) =>
        Lines(n, i => $"{indent}    if ((TJob.WriteMask & ~TJob.SelfStamped & {1 << (i - 1)}) != 0) archetype.StampWord<T{i}>({word}, {rows}, _changeTick);");

    /// <summary>Stamp a run of rows of every component the job writes.</summary>
    private static string StampRows(int n, string indent, string start, string count) =>
        Lines(n, i => $"{indent}    if ((TJob.WriteMask & ~TJob.SelfStamped & {1 << (i - 1)}) != 0) archetype.StampRows<T{i}>({start}, {count}, _changeTick);");

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
                /// writes through the refs are carried over to the entity's new row (<c>{{job}}.WriteMask</c>). A job
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
                        bool froze = Gate.FreezeAlone(); // the owner's plain freeze, as ForEachBatch
                        try
                        {
                            job = {{name}}Stable<{{tps}}, TJob>(job, in filter);
                        }
                        finally
                        {
                            if (froze) Gate.ThawAlone();
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
                    bool alone = BeginPassAlone(); // a plain count on the owner thread while nothing holds the gate
                    try // a job that throws still ends the pass, so its tombstones are closed
                    {
                        for (int ai = 0; ai < archetypes.Length; ai++)
                        {
                            var archetype = archetypes[ai];
                            var rows = new RowFilter(filters[ai], since);
                            var (first, count) = filter.Rows(ai, counts[ai]); // all rows, or a slice's
                            int mode = rows.Mode(archetype);
                            if (archetype.AliveCount == 0 || mode == RowFilter.None) continue;
                            // The row loops return after a structural change (the next row and the visited entity): the
                            // refs are re-read and the visited entity's writes carried over here, so the loops keep only
                            // their row state live across the job. A table whose rows all pass takes the lean loop.
                            for (int at = first; at < count && mode != RowFilter.None;)
                            {
                                job = mode == RowFilter.All
                                    ? (archetype.Count == archetype.AliveCount ? {{name}}Live<{{tps}}, TJob, NoTombstones>(job, archetype, at, count, {{bits}}) : {{name}}Live<{{tps}}, TJob, Tombstones>(job, archetype, at, count, {{bits}}))
                                    : {{name}}LiveTested<{{tps}}, TJob>(job, archetype, rows, at, count, {{bits}});
                                long stop = _stop;
                                if (stop < 0) break; // the rows ran out without a structural change
                                at = count - (int)stop;
                                int moved = (int)(stop >> 32);
                                if (archetype.Slots[at - 1].Index < 0) CarryOver(archetype, at - 1, moved, TJob.WriteMask, [{{bits}}]);
                                mode = rows.Mode(archetype); // the change may have made rows fail (or pass)
                            }
                        }
                    }
                    finally
                    {
                        EndPassAlone(alone);
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
                private TJob {{name}}Live<{{tps}}, TJob, TRows>(TJob job, Archetype archetype, int start, int count, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, allows ref struct
                    where TRows : struct
                {
                    // TRows is NoTombstones when the table had none at the call: one appears only with a structural
                    // change (a destroy or a move bumps StorageVersion), which ends the loop, so it tests no row.
                    // The loop walks refs up to an end ref: no row index or count stays live across the job (the row
                    // is computed only to stamp a tracked write), so the job's own values keep more registers. The
                    // version seen stays in memory (Volatile.Read takes its address): one load per row, while held in
                    // a register it pushes a column ref out, which is then stored and reloaded every row, a dependency
                    // chain through memory (a structural job's call leaves too few callee-saved registers for all).
                    int seen = StorageVersion;
                    ref EntityId slots = ref MemoryMarshal.GetArrayDataReference(archetype.Slots);
                    ref EntityId idp = ref Unsafe.Add(ref slots, start);
                    ref EntityId end = ref Unsafe.Add(ref slots, count);
            {{startAt}}
                    for (; Unsafe.IsAddressLessThan(ref idp, ref end); idp = ref Unsafe.Add(ref idp, 1), {{advance}})
                    {
                        if (typeof(TRows) == typeof(Tombstones) && idp.Index < 0) continue; // a JIT-time test

            {{StampRow(n, "            ", "(int)(Unsafe.ByteOffset(ref slots, ref idp) / Unsafe.SizeOf<EntityId>())")}}
                        {{execute}}

                        if (Volatile.Read(ref seen) != StorageVersion)
                        {
                            _stop = (long)VisitedId(idp) << 32 | (uint)(Unsafe.ByteOffset(ref idp, ref end) / Unsafe.SizeOf<EntityId>() - 1);
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
                    int ahead = 4; // whole words a run may span, doubled after each run without a structural change
                    ulong from = ulong.MaxValue << start; // the first word's rows before start fail (the shift is mod 64)
                    for (int w = start >> 6; w < words; w++, from = ulong.MaxValue)
                    {
                        ulong pass = rows.Word(archetype, w) & from;
                        if (pass == from && (w + 1) << 6 <= count)
                        {
                            if (run < 0) run = (w << 6) + BitOperations.TrailingZeroCount(from);
                            // The whole words that follow, at once when one presence column decides (else none).
                            w = rows.WholeEnd(archetype, w + 1, Math.Min(count >> 6, (run >> 6) + ahead)) - 1;
                            // A bounded lookahead: a structural change ends the run, and the pass then comes back here
                            // and finds its words again (unbounded, a table of passing rows would be scanned per change).
                            // Doubling from 4 words keeps the words scanned again within twice the rows run since the
                            // last change, while a long stretch without changes takes few runs.
                            if ((w + 1) - (run >> 6) < ahead) continue;
                            job = (archetype.Count == archetype.AliveCount ? {{name}}Live<{{tps}}, TJob, NoTombstones>(job, archetype, run, (w + 1) << 6, {{bits}}) : {{name}}Live<{{tps}}, TJob, Tombstones>(job, archetype, run, (w + 1) << 6, {{bits}}));
                            if (Stopped((w + 1) << 6, count)) return job;
                            run = -1;
                            ahead = Math.Min(ahead * 2, 1024);
                            continue;
                        }

                        if (run >= 0)
                        {
                            job = (archetype.Count == archetype.AliveCount ? {{name}}Live<{{tps}}, TJob, NoTombstones>(job, archetype, run, w << 6, {{bits}}) : {{name}}Live<{{tps}}, TJob, Tombstones>(job, archetype, run, w << 6, {{bits}}));
                            if (Stopped(w << 6, count)) return job;
                            run = -1;
                        }

                        pass &= RowFilter.RowMask(w, count);
                        if (pass == 0)
                        {
                            w = rows.NextPassing(archetype, w + 1, words) - 1; // words without passing rows
                            continue;
                        }

                        job = {{name}}Bits<{{tps}}, TJob>(job, archetype, w, pass, count, {{bits}});
                        if (_stop >= 0) return job;
                    }

                    if (run >= 0)
                    {
                        job = (archetype.Count == archetype.AliveCount ? {{name}}Live<{{tps}}, TJob, NoTombstones>(job, archetype, run, words << 6, {{bits}}) : {{name}}Live<{{tps}}, TJob, Tombstones>(job, archetype, run, words << 6, {{bits}})); // whole words: <= count
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
                    ref EntityId slots = ref MemoryMarshal.GetArrayDataReference(archetype.Slots);
            {{Lines(n, i => $"            ref T{i} c{i} = ref archetype.Base<T{i}>(b{i});")}}
                    for (; pass != 0; pass &= pass - 1)
                    {
                        int row = (w << 6) + BitOperations.TrailingZeroCount(pass);
                        ref EntityId idp = ref Unsafe.Add(ref slots, row);
                        if (idp.Index < 0) continue; // tombstone
            {{Lines(n, i => $"            ref T{i} r{i} = ref Unsafe.Add(ref c{i}, row);")}}

            {{StampRow(n, "        ", "row")}}
                        {{execute}}

                        if (Volatile.Read(ref seen) != StorageVersion)
                        {
                            _stop = (long)VisitedId(idp) << 32 | (uint)(count - 1 - row);
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
                        var (first, count) = filter.Rows(ai, archetype.Count); // all rows, or a slice's (from a word's first row)
                        int mode = rows.Mode(archetype);
                        if (archetype.AliveCount == 0 || mode == RowFilter.None || first >= count) continue;
                        if (mode == RowFilter.Test)
                            job = {{name}}Filtered<{{tps}}, TJob>(job, archetype, rows, first, count, {{bits}});
                        else if (archetype.Count == archetype.AliveCount)
                            job = {{name}}Rows<{{tps}}, TJob>(job, archetype, first, count, {{bits}}); // dense table
                        else // tombstones: runs of alive rows, found with vectorized scans
                            for (int start = first; archetype.NextRun(ref start, count, out int length); start += length)
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
                    ref EntityId ids = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(archetype.Slots), start);
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
                    ref EntityId ids = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(archetype.Slots), start);
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
                        if (bits == 0 && rows.NextPassing(archetype, w + 1, words) is var to && to > w + 1)
                        {
                            // Words without a passing row (vectorized when one presence column decides): skip them.
                            int skip = (to - w - 1) << 6;
                            w = to - 1; // the step below moves to word `to`
                            ids = ref Unsafe.Add(ref ids, skip);
            {{Lines(n, i => $"                    r{i} = ref Unsafe.Add(ref r{i}, skip);")}}
                        }

                        if (tombstones) bits &= AliveRows(ref ids, left); // rare: the run loops then test nothing
            {{StampWord(n, "            ", "w", "bits")}}
                        while (bits != 0)
                        {
                            int k = BitOperations.TrailingZeroCount(bits);
                            // The shift fills zeros, so ~ stops the run at the word's end at the latest.
                            int end = k + BitOperations.TrailingZeroCount(~(bits >> k));
                            bits = end >= 64 ? 0 : bits & (ulong.MaxValue << end);
                            ref EntityId idk = ref Unsafe.Add(ref ids, k);
            {{Lines(n, i => $"                ref T{i} q{i} = ref Unsafe.Add(ref r{i}, k);")}}
                            for (int run = end - k; run > 0; run--, idk = ref Unsafe.Add(ref idk, 1), {{List(n, i => $"q{i} = ref Unsafe.Add(ref q{i}, 1)")}})
                                job.Execute({{Args(rowArgRun, List(n, i => $"ref q{i}"))}});
                        }

                        ids = ref Unsafe.Add(ref ids, left);
            {{Lines(n, i => $"            r{i} = ref Unsafe.Add(ref r{i}, left);")}}
                    }

                    return job;
                }

            {{ParallelLoop(n, name, job)}}
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
            """);
    }

    /// <summary>
    /// <c>{name}Parallel</c> and its chunks (<c>{name}Chunks</c>): one template for every parallel pass. Each pass shape
    /// supplies <c>{name}Chunk</c>, which runs one chunk of rows with the chunk's copy of the job.
    /// </summary>
    private static string ParallelLoop(int n, string name, string job)
    {
        string tps = TypeParams(n);
        string bits = List(n, i => $"b{i}");
        string ushorts = List(n, i => $"ushort b{i}");
        return $$"""
                /// <summary>
                /// <see cref="{{name}}{{{tps}}, TJob}"/> on several threads: the matching rows are cut into chunks (about four per
                /// thread, a multiple of 64 rows, at most <see cref="ParallelChunkRows"/>; or <paramref name="chunkRows"/> rounded
                /// up to 64, for few heavy rows: <c>[ForEach(Parallel = true, ChunkRows = n)]</c>) that the calling thread and up to
                /// <see cref="WorldOptions.MaxDegreeOfParallelism"/> - 1 thread-pool threads run at the same time, each with its own copy of <paramref name="job"/> (state
                /// a job keeps in its fields is not merged back, unless the job merges: <c>Merges</c>, a <c>Reduce&lt;T&gt;</c>;
                /// its copies, one per chunk, the size of the job's fields, are then kept in an array reused by every call and
                /// combined in chunk order). The job must not change structure or write anything
                /// that another row's run reads (a singleton, another entity); nothing else may change the world
                /// during the call. Fewer than two chunks of rows, or a runner already busy, run on the calling thread.
                /// Allocation-free after the first call per job type.
                /// </summary>
                public void {{name}}Parallel<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default, int chunkRows = 0)
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>, INoStructuralChanges
                {
                    var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                    var work = WorkOf<{{name}}Chunks<{{tps}}, TJob>>();
                    int chunks = work.Cut(this, match, in filter, chunkRows, ref job, {{bits}});
                    try
                    {
                        RunChunks(work, chunks);
                        if (TJob.Merges) work.MergeInto(ref job, chunks); // after the run: the helpers are done with the copies
                    }
                    finally
                    {
                        work.Release();
                    }
                }

                /// <summary>The chunks of one <see cref="{{name}}Parallel{{{tps}}, TJob}"/> call: (table, first row) pairs.</summary>
                private sealed class {{name}}Chunks<{{tps}}, TJob> : ParallelWork
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    public TJob Job;
                    // Each chunk's copy of the job, when it keeps partial results (TJob.Merges): a box per chunk, reused by
                    // every call. Not a TJob[]: the runtime refuses arrays of value types of 64 KB or more (a large Reduce<T>).
                    private Part[] _parts = [];
                    private World _world;
                    private Archetype[] _tables = [];
                    private RowFilter[] _filters = [];
                    private int[] _starts = [], _ends = [];
            {{Lines(n, i => $"            private ushort _b{i};")}}

                    /// <summary>Cut the matching rows into chunks; returns how many.</summary>
                    public int Cut(World world, QueryMatch match, in QueryFilter filter, int chunkRows, ref TJob job, {{ushorts}})
                    {
                        _world = world;
                        Job = job;
            {{Lines(n, i => $"            _b{i} = b{i};")}}
                        int chunks = 0;
                        var archetypes = match.Archetypes;
                        // A job writing a tracked component stamps change-tick summaries shared by 4,096 rows (a JIT-time constant).
                        bool stamps = {{List(n, i => $"(TJob.WriteMask & {1 << (i - 1)}) != 0 && default(T{i}) is ITracked", " || ")}} || TJob.OptionalWrites.HasComponents;
                        int size = world.ChunkRows(archetypes, stamps, filter.SliceCount, chunkRows);
                        bool open = stamps && (size & 4095) != 0; // chunks split blocks: open them first (Archetype.OpenBlocks)
                        for (int a = 0; a < archetypes.Length; a++)
                        {
                            var archetype = archetypes[a];
                            var rows = new RowFilter(match.Filters[a], filter.Tick);
                            if (archetype.AliveCount == 0 || rows.Mode(archetype) == RowFilter.None) continue;
                            var (first, end) = filter.Rows(a, archetype.Count); // all rows, or a slice's (from a word's first row)
                            if (open)
                            {
                                ulong tick = world.ChangeTick;
            {{Lines(n, i => $"                    if ((TJob.WriteMask & {1 << (i - 1)}) != 0) archetype.OpenBlocks<T{i}>(first, end, tick);")}}
                                if (TJob.OptionalWrites.HasComponents) archetype.OpenBlocks(TJob.OptionalWrites, first, end, tick);
                            }
                            // Chunks end on multiples of size, not size rows after a slice's start: a slice starts at any
                            // word, and two chunks of one unopened 4,096-row block would race on its change-tick summary.
                            for (int start = first, stop; start < end; start = stop)
                            {
                                stop = Math.Min(start - start % size + size, end);
                                if (chunks == _starts.Length)
                                {
                                    int grown = Math.Max(8, chunks * 2);
                                    Array.Resize(ref _tables, grown);
                                    Array.Resize(ref _filters, grown);
                                    Array.Resize(ref _starts, grown);
                                    Array.Resize(ref _ends, grown);
                                    if (TJob.Merges) Grow(grown);
                                }

                                _tables[chunks] = archetype;
                                _filters[chunks] = rows;
                                _ends[chunks] = stop;
                                _starts[chunks++] = start;
                            }
                        }

                        return chunks;
                    }

                    public override void Run(int index)
                    {
                        var archetype = _tables[index];
                        var job = Job;
                        _world.{{name}}Chunk<{{tps}}, TJob>(ref job, archetype, _filters[index], _starts[index], _ends[index], {{List(n, i => $"_b{i}")}});
                        // One write per chunk: neighboring slots written by other threads share a line only for this store.
                        if (TJob.Merges) _parts[index].Job = job;
                    }

                    private void Grow(int length)
                    {
                        int old = _parts.Length;
                        Array.Resize(ref _parts, length);
                        for (int i = old; i < length; i++) _parts[i] = new Part();
                    }

                    private sealed class Part
                    {
                        public TJob Job;
                    }

                    /// <summary>
                    /// Combine the chunks' partial results into <paramref name="job"/> in chunk order (tables in match order,
                    /// rows in table order): the result depends on where chunks begin, never on which thread ran which.
                    /// </summary>
                    public void MergeInto(ref TJob job, int chunks)
                    {
                        for (int i = 0; i < chunks; i++) TJob.Merge(ref job, in _parts[i].Job);
                    }

                    /// <summary>Drop the references of the last call (tables, filters, the job's own) until the next one.</summary>
                    public void Release()
                    {
                        Array.Clear(_tables);
                        Array.Clear(_filters);
                        if (TJob.Merges && RuntimeHelpers.IsReferenceOrContainsReferences<TJob>()) // the copies hold the job's references
                            for (int i = 0; i < _parts.Length; i++) _parts[i].Job = default;
                        Job = default;
                        _world = null;
                    }
                }

            """;
    }

    private static string QueryItems()
    {
        var sb = new StringBuilder(Header).Append("""
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;

            namespace EasyEcs;


            """);

        // Query items hand out refs (a visited row of a tracked component counts as written); Read items hand out
        // ref readonly refs and stamp nothing. One template, two families.
        foreach (bool read in new[] { false, true })
        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            string item = read ? "ReadItem" : "QueryItem", enumerator = read ? "ReadEnumerator" : "QueryEnumerator";
            string refKind = read ? "ref readonly" : "ref";
            sb.Append($$"""
                /// <summary>
                /// One {{(read ? "read-only query result: the entity and read-only refs into its row (nothing is stamped)" : "query result: the entity and refs into its row")}}. Like every ref into ECS storage, the refs are valid
                /// until the next structural change; re-read through <see cref="Entity"/> after one.
                /// </summary>
                public readonly ref struct {{item}}<{{tps}}>
                {{Constraints(n, "    ")}}
                {
                    private readonly World _world;
                    private readonly int _id;
                    private readonly ref int _version;
                {{Lines(n, i => $"    private readonly ref T{i} _c{i};")}}

                    internal {{item}}(World world, int id, ref int version, {{List(n, i => $"ref T{i} c{i}")}})
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
                    /// <summary>The current entity's T{{i}}, a reference into its row (the generator names it after the component: <c>item.Position</c>).</summary>
                    public {{refKind}} T{{i}} Component{{i}}
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        get => ref _c{{i}};
                    }

                """)}}
                }

                /// <summary>
                /// Allocation-free {{(read ? "read-only " : "")}}query enumerator; safe under structural changes during iteration (same visiting rules as
                /// ForEach). A ref struct: it holds refs into the current table, so a loop over it cannot <c>await</c>.
                /// </summary>
                /// <remarks>
                /// Every member the loop calls is inlined and the cold work is static (<see cref="QueryWalk"/>, by value
                /// in and out): the enumerator is never passed by reference, so its fields live in registers.
                /// </remarks>
                public ref struct {{enumerator}}<{{tps}}>
                {{Constraints(n, "    ")}}
                {
                    private readonly World _world;
                    private readonly QueryMatch _match;
                    private readonly ulong _since;
                {{Lines(n, i => $"    private readonly ushort _b{i};")}}
                    private QueryWalk _walk;
                    private int _ticket; // 0: not started; QueryWalk.Ended: ended
                    private ref EntityId _rows;
                {{Lines(n, i => $"    private ref T{i} _r{i};")}}
                    private int _id, _storage;
                    // Native-sized: indexing needs no sign extension (movsxd on x64, sbfiz/sxtw on arm64) per row.
                    private nint _slot;
                    private nint _count; // rows of the current table to walk; negative: rows to test (QueryWalk.NextTested)

                    internal {{enumerator}}(World world, QueryMatch match, ulong since, {{List(n, i => $"ushort b{i}")}})
                    {
                        _world = world;
                        _match = match;
                        _since = since;
                {{Lines(n, i => $"        _b{i} = b{i};")}}
                    }

                    /// <summary>The current entity and its components.</summary>
                    public readonly {{item}}<{{tps}}> Current
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        get
                        {
                            nint slot = _slot;
                            // Version is the row's first field: the row's address is the version's.
                            return new(_world, _id, ref Unsafe.As<EntityId, int>(ref Unsafe.Add(ref _rows, slot)), {{List(n, i => $"ref Unsafe.Add(ref _r{i}, slot)")}});
                        }
                    }

                    /// <summary>The enumerator itself (<c>foreach</c> support, no allocation).</summary>
                    public readonly {{enumerator}}<{{tps}}> GetEnumerator() => this;

                    /// <summary>Move to the next matching entity. O(1) per row, plus the tables it steps over.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool MoveNext()
                    {
                        if (_storage == _world.StorageVersion) // no structural change since the last row
                        {
                            if (_count >= 0)
                            {
                                while (++_slot < _count)
                                {
                                    int id = Unsafe.Add(ref _rows, _slot).Index; // _slot < _count <= rows
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
                                    _id = Unsafe.Add(ref _rows, slot).Index;
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
                        _id = Unsafe.Add(ref _rows, _slot).Index;
                {{Lines(n, i => $"        _r{i} = ref table.Base<T{i}>(_b{i});")}}

                    Found:
                {{(read ? "        return true;\n    }\n" : Stamping(n))}}
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

    /// <summary>
    /// The end of a <c>QueryEnumerator.MoveNext</c> and its stamping: items hand out refs, so a visited row of a tracked
    /// component counts as written (a JIT-time constant: no code for queries without tracked components).
    /// </summary>
    private static string Stamping(int n) => $$"""
                if ({{List(n, i => $"default(T{i}) is ITracked", " || ")}}) Stamp(_walk, (int)_slot);
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
                    StampWord(walk, archetype, slot, tick);
                    return;
                }

        {{Lines(n, i => $"        archetype.StampRowTick<T{i}>(slot, tick);")}}
            }

            // The first row of a word: a call in tail position, so the per-row Stamp keeps no register of
            // the summaries' code (its prologue would save them for every row).
            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void StampWord(QueryWalk walk, Archetype archetype, int slot, ulong tick)
            {
                walk.StampWord = slot >> 6;
                walk.StampTick = tick;
        {{Lines(n, i => $"        archetype.NoteVisited<T{i}>(slot, tick);")}}
        {{Lines(n, i => $"        archetype.StampRowTick<T{i}>(slot, tick);")}}
            }
        """;
}
