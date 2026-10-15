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
            "using System;\n\nnamespace EasyEcs;\n\npublic readonly partial struct Entity\n{\n");
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
                    public Entity Set<{{tps}}>({{values}})
                {{cons}}
                    {
                        var world = _world;
                        lock (world.StructuralLock)
                        {
                            world.ThrowIfStale(this);
                {{Lines(n, i => $"            ushort b{i} = world.PrepareColumn<T{i}>();")}}
                            {{(n == 1 ? "world.AddBit(Id, b1);" : $"world.AddBits(Id, [{List(n, i => $"b{i}")}], {signature});")}}
                {{Lines(n, i => $"            world.ColumnRef<T{i}>(b{i}, Id) = c{i};")}}
                        }

                        return this;
                    }

                    /// <summary>
                    /// Remove {{what}} in one archetype transition; missing components are ignored. O({{n}}). Thread-safe.
                    /// </summary>
                    public void Remove<{{tps}}>()
                {{cons}}
                    {
                        var world = _world;
                        lock (world.StructuralLock)
                        {
                            world.ThrowIfStale(this);
                            {{(n == 1 ? "world.RemoveBit(Id, ComponentType<T1>.Bit);"
                                : $"world.RemoveBits(Id, [{List(n, i => $"ComponentType<T{i}>.Bit")}], {signature});")}}
                        }
                    }


                """);

            world.Append($$"""
                    /// <summary>
                    /// Create an entity with {{what}} placed directly in its final archetype (no intermediate moves).
                    /// O({{n}}) amortized. Thread-safe.
                    /// </summary>
                    public Entity CreateEntity<{{tps}}>({{values}})
                {{cons}}
                    {
                        lock (StructuralLock)
                        {
                            int id = AllocateId();
                {{Lines(n, i => $"            ushort b{i} = PrepareColumn<T{i}>();")}}
                            // Straight into the final archetype through an edge cached on the empty archetype (no mask hashing).
                            MoveToArchetype(id, {{(n == 1 ? "ArchetypeAfterAdd(_emptyArchetype, b1)"
                                : $"_emptyArchetype.GetAddSetEdge({signature}) ?? CreateSetEdge(_emptyArchetype, [{List(n, i => $"b{i}")}], {signature}, add: true)")}});
                {{Lines(n, i => $"            ColumnRef<T{i}>(b{i}, id) = c{i};")}}
                            return new Entity(this, id, Meta[id].Version);
                        }
                    }


                """);
        }

        return entity.Append("}\n\n").Append(world).Append("}\n").ToString();
    }

    // ---------------- IForEach / ForEach / Query / Count / Any ----------------

    private static string Queries()
    {
        var sb = new StringBuilder(Header).Append("""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;

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
                    private Archetype[] Match<{{tps}}>(in QueryFilter filter, {{List(n, i => $"out ushort b{i}")}})
                {{cons}}
                    {
                {{Lines(n, i => $"        b{i} = ComponentType<T{i}>.Bit;")}}
                        return GetMatchingArchetypes([{{bits}}], in filter);
                    }

                    /// <summary>
                    /// Run <paramref name="job"/> for every entity having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>).
                    /// The fastest iteration: O(matching archetypes + slots), allocation-free. Structural changes inside the job are
                    /// allowed (entities added during the pass may or may not be visited, removed ones are skipped).
                    /// </summary>
                    public void ForEach<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default)
                {{cons}}
                        where TJob : struct, IForEach<{{tps}}>
                    {
                        var archetypes = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});

                        // Columns are cached and refreshed only when the job reallocated storage (StorageVersion changed).
                        int version = StorageVersion;
                {{Lines(n, i => $"        var a{i} = Column<T{i}>(b{i});")}}

                        for (int ai = 0; ai < archetypes.Length; ai++)
                        {
                            var archetype = archetypes[ai];
                            if (archetype.AliveCount == 0) continue;

                            int[] ids = archetype.EntityIds;
                            int count = archetype.Count;
                            for (int i = 0; i < count; i++)
                            {
                                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ids), i);
                                if (id < 0) continue; // tombstone

                                job.Execute(id, {{List(n, i => $"ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a{i}), id)")}});

                                if (version != StorageVersion)
                                {
                                    version = StorageVersion;
                {{Lines(n, i => $"                    a{i} = Column<T{i}>(b{i});")}}
                                    ids = archetype.EntityIds;
                                    count = archetype.Count;
                                }
                            }
                        }
                    }

                    /// <summary>Enumerate entities having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>). Allocation-free.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public QueryEnumerator<{{tps}}> Query<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        var archetypes = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        return new QueryEnumerator<{{tps}}>(this, archetypes, {{bits}});
                    }

                    /// <summary>Number of alive entities having all of <c>{{tps}}</c>. O(matching archetypes).</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public int Count<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => ArchetypeCursor.CountAlive(Match<{{tps}}>(in filter, {{List(n, i => "out _")}}));

                    /// <summary>Whether any alive entity has all of <c>{{tps}}</c>. O(matching archetypes), early exit.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool Any<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => ArchetypeCursor.AnyAlive(Match<{{tps}}>(in filter, {{List(n, i => "out _")}}));


                """);
        }

        return sb.Append("}\n").ToString();
    }

    private static string QueryItems()
    {
        var sb = new StringBuilder(Header).Append("""
            using System;
            using System.Runtime.CompilerServices;

            namespace EasyEcs;


            """);

        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            sb.Append($$"""
                /// <summary>One query result: the entity and refs to its queried components.</summary>
                public readonly struct QueryItem<{{tps}}>
                {{Constraints(n, "    ")}}
                {
                    private readonly World _world;
                    private readonly int _entityId;
                {{Lines(n, i => $"    private readonly ushort _b{i};")}}

                    internal QueryItem(World world, int entityId, {{List(n, i => $"ushort b{i}")}})
                    {
                        _world = world;
                        _entityId = entityId;
                {{Lines(n, i => $"        _b{i} = b{i};")}}
                    }

                    /// <summary>Handle of the current entity.</summary>
                    public Entity Entity => _world.EntityAt(_entityId);

                {{Lines(n, i => $$"""
                    public ref T{{i}} Component{{i}}
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        get => ref _world.ColumnRef<T{{i}}>(_b{{i}}, _entityId);
                    }

                """)}}
                }

                /// <summary>Allocation-free query enumerator; safe under structural changes during iteration.</summary>
                public struct QueryEnumerator<{{tps}}>
                {{Constraints(n, "    ")}}
                {
                    private readonly World _world;
                    private ArchetypeCursor _cursor;
                {{Lines(n, i => $"    private readonly ushort _b{i};")}}
                    private int _current;

                    internal QueryEnumerator(World world, Archetype[] archetypes, {{List(n, i => $"ushort b{i}")}})
                    {
                        _world = world;
                        _cursor = new ArchetypeCursor(archetypes);
                {{Lines(n, i => $"        _b{i} = b{i};")}}
                        _current = -1;
                    }

                    public readonly QueryItem<{{tps}}> Current => new(_world, _current, {{List(n, i => $"_b{i}")}});

                    /// <summary>Number of alive matching entities. O(matching archetypes).</summary>
                    public readonly int Count => _cursor.AliveCount;

                    public readonly bool IsEmpty => !_cursor.AnyAlive();

                    public readonly QueryEnumerator<{{tps}}> GetEnumerator() => this;

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool MoveNext() => _cursor.MoveNext(out _current);
                }


                """);
        }

        return sb.ToString();
    }
}
