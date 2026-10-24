---
name: audit-code
description: Audit all EasyEcs code for maintainability and fix it - coupling, non-DRY code, dead code, layering, god files, unclear names and comments, code that is neither fast nor easy to change - in the core, generators, analyzer, code fixes, tools, tests, benchmarks, samples, templates and demo. Use for a cleanup round, after large merges, or when code was written in a hurry.
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

## Fixing

- One concern per commit (logic, docs and CI apart); behavior unchanged unless a bug is found (then a regression
  test).
- Hot-path code: BenchmarkDotNet before and after (`audit-perf`); anything slower is reverted.
- After each change: `dotnet build -c Release` (0 warnings), `dotnet test`, and the sample checks of `verify` for
  samples and templates.
- Report what was merged, split, deleted and why; what was left (with the reason) goes on the round's list.
