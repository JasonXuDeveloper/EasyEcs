---
name: feature
description: "The definition of done for an EasyEcs feature, rule or behavior change - core, generator, analyzer, code fix, tests, benchmarks, docs, commits. Use when building or reviewing any change before it merges; a fresh session working on a new request follows it. Triggers: any edit under src/, a new attribute, parameter, API or EECS rule, a bug fix, \"add\", \"implement\", \"fix\", 加功能, 实现, 修复, 改一下."
metadata:
  internal: true
---

# Shipping a feature

A feature, rule or change ships only with every part below that applies to it. Check them off before merging. Each
part names the skill that audits it.

## 1. Core (`audit-core`)

- One shared non-generic core with thin typed wrappers; T1..T9 variants come from `ArityGenerator` templates.
- Hot paths allocate nothing and run no LINQ, closures, boxing, `params object[]`, string formatting or iterators.
- Each invariant that skips a bounds check is documented next to the code.
- Structural changes go through the gate; `StorageVersion` is bumped where a pass must notice; per-entity state is
  `EntityMeta` only; id-keyed APIs validate the version.
- Synchronization is `System.Threading.Lock` or CAS, never `lock (object)`.
- The feature is general (servers, Godot games, simulations) and off unless used: what it costs is paid only by
  worlds that use it.
- It works on every platform EasyEcs runs on: x64 and arm64, Native AOT, WebAssembly (the demo: Mono AOT, no
  threads), Godot (desktop and mobile); threads, native memory and intrinsics have a fallback (`audit-core`).

## 2. Generator (`audit-generator`)

- Whatever users would otherwise write twice is generated (an attribute, a `[ForEach]` parameter, a generated
  member); where users write the plain form, the generator emits the fast one (vector loop: `audit-vector-loops`; per-chunk parameter,
  schedule) when it can.
- A new attribute, marker interface, parameter or option passes the declaration surface check (`audit-generator`):
  it removes code users write, the generator could not infer it, and no existing declaration says the same thing.
- Declarations users write once are read through one `Symbols` helper that the generator and the analyzer share, so
  "skipped by the generator" is exactly "reported by the analyzer".
- Models are equatable and symbol-free, a tracking name per stage, a caching assertion and a generated-output test
  in `GeneratorTests`; generators report no diagnostics; partial and nested types are handled (the declaring chain
  is partial or reported).

## 3. Analyzer (`audit-analyzer`)

- Every misuse that compiles and every pattern that works but costs gets an EECS rule, and so does hand-written
  code the generator would write.
- Severity: Error when the code is wrong; Warning when it is likely wrong, costs, or has a better form (a generated
    member, an attribute, a parameter: users miss Info); Info only for hints the user may not be able to act on.
- The message names the type or member and the fix; a scalar fallback's reason comes from `Kernel.Vectorize` and names the part to split when the user can fix it (`audit-vector-loops`).
- Tests: positive cases, negative cases, false-positive guards, the overlap test (one diagnostic per mistake).
- The rule list and range in AGENTS.md, `docs/content/guide/analyzer.mdx` + `.zh.mdx`, `skills/easyecs/SKILL.md` (the public skill).

## 4. Code fix (`audit-analyzer`)

Every rule whose fix is mechanical, including converting hand-written patterns to the generated or attribute form.
The analyzer computes the fix as diagnostic properties; a `CodeFixTests` case covers it.

## 5. Tests (`tests/EasyEcs.UnitTest`, one file per feature)

Behaviour; a regression test that fails before each bug fix; edge cases (stale and recycled ids, empty tables, structural changes mid-pass, retired entities,
exceptions mid-pass); the allocation test (0 B in steady state); bit-identical results at 1 and 4 threads for
parallel code; equivalence with the scalar method for vector loops; the AOT test when the feature reaches
generics or reflection (`tests/EasyEcs.Aot`).

## 6. Benchmarks (`audit-perf` has the method)

BenchmarkDotNet only: before and after, two rounds, A/B copies outside the repo tree, under `flock /tmp/bench.lock`
on a quiet machine; Tier1 asm of new hot loops (`tools/jitdisasm.sh`). Ships only as a net win at 0 B with no
regression in WorldSimulation, BattleServer and the feature's neighbors. A benchmark for anything not covered yet.

## 7. Docs (`audit-docs`; written in the final docs pass)

Per `docs/WRITING.md`, en and zh structurally identical:
- the guide page's anatomy: what it is; when to use it (idea / warn Callouts); how to use it, recommended (generated)
  form first, with `You write` / `The generator writes` tabs (and in every headline example that uses the form:
  README `<details>`, docs home, getting started, the battle server walkthrough); a full example; how to use it fast; what it costs;
  pitfalls with their EECS rule; related Cards;
- every term borrowed from another field (OpenMP, MPI, stencil, halo, shader, lanes) explained where first used or
  in the page's "Before you start" section, with a glossary entry (WRITING.md);
- complexity rows in `docs/content/api.mdx` + `.zh.mdx`; XML docs with zh entries in `tools/ApiDocs/zh`;
- numbers only through `docs/src/data/bench.json` (BenchTable), never typed on a page (decision records may quote);
- a decision record for every non-obvious choice and dropped idea; the roadmap;
- changelog, README "What's new"; a migration entry for a breaking change;
- `skills/easyecs/SKILL.md` (the public skill), the file map of each directory it touches (root AGENTS.md layout, `src/*/AGENTS.md`, `tests/` and `benchmarks/AGENTS.md`);
- the battle server (`BattleServer.cs` regions + `example.mdx`) and the samples use the feature where it belongs.

While code is still changing, collect these as docs to-dos; pages are written once at the end.

## 8. Commits and merge (`verify`)

- Logic, docs and CI in separate commits; author = repository owner, committer = the environment's signing
  identity; no session links.
- Parallel work in worktrees on branches off the work branch (`audit`).
- `verify` runs before anything reaches master.
- What the change taught (a rule, a technique, a check) is written into the skill it belongs to (AGENTS.md,
  Workflows).
- Renames of released public API are reported first and applied after the owner's approval (`audit-naming`).
- **Names**: every name the change adds (API, generated members, attributes, rule ids' titles, internal types,
  files, tests, benchmarks, examples, headings, Chinese terms) passes `audit-naming`'s questions before it merges;
  it is cheapest to change before a release.
