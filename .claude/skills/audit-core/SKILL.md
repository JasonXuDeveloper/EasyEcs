---
name: audit-core
description: "Audit src/EasyEcs.Core for redundant designs, duplicated logic, coupling, lifecycle bugs, thread-safety, unbounded memory and hot-path cost, and fix what is proven. Use for a core review round or before a release. Triggers: src/EasyEcs.Core, World*.cs, Archetype.cs, Query.cs, StructuralGate, Workers, threads, races, leaks, 核心, 冗余, 线程安全."
metadata:
  internal: true
---

# Core audit

AGENTS.md's engineering principles and rules apply (performance first, zero allocation on hot paths, structural gate,
`StorageVersion`, `EntityMeta` only); a fix is done when the `feature` checklist is.

## Look for

- **Redundancy**: two mechanisms for one job; public APIs that duplicate each other; overloads or options nobody
  needs. Pre-1.0: delete the redundant one (no `[Obsolete]` alias).
- **Non-DRY code**: the same loop or bookkeeping in Bulk / Components / Migration / Serialization / Snapshots /
  Indexes / Relations / Hierarchy; a generic method with a body of its own per T; a T1..T9 family written by hand.
  Merge into one non-generic core with thin typed wrappers (arity families from `ArityGenerator` templates).
- **Not uniform**: features whose APIs have different shapes for the same act (one takes `EntityId`, its sibling an
  `Entity`; a `TryX` here and a nullable there; a bulk form for one operation and not its pair), or internals that
  solve one problem two ways; make them one shape (the checklist is `audit-code`, "Not uniform").
- **Coupling**: the core depending on an optional feature (indexes, relations, hierarchy, groups, serialization);
  features reaching into each other's internals instead of a hook.
- **Names**: two names for one concept across APIs, comments and docs; a private helper sharing a public call's name
  but doing something else.
- **Guards in the wrong layer**: a check inside a shared helper that refuses callers it does not concern.
- **Dead private helpers** left behind by refactors or merges.
- **Lifecycle edges**: stale and recycled handles (version checks), retired entities (the Retired tag + cleanup
  components), calls during a pass (`ThrowIfIterating`, tombstones, pass count), parallel stages (shared gate),
  reentrancy, exceptions in the middle of a pass. Exception messages name the call that was refused.
- **Hot paths**: allocations (0 B allocation tests), bounds checks, inlining (`NoInlining` cold paths and per-run helpers called from several sites, `audit-perf` "Check the inlining before crediting a rewrite"), cache lines
  (hot structs power-of-two sized, contended fields padded apart), a lookup done twice (`Has` then `Get`).
- **Word loops** (any walk of a table by 64-row words: batch, masked, structural, bulk, query enumerators): a word
  without a passing row jumps with `RowFilter.NextPassing`, a run of whole words with `WholeEnd`, a Changed filter's
  unwritten words with `NextWord`. A loop that tests every word costs a few rows in a large table as much as a scan
  (`SparseBenchmark.PassOverTheFew`).
- **Threads**: synchronization is CAS, `SpinGate` or `System.Threading.Lock`, never `lock (object)` (D106). A section
  a parallel pass's threads enter (the helpers' queue, pools they rent from) takes a `SpinGate`: a `Lock` allocates
  its wait event on the thread that first waits past its spin, any time after warm-up (D128); `Lock` stays for the
  rarely used registries. An atomic
  instruction only where another thread can really race (a plain write when the owner is alone, as
  `BlockTicks.Raise` does); per-thread state (`[ThreadStatic]`) does not allocate on a thread's first run after
  warm-up; parallel results are bit-identical at 1 and 4 threads (chunks, `Reduce<T>`, `Emit<T>` combine in chunk
  order).
- **Platforms**: x64 and arm64 intrinsics with a scalar fallback (`IsSupported` checks), Native AOT (no reflection or
  generic recursion it cannot see, `tests/EasyEcs.Aot`), WebAssembly (the demo: Mono AOT, single-threaded, so the
  runner and `Workers` must run everything on the caller), Godot desktop and mobile; native memory freed on every
  path (World dispose, snapshots, collectible unload). Huge pages come from advising the large pinned arrays
  (`HugePages`: Linux only, off after a failed call), never from native blocks: the GC frees the arrays, so a `ref`
  kept across a resize never reads freed memory; a new large unmanaged array of a table goes through
  `ComponentInfo.Create` (or calls `HugePages.AllocatePinned`), with `sized` false for a doubling step or a trim
  (only arrays a reservation or a copy sized take huge pages, as `World.AllocateMeta` does for the records).
- **Unbounded memory**: a list, pool or log that grows without a cap or a release (`TrimExcess`, pool limits, the
  destroy log's capacity); per-entity state outside `EntityMeta`.
- **Per-thread pools and copy-on-write**: whichever thread makes the first write to a shared list pays the copy from
  its own pool, and a list freed on another thread goes to that thread's pool, so helpers allocate while the owner's
  pool fills (Restore after Capture, 2026-10-27). A shared list reports full capacity: a `Capacity` guard does not show
  sharing.
- **Change ticks and snapshots**: a Capture or Restore that does not advance the change tick makes every incremental
  copy conservative (everything ever written counts as written since); a restore stamps only the rows it copies back.
- **Large copies**: a whole-column copy of a snapshot goes through `Archetype.CopyBlock` with one
  `StreamingStores.CopyPays` per table (the bytes of all its columns, not of one copy: memmove streams each copy past
  its own threshold already); a resize copies plainly (measured: a wash). A pointer copy into a `byte[]` that is not
  pinned (a `TableCopy`'s columns) pins it with `fixed` first. Measurements and the rules: `audit-perf`.
- **Filters past bit 255**: a `QueryFilter` built per pass with a component bit past 255 allocates
  (`FilterOverflow.With`); cache filters per type (`ChangedSince<T>`). It shows only in the full suite, after other
  tests registered 300 types, so allocation tests that build filters must also run there (feat-tiles-pingpong).
- **Attributing a 0 B failure**: read `GC.GetAllocatedBytesForCurrentThread` and
  `JitInfo.GetCompiledMethodCount(currentThread: true)` around each part of the measured body (cheap; an
  `EventListener` on JIT events shifted the timing and hid the failure); a compile on the measuring thread of a method
  already running is OSR, confirmed by a rerun with `DOTNET_TC_OnStackReplacement=0` (fix-flaky-alloc).
- **Ideas from elsewhere**: a loop a GPU, stencil, OpenMP or database technique would run faster (whole words, zone
  maps, halos, chunk claiming, column loads); a .NET 10 API that does the job cheaper. Report them to `audit-perf`
  (its list of fields to borrow from); build only with numbers.

- **A per-row path that must allow `LoadEntities` / `SaveEntities`** cannot go through `entity.Run`, which opens a
  pass: it works on copies of the components and writes the `ref` ones back (`Entity.RunCollected`, `[ForEach(MaxRows
  = N)]`).

## Fixing

- Every bug gets a regression test that fails before the fix (run it against the old code once).
- A hot-path change ships only with BenchmarkDotNet numbers before and after (`audit-perf` has the method); revert it
  if anything regresses. Never stopwatches.
- A non-obvious choice or a rejected idea gets a decision record.
- A changed public API: its XML doc with the zh entry (`tools/ApiDocs/zh`, rerun ApiDocs) and its `api.mdx`
  complexity row; changelog, migration and the pages are docs to-dos for the final docs pass.
