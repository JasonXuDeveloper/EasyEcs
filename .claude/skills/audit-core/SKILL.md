---
name: audit-core
description: Audit src/EasyEcs.Core for redundant designs, duplicated logic, coupling, bugs and hot-path cost, and fix what is proven. Use for a core review round or before a release.
---

# Core audit

Follow CLAUDE.md (performance first, zero allocation on hot paths, structural gate, StorageVersion, EntityMeta only).

## Look for

- **Redundancy**: two mechanisms for one job; public APIs that duplicate each other; overloads or options nobody
  needs. Pre-1.0: delete the redundant one (no `[Obsolete]` alias) and update tests, docs, migration, changelog,
  `tools/ApiDocs/zh`.
- **Non-DRY code**: the same loop or bookkeeping in Bulk / Components / Migration / Serialization / Snapshots /
  Indexes / Relations / Hierarchy. Merge into one non-generic core with thin typed wrappers.
- **Coupling**: the core depending on an optional feature (indexes, relations, hierarchy, groups, serialization);
  features reaching into each other's internals instead of a hook.
- **Two names for one concept** across APIs, comments and docs.
- **One name for two concepts**: a private overload or helper that shares a public call's name but does something else.
- **Guards in the wrong layer**: a check inside a shared helper that refuses callers it does not concern; it belongs
  in the public calls that need it.
- **Dead private helpers** left behind by refactors or merges (a member nothing calls any more).
- **Lifecycle edges**: stale handles (version checks), retired entities (the Retired tag + cleanup components), calls
  during a pass (`ThrowIfIterating`, tombstones, pass count), parallel stages (shared gate), reentrancy,
  exceptions in the middle of a pass. Exception messages name the call that was refused.
- **Hot paths**: allocations (allocation tests: 0 B), bounds checks, inlining, cache lines, a lookup done twice
  (`Has` then `Get`).
- **Threads**: synchronization is `System.Threading.Lock` or CAS (`Interlocked`), never `lock (object)`; an atomic
  instruction only where another thread can really race (a plain write when the owner is alone, as
  `BlockTicks.Raise` does); per-thread state (`[ThreadStatic]`) must not allocate on a thread's first run after
  warm-up; parallel results are bit-identical at 1 and 4 threads (chunks, `Reduce<T>`, `Emit<T>` combine in chunk order).
- **Word loops** (any walk of a table by 64-row words: batch, masked, structural, bulk, query enumerators): a word
  without a passing row jumps with `RowFilter.NextPassing`, a run of whole words with `WholeEnd`, and a Changed
  filter's unwritten words with `NextWord`. A loop that tests every word costs a few rows in a large table as much as
  a scan (`SparseBenchmark.PassOverTheFew`).

- **One shared core, thin typed wrappers**: a generic method with a body of its own per T, or a T1..T9 family
  written by hand instead of an `ArityGenerator` template.
- **Unbounded memory**: a list, pool or log that grows without a cap or a release (`TrimExcess`, pool limits, the
  destroy log's capacity); per-entity state outside `EntityMeta`.
- **Ideas from elsewhere** (`audit-perf`): a loop that a GPU, stencil, OpenMP or database technique would run
  faster (whole words, zone maps, halos, chunk claiming, column loads); a .NET 10 API that does the job cheaper
  (`Vector512`, `InlineArray`, `AlternateLookup`, `Lock`). Report them; build only with numbers.

## Rules for fixes

- Every bug gets a regression test that fails before the fix (run it against the old code once).
- A hot-path change ships only with BenchmarkDotNet numbers before and after (the relevant `EasyEcs.Benchmark`
  cases); revert it if anything regresses. Never stopwatches.
- A non-obvious choice or a rejected idea gets a decision record (`docs/content/decisions.mdx` + `.zh.mdx`).
- User-visible behavior: changelog (Fixes / Changes), migration entry when code must change, README "What's new".
- Tests, one file per feature: behaviour; edge cases (stale ids, recycled ids, empty tables, structural changes
  mid-pass, retired entities, exceptions mid-pass); the 0 B allocation test; bit-identical at 1 and 4 threads for
  parallel code.
- A public API's complexity row in `docs/content/api.mdx` / `.zh.mdx` and its XML doc with the zh entry
  (`tools/ApiDocs/zh`, rerun ApiDocs); docs pages themselves wait for the final docs pass (collect the to-dos).
