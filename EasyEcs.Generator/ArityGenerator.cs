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
            "using System;\nusing System.Runtime.CompilerServices;\n\nnamespace EasyEcs;\n\npublic readonly partial struct Entity\n{\n");
        var world = new StringBuilder("public sealed partial class World\n{\n");

        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            string cons = Constraints(n, "        ");
            string values = List(n, i => $"in T{i} c{i}");
            string what = n == 1 ? "a component" : n + " components";
            // Dense index of the generic signature: keys the cached multi-component transitions of an archetype.
            string signature = $"TypeIndex<ComponentSignature, ({tps})>.Value";

            entity.Append($$"""
                    /// <summary>
                    /// Add or overwrite {{what}} with the given value{{(n == 1 ? "" : "s")}} in one archetype transition. Returns the entity
                    /// for chaining. O({{n}}) amortized. Thread-safe.
                    /// </summary>
                    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the loops that call it lean
                    public Entity Set<{{tps}}>({{values}})
                {{cons}}
                    {
                        var world = _world;
                        using (world.Structural())
                        {
                            ref var meta = ref world.Live(this);
                {{Lines(n, i => $"            ushort b{i} = ComponentType<T{i}>.Bit;")}}
                            {{(n == 1 ? "world.AddBit(ref meta, Id, b1);" : $"world.AddBits(ref meta, Id, [{List(n, i => $"b{i}")}], {signature});")}}
                {{Lines(n, i => $"            World.Row<T{i}>(ref meta, b{i}) = c{i};")}}
                        }

                        return this;
                    }

                    /// <summary>
                    /// Remove {{what}}; missing components are ignored. The entity keeps its row (no move). O({{n}}). Thread-safe.
                    /// </summary>
                    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the loops that call it lean
                    public void Remove<{{tps}}>()
                {{cons}}
                    {
                        var world = _world;
                        using (world.Structural())
                        {
                            ref var meta = ref world.Live(this);
                            {{(n == 1 ? "world.RemoveBit(ref meta, ComponentType<T1>.Bit);"
                                : $"world.RemoveBits(ref meta, [{List(n, i => $"ComponentType<T{i}>.Bit")}]);")}}
                        }
                    }


                """);

            world.Append($$"""
                    /// <summary>
                    /// Create an entity with {{what}} placed directly in its final archetype (no intermediate moves).
                    /// O({{n}}) amortized. Thread-safe.
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
                            return new Entity(this, id, Meta[id].Version);
                        }
                    }


                """);
        }

        return entity.Append("}\n\n").Append(world).Append("}\n").ToString();
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
                    void Execute(int entityId, {{List(n, i => $"ref T{i} c{i}")}});

                    /// <summary>
                    /// Whether <see cref="Execute"/> reads its entity id (default: true). When false, ForEach passes 0 and
                    /// skips loading the id column. Generated jobs set it: they never read the id.
                    /// </summary>
                    static virtual bool ReadsId => true;

                {{Writes}}
                }

                /// <summary>
                /// Struct callback for <c>World.ForEachBatch</c>: receives whole runs of rows as spans, one per component, so
                /// the job can process them with SIMD (<c>System.Numerics.Vector</c>, <c>MemoryMarshal.Cast</c>).
                /// <c>[ForEach]</c> methods taking <c>Span&lt;T&gt;</c> / <c>ReadOnlySpan&lt;T&gt;</c> compile to this.
                /// </summary>
                public interface IForEachBatch<{{TypeParams(n)}}>
                {{Constraints(n, "    ")}}
                {
                    /// <summary>One run of consecutive rows of one archetype: element i of every span belongs to the same entity.</summary>
                    void Execute({{List(n, i => $"Span<T{i}> c{i}")}});
                }

                /// <summary>
                /// Like <see cref="IForEach{{{TypeParams(n)}}}"/> but receives the <see cref="Entity"/> handle, built from the
                /// archetype's id and version arrays without a per-entity lookup. <c>[ForEach]</c> methods taking an
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
                        return GetMatchingArchetypes([{{bits}}], in filter);
                    }

                {{ForEach(n, "ForEach", "IForEach", "id", "TJob.ReadsId ? ids : 0", "the entity id")}}
                {{ForEach(n, "ForEachEntity", "IForEachEntity", "new Entity(this, id, Unsafe.Add(ref versions, i))", "new Entity(this, ids, versions)", "the entity handle")}}
                    /// <summary>
                    /// Run <paramref name="job"/> over every entity having all of <c>{{tps}}</c> (and matching
                    /// <paramref name="filter"/>), one run of consecutive rows at a time: a dense archetype is a single
                    /// call. Allocation-free. The job sees spans into the tables, so it must not make structural changes.
                    /// </summary>
                    [MethodImpl(MethodImplOptions.NoInlining)] // like ForEach: the job inlines into this loop, not further up
                    public void ForEachBatch<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default)
                {{cons}}
                        where TJob : struct, IForEachBatch<{{tps}}>
                    {
                        var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        var archetypes = match.Archetypes;
                        for (int ai = 0; ai < archetypes.Length; ai++)
                        {
                            var archetype = archetypes[ai];
                            var rows = match.Filters[ai];
                            int count = archetype.Count;
                            int mode = rows.Mode(archetype);
                            if (archetype.AliveCount == 0 || mode == RowFilter.None) continue;
                {{Lines(n, i => $"            ref T{i} r{i} = ref archetype.Base<T{i}>(b{i});")}}
                            if (mode == RowFilter.Test)
                            {
                                // Rows without some component: runs of rows whose presence bits pass.
                                for (int start = 0; rows.NextRun(archetype, ref start, count, out int length); start += length)
                                    job.Execute({{List(n, i => $"MemoryMarshal.CreateSpan(ref Unsafe.Add(ref r{i}, start), length)")}});
                                continue;
                            }

                            if (count == archetype.AliveCount)
                            {
                                job.Execute({{List(n, i => $"MemoryMarshal.CreateSpan(ref r{i}, count)")}});
                                continue;
                            }

                            for (int start = 0; archetype.NextRun(ref start, count, out int length); start += length)
                                job.Execute({{List(n, i => $"MemoryMarshal.CreateSpan(ref Unsafe.Add(ref r{i}, start), length)")}});
                        }
                    }

                    /// <summary>Enumerate entities having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>). Allocation-free.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public QueryEnumerator<{{tps}}> Query<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        return new QueryEnumerator<{{tps}}>(this, match, {{bits}});
                    }

                    /// <summary>Number of alive entities having all of <c>{{tps}}</c>. O(matching archetypes), plus O(rows / 64) for tables with rows to test.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public int Count<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => Match<{{tps}}>(in filter, {{List(n, i => "out _")}}).CountAlive();

                    /// <summary>Whether any alive entity has all of <c>{{tps}}</c>. O(matching archetypes), plus O(rows / 64) for tables with rows to test; early exit.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool Any<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => Match<{{tps}}>(in filter, {{List(n, i => "out _")}}).AnyAlive();


                """);
        }

        return sb.Append("}\n").ToString();
    }

    /// <summary>
    /// One ForEach flavor: <paramref name="entityArg"/> is what the job receives first (the id, or an Entity built from
    /// the archetype's parallel id/version arrays).
    /// </summary>
    private static string ForEach(int n, string name, string job, string entityArg, string rowArg, string receives)
    {
        string tps = TypeParams(n);
        string execute = $"job.Execute({entityArg}, {List(n, i => $"ref r{i}")});";
        // Row loops move one ref per column by one element per row (strength-reduced by hand): with an int index the
        // JIT sign-extends and rebuilds every address, which nearly doubles the cost of a tight loop.
        string advance = List(n, i => $"r{i} = ref Unsafe.Add(ref r{i}, 1)");
        string startAt = Lines(n, i => $"                ref T{i} r{i} = ref Unsafe.Add(ref archetype.Base<T{i}>(b{i}), start);");
        string bits = List(n, i => $"b{i}");
        string ushorts = List(n, i => $"ushort b{i}");
        string bases = Lines(n, i => $"                ref T{i} r{i} = ref archetype.Base<T{i}>(b{i});");
        return $$"""
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
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    var match = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                    var archetypes = match.Archetypes;
                    if (default(TJob) is INoStructuralChanges)
                    {
                        // The job cannot change rows or reallocate storage (the type test is a JIT-time constant). The row
                        // loops live in small methods of their own, so the JIT keeps their refs in registers.
                        for (int ai = 0; ai < archetypes.Length; ai++)
                        {
                            var archetype = archetypes[ai];
                            var rows = match.Filters[ai];
                            int count = archetype.Count;
                            int mode = rows.Mode(archetype);
                            if (archetype.AliveCount == 0 || mode == RowFilter.None) continue;
                            if (mode == RowFilter.Test)
                                {{name}}Filtered<{{tps}}, TJob>(ref job, archetype, rows, count, {{bits}});
                            else if (count == archetype.AliveCount)
                                {{name}}Rows<{{tps}}, TJob>(ref job, archetype, 0, count, {{bits}}); // dense table
                            else // tombstones: runs of alive rows, found with vectorized scans
                                for (int start = 0; archetype.NextRun(ref start, count, out int length); start += length)
                                    {{name}}Rows<{{tps}}, TJob>(ref job, archetype, start, start + length, {{bits}});
                        }

                        return;
                    }

                    // Row counts at the start of the pass: rows appended during the pass are not visited.
                    var filters = match.Filters;
                    // Rented, not stackalloc'd: a localloc would keep this method out of tiered compilation (no OSR, no
                    // PGO), and with it the inlining of the job into the loop.
                    int[] counts = ArrayPool<int>.Shared.Rent(Math.Max(1, archetypes.Length));
                    for (int ai = 0; ai < archetypes.Length; ai++) counts[ai] = archetypes[ai].Count;
                    BeginPass();
                    int storage = StorageVersion;
                    for (int ai = 0; ai < archetypes.Length; ai++)
                    {
                        var archetype = archetypes[ai];
                        var rows = filters[ai];
                        int count = counts[ai];
                        int mode = rows.Mode(archetype);
                        if (archetype.AliveCount == 0 || mode == RowFilter.None) continue;
                        // Rows of a table whose rows all pass take the lean loop; it hands over to the testing one when
                        // a structural change makes some rows fail.
                        int at = mode == RowFilter.All ? {{name}}Live<{{tps}}, TJob>(ref job, archetype, rows, count, ref storage, {{bits}}) : 0;
                        if (at < count) {{name}}LiveTested<{{tps}}, TJob>(ref job, archetype, rows, at, count, ref storage, {{bits}});
                    }

                    ArrayPool<int>.Shared.Return(counts);
                    EndPass();
                }

                /// <summary>
                /// The rows of a pass that may make structural changes, while every row passes the query: returns
                /// <paramref name="count"/>, or the next row when a change made some rows fail (the caller tests from there).
                /// </summary>
                private int {{name}}Live<{{tps}}, TJob>(ref TJob job, Archetype archetype, RowFilter rows, int count, ref int storage, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    int seen = storage;
                    ref int ids = ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds);
                    ref int versions = ref MemoryMarshal.GetArrayDataReference(archetype.Versions);
            {{bases}}
                    for (int i = 0; i < count; i++, {{advance}})
                    {
                        int id = Unsafe.Add(ref ids, i);
                        if (id < 0) continue; // tombstone

                        {{execute}}

                        if (seen != StorageVersion)
                        {
                            // A structural change: refresh the refs (storage may have grown) and, if the job moved
                            // the visited entity, carry its writes over to its new row.
                            seen = StorageVersion;
                            ids = ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds);
                            versions = ref MemoryMarshal.GetArrayDataReference(archetype.Versions);
            {{Lines(n, i => $"                r{i} = ref Unsafe.Add(ref archetype.Base<T{i}>(b{i}), i);")}}
                            if (Unsafe.Add(ref ids, i) < 0) CarryOver(archetype, i, id, TJob.Writes, [{{bits}}]);
                            if (rows.Mode(archetype) == RowFilter.Test)
                            {
                                storage = seen;
                                return i + 1;
                            }
                        }
                    }

                    storage = seen;
                    return count;
                }

                /// <summary>
                /// The rows from <paramref name="start"/> of a pass that may make structural changes, testing presence
                /// bits once per run: each 64-row word yields runs of consecutive passing rows that take the lean loop,
                /// failing rows are skipped by the run (a word of failing rows costs one load).
                /// </summary>
                private void {{name}}LiveTested<{{tps}}, TJob>(ref TJob job, Archetype archetype, RowFilter rows, int start, int count, ref int storage, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    int seen = storage;
                    ref int ids = ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds);
                    ref int versions = ref MemoryMarshal.GetArrayDataReference(archetype.Versions);
            {{startAt}}
                    for (int i = start; i < count;)
                    {
                        int w = i >> 6;
                        int end = Math.Min((w + 1) << 6, count);
                        ulong pass = rows.Word(archetype, w) & (ulong.MaxValue << i); // shift is mod 64
                        int first = pass == 0 ? end : Math.Min((w << 6) + BitOperations.TrailingZeroCount(pass), end);
                        int skip = first - i;
                        i = first;
            {{Lines(n, i => $"                    r{i} = ref Unsafe.Add(ref r{i}, skip);")}}
                        if (i == end) continue;

                        // The run of passing rows from i (the shift fills zeros, so ~ stops it at the word's end).
                        int run = Math.Min(BitOperations.TrailingZeroCount(~(pass >> i)), end - i);
                        for (; run > 0; run--, i++, {{advance}})
                        {
                            int id = Unsafe.Add(ref ids, i);
                            if (id < 0) continue; // tombstone

                            {{execute}}

                            if (seen != StorageVersion)
                            {
                                // A structural change: refresh the refs, carry writes over, re-read the presence bits.
                                seen = StorageVersion;
                                ids = ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds);
                                versions = ref MemoryMarshal.GetArrayDataReference(archetype.Versions);
            {{Lines(n, i => $"                        r{i} = ref Unsafe.Add(ref archetype.Base<T{i}>(b{i}), i + 1);")}}
                                if (Unsafe.Add(ref ids, i) < 0) CarryOver(archetype, i, id, TJob.Writes, [{{bits}}]);
                                i++;
                                break;
                            }
                        }
                    }

                    storage = seen;
                }

                /// <summary>Rows [<paramref name="start"/>, <paramref name="end"/>) of a stable pass: every row is alive.</summary>
                private void {{name}}Rows<{{tps}}, TJob>(ref TJob job, Archetype archetype, int start, int end, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    ref int ids = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds), start);
                    ref int versions = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(archetype.Versions), start);
            {{startAt}}
                    for (int left = end - start; left > 0; left--, ids = ref Unsafe.Add(ref ids, 1), versions = ref Unsafe.Add(ref versions, 1), {{advance}})
                        job.Execute({{rowArg}}, {{List(n, i => $"ref r{i}")}});
                }

                /// <summary>
                /// A stable pass over a table with rows lacking a queried component: the refs walk every row like a
                /// dense table, 64 presence bits per load decide which rows run (a word with none skips 64 rows).
                /// </summary>
                private void {{name}}Filtered<{{tps}}, TJob>(ref TJob job, Archetype archetype, RowFilter rows, int count, {{ushorts}})
            {{Constraints(n, "        ")}}
                    where TJob : struct, {{job}}<{{tps}}>
                {
                    bool tombstones = count != archetype.AliveCount;
                    ref int ids = ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds);
                    ref int versions = ref MemoryMarshal.GetArrayDataReference(archetype.Versions);
            {{bases}}
                    for (int w = 0; w << 6 < count; w++)
                    {
                        int left = Math.Min(64, count - (w << 6));
                        ulong bits = rows.Word(archetype, w);
                        if (tombstones || bits != ulong.MaxValue)
                        {
                            for (; left > 0; left--, bits >>= 1, ids = ref Unsafe.Add(ref ids, 1), versions = ref Unsafe.Add(ref versions, 1), {{advance}})
                                if ((bits & 1) != 0 && (!tombstones || ids >= 0))
                                    job.Execute({{rowArg}}, {{List(n, i => $"ref r{i}")}});
                            continue;
                        }

                        for (; left > 0; left--, ids = ref Unsafe.Add(ref ids, 1), versions = ref Unsafe.Add(ref versions, 1), {{advance}})
                            job.Execute({{rowArg}}, {{List(n, i => $"ref r{i}")}});
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
                public ref struct QueryEnumerator<{{tps}}>
                {{Constraints(n, "    ")}}
                {
                    private readonly World _world;
                    private ArchetypeCursor _cursor;
                {{Lines(n, i => $"    private readonly ushort _b{i};")}}
                    private ref int _versions;
                {{Lines(n, i => $"    private ref T{i} _r{i};")}}

                    internal QueryEnumerator(World world, QueryMatch match, {{List(n, i => $"ushort b{i}")}})
                    {
                        _world = world;
                        _cursor = new ArchetypeCursor(world, match);
                {{Lines(n, i => $"        _b{i} = b{i};")}}
                    }

                    public readonly QueryItem<{{tps}}> Current
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        get
                        {
                            int slot = _cursor.Slot;
                            return new(_world, _cursor.Id, ref Unsafe.Add(ref _versions, slot), {{List(n, i => $"ref Unsafe.Add(ref _r{i}, slot)")}});
                        }
                    }

                    /// <summary>Number of alive matching entities. O(matching archetypes), plus O(rows / 64) for tables with rows to test.</summary>
                    public readonly int Count => _cursor.Match.CountAlive();

                    public readonly bool IsEmpty => !_cursor.Match.AnyAlive();

                    public readonly QueryEnumerator<{{tps}}> GetEnumerator() => this;

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool MoveNext()
                    {
                        int next = _cursor.MoveNext();
                        if (next == ArchetypeCursor.Moved) Refresh();
                        return next != ArchetypeCursor.End;
                    }

                    // Another table, or its storage changed: re-read the column refs.
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    private void Refresh()
                    {
                        var archetype = _cursor.Current;
                        _versions = ref MemoryMarshal.GetArrayDataReference(archetype.Versions);
                {{Lines(n, i => $"        _r{i} = ref archetype.Base<T{i}>(_b{i});")}}
                    }

                    /// <summary>Ends the walk early (<c>foreach</c> calls it); a walk that reaches the end ends by itself.</summary>
                    public void Dispose() => _cursor.Dispose();
                }


                """);
        }

        return sb.ToString();
    }
}
