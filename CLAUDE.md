# EasyEcs

Allocation-free archetype ECS for long-running .NET servers. Library: `EasyEcs.Core` (targets `net10.0` only),
published to NuGet as `EasyEcs`. Public API and code comments are in English.

## Commands

```bash
dotnet build                                                   # whole solution
dotnet test                                                    # NUnit
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*'            # all benchmarks
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*Iterate*'    # a subset
```

Both benchmark projects use the job in `EasyEcs.Benchmark/BenchmarkConfig.cs` (no `--job` needed).

`global.json` requires the .NET 10 SDK (`rollForward: latestFeature`).

## Layout

```
EasyEcs.Core/                  net10.0 library, namespace EasyEcs (hand-written shared core only)
  Contracts.cs                 IComponent, ITrackChanges, ISingletonComponent, INoStructuralChanges
  Entity.cs                    the entity handle (id, version, world): Has/Get/Read/TryGet/GetOrAdd/Clone/Destroy
  EcsSystem.cs                 OnCreate/OnUpdate/OnDestroy + generated GeneratedUpdate / GeneratedAccess
  ForEachAttributes.cs         [ForEach] (Parallel), [With<T>], [Without<T>], [WithAny<T>], [Changed<T>], [Removed<T>]
  SystemGroups.cs              ISystemGroup, [UpdateInGroup<T>] (World.Update<T>() runs a group)
  World.cs                     entity lifecycle (create/destroy, id recycling, lookup, enumeration, Instantiate of a
                               Prefab and Entity.Clone: one row copy, CloneRow); destroy unlinks the entity from value
                               indexes
  Prefab.cs                    a recipe of component values outside any world (one row of a private world)
  World.Components.cs          AddBit/AddBits/RemoveBit/RemoveBits (shared core of every Set/GetOrAdd/Remove; a removed
                               indexed component unlinks the entity, a removed tracked one stamps its tick for
                               Removed<T>), passes (BeginPass/EndPass, tombstones closed by
                               the last pass), CarryOver
  World.Archetypes.cs          archetype registry, transition edges, copy-on-write query cache (GetQueryEntry: the
                               live entry a value index holds), CompactArchetypes (an update's recovery from an
                               abandoned query), the structural gate (Structural())
  StructuralGate.cs            owner flag (plain check-and-set, throws when reentered or while frozen: Freeze/Thaw around
                               parallel regions, batch jobs and INoStructuralChanges passes) +
                               shared CAS gate for the cold paths parallel stages reach; StructuralScope / SharedScope
  World.Systems.cs             system groups, priority order, parallel stages (consecutive non-conflicting systems of
                               one priority), synchronous Update / Update<TGroup>,
                               Dispose (also resets value indexes)
  World.Parallel.cs            allocation-free runner (caller + reusable thread-pool work items) for system stages
                               and the row chunks of parallel [ForEach]
  World.Snapshots.cs           Capture / Restore (WorldSnapshot.cs holds the copies)
  World.Indexes.cs             world.Index<T, TKey>() (one ValueIndex per indexed component type)
  ValueIndex.cs                IIndexed<TKey> + ValueIndex: key -> linked entity ids + count, built by the first
                               lookup, kept by Set / CreateEntity(ies) (World.Indexed, a JIT-time no-op for
                               unindexed types); destroy / remove unlink at once (World.Indexes.cs hooks)
  SystemAccess.cs              a system's read/write sets (generated from [ForEach] parameters) and the conflict rule
  SystemStats.cs               per-system run counts and times (WorldOptions.MeasureSystems)
  World.Singletons.cs          per-world singletons (ref returns)
  WorldOptions.cs              configuration (ParallelSystems, MaxDegreeOfParallelism, MeasureSystems, capacities)
  Query.cs                     QueryFilter (With/Without/WithAny/Changed/Removed/Since), QueryKey, QueryMatch, RowPlan (cached per
                               query and table), RowFilter (plan + the pass's change tick: skip / all rows / test
                               presence bits and change ticks; NextWord skips unwritten 4,096-row blocks and 64-row
                               words), QueryWalk (the cold state of a query walk, pooled per thread; the enumerator keeps
                               the hot state in registers)
  TypeIndex.cs                 dense per-type index families (ComponentType bit, SingletonType slot, SystemGroup
                               index, ComponentSignature of multi-component calls) + SingletonBox + ChangeTicks<T>
                               (the hidden change-tick column of an ITrackChanges component)
  EntityMeta.cs                16-byte per-entity record (archetype, version, slot)
  EntitySpan.cs                the entities of a batch job's run of rows (lazy handles over the run's EntitySlots)
  Archetype.cs                 a table: mask, EntitySlot rows (version + id), one column per component (unmanaged
                               columns pinned and 64-byte aligned), presence bits + absent counts per column,
                               per-bit and per-signature add transitions (copy plans); swap-remove / tombstones,
                               FillHoles; change-tick columns with their summaries (LastWrite per column, BlockWrite
                               per 4,096 rows, per 64 rows WordWrite + WordRows + WordRest: the rows at the word's
                               latest tick), Stamp/StampRows/SetTick, MarkPresent/MarkAbsent (presence bits), CloneRow
  DynamicBuffer.cs             DynamicBuffer<T> (a list per entity as a tracked component; writes are ref-this extensions)
                               and its BufferStore (snapshots copy it through ComponentInfo.CopyRows)
  ComponentInfo.cs             per-type column operations (size, managed, create/grow/copy/clone/clear) indexed by bit;
                               Pair links a tracked component with its change-tick column
  BitMask.cs                   256-bit SIMD bitset with overflow (component masks, query sets)
EasyEcs.Generator/             Roslyn generators + analyzer (netstandard2.0, required by Roslyn hosts)
  ArityGenerator.cs            T1..T9 families of EasyEcs.Core (Entity.Set/Remove, World.CreateEntity/
                               CreateEntities/DestroyEntities, IForEach/IForEachEntity/IForEachBatch,
                               ForEach/ForEachEntity/ForEachBatch/ForEachParallel/ForEachEntityParallel with their
                               row loops, Query/Count/Any, QueryItem/QueryEnumerator), one template each
  SystemGenerator.cs           [ForEach] -> struct jobs (per entity or per run of rows; INoStructuralChanges when
                               the method cannot change structure; Parallel -> chunked jobs; [Changed<T>] /
                               [Removed<T>] -> Since(LastRunTick + 1) and a read of T) + GeneratedUpdate + GeneratedAccess
  ComponentGenerator.cs        C# 14 extension members per component / singleton: entity.X (ref T; ref readonly for
                               tracked or indexed), HasX, TryGetX, GetOrAddX, SetX(ctor args), RemoveX, tags IsX,
                               world.XIndex, singletons world.X / HasX / SetX / RemoveX; classes carry
                               [GeneratedCode("EasyEcs.Generator")], so Symbols.IsGenerated lets the analyzer and
                               SystemGenerator treat SetX / GetOrAddX / RemoveX as structural, the rest as reads
  EasyEcsAnalyzer.cs           EECS001-016 (generators never report diagnostics)
  Threads.cs                   EECS014's check: does a structural call in a delegate handed to another thread race
  Symbols.cs                   symbol checks shared by generator and analyzer
  Code.cs                      tiny string helpers for the generators' templates (List, Lines, TypeParams)
  EquatableArray.cs            array with value equality (generator models cache between edits)
  Polyfills.cs                 IsExternalInit for netstandard2.0 (records); do not delete
EasyEcs.UnitTest/              NUnit, one file per feature: Entity, Component, Presence, Query, System, ForEachSystem,
                               Singleton, Concurrency, Parallel, Changed, Removed, Instantiate, DynamicBuffer, Index,
                               Snapshot, Allocation (0 B budget),
                               Generator (diagnostics + caching), BattleServer (the README world as a whole, linked
                               from EasyEcs.Benchmark); Fixtures.cs holds the shared test components
EasyEcs.Benchmark/             BenchmarkDotNet: WorldSimulationBenchmark / MultiWorldBenchmark (headline, realistic
                               server tick; the world itself is WorldSimulation.cs), BattleServerBenchmark (the
                               doc/example.md, BattleServer.cs, every feature), ParallelSystemsBenchmark,
                               ParallelForEachBenchmark, ChangeTrackingBenchmark, DynamicBufferBenchmark, IndexBenchmark, SnapshotBenchmark +
                               MicroBenchmarks.
                               Always measure with BenchmarkDotNet.
EasyEcs.Comparison/            BenchmarkDotNet: the headline world (WorldSimulation.cs, compiled unchanged) ported to
                               Arch, Friflo.Engine.ECS and DefaultEcs, counters checked against EasyEcs;
                               OperationBenchmark (per operation, vs Friflo); FeatureBenchmark (change tracking,
                               removal detection and indexes in each library's own way, results checked in setup)
doc/                           README.md (index), getting-started.md, example.md (the battle server), guide/ (one page per concept, analyzer.md
                               lists EECS001-016), api.md (API + complexity), ecs-principles.md, architecture.md,
                               performance.md (diagrams in mermaid)
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

- **Follow the ECS principles in `doc/ecs-principles.md`, including in tests, benchmarks and docs**: components and
  singletons are plain data structs; one system per responsibility; systems are stateless (state goes into
  components or singletons, EECS007) and never reference other systems (EECS008); state changes are data.
  Examples are what users copy, so they must be exemplary. The battle server example (`doc/example.md`) is `EasyEcs.Benchmark/BattleServer.cs`
  verbatim: change both together, and give a new feature a place in it.
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
  reach on their own: query cache misses, missing singletons, closing tombstones) and through `AddBit(s)` /
  `RemoveBit(s)` / `MoveTo` / `RemoveRow` (and `Archetype.FillHoles`, which rewrites rows and slots); never mutate
  `Archetype.Slots`, presence bits or `EntityMeta` directly elsewhere.
- **Per-entity state outside the tables is `EntityMeta` only** (16 bytes). Do not add per-entity arrays or fields
  without measuring: the component set is the archetype mask (plus the row's presence bits). The one exception is
  opt-in: a value index keeps a node per entity id (`ValueIndex._nodes`, and `_byTarget` for relations), paid only by
  worlds that create one.
- **Every change a pass must notice bumps `StorageVersion`**: reallocation (columns, `Meta`, entity rows), row moves,
  closing tombstones, and presence bits flipping, so in-flight passes refresh their refs and re-read row filters.
- **Decisions**: every non-obvious choice, and every feature dropped after weighing it, gets a record in
  `doc/decisions.md` (decision, why, what was considered, numbers); `doc/roadmap.md` lists what is planned.
- **Docs**: diagrams are mermaid; `doc/api.md` lists the complexity of every public API (README is the landing page).
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
