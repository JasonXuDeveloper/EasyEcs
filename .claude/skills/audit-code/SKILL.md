---
name: audit-code
description: "Audit all EasyEcs code for maintainability and fix it - coupling, non-DRY code, dead code, layering, god files, unclear names and comments, code that is neither fast nor easy to change - in the core, generators, analyzer, code fixes, tools, tests, benchmarks, samples, templates and demo. Use for a cleanup round, after large merges, or when code was written in a hurry. Triggers: tools/, tests/, benchmarks/, samples/, templates/, demo/, dead code, duplicated code, god file, \"clean up\", 清理, 重构, 可维护性, 重复代码."
metadata:
  internal: true
---

# Code audit (maintainability)

High-performance code that nobody can change is not done. This audit reads every project for what makes code hard
to change, and fixes it without making it slower. The specialist audits apply the same checklist to their own files
(`audit-core`, `audit-generator`, `audit-vector-loops`, `audit-analyzer`); this one owns everything else and the seams between projects.

## Owns

`tools/` (DocCheck, ApiDocs, bench.sh, jitdisasm.sh), `tests/` (structure and shared fixtures), `benchmarks/`
(harness, shared setups), `samples/`, `templates/`, `demo/`, `.github/` scripts, `docs/scripts/`; and the seams:
code duplicated across projects, a project reaching into another's internals.

## Look for

- **Coupling**: a module that knows another's internals instead of a hook or an interface; dependencies that point
  the wrong way (the core depending on a feature, a sample on a benchmark, a tool on test code); circular references
  between files or types; one change that must touch many files.
- **Non-DRY code**: the same logic in two places (two loops, two parsers, two setup blocks, two copies of a test
  fixture, two shell scripts doing one job). Merge into one shared helper with thin wrappers; generate what differs
  only by arity or type.
- **Not uniform**: one problem solved two ways (two pooling schemes, two styles of throw helper, two ways to walk a
  table, two setup idioms in tests or samples, two benchmark harness patterns). Pick the better one, move every site
  to it, and say which in the owning skill so new code follows it. The public API side (call families, overload
  sets, declarations) is `audit-core` and `audit-generator` (declaration surface).
- **Dead code**: members, files, options, scripts and test helpers nothing uses; commented-out code; flags no caller
  sets. Delete them (pre-1.0: no `[Obsolete]`).
- **God files and god methods**: a file that mixes responsibilities, a method that does several jobs. Split by
  responsibility (partial classes, one file per concern), keeping hot paths inlined.
- **Names**: one name for two things, two names for one thing, names that hide cost or side effects (`audit-naming`
  for the public API).
- **Comments say why**: invariants, formulas, magic numbers, the reason for a non-obvious choice; no comment that
  restates the code; XML docs on public members.
- **Examples are exemplary**: samples, templates, the demo and the battle server follow `ecs-principles.mdx`, build
  with no analyzer warning (no EECS055: their `[ForEach]` methods run as vector loops where the form allows) and use
  names that model their domain (`audit-naming`); users copy them.
- **Fast and simple together**: a simplification that costs speed is not one; a fast path that duplicates a slow one
  should share its core.

- **tools/Inlining** runs each benchmark method once: it reads `[Params]` and `[ParamsSource]` (a gate parameter is a
  `[ParamsSource]`); a benchmark shape it cannot run is a gap in the tool, fixed there. It takes full class names
  (`KernelBenchmark`, not `Kernel`: a wrong name throws "Sequence contains no matching element").
- **Scripted edits of C# keep its escapes**: a Python heredoc rewriting C# string literals broke their escapes;
  check the result or use the Edit tool.

## Exemplar pass (before every docs pass)

Users copy what they read, so once a round's features have merged, every piece of code users read is rewritten in the
newest recommended form, the one the docs will teach (the owner's rule: the docs pass starts from code that already
shows the newest forms).

- **Scope**: `samples/` (every sample, VoxelServer included), `templates/`, `demo/EasyEcs.Demo`, the headline
  benchmark worlds (`BattleServer.cs`, `WorldSimulation.cs`, the recommended variant of each feature benchmark), the
  tests that model usage (scenario and BattleServer tests, fixtures, feature tests written as users would write the
  feature), and every C# fence of `docs/content` (en + zh, the guide pages' full examples), the public skill and the
  README (with the docs pass when the page itself changes).
- **When**: once every feature is built and the last gaps the generated-members scan found are code, and before the
  docs pass and the benchmark sync, so docs and numbers are written once about the final code (owner, 2026-10-27:
  "重构应该排在写文档和 benchmark 前吧，等所有功能先落实好"; and: every sample, the demo, the
  benchmarks, the unit tests and the docs' code examples use the most correct, most fitting features, new ones
  included). A sample far behind the newest forms (VoxelServer) gets a large rewrite, not a touch-up: plan it per
  system first, read-only; a line users would still write twice is a code gap, fixed in the library before the
  rewrite, so the rewrite only uses what exists.
- **What changes**: each place uses the form the round made the recommended one (generated members and
  declarations, `[ForEach]` / lambda passes, views and prefabs, vector-loop-eligible methods, fusion-friendly passes,
  structural changes in passes, `[LookupKey]`, ...): read the changelog's entries of the round and the feature
  guide pages' "how to use it" for the list, and make the analyzer clean (no EECS055: methods run as vector loops
  where the form allows).
- **What stays**: a benchmark or test whose job is a specific form (hand-written vs generated, old vs new, a
  low-level API's own tests, the comparison projects' forms per library) keeps it; say so in a comment if a reader
  could take it for an old form.
- **Proof**: samples' outputs still match their README (`check-sample.sh`), the templates' smoke test, the demo
  builds and runs, `dotnet test`; a rewritten benchmark world is A/B-measured against the old one (it must not be
  slower; a slower recommended form is a finding for the generator or the docs, not a reason to keep the old form
  silently) before the site's numbers are synced.
- Commits: samples, templates/demo, benchmarks and tests apart; their README/page text with the docs pass.

## Fixing

- One concern per commit (logic, docs and CI apart); behavior unchanged unless a bug is found (then a regression
  test).
- Hot-path code: BenchmarkDotNet before and after (`audit-perf`); anything slower is reverted.
- After each change: `dotnet build -c Release` (0 warnings), `dotnet test`, and the sample checks of `verify` for
  samples and templates.
- Report what was merged, split, deleted and why; what was left (with the reason) goes on the round's list.
