# EasyEcs

Allocation-free archetype ECS for .NET 10: game servers, Godot 4 games (`samples/Godot`), simulations, real-time backends. Library: `EasyEcs.Core` (targets `net10.0` only),
published to NuGet as `EasyEcs`. Public API and code comments are in English.

## Commands

```bash
dotnet build                                                   # whole solution
dotnet test                                                    # NUnit
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*'            # all benchmarks
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*Iterate*'    # a subset
tools/bench.sh BulkBenchmark Features.RemovalComparison --sync   # classes, resumable, merged into the site's numbers
tools/bench.sh --site --sync                                   # every class the site shows (before a release)
```

A full run of both projects takes hours. Run what a change touches (its benchmark and the neighbors the definition of
done names) and merge it with `--sync`; `--site` refreshes the site's numbers; `--all` adds the classes that serve
A/B comparisons only.

Both benchmark projects use the job in `benchmarks/EasyEcs.Benchmark/BenchmarkConfig.cs` (no `--job` needed).

The website (https://ecs.xgamedev.net) is built from `docs/content`: `docs/` is its project (`bun run check`,
`bun run build`; `docs/CLAUDE.md`), and `docs/WRITING.md` is how a page is written. Each page is
`<page>.mdx` + `<page>.zh.mdx` (English and Chinese), and its C# fences compile (`tools/DocCheck`). Benchmark
numbers on the site come only from `docs/src/data/bench.json` (`docs/scripts/sync-benchmarks.ts` over the
BenchmarkDotNet reports); pages never type a number.

`global.json` requires the .NET 10 SDK (`rollForward: latestFeature`).

## Layout

```
src/EasyEcs.Core/              net10.0 library, namespace EasyEcs (hand-written shared core only)
  Contracts.cs                 IComponent, ITracked, ISingletonComponent, INoStructuralChanges and the other markers
  Declarations.cs              attributes whose code the generators write: [Sum]/[Product]/[Min]/[Max]/[All]/[Any]
                               (a reduction), [IndexKey]/[UniqueKey]/[Group] (an index key, a group), and world setup:
                               [GroupBy], [WhenTargetDestroyed], [NotSaved]
  Entity.cs                    the entity handle (id, version, world): Has/Get/Read/TryGet/GetOrAdd/SetIfChanged/
                               Disable/Enable/Clone/Destroy
  Entity.Relations.cs          entity.Link / Unlink / Links (IRelation buffers + their index)
  EntityId.cs                  index + incarnation (8 bytes, unmanaged): how data refers to an entity
  EntityMeta.cs                16-byte per-entity record (archetype, version, slot)
  EntitySpan.cs                the entities of a batch job's run of rows (lazy handles over the run's EntitySlots; its
                               table and first row, through which a Mut<T> of the run stamps its row)
  EntityDebugView.cs           what a debugger shows for an entity (Entity.Describe)
  EcsSystem.cs                 [EcsSystem] (a system: a ref struct of static members, never instantiated), the hooks
                               [OnCreate] / [OnUpdate] / [OnDestroy] (static methods taking the World, the SystemHandle,
                               both or neither), [UpdateInterval(n, Phase = p)] (the cadence, read from the generated code);
                               SystemHandle.cs: what world.AddSystem<T>() / System<T>() / TryGetSystem<T>() / Systems
                               hand out (Enabled, Error, RunCount: completed runs, the slice of a sliced method;
                               LastRunTick, Stats, Type, World), a (world, slot, version) that throws once its system
                               is removed; SystemCode.cs: ISystemCode (a system's generated Update / Create / Destroy,
                               access and cadence) and ISystemSet (one generated __EasyEcsSystems per assembly: the
                               switch over its systems' codes, registered by a module initializer, weakly held for
                               collectible assemblies), the registry World.AddSystem<T> resolves a type through
  ForEachAttributes.cs         [ForEach] (Parallel, Slices, ChunkRows, SimdMode), [With<T>], [Without<T>], [WithAny<T>], [Changed<T>],
                               [Removed<T>] ([ChangedAny<..>] / [RemovedAny<..>] are generated: Filters.g.cs),
                               [Consume] (the visited rows destroyed after the pass), [ForEntity] (a static method run
                               on one entity through a generated entry point)
  Maybe.cs                     Maybe<T>: an optional component parameter of a [ForEach] method
  Mut.cs                       Mut<T>: a [ForEach] parameter stamped only on the rows it writes (Write, SetIfChanged);
                               Values.Same, the boxing-free equality of SetIfChanged
  Reduce.cs                    IReduction<T> + Reduce<T> (taken by ref): a [ForEach] method's partial result per chunk,
                               combined in chunk order into a singleton when the pass ends (OpenMP reduction)
  Scratch.cs                   Scratch<T>: per-thread working memory of a [ForEach] method, never shared, never reset
  Lookup.cs                    Lookup<T> + LookupExtensions: read-only lookups of any entity (and index keys) from a
                               [ForEach] method, parallel ones included; the system reads T, so no writer shares its stage
  Tile.cs                      grid tiles: ITileShape / ITile<TKey> (a shape, halo, neighborhood, boundary and a key),
                               Tile<TCell, TTile> (a [ForEach] parameter: up to 26 neighbors found by generated code,
                               TileNeighbors), Pad into a [ThreadStatic] texture padded with the halo and a row mask,
                               PaddedTile.Rows (TZCNT walk), TileCursor.Load128, ChangedEdges / Facing (sleeping tiles)
  Emit.cs                      Emit<T>: records a [ForEach] pass appends to a pooled list per chunk (EmitPool), joined in
                               chunk order into singleton Emitted<T> (a DynamicBuffer<T>; passes append, readers clear)
  EntityRandom.cs              EntityRandom: deterministic random numbers, a pure function of (seed, entity, tick, stream)
                               (four SplitMix64 finalizer rounds); a singleton (Seed, Tick) and static members
  Interleaved.cs               the deinterleaving loads / interleaving stores of generated vector loops over components
                               of 2 to 4 fields of one lane type, and the lane masks of the passing rows of a word
  Lanes.cs                     Lanes.Compact: stream compaction of the elements a lane mask selects (AVX-512 compress),
                               for batch methods and CollectIds
  SystemGroups.cs              ISystemGroup, [UpdateInGroup<T>] (World.Update<T>() runs a group)
  SystemOrder.cs               [UpdateBefore<T>] / [UpdateAfter<T>]
  SystemAccess.cs              a system's read/write sets (generated from [ForEach] parameters) and the conflict rule
  SystemStats.cs               per-system run counts, times, allocated bytes and visited entities
                               (WorldOptions.MeasureSystems)
  ScheduleInfo.cs              what World.DescribeSchedule returns (groups, stages, systems, waits, registration-only
                               pairs) and its ToString / ToMermaid
  World.cs                     entity lifecycle (create/destroy, id recycling, lookup, enumeration, Instantiate of a
                               Prefab, with given components too, and Entity.Clone: one row copy, CloneRow)
  World.Defaults.cs            TypeDefaults: the setup a type declares ([GroupBy], [WhenTargetDestroyed]), applied by
                               every world from its first table holding the type (rules pending until a destroy or
                               the index needs them); what a world set up itself is never overridden
  World.DestroyLog.cs          WorldOptions.DestroyLogCapacity: the ring of (EntityId, tick) records of freed ids,
                               DestroyedSince / DestroyLogOldestTick
  World.Components.cs          AddBit/AddBits/RemoveBit/RemoveBits (shared core of every Set/GetOrAdd/Remove/Disable;
                               SetRow / EnableRow for one component), passes (BeginPass/EndPass, tombstones closed by
                               the last pass), CarryOver
  World.Bulk.cs                SetAll / RemoveAll / DisableAll / EnableAll (IRowOp structs, whole words of rows at once)
  World.Archetypes.cs          archetype registry, transition edges, copy-on-write query cache, CompactArchetypes,
                               the structural gate (Structural())
  StructuralGate.cs            owner flag (plain check-and-set, throws when reentered or while frozen: Freeze/Thaw around
                               parallel regions, batch jobs and INoStructuralChanges passes) +
                               shared CAS gate for the cold paths parallel stages reach; StructuralScope / SharedScope
  World.Systems.cs             system groups, ordering, parallel stages (consecutive non-conflicting systems),
                               synchronous Update / Update<TGroup>, Dispose
  World.Parallel.cs            allocation-free runner (caller + reusable thread-pool work items, adaptive helper waking)
                               for system stages and the row chunks of parallel [ForEach]; World.For.cs: ForParallel
                               (IForRange over a range, IForSpan<T> over a buffer's elements) on the same runner
  World.Singletons.cs          per-world singletons (ref returns)
  World.Indexes.cs             world.Index<T, TKey>() (one ValueIndex per indexed component type) and its hooks
  ValueIndex.cs                IIndexed<TKey> + ValueIndex: key -> one id array per key (IndexHead), built by the first
                               lookup, kept by Set / CreateEntities / GetOrAdd / Remove / Destroy
  UniqueIndex.cs               IUniqueIndexed<TKey> (one entity per key, an eager index that every write claims keys in
                               before it changes anything) and index.Get / TryGet
  OrderedIndex.cs              sorted walks and ranges of a value index with a comparable key
  Relations.cs                 IRelation buffers + RelationIndex<T> (entities linking to a target)
  Hierarchy.cs                 ChildOf (the hierarchy relation); World.Hierarchy.cs: Children, destroy cascades,
                               TargetPolicyExtensions (what happens to links when their target is destroyed)
  World.Groups.cs              GroupBy: tables split by the value of an IGroup component
  Cleanup.cs                   ICleanupComponent + Retired tag (entities kept alive until their cleanup components go)
  Disabled.cs                  Disabled<T> tag (the value kept, the component hidden from queries); its bit is a
                               non-generic DisabledTag per component (ComponentInfo.DisablerOf)
  DynamicBuffer.cs             DynamicBuffer<T> (a list per entity as a tracked component; writes are ref-this extensions)
                               and its BufferStore
  Prefab.cs                    a recipe of component values outside any world (one row of a private world)
  World.Snapshots.cs           Capture / Restore (WorldSnapshot.cs holds the copies)
  World.Serialization.cs       Save / Load of a whole world (SaveWithout(output, exclude) / LoadKeeping(input, keep) for a restart);
                               TypeRegistry.cs: the types a save may name (generated)
  ComponentSet.cs              a set of component bits and singleton slots: what SaveEntities writes, what Save leaves
                               out, which singletons Load keeps
  World.Migration.cs           SaveEntities / LoadEntities / MoveEntities: a group of entities to another world or process
  World.Types.cs               RemoveTypes(assembly): unload a plugin's types from a world; Collectible.cs: forgetting
                               the types of an unloaded AssemblyLoadContext process-wide
  World.Introspection.cs       world.DescribeTables() (tables, columns, sizes), Entity.Describe() for tools and debuggers,
                               world.DescribeSchedule()
  World.Trim.cs                TrimExcess: drop empty tables, shrink the others, release side buffers, renumber live
                               snapshots (tracked weakly)
  WorldOptions.cs              configuration (ParallelSystems, MaxDegreeOfParallelism, MeasureSystems, DestroyLogCapacity,
                               capacities)
  Query.cs                     QueryFilter (With/Without/WithAny/Changed/Removed, the ChangedAny / RemovedAny sets,
                               Since, the slice fields; partial: Filters.g.cs, generated by ArityGenerator, holds
                               ChangedAny<T1..Tn> / RemovedAny<T1..Tn>), QueryKey, QueryMatch, RowPlan
                               (cached per query and table), RowFilter (skip / all rows / test presence bits and change
                               ticks, skipping unwritten blocks and words), Tombstones / NoTombstones (the two compiled
                               forms of a structural loop), QueryWalk (the cold state of a walk, pooled per thread)
  QuerySlice.cs                QueryFilter.Slice: the rows of a table one slice visits (64-row words, rotated per table)
  FilterOverflow.cs            interned filter bits past 255, and FilterAnySets: the ChangedAny / RemovedAny sets
                               interned whole (a QueryFilter holds ids, no object reference)
  TypeIndex.cs                 dense per-type index families (ComponentType bit, SingletonType slot, SystemGroup
                               index, ComponentSignature of multi-component calls) + SingletonBox; a tracked
                               component's TicksBit: its hidden RowTick column (Query.cs), non-generic so Native
                               AOT sees no generic recursion (IL3054)
  Archetype.cs                 a table: mask, EntitySlot rows (version + id), one column per component (unmanaged
                               columns pinned and 64-byte aligned), presence bits + absent counts per column,
                               per-bit and per-signature add transitions (copy plans); swap-remove / tombstones,
                               FillHoles; change-tick summaries (LastWrite per column, WordTicks per 64 rows, BlockTicks
                               per 4,096 rows for writes and for removals), Stamp/StampRows/StampWord/SetTick,
                               MarkPresent/MarkAbsent, CloneRow; Resize (growth and Trim, which shrinks a table to a
                               power of two); TableCopy: the copy of a table's rows a snapshot or load holds, naming its
                               table (reused for no other)
  ComponentInfo.cs             per-type column operations (size, managed, create/grow/copy/clone/clear) indexed by bit;
                               Pair links a tracked component with its change-tick column
  BitMask.cs                   256-bit SIMD bitset with overflow (component masks, query sets)
src/EasyEcs.Generator/         Roslyn generators + analyzer (netstandard2.0, required by Roslyn hosts)
  ArityGenerator.cs            T1..T9 families of EasyEcs.Core (Entity.Set/Remove, World.CreateEntity/CreateEntities/
                               DestroyAll, Instantiate with components, IForEach/IForEachEntity/IForEachBatch/
                               IForEachMasked, ForEach/ForEachEntity/ForEachBatch/ForEachMasked/ForEachParallel/
                               ForEachEntityParallel/ForEachBatchParallel with their row loops, CollectIds,
                               Query/Count/Any, QueryItem/QueryEnumerator, Filters.g.cs: QueryFilter.ChangedAny /
                               RemovedAny and their attributes), one template each
  SystemGenerator.cs           [ForEach] -> struct jobs (per entity or per run of rows; INoStructuralChanges when
                               the method cannot change structure; Parallel -> chunked jobs; [Changed<T>] /
                               [Removed<T>] -> Since(LastRunTick + 1) and a read of T; Mut<T> -> a write stamped per
                               write; Slices -> the slice of the run; ChunkRows -> the chunkRows argument; [Consume] -> DestroyAll of
                               the filter after a pass without structural changes, a destroy per row otherwise;
                               [ForEntity] -> the IForEachEntity job and an entry point Owner.Method(entity, values)
                               over entity.Run; [EcsSystem] -> the code struct __X_SystemCode (hooks, then the
                               methods in declaration order; [UpdateInterval] -> its Interval / Phase), and per
                               assembly the set __EasyEcsSystems: one Add per system and per closed generic system any
                               API names (AddSystem<G<P>>, [UpdateAfter<G<P>>], ...), a switch per hook; no system
                               needs partial, its methods are reached through UnsafeAccessor)
  ComponentGenerator.cs        C# 14 extension members per component / singleton: entity.X (ref T; ref readonly for
                               tracked or indexed), HasX, TryGetX, GetOrAddX, SetX(ctor args), SetXIfChanged, RemoveX,
                               tags IsX,
                               world.XIndex, singletons world.X / HasX / GetOrAddX / SetX / RemoveX, query items
                               item.X per Query<T1..Tn> shape the code calls, TypeRegistry registration; classes carry [GeneratedCode("EasyEcs.Generator")], so Symbols.IsGenerated
                               lets the analyzer and SystemGenerator treat SetX / GetOrAddX / RemoveX as structural
  EasyEcsAnalyzer.cs           EECS002-006, EECS009-024, EECS035-036, EECS054, EECS056, EECS070 and EECS072-073 (the
                               declaration and [ForEach] rules of the range EECS002-089 that are not performance rules;
                               generators never report diagnostics)
  EasyEcsAnalyzer.Systems.cs   EECS007-008 and EECS074-077: systems as declarations (state in a static field, a reach
                               into another system; an [EcsSystem] that is no ref struct, an instance member, a hook
                               no signature fits, a system API given a type that is no [EcsSystem])
  UnusedSuppressor.cs          suppresses IDE0051 / IDE0052 on [ForEach], [ForEntity] and hook methods (called through
                               UnsafeAccessor; Rider reads their [MeansImplicitUse])
  EasyEcsAnalyzer.Performance.cs  EECS025-028, EECS030-034, EECS039, EECS046, EECS052, EECS053 and EECS055 (with the
                               check of EECS056):
                               code that works but costs (write access that
                               only reads, unused or per-row lookups in [ForEach] methods and jobs, allocations per
                               update, per-entity structural changes and counting over queries, creation loops, the
                               generated member, Update in a system, a Mut<T> of an untracked component, a Query loop
                               that only reads, a Set right after a creation, a tracked ref written on some rows only, a
                               method left without a vector loop); each computes its fix as
                               diagnostic properties for EasyEcs.CodeFixes
  PartGenerator.cs             [Sum]..[Any], [IndexKey]/[UniqueKey], [Group] -> a part of the partial struct (IReduction<T>,
                               IIndexed<K>.Key, IGroup.Group); Parts.cs: the models and Problems read once for the
                               generator and the analyzer (other generators read keys and groups through Symbols)
  Kernel.cs                    a per-entity [ForEach] method as a Vector<T> loop (Kernel.Vectorize: the loop, or the
                               reason EECS055 / EECS056 report); Kernel.Narrow.cs: byte / sbyte / short / ushort lanes
                               (int values followed as low bits and an interval, saturating idioms); Kernel.Struct.cs:
                               struct values (Vector2/3/4 fields, new T(...), with) as one vector per lane;
                               Kernel.Wide.cs: components of up to 16 words on AVX-512 (fields gathered by a pruned
                               permute tree, where it pays)
  EasyEcsAnalyzer.Usage.cs     EECS037-038, EECS040-045 and EECS047-051: calls and declarations that compile but do not
                               do what they say (a job's WriteMask, a dropped copy, SetIfChanged that cannot compare, calls
                               a pass refuses, an index added to while walked, a record key that prints itself, a sliced
                               filter where every row is needed, the world's own tags written by hand, a ref used after
                               a structural change, Disable of a component that cannot be disabled, filters no entity
                               passes, a second group in one filter, an update during a pass)
  EasyEcsAnalyzer.Parallel.cs  EECS060-061: code on several threads (a parallel [ForEach] method, the Execute of a job
                               run by ForEachParallel or ForParallel) that writes memory every thread shares, a parallel
                               job's field read after the call; EECS071: System.Random in a [ForEach] method (fix:
                               EntityRandom); EECS088: two EntityRandom draws of one stream for one entity in pass code
                               (fix: the next free stream), or a draw in a loop that does not vary its stream
  EasyEcsAnalyzer.Parts.cs     EECS062-069: generated parts (not partial, hand-written members, fields no attribute
                               combines), hand-written reductions, keys and groups the attributes could declare, a wrong
                               Identity, a setup attribute on a type it does not fit, a managed type without a Nino
                               formatter (Warning when the code saves what holds it: entity saves write no singletons)
  EasyEcsAnalyzer.Forms.cs     EECS080-085: what the generator declares for code written by hand (a one-entity job
                               [ForEntity] writes, a [ForEach] method that destroys every entity it visits: [Consume])
                               and those attributes (and [UpdateInterval]) where they cannot apply (EECS082 is retired)
  EasyEcsAnalyzer.Parameters.cs  EECS086, EECS087, EECS089: a [ForEach] method doing by hand what a parameter does per
                               chunk (a singleton reset by system code and accumulated row by row: Reduce<T>, reported
                               at the end of the compilation; other entities read through the World: Lookup<T>; records
                               appended to a singleton's list: Emit<T>); no code fix (the reset, the readers move too)
  Uses.cs                      whether a use of a value may write it (EECS025's conservative read/write test)
  Threads.cs                   EECS014's check: does a structural call in a delegate handed to another thread race;
                               the writes of code on several threads that reach shared memory (EECS060, EECS061)
  Symbols.cs                   symbol checks shared by generator and analyzer
  Code.cs                      tiny string helpers for the generators' templates (List, Lines, TypeParams)
  EquatableArray.cs            array with value equality (generator models cache between edits)
  Polyfills.cs                 IsExternalInit for netstandard2.0 (records); do not delete
src/EasyEcs.CodeFixes/         EasyEcsCodeFixes.cs: fixes for the analyzer's diagnostics (bundled in the package);
                               EasyEcsCodeFixes.Forms.cs: EECS080 (a job and its calls to [ForEntity], solution-wide),
                               EECS084 ([Consume]); EasyEcsCodeFixes.Systems.cs: EECS074 (a ref struct), EECS075
                               (static), and the migration of a class deriving from the removed EcsSystem (the
                               compiler's CS0246) into an [EcsSystem] ref struct
tests/EasyEcs.UnitTest/        NUnit, one file per feature: Entity, Component, Presence, Query, System (and stats),
                               ForEachSystem (and Slices), Singleton, Concurrency, Parallel, Runner, Run (entity.Run),
                               ForEntity, Consume, Form (EECS080 / EECS084),
                               Changed (and Mut, ChangedAny), Removed (and the destroy log), Instantiate, DynamicBuffer,
                               Index (and unique keys), Tile, OrderedIndex, Relation, Hierarchy, TargetPolicy, Group, Cleanup,
                               Disable, Bulk, Move, Part (generated parts, EECS062-069), Snapshot, Serialization (and restart saves), Unload, Introspection
                               (and Trim, DescribeSchedule), Generic, Allocation (0 B budget), Emit, ForParallel, EntityRandom,
                               Generator (diagnostics + caching), CodeFix, BattleServer (the world of
                               docs/content/example.mdx as a whole, linked from EasyEcs.Benchmark); Fixtures.cs holds
                               the shared test components
tests/EasyEcs.Aot/             the battle server (BattleServer.cs) published with Native AOT, warnings as errors, and
                               run by CI: no AOT warning and the native binary passes its checks
benchmarks/EasyEcs.Benchmark/  BenchmarkDotNet: WorldSimulationBenchmark / MultiWorldBenchmark (headline, realistic
                               server tick; the world itself is WorldSimulation.cs), BattleServerBenchmark
                               (BattleServer.cs, every feature; docs/content/example.mdx includes its regions), one
                               benchmark per feature (Access: the analyzer's performance rules, Bulk, CallSite,
                               ChangeTracking (+ ChangeTrackingWrite: Mut<T>, ChangeTrackingRead: Read, ChangeTrackingSomeRows: EECS053), Cleanup, Consume ([Consume] in bulk and per row against a hand-written Destroy), Count, DestroyLog, Emit, EntityRandom, ForEntity (against the hand-written job), ForParallel,
                               DynamicBuffer (+ ChunkChurn: MaxPooledLength), Generator (the generators per keystroke and the analyzer per
                               build, through
                               Roslyn), Group, Hierarchy, Histogram (keyed reductions), Index (+ unique keys), Introspection (+ Schedule),
                               Maybe, Migration, OrderedIndex, Ordering, ParallelForEach, ParallelSystems (Measure),
                               Prefab, Relation, Runner, SingleEntity, Slices, Snapshot (+ SaveWithout / LoadKeeping),
                               Sparse, SystemDispatch (what a system costs per update apart from its rows), Trim,
                               Unload) + MicroBenchmarks.
                               Always measure with BenchmarkDotNet.
benchmarks/EasyEcs.Benchmark.Plugin/  a collectible plugin (Scoring.cs) the Unload benchmark loads and unloads
benchmarks/EasyEcs.Comparison/ BenchmarkDotNet against Arch, Friflo.Engine.ECS and DefaultEcs, every result checked in
                               setup: the headline world (WorldSimulation.cs, compiled unchanged, *Simulation.cs ports);
                               OperationBenchmark (per operation); FeatureBenchmark (change tracking, removal detection,
                               indexes); QueryComparisons (value queries, parallel queries, saves); StructureComparisons
                               (hierarchy, relations, disable, structural changes); BulkComparisons (bulk changes,
                               spawns); SystemComparisons (parallel systems)
docs/content/                  the docs (source of https://ecs.xgamedev.net), each page <page>.mdx + <page>.zh.mdx:
                               index, learn pages (what-is-ecs, features, getting-started, ecs-principles, example: the
                               battle server through regions of BattleServer.cs, cheat-sheet, glossary), samples/ (one
                               page per sample of samples/, its files included, its output checked by
                               check-sample.sh), guide/ (one
                               page per concept, analyzer lists EECS002-089), compare pages (performance, why-fast,
                               comparison, vs-*, migrate-from-*), reference pages (api: API + complexity, read by
                               tools/ApiDocs; architecture, faq, ai, decisions, roadmap, changelog); reference/ (API
                               reference generated by tools/ApiDocs, <page>.mdx + <page>.zh.mdx); README.md (GitHub index);
                               diagrams in mermaid; meta.json files order the site's sidebar
docs/                          the docs site: React Router 7 + Fumadocs, prerendered, Bun (docs/CLAUDE.md);
                               app/ routes, src/ components and libs, src/data/bench.json, scripts/, functions/
                               (Cloudflare Pages Functions), WRITING.md (page anatomy, tone, numbers); public/
                               easyecs-rules.md (the rules file for AI assistants, docs/content/ai.mdx)
samples/                       runnable projects in learning order (README.md): HelloWorld, GameLoop, SaveLoad,
                               MultiWorld, HotReload (Contracts / Plugin / Host), Actors, VoxelServer (a Minecraft-style
                               server), Godot; built and run by CI
                               (.github/scripts/check-sample.sh compares the deterministic ones with their README's
                               expected output). Each reads top-down: Components.cs, Systems.cs (or Systems/ per
                               responsibility), a short Program.cs, plumbing that is not the lesson in Host/ or
                               Support.cs, a README with the file map and the real output. The per-feature examples are the "Full example" sections
                               of the guide pages (doccheck=example fences)
templates/                     `dotnet new easyecs` (Components/, Systems/ one file per system, Program.cs = host,
                               --tests: one test file per system) and `easyecs-system` (EasyEcs.Templates package);
                               smoke.sh packs, creates, builds, runs and tests them
demo/EasyEcs.Demo/             the docs site's live demo (docs/content/demo.mdx): a .NET 10 WebAssembly app (no Blazor,
                               [JSExport] host in Demo.cs, Components.cs, Systems/ one file per system); the page
                               includes its files. docs/scripts/build-demo.sh publishes it into docs/public/demo
                               (committed: Cloudflare has no .NET); rerun it after changing the demo or EasyEcs.Core
tools/bench.sh                 benchmark classes one BenchmarkDotNet process per class (resumable, under a shared lock),
                               merged into docs/src/data/bench.json (--sync); --site runs the classes the site shows
tools/jitdisasm.sh             the Tier1 machine code of a [ForEach] method's job (DOTNET_JitDisasm), to check a vector
                               loop: no call in the loop body, no needless range check
tools/DocCheck/                compiles the C# fences of the docs pages (`doccheck=` flags on a fence; `example`
                               fences of a page compile together as its full example)
tools/ApiDocs/                 writes docs/content/reference/ from the XML docs and the complexity tables of api.mdx /
                               api.zh.mdx (Complexity.cs), in English and in Chinese: zh/<page>.xml holds the Chinese
                               text per doc id with a hash of the English it translates (Translations.cs; zh/README.md:
                               workflow and glossary); missing, stale or orphaned entries fail the tool
```

## Definition of done for a feature

A feature, rule or change ships only with every part below that applies to it. Check them off before merging; a
fresh session working on a new request follows this list.

1. **Core**: one shared non-generic core with thin typed wrappers. T1..T9 variants come from `ArityGenerator`
   templates. Hot paths allocate nothing and run no LINQ, closures or boxing. Each invariant that skips a bounds
   check is documented next to the code. Structural changes go through the gate, and `StorageVersion` is bumped
   where a pass must notice.
2. **Generator**: whatever users would otherwise write twice is generated.
   - Declarations users write once, such as an attribute on a field or type, are read through one `Symbols` helper.
     Both the generator and the analyzer use it, so "skipped by the generator" is exactly "reported by the analyzer".
   - Models are equatable and symbol-free, with a tracking name per stage and a caching assertion in
     `GeneratorTests`, plus a generated-output test.
   - Generators report no diagnostics.
   - Partial types and nested types are handled; the declaring chain is partial or reported.
3. **Analyzer**: every misuse that compiles and every pattern that works but costs gets an EECS rule.
   - Severity: Error when the code is wrong, Warning when it is likely wrong, Info for performance hints.
   - The descriptor message names the type or member and the fix.
   - For a fallback to scalar, the reason comes from the same function the generator uses (`Kernel.Vectorize`), so
     EECS055 or EECS056 names exactly why.
   - Tests cover the positive cases, the negative cases and false-positive guards.
   - Update the rule list and range in this file, in `docs/content/guide/analyzer.mdx` and `.zh.mdx`, and in
     `docs/public/easyecs-rules.md`.
4. **Code fix** (`EasyEcs.CodeFixes`) for every rule whose fix is mechanical, including converting hand-written
   patterns to the generated or attribute form. The analyzer computes the fix as diagnostic properties, and a
   `CodeFixTests` case covers it.
5. **Tests** in `tests/EasyEcs.UnitTest`, one file per feature:
   - behaviour;
   - edge cases (stale ids, empty tables, structural changes mid-pass);
   - the allocation test (0 B in steady state);
   - for parallel code, bit-identical results at 1 and 4 threads;
   - for vector loops, equivalence with the scalar method.
6. **Benchmarks** with BenchmarkDotNet only.
   - Measure before and after, two rounds each, with the A/B copy outside the repo tree (duplicate project names
     break BDN).
   - When several agents share a machine, run one benchmark at a time behind a shared lock file (`flock`).
   - Check the Tier1 asm of new hot loops (JitDisasm): no calls in the loop body, no unexpected range checks.
   - A change ships only if it is a net win at 0 B with no regression in WorldSimulation, BattleServer and the
     feature's neighbors.
   - Add a benchmark for anything not covered yet.
7. **Docs**, written per `docs/WRITING.md`, en and zh structurally identical:
   - the feature's guide page follows the page anatomy (what it is, when to use it as idea/warn Callouts, how to use
     it with compiling examples, a full example, how to use it fast, what it costs, pitfalls with their EECS rule,
     related Cards);
   - components fit their content: Tabs for alternatives (`You write` / `The generator writes`, `Reported` /
     `Fixed`), Steps for sequences, TypeTable for options, Accordions for multi-file examples, Files for trees;
   - numbers appear only through `docs/src/data/bench.json` (BenchTable and the related components), never typed on
     a page (decision records may quote numbers);
   - complexity rows go in `docs/content/api.mdx` and `.zh.mdx`;
   - XML docs need Chinese entries in `tools/ApiDocs/zh` (`dotnet run --project tools/ApiDocs`);
   - add a decision record (`decisions.mdx` + `.zh.mdx`) for every non-obvious choice and every dropped idea, and
     update the roadmap;
   - add a changelog entry and a README "What's new" entry;
   - a breaking change also needs a migration entry;
   - update the rules file for AI assistants (`docs/public/easyecs-rules.md`) and this file's layout;
   - the battle server (`BattleServer.cs` regions and `example.mdx`) and the samples use the feature where it
     belongs, as exemplary code.
8. **Commits and merge**:
   - logic, docs and CI go in separate commits;
   - parallel work happens in worktrees on branches off the work branch;
   - `verify` runs before anything reaches master;
   - renames of released public API are reported first and applied after the owner's approval (`audit-naming`).

## Workflows

Repeatable procedures live in `.claude/skills/<name>/SKILL.md` (Claude Code loads them as skills; any agent or
person can follow them as checklists). Update a skill when a round teaches something new.

| Skill | When |
|---|---|
| `verify` | before merging to master: build, tests, DocCheck, ApiDocs, docs site, samples, templates, demo |
| `audit` | a review round: parallel audits in worktrees on the work branch, triage, merge, docs pass, verify |
| `audit-core` | redundancy, duplicated logic, coupling, lifecycle edges and hot-path cost in `EasyEcs.Core` |
| `audit-generator` | what users still write twice, plain forms the generator could emit fast (vector loops, parameters, schedule), generator speed, incremental best practices, caching tests |
| `audit-analyzer` | missing EECS rules (wrong, costly, hand-written code), overlaps, false positives, code fixes to the generated forms, shared `Symbols` checks |
| `audit-naming` | ambiguous or wrong names in the public API, generated members, analyzer descriptors, internals |
| `audit-perf` | the next performance win: ideas from Burst, CUDA, stencil codes, OpenMP, MPI, OS schedulers and .NET 10, proven with BenchmarkDotNet |
| `audit-docs` | every part of a feature page (when, recommended form, fast form, costs, complexity, pitfalls), stale names, exemplary examples, duplication, en/zh drift |

## Engineering principles (apply to every change)

1. **Performance is the first constraint, and GC is part of performance.** Hot paths (queries, iteration, component
   access, structural changes after warm-up) allocate nothing. No LINQ, closures, boxing, `params object[]`,
   string formatting or iterator blocks there.
2. **CPU**: branch-light inner loops, no redundant bounds checks (use `Unsafe`/`MemoryMarshal` only behind a
   documented invariant), inline fast paths with `NoInlining` cold paths (throws, growth), SIMD where it pays.
3. **Memory / cache lines (64 bytes)**: pack data touched together into one struct (e.g. `EntityMeta`, 16 bytes =
   4 per line), keep hot structs power-of-two sized so they never straddle lines, pad contended fields apart,
   keep cold data out of hot structs. Bound memory: recycle ids/slots/buffers instead of growing.
4. **Thread safety is explicit**: every shared structure documents its contract (single owner vs. lock vs.
   lock-free). The archetype lists lock-free readers walk are immutable snapshots (copy-on-write arrays
   published with `Volatile.Write`); tables (entity rows, columns, presence bits) are mutated under the world's
   structural gate (`World.Structural()`), which belongs to the owner thread like component values do.
5. **Concise, elegant, no duplication**: write one implementation and generate arity variants (`EasyEcs.Generator`)
   instead of copy-pasting T1..T9 code by hand. Prefer small shared non-generic cores with thin typed wrappers.
6. **Every change is proven**: unit tests for behavior (`EasyEcs.UnitTest`) and BenchmarkDotNet numbers for
   performance (`EasyEcs.Benchmark`), before and after.

## Rules

- **Follow the ECS principles in `docs/content/ecs-principles.mdx`, including in tests, benchmarks and docs**: components and
  singletons are plain data structs; one system per responsibility; systems are stateless (state goes into
  components or singletons, EECS007) and never reference other systems (EECS008); state changes are data.
  Examples are what users copy, so they must be exemplary. The battle server example
  (`docs/content/example.mdx`) includes the `// #region` blocks of `benchmarks/EasyEcs.Benchmark/BattleServer.cs`:
  keep the regions when changing the file, update the page's walkthrough with it, and give a new feature a place in it.
- **Performance changes must be measured.** Run the relevant `EasyEcs.Benchmark` cases before and after; a change
  ships only if it is a net improvement (time and allocations) with no regression elsewhere. Add a benchmark
  when you optimize something not covered yet.
- **Zero allocation on hot paths**: queries, iteration, component access, add/remove after warm-up. No LINQ, no
  closures, no boxing, no `params object[]`. Use `Unsafe`/`MemoryMarshal` only where an invariant guarantees bounds
  (document the invariant next to the code).
- **Never hand-write arity variants**: T1..T9 APIs come from `ArityGenerator` templates. Change the template, not
  generated output (see `obj/**/EasyEcs.Generator/*.g.cs` to inspect it).
- **Generators follow the incremental rules**: `ForAttributeWithMetadataName` or syntax-only predicates first,
  equatable symbol-free models, no diagnostics from generators (add them to `EasyEcsAnalyzer` using `Symbols`), a
  tracking name per stage and a caching assertion in `GeneratorTests`.
- **Structural changes go through the gate** (`using (Structural())`: owner thread, not reentrant; `Shared()` only for the cold paths a parallel stage's threads
  reach on their own: query cache misses, a missing singleton added by `GetOrAddSingleton`, closing tombstones) and through `AddBit(s)` /
  `RemoveBit(s)` / `MoveTo` / `RemoveRow` (and `Archetype.FillHoles`, which rewrites rows and slots); never mutate
  `Archetype.Slots`, presence bits or `EntityMeta` directly elsewhere.
- **Per-entity state outside the tables is `EntityMeta` only** (16 bytes). Do not add per-entity arrays or fields
  without measuring: the component set is the archetype mask (plus the row's presence bits). The one exception is
  opt-in: a value index keeps a node per entity id (`ValueIndex._nodes`, and `_byTarget` for relations), paid only by
  worlds that create one.
- **Every change a pass must notice bumps `StorageVersion`**: reallocation (columns, `Meta`, entity rows), row moves,
  closing tombstones, and presence bits flipping, so in-flight passes refresh their refs and re-read row filters.
- **Decisions**: every non-obvious choice, and every feature dropped after weighing it, gets a record in
  `docs/content/decisions.mdx` + `.zh.mdx` (decision, why, what was considered, numbers); `docs/content/roadmap.mdx` lists
  what is planned and what was dropped.
- **Docs**: diagrams are mermaid; `docs/content/api.mdx` lists the complexity of every public API (rerun
  `dotnet run --project tools/ApiDocs` after changing it); a new or changed XML doc comment of a public API needs its
  Chinese entry in `tools/ApiDocs/zh/` (the tool reports it); README is the landing page.
- **Entity ids are recycled**: any id-keyed API must also validate the version (`World.Live`, `Entity.ThrowStale`).
- Behavioral/breaking changes need an entry in the migration guide (`docs/content/migration.mdx` + `.zh.mdx`,
  https://ecs.xgamedev.net/en/docs/migration) and a README "What's new" entry.
- Commits: logic, docs and CI in separate commits. The author is the repository owner; the committer is the
  environment's signing identity so the signature shows as Verified. No session links in commit messages.
- The version lives only in `Directory.Build.props` (package, EasyEcs.Core and the bundled generator carry it).
- Releases are cut manually by the owner: GitHub Actions -> Release -> Run workflow on `master` with a version. Job
  `tag` bumps `Directory.Build.props`, builds and tests, commits `Bump to vX.Y.Z` and tags it; job `publish` packs that
  tag, pushes to NuGet through Trusted Publishing (OIDC, no API key secret; policy on nuget.org: repository EasyEcs,
  workflow `release.yml`) and creates the GitHub release with GitHub's generated notes (What's Changed from merged
  pull requests; New Contributors; Full Changelog), so changes land through pull requests with meaningful titles. A
  failed publish is retried with "Re-run failed jobs". Do not trigger releases unless asked.
