# EasyEcs

Allocation-free archetype ECS for long-running .NET servers. Library: `EasyEcs.Core` (targets `net10.0` only),
published to NuGet as `EasyEcs`. Public API and code comments are in English.

## Commands

```bash
dotnet build                                                   # whole solution
dotnet test                                                    # NUnit
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*'            # all benchmarks
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*Iterate*' --job short   # quick subset
```

`global.json` requires the .NET 10 SDK (`rollForward: latestFeature`).

## Layout

```
EasyEcs.Core/                  net10.0 library, namespace EasyEcs (hand-written shared core only)
  Contracts.cs                 IComponent, ISingletonComponent
  Entity.cs                    the entity handle (id, version, world): Has/Get/TryGet/Add/GetOrAdd/Destroy
  EcsSystem.cs                 OnCreate/OnUpdate/OnDestroy + generated GeneratedUpdate
  ForEachAttributes.cs         [ForEach], [With<T>], [Without<T>]
  SystemGroups.cs              ISystemGroup, [UpdateInGroup<T>] (World.Update<T>() runs a group)
  World.cs                     entity lifecycle (create/destroy, id recycling, lookup, enumeration)
  World.Components.cs          columns + AddBit/AddBits/RemoveBits (shared core of every Add/Set/Remove)
  World.Archetypes.cs          archetype registry, transition edges, copy-on-write query cache, compaction
  World.Systems.cs             system groups (priority buckets each), synchronous Update / Update<TGroup>, Dispose
  World.Singletons.cs          per-world singletons (ref returns)
  WorldOptions.cs              configuration
  Query.cs                     QueryFilter (With/Without), QueryKey, ArchetypeCursor (shared iteration core)
  TypeIndex.cs                 dense per-type indices (component bit, singleton slot, system group)
  EntityMeta.cs                16-byte per-entity record (archetype, version, slot)
  Archetype.cs                 mask + component bits + entity ids (tombstones, free slots) + edges; sorted Compact
  Tag.cs                       256-bit SIMD bitset with overflow
EasyEcs.Generator/             Roslyn generators + analyzer (netstandard2.0, required by Roslyn hosts)
  ArityGenerator.cs            T1..T9 families of EasyEcs.Core (Entity.Set/Remove, World.CreateEntity, IForEach,
                               ForEach/Query/Count/Any, QueryItem/QueryEnumerator), one template each
  SystemGenerator.cs           [ForEach] -> struct jobs + GeneratedUpdate
  EasyEcsAnalyzer.cs           EECS001-008 (generators never report diagnostics)
  Symbols.cs                   symbol checks shared by generator and analyzer
EasyEcs.UnitTest/              NUnit, one file per feature: Entity, Component, Query, System, ForEachSystem,
                               Singleton, Concurrency, Allocation (0 B budget), Generator (diagnostics + caching)
EasyEcs.Benchmark/             BenchmarkDotNet: WorldSimulationBenchmark / ShardedWorldBenchmark (headline, realistic
                               server tick) + MicroBenchmarks (diagnostics). Always measure with BenchmarkDotNet.
doc/                           ecs-principles.md, architecture.md, performance.md (diagrams in mermaid)
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
   lock-free). Lock-free readers only ever see immutable snapshots (copy-on-write arrays published with
   `Volatile.Write`); mutation happens under `World.StructuralLock`.
5. **Concise, elegant, no duplication**: write one implementation and generate arity variants (`EasyEcs.Generator`)
   instead of copy-pasting T1..T9 code by hand. Prefer small shared non-generic cores with thin typed wrappers.
6. **Every change is proven**: unit tests for behavior (`EasyEcs.UnitTest`) and BenchmarkDotNet numbers for
   performance (`EasyEcs.Benchmark`), before and after.

## Rules

- **Follow the ECS principles in `doc/ecs-principles.md`, including in tests, benchmarks and docs**: components and
  singletons are plain data structs; one system per responsibility; systems are stateless (state goes into
  components or singletons, EECS007) and never reference other systems (EECS008); state changes are data.
  Examples are what users copy, so they must be exemplary.
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
- **Structural changes go through the lock** (`StructuralLock`) and through `MoveToArchetype` / `AddBits` /
  `RemoveBits`; never mutate `Archetype.EntityIds` or `EntityMeta` directly elsewhere.
- **Per-entity state is `EntityMeta` only** (16 bytes). Do not add per-entity arrays or fields without measuring:
  the component set is the archetype mask.
- **Storage reallocation must bump `StorageVersion`** (columns, `Meta`, archetype id arrays, compaction) so in-flight
  `ForEach` passes refresh their cached arrays.
- **Docs**: diagrams are mermaid; README lists the complexity of every public API.
- **Entity ids are recycled**: any id-keyed API must also validate the version (`ThrowIfStale`, `Entity.Archetype`).
- Behavioral/breaking changes need a `MIGRATION_GUIDE.md` entry and a README "What's new" entry.
- Commits: logic, docs and CI in separate commits. The author is the repository owner; the committer is the
  environment's signing identity so the signature shows as Verified. No session links in commit messages.
- The version lives only in `Directory.Build.props` (package, EasyEcs.Core and the bundled generator carry it).
- Releases are cut manually by the owner: GitHub Actions -> Release -> Run workflow on `master` with a version. Job
  `tag` bumps `Directory.Build.props`, builds and tests, commits `Bump to vX.Y.Z` and tags it; job `publish` packs that
  tag, pushes to NuGet (secret `MYTOKEN`) and creates the GitHub release with GitHub's generated notes (What's
  Changed from merged pull requests; New Contributors; Full Changelog), so changes land through pull requests with
  meaningful titles. A failed publish is retried
  with "Re-run failed jobs". Do not trigger releases unless asked.
