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
EasyEcs.Core/                  net10.0 library (hand-written shared core only)
  Context.cs                   entity lifecycle (create/destroy, id recycling), systems, Init/Update/Dispose, singletons
  Context.Components.cs        column preparation + AddBits/RemoveBits (shared core of every Add/Remove overload)
  Context.Archetypes.cs        archetype registry, entity membership, transition edges, copy-on-write query cache
  Query.cs                     QueryFilter (With/Without), QueryKey, ArchetypeCursor (shared iteration core)
  EntityMeta.cs                16-byte per-entity record (archetype, version, slot)
  Archetype.cs                 entity-id list with tombstones + free slots, add/remove edge caches
  Entity.cs / EntityRef.cs     entity value (id, version, tag) and version-checked reference
  Components/                  Tag (SIMD bitset + overflow), TagRegistry (global type -> bit), ComponentRef
  Systems/                     SystemBase, IInit/IExecute/IEndSystem, [Query]/[With]/[Without] attributes
  Singleton.cs                 per-type dense index + per-context box for singleton components
EasyEcs.Generator/             Roslyn incremental generators (netstandard2.0), packed into the NuGet analyzers folder
  ArityGenerator.cs            T1..T9 families for EasyEcs.Core itself: Add/RemoveComponent(s), Entity wrappers,
                               IForEach/ForEach/GroupOf/CountOf/AnyOf, GroupResult + enumerators (one template each)
  QueryGenerator.cs            user [Query] methods -> struct job + filter + Run{Method} + OnExecute; EECS001-004
EasyEcs.UnitTest/              NUnit tests (lifecycle, queries/filters, concurrency, generated systems)
EasyEcs.Benchmark/             BenchmarkDotNet: WorldSimulationBenchmark / ShardedWorldBenchmark (headline, realistic
                               server tick) + MicroBenchmarks (diagnostics). Always measure with BenchmarkDotNet.
doc/                           architecture.md, performance.md
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
   `Volatile.Write`); mutation happens under `_structuralLock`.
5. **Concise, elegant, no duplication**: write one implementation and generate arity variants (`EasyEcs.Generator`)
   instead of copy-pasting T1..T9 code by hand. Prefer small shared non-generic cores with thin typed wrappers.
6. **Every change is proven**: unit tests for behavior (`EasyEcs.UnitTest`) and BenchmarkDotNet numbers for
   performance (`EasyEcs.Benchmark`), before and after.

## Rules

- **Performance changes must be measured.** Run the relevant `EasyEcs.Benchmark` cases before and after; a change
  ships only if it is a net improvement (time and allocations) with no regression elsewhere. Add a benchmark
  when you optimize something not covered yet.
- **Zero allocation on hot paths**: queries, iteration, component access, add/remove after warm-up. No LINQ, no
  closures, no boxing, no `params object[]`. Use `Unsafe`/`MemoryMarshal` only where an invariant guarantees bounds
  (document the invariant next to the code).
- **Never hand-write arity variants**: T1..T9 APIs come from `ArityGenerator` templates. Change the template, not
  generated output (see `obj/**/EasyEcs.Generator/*.g.cs` to inspect it).
- **Structural changes go through the lock** (`_structuralLock`) and through `MoveToArchetype` / `DetachFromArchetype`;
  never mutate `Archetype.EntityIds` or `Entity.Tag` directly elsewhere.
- **Storage reallocation must bump `StorageVersion`** (component arrays, entity arrays, archetype id arrays,
  compaction) so in-flight `ForEach` passes refresh their cached arrays.
- **Entity ids are recycled**: any id-keyed API must also validate the version (`ThrowIfStale`).
- Behavioral/breaking changes need a `MIGRATION_GUIDE.md` entry and a README "What's new" entry.
- Releases are cut by a commit titled `release vX.Y.Z` on `master` (CI bumps `<Version>`, tags, pushes NuGet).
  Do not create such commits unless asked.
