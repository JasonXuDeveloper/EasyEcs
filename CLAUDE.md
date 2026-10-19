# EasyEcs

Allocation-free archetype ECS for long-running .NET servers. Library: `EasyEcs.Core` (targets `net10.0` only),
published to NuGet as `EasyEcs`. Public API and code comments are in English.

## Commands

```bash
dotnet build                                                   # whole solution
dotnet test                                                    # NUnit
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*'            # all benchmarks
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*Iterate*'    # a subset
```

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
  Contracts.cs                 IComponent, ITrackChanges, ISingletonComponent, INoStructuralChanges and the other markers
  Entity.cs                    the entity handle (id, version, world): Has/Get/Read/TryGet/GetOrAdd/Disable/Enable/
                               Clone/Destroy
  Entity.Relations.cs          entity.AddRelation / RemoveRelation / HasRelation (IRelation buffers + their index)
  EntityId.cs                  index + incarnation (8 bytes, unmanaged): how data refers to an entity
  EntityMeta.cs                16-byte per-entity record (archetype, version, slot)
  EntitySpan.cs                the entities of a batch job's run of rows (lazy handles over the run's EntitySlots)
  EntityDebugView.cs           what a debugger shows for an entity (Entity.Describe)
  EcsSystem.cs                 OnCreate/OnUpdate/OnDestroy; SystemCode.cs: the generated jobs and access of a system type,
                               registered by a module initializer (systems need no partial)
  ForEachAttributes.cs         [ForEach] (Parallel), [With<T>], [Without<T>], [WithAny<T>], [Changed<T>], [Removed<T>]
  Maybe.cs                     Maybe<T>: an optional component parameter of a [ForEach] method
  SystemGroups.cs              ISystemGroup, [UpdateInGroup<T>] (World.Update<T>() runs a group)
  SystemOrder.cs               [UpdateBefore<T>] / [UpdateAfter<T>]
  SystemAccess.cs              a system's read/write sets (generated from [ForEach] parameters) and the conflict rule
  SystemStats.cs               per-system run counts and times (WorldOptions.MeasureSystems)
  World.cs                     entity lifecycle (create/destroy, id recycling, lookup, enumeration, Instantiate of a
                               Prefab and Entity.Clone: one row copy, CloneRow)
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
                               for system stages and the row chunks of parallel [ForEach]
  World.Singletons.cs          per-world singletons (ref returns)
  World.Indexes.cs             world.Index<T, TKey>() (one ValueIndex per indexed component type) and its hooks
  ValueIndex.cs                IIndexed<TKey> + ValueIndex: key -> one id array per key (IndexHead), built by the first
                               lookup, kept by Set / CreateEntities / Remove / Destroy
  OrderedIndex.cs              sorted walks and ranges of a value index with a comparable key
  Relations.cs                 IRelation buffers + RelationIndex<T> (entities linking to a target)
  Hierarchy.cs                 Parent (the hierarchy relation); World.Hierarchy.cs: Children, destroy cascades,
                               TargetRules (what happens to links when their target is destroyed)
  World.Groups.cs              GroupBy: tables split by the value of an IGroup component
  Cleanup.cs                   ICleanupComponent + Destroyed tag (entities kept until their cleanup components go)
  Disabled.cs                  Disabled<T> tag (the value kept, the component hidden from queries)
  DynamicBuffer.cs             DynamicBuffer<T> (a list per entity as a tracked component; writes are ref-this extensions)
                               and its BufferStore
  Prefab.cs                    a recipe of component values outside any world (one row of a private world)
  World.Snapshots.cs           Capture / Restore (WorldSnapshot.cs holds the copies)
  World.Serialization.cs       Save / Load of a whole world; SavedTypes.cs: the types a save may name (generated)
  World.Migration.cs           SaveEntities / LoadEntities / MoveEntities: a group of entities to another world or process
  World.Types.cs               RemoveTypes(assembly): unload a plugin's types from a world; Collectible.cs: forgetting
                               the types of an unloaded AssemblyLoadContext process-wide
  World.Introspection.cs       world.Describe() (tables, columns, sizes) and Entity.Describe() for tools and debuggers
  WorldOptions.cs              configuration (ParallelSystems, MaxDegreeOfParallelism, MeasureSystems, capacities)
  Query.cs                     QueryFilter (With/Without/WithAny/Changed/Removed/Since), QueryKey, QueryMatch, RowPlan
                               (cached per query and table), RowFilter (skip / all rows / test presence bits and change
                               ticks, skipping unwritten blocks and words), Tombstones / NoTombstones (the two compiled
                               forms of a structural loop), QueryWalk (the cold state of a walk, pooled per thread)
  FilterOverflow.cs            interned filter bits past 255 (a QueryFilter holds no object reference)
  TypeIndex.cs                 dense per-type index families (ComponentType bit, SingletonType slot, SystemGroup
                               index, ComponentSignature of multi-component calls) + SingletonBox + ChangeTicks<T>
                               (the hidden change-tick column of an ITrackChanges component)
  Archetype.cs                 a table: mask, EntitySlot rows (version + id), one column per component (unmanaged
                               columns pinned and 64-byte aligned), presence bits + absent counts per column,
                               per-bit and per-signature add transitions (copy plans); swap-remove / tombstones,
                               FillHoles; change-tick summaries (LastWrite per column, WordTicks per 64 rows, BlockTicks
                               per 4,096 rows for writes and for removals), Stamp/StampRows/StampWord/SetTick,
                               MarkPresent/MarkAbsent, CloneRow
  ComponentInfo.cs             per-type column operations (size, managed, create/grow/copy/clone/clear) indexed by bit;
                               Pair links a tracked component with its change-tick column
  BitMask.cs                   256-bit SIMD bitset with overflow (component masks, query sets)
src/EasyEcs.Generator/         Roslyn generators + analyzer (netstandard2.0, required by Roslyn hosts)
  ArityGenerator.cs            T1..T9 families of EasyEcs.Core (Entity.Set/Remove, World.CreateEntity/CreateEntities/
                               DestroyEntities, IForEach/IForEachEntity/IForEachBatch, ForEach/ForEachEntity/
                               ForEachBatch/ForEachParallel/ForEachEntityParallel with their row loops, Query/Count/Any,
                               QueryItem/QueryEnumerator), one template each
  SystemGenerator.cs           [ForEach] -> struct jobs (per entity or per run of rows; INoStructuralChanges when
                               the method cannot change structure; Parallel -> chunked jobs; [Changed<T>] /
                               [Removed<T>] -> Since(LastRunTick + 1) and a read of T), registered as SystemCode
  ComponentGenerator.cs        C# 14 extension members per component / singleton: entity.X (ref T; ref readonly for
                               tracked or indexed), HasX, TryGetX, GetOrAddX, SetX(ctor args), RemoveX, tags IsX,
                               world.XIndex, singletons world.X / HasX / GetOrAddX / SetX / RemoveX, query items
                               item.X per Query<T1..Tn> shape the code calls, SavedTypes registration; classes carry [GeneratedCode("EasyEcs.Generator")], so Symbols.IsGenerated
                               lets the analyzer and SystemGenerator treat SetX / GetOrAddX / RemoveX as structural
  EasyEcsAnalyzer.cs           EECS002-024 (generators never report diagnostics)
  Threads.cs                   EECS014's check: does a structural call in a delegate handed to another thread race
  Symbols.cs                   symbol checks shared by generator and analyzer
  Code.cs                      tiny string helpers for the generators' templates (List, Lines, TypeParams)
  EquatableArray.cs            array with value equality (generator models cache between edits)
  Polyfills.cs                 IsExternalInit for netstandard2.0 (records); do not delete
src/EasyEcs.CodeFixes/         EasyEcsCodeFixes.cs: fixes for the analyzer's diagnostics (bundled in the package)
tests/EasyEcs.UnitTest/        NUnit, one file per feature: Entity, Component, Presence, Query, System, ForEachSystem,
                               Singleton, Concurrency, Parallel, Runner, Changed, Removed, Instantiate, DynamicBuffer,
                               Index, OrderedIndex, Relation, Hierarchy, TargetPolicy, Group, Cleanup, Disable, Bulk,
                               Move, Snapshot, Serialization, Unload, Introspection, Generic, Allocation (0 B budget),
                               Generator (diagnostics + caching), CodeFix, BattleServer (the world of
                               docs/content/example.mdx as a whole, linked from EasyEcs.Benchmark); Fixtures.cs holds
                               the shared test components
benchmarks/EasyEcs.Benchmark/  BenchmarkDotNet: WorldSimulationBenchmark / MultiWorldBenchmark (headline, realistic
                               server tick; the world itself is WorldSimulation.cs), BattleServerBenchmark
                               (BattleServer.cs, every feature; docs/content/example.mdx includes its regions), one
                               benchmark per feature (Bulk, CallSite, ChangeTracking, Cleanup, Count, DynamicBuffer,
                               Group, Hierarchy, Index, Introspection, Maybe, Migration, OrderedIndex, Ordering, ParallelForEach,
                               ParallelSystems, Prefab, Relation, Runner, Snapshot, Sparse, Unload) + MicroBenchmarks.
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
                               battle server through regions of BattleServer.cs, cheat-sheet, glossary), guide/ (one
                               page per concept, analyzer lists EECS002-024), compare pages (performance, why-fast,
                               comparison, vs-*, migrate-from-*), reference pages (api: API + complexity, read by
                               tools/ApiDocs; architecture, faq, ai, decisions, roadmap, changelog); reference/ (API
                               reference generated by tools/ApiDocs, <page>.mdx + <page>.zh.mdx); README.md (GitHub index);
                               diagrams in mermaid; meta.json files order the site's sidebar
docs/                          the docs site: React Router 7 + Fumadocs, prerendered, Bun (docs/CLAUDE.md);
                               app/ routes, src/ components and libs, src/data/bench.json, scripts/, functions/
                               (Cloudflare Pages Functions), WRITING.md (page anatomy, tone, numbers); public/
                               easyecs-rules.md (the rules file for AI assistants, docs/content/ai.mdx)
samples/                       runnable projects in learning order (README.md): HelloWorld, GameLoop, SaveLoad,
                               MultiWorld, HotReload (Contracts / Plugin / Host); built and run by CI. Each reads
                               top-down: Components.cs, Systems.cs (or Systems/ per responsibility), a short
                               Program.cs, plumbing that is not the lesson in Host/ or Support.cs, a README with the
                               file map and the real output. The per-feature examples are the "Full example" sections
                               of the guide pages (doccheck=example fences)
templates/                     `dotnet new easyecs` (Components/, Systems/ one file per system, Program.cs = host,
                               --tests: one test file per system) and `easyecs-system` (EasyEcs.Templates package);
                               smoke.sh packs, creates, builds, runs and tests them
demo/EasyEcs.Demo/             the docs site's live demo (docs/content/demo.mdx): a .NET 10 WebAssembly app (no Blazor,
                               [JSExport] host in Demo.cs, Components.cs, Systems/ one file per system); the page
                               includes its files. docs/scripts/build-demo.sh publishes it into docs/public/demo
                               (committed: Cloudflare has no .NET); rerun it after changing the demo or EasyEcs.Core
tools/DocCheck/                compiles the C# fences of the docs pages (`doccheck=` flags on a fence; `example`
                               fences of a page compile together as its full example)
tools/ApiDocs/                 writes docs/content/reference/ from the XML docs and the complexity tables of api.mdx /
                               api.zh.mdx (Complexity.cs), in English and in Chinese: zh/<page>.xml holds the Chinese
                               text per doc id with a hash of the English it translates (Translations.cs; zh/README.md:
                               workflow and glossary); missing, stale or orphaned entries fail the tool
```

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
- Behavioral/breaking changes need a `MIGRATION_GUIDE.md` entry and a README "What's new" entry.
- Commits: logic, docs and CI in separate commits. The author is the repository owner; the committer is the
  environment's signing identity so the signature shows as Verified. No session links in commit messages.
- The version lives only in `Directory.Build.props` (package, EasyEcs.Core and the bundled generator carry it).
- Releases are cut manually by the owner: GitHub Actions -> Release -> Run workflow on `master` with a version. Job
  `tag` bumps `Directory.Build.props`, builds and tests, commits `Bump to vX.Y.Z` and tags it; job `publish` packs that
  tag, pushes to NuGet through Trusted Publishing (OIDC, no API key secret; policy on nuget.org: repository EasyEcs,
  workflow `release.yml`) and creates the GitHub release with GitHub's generated notes (What's Changed from merged
  pull requests; New Contributors; Full Changelog), so changes land through pull requests with meaningful titles. A
  failed publish is retried with "Re-run failed jobs". Do not trigger releases unless asked.
