---
name: audit-core
description: Audit src/EasyEcs.Core for redundant designs, duplicated logic, coupling, bugs and hot-path cost, and fix what is proven. Use for a core review round or before a release.
---

# Core audit

Follow AGENTS.md (performance first, zero allocation on hot paths, structural gate, StorageVersion, EntityMeta only).

## Look for

- **Redundancy**: two mechanisms for one job; public APIs that duplicate each other; overloads or options nobody
  needs. Pre-1.0: delete the redundant one (no `[Obsolete]` alias) and update tests, docs, migration, changelog,
  `tools/ApiDocs/zh`.
- **Non-DRY code**: the same loop or bookkeeping in Bulk / Components / Migration / Serialization / Snapshots /
  Indexes / Relations / Hierarchy. Merge into one non-generic core with thin typed wrappers.
- **Coupling**: the core depending on an optional feature (indexes, relations, hierarchy, groups, serialization);
  features reaching into each other's internals instead of a hook.
- **Two names for one concept** across APIs, comments and docs.
- **Lifecycle edges**: stale handles (version checks), retired entities (Destroyed + cleanup components), calls
  during a pass (`ThrowIfIterating`, tombstones, pass count), parallel stages (shared gate), reentrancy,
  exceptions in the middle of a pass. Exception messages name the call that was refused.
- **Hot paths**: allocations (allocation tests: 0 B), bounds checks, inlining, cache lines.

## Rules for fixes

- Every bug gets a regression test that fails before the fix (run it against the old code once).
- A hot-path change ships only with BenchmarkDotNet numbers before and after (the relevant `EasyEcs.Benchmark`
  cases); revert it if anything regresses. Never stopwatches.
- A non-obvious choice or a rejected idea gets a decision record (`docs/content/decisions.mdx` + `.zh.mdx`).
- User-visible behavior: changelog (Fixes / Changes), migration entry when code must change, README "What's new".
