using System.Text;
using Microsoft.CodeAnalysis;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// Expands the T1..T9 API families of EasyEcs.Core from one template each, so the hand-written runtime stays a
/// single shared implementation. Only runs when compiling the EasyEcs.Core assembly itself.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ArityGenerator : IIncrementalGenerator
{
    private const int MaxArity = 9;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var isCore = context.CompilationProvider.Select(static (c, _) => c.AssemblyName == "EasyEcs.Core");
        context.RegisterSourceOutput(isCore, static (spc, isCoreAssembly) =>
        {
            if (!isCoreAssembly) return;
            spc.AddSource("Context.Structural.g.cs", Structural());
            spc.AddSource("Entity.Structural.g.cs", EntityWrappers());
            spc.AddSource("Context.Queries.g.cs", Queries());
            spc.AddSource("GroupResults.g.cs", GroupResults());
        });
    }

    // ---------------- AddComponent(s) / RemoveComponent(s) ----------------

    private static string Structural()
    {
        var sb = new StringBuilder(Header).Append(
            "using System;\nusing EasyEcs.Core.Components;\n\nnamespace EasyEcs.Core;\n\npublic partial class Context\n{\n");

        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            string returns = n == 1 ? "ComponentRef<T1>" : $"({List(n, i => $"ComponentRef<T{i}>")})";
            string refs = n == 1
                ? "new ComponentRef<T1>(id, version, b1, this)"
                : $"({List(n, i => $"new ComponentRef<T{i}>(id, version, b{i}, this)")})";
            string add = n == 1 ? "AddComponent" : "AddComponents";
            string remove = n == 1 ? "RemoveComponent" : "RemoveComponents";

            sb.Append($$"""
                /// <summary>
                /// Add {{(n == 1 ? "a component" : n + " components")}} to <paramref name="entity"/> with a single archetype
                /// transition and return {{(n == 1 ? "a reference" : "references")}} to the (default-initialized) data. Thread-safe.
                /// </summary>
                public {{returns}} {{add}}<{{tps}}>(Entity entity)
            {{Constraints(n, "        ")}}
                {
                    lock (_structuralLock)
                    {
                        ThrowIfStale(in entity);
                        int id = entity.Id;
            {{Lines(n, i => $"            ushort b{i} = PrepareColumn<T{i}>();")}}
                        AddBits(id, stackalloc ushort[] { {{List(n, i => $"b{i}")}} });
            {{Lines(n, i => $"            Column<T{i}>(b{i})[id] = default;")}}
                        int version = Meta[id].Version;
                        return {{refs}};
                    }
                }

                /// <summary>
                /// Remove {{(n == 1 ? "a component" : n + " components")}} from <paramref name="entity"/> with a single archetype
                /// transition; components the entity does not have are ignored. Thread-safe.
                /// </summary>
                public void {{remove}}<{{tps}}>(Entity entity)
            {{Constraints(n, "        ")}}
                {
                    lock (_structuralLock)
                    {
                        ThrowIfStale(in entity);
                        Span<ushort> bits = stackalloc ushort[{{n}}];
                        int count = 0;
            {{Lines(n, i => $"            if (TagRegistry.TryGetTagBitIndex<T{i}>(out var b{i})) bits[count++] = b{i};")}}
                        RemoveBits(entity.Id, bits.Slice(0, count));
                    }
                }


            """);
        }

        return sb.Append("}\n").ToString();
    }

    private static string EntityWrappers()
    {
        var sb = new StringBuilder(Header).Append(
            "using System.Runtime.CompilerServices;\nusing EasyEcs.Core.Components;\n\nnamespace EasyEcs.Core;\n\npublic partial struct Entity\n{\n");

        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            string returns = n == 1 ? "ComponentRef<T1>" : $"({List(n, i => $"ComponentRef<T{i}>")})";
            string add = n == 1 ? "AddComponent" : "AddComponents";
            string remove = n == 1 ? "RemoveComponent" : "RemoveComponents";
            sb.Append($$"""
                /// <inheritdoc cref="Context.{{add}}{{"{"}}{{tps}}{{"}"}}(Entity)"/>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public {{returns}} {{add}}<{{tps}}>()
            {{Constraints(n, "        ")}}
                    => Context.{{add}}<{{tps}}>(this);

                /// <inheritdoc cref="Context.{{remove}}{{"{"}}{{tps}}{{"}"}}(Entity)"/>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void {{remove}}<{{tps}}>()
            {{Constraints(n, "        ")}}
                    => Context.{{remove}}<{{tps}}>(this);


            """);
        }

        return sb.Append("}\n").ToString();
    }

    // ---------------- ForEach / GroupOf / CountOf / AnyOf ----------------

    private static string Queries()
    {
        var sb = new StringBuilder(Header).Append("""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using EasyEcs.Core.Components;
            using EasyEcs.Core.Enumerators;

            namespace EasyEcs.Core;


            """);

        for (int n = 1; n <= MaxArity; n++)
        {
            sb.Append($$"""
                /// <summary>
                /// Struct callback for <c>Context.ForEach</c>. Being a struct-constrained generic, calls are devirtualized and
                /// usually inlined by the JIT: no delegate, no closure, no allocation.
                /// </summary>
                public interface IForEach<{{TypeParams(n)}}>
                {{Constraints(n, "    ")}}
                {
                    void Execute(int entityId, {{List(n, i => $"ref T{i} c{i}")}});
                }


                """);
        }

        sb.Append("public partial class Context\n{\n");
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
                {{Lines(n, i => $"        b{i} = 0;")}}
                        if ({{List(n, i => $"!TagRegistry.TryGetTagBitIndex<T{i}>(out b{i})", " || ")}})
                            return System.Array.Empty<Archetype>();
                        return GetMatchingArchetypes(stackalloc ushort[] { {{bits}} }, in filter);
                    }

                    /// <summary>
                    /// Run <paramref name="job"/> for every entity having all of <c>{{tps}}</c> (and matching <paramref name="filter"/>).
                    /// Fastest, allocation-free iteration. Structural changes inside the job are allowed (entities added during the
                    /// pass may or may not be visited, removed ones are skipped); do not call <see cref="CompactArchetypes"/> inside.
                    /// </summary>
                    public void ForEach<{{tps}}, TJob>(ref TJob job, in QueryFilter filter = default)
                {{cons}}
                        where TJob : struct, IForEach<{{tps}}>
                    {
                        var archetypes = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        if (archetypes.Length == 0) return;

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
                    public GroupResultEnumerator<{{tps}}> GroupOf<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                    {
                        var archetypes = Match<{{tps}}>(in filter, {{List(n, i => $"out var b{i}")}});
                        return new GroupResultEnumerator<{{tps}}>(this, archetypes, {{bits}});
                    }

                    /// <summary>Number of alive entities having all of <c>{{tps}}</c>, without iterating them.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public int CountOf<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => ArchetypeCursor.CountAlive(Match<{{tps}}>(in filter, {{List(n, i => "out _")}}));

                    /// <summary>Whether any alive entity has all of <c>{{tps}}</c>, with early exit.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public bool AnyOf<{{tps}}>(in QueryFilter filter = default)
                {{cons}}
                        => ArchetypeCursor.AnyAlive(Match<{{tps}}>(in filter, {{List(n, i => "out _")}}));


                """);
        }

        return sb.Append("}\n").ToString();
    }

    private static string GroupResults()
    {
        var sb = new StringBuilder(Header).Append("""
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using EasyEcs.Core.Components;
            using EasyEcs.Core.Group;

            namespace EasyEcs.Core.Group
            {

            """);

        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            sb.Append($$"""
                    /// <summary>One query hit: the entity and refs to its queried components.</summary>
                    public readonly struct GroupResult<{{tps}}>
                {{Constraints(n, "        ")}}
                    {
                        private readonly Context _context;
                        private readonly int _entityId;
                {{Lines(n, i => $"        private readonly ushort _b{i};")}}

                        internal GroupResult(Context context, int entityId, {{List(n, i => $"ushort b{i}")}})
                        {
                            _context = context;
                            _entityId = entityId;
                {{Lines(n, i => $"            _b{i} = b{i};")}}
                        }

                        public int EntityId => _entityId;

                        public ref Entity Entity
                        {
                            [MethodImpl(MethodImplOptions.AggressiveInlining)]
                            get => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_context.Entities), _entityId);
                        }

                {{Lines(n, i => $$"""
                        public ref T{{i}} Component{{i}}
                        {
                            [MethodImpl(MethodImplOptions.AggressiveInlining)]
                            get => ref _context.ColumnRef<T{{i}}>(_b{{i}}, _entityId);
                        }

                """)}}
                    }


                """);
        }

        sb.Append("}\n\nnamespace EasyEcs.Core.Enumerators\n{\n");
        for (int n = 1; n <= MaxArity; n++)
        {
            string tps = TypeParams(n);
            sb.Append($$"""
                    /// <summary>Allocation-free query enumerator; safe under structural changes during iteration.</summary>
                    public struct GroupResultEnumerator<{{tps}}> : IDisposable
                {{Constraints(n, "        ")}}
                    {
                        private readonly Context _context;
                        private ArchetypeCursor _cursor;
                {{Lines(n, i => $"        private readonly ushort _b{i};")}}

                        internal GroupResultEnumerator(Context context, Archetype[] archetypes, {{List(n, i => $"ushort b{i}")}})
                        {
                            _context = context;
                            _cursor = new ArchetypeCursor(archetypes);
                {{Lines(n, i => $"            _b{i} = b{i};")}}
                            Current = default;
                        }

                        public GroupResult<{{tps}}> Current { get; private set; }

                        /// <summary>Number of alive matching entities (sums archetype counts, does not iterate).</summary>
                        public readonly int Count => _cursor.AliveCount;

                        public readonly bool IsEmpty => !_cursor.AnyAlive();

                        public GroupResultEnumerator<{{tps}}> GetEnumerator() => this;

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public bool MoveNext()
                        {
                            if (!_cursor.MoveNext(out int id))
                                return false;

                            Current = new GroupResult<{{tps}}>(_context, id, {{List(n, i => $"_b{i}")}});
                            return true;
                        }

                        public void Dispose()
                        {
                        }
                    }


                """);
        }

        return sb.Append("}\n").ToString();
    }
}
