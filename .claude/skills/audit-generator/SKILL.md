---
name: audit-generator
description: "Audit the EasyEcs source generators (ArityGenerator, ComponentGenerator, SystemGenerator, PartGenerator; the vector loops are audit-vector-loops) on their three jobs - generate what users would write twice, turn the plain form users write into the fast one (vector loops, per-chunk parameters, schedule), and stay fast on every keystroke (incremental best practices, caching, newer Roslyn features). Use when changing a generator, adding a generated form, or reviewing IDE/build performance. Triggers: SystemGenerator, ComponentGenerator, ArityGenerator, PartGenerator, *.g.cs, slow IDE, boilerplate, interceptors, 源生成器, 生成代码, 样板代码, 少写代码."
metadata:
  internal: true
---

# Generator audit

Files: `src/EasyEcs.Generator` (netstandard2.0, loaded by every Roslyn host): `ArityGenerator`, `ComponentGenerator`,
`SystemGenerator`, `PartGenerator` (+ `Parts.cs`), `Code.cs`, `EquatableArray.cs`; `Symbols.cs` is
shared with the analyzer (`audit-analyzer`).

## The three jobs

1. **Users write a thing once** (`feature`, part 2). Generated today: members per component (`entity.X`, `SetX`,
   `TryGetX`, `world.XIndex`), arity families, system code and the set per assembly, jobs and entry points
   (`[ForEntity]`), parts (`[Sum]`, `[IndexKey]`, `[Group]`), type setup (`[GroupBy]`, `[WhenTargetDestroyed]`,
   `[NotSaved]`), the save registry. Look for boilerplate still left in `samples/`, templates, `BattleServer.cs` and
   the docs examples (a hand-written job, a reset-then-accumulate singleton, a registry call, a copy per arity): each
   is a candidate for an attribute, a parameter or a generated member, with the rule and code fix that move users to
   it.
2. **Users write the plain form, the generator emits the fast one** (Burst's idea, D68 / D90). A `[ForEach]` method
   becomes a vector loop (`audit-vector-loops`); a parameter does per chunk what users would do by hand (`Reduce<T>`,
   `Emit<T>`, `Scratch<T>`, `Lookup<T>`, `Tile<,>`); the schedule follows declarations (`Parallel`, `ChunkRows`,
   `Slices`, `[UpdateInterval]`, `[Consume]`, stages from read/write sets). Look for forms that fall back to scalar
   or to a serial pass, rank them by how often they appear (the EECS055 / EECS056 reasons over the samples and docs),
   and support the most frequent next. The research behind a new form belongs to `audit-perf`.
3. **The generators are fast**: they run on every keystroke of every user (below).

## ECS without the burden

ECS's usual costs are business logic scattered across components and systems, boilerplate, and repeated code. The
goal is that users write ordinary code and get the ECS form and its speed without writing it. Each approach below is
a candidate; weigh it against the decisions (D18 interceptors, D63 Roslyn features), build it with numbers, and
record a dropped one.

- **Scoped inline queries** (C# interceptors): a lambda or `foreach` over a query inside a method becomes a generated
  job, its captured locals the job's fields, eligible for vector loops and `Parallel`. D18 dropped interceptors for
  speed; this reason is boilerplate, so it reopens only with that reason and a benchmark showing the job is as fast.
- **Views / aspects** (`[View]` structs, Unity `IAspect`): several components of one entity grouped with business
  methods (`player.TakeDamage(n)`), generated accessors over the row; logic stays in one place.
- **Archetype as a class** (`[Entity]`): a type lists its components; the generator writes the prefab, the creation
  call, the query filter and the systems from its methods.
- **Feature modules** (`[Feature]`): components, systems, groups and setup of one feature registered together.
- **Data events**: an event type declared once; the generator writes the buffer, the emit call and the reader system.
- **Schedule apart from the algorithm** (Halide): `ChunkRows`, `Parallel`, `Slices` chosen from `SystemStats`
  measurements instead of by hand (autotuning).
- **Pass fusion** (FlashAttention, loop fusion in Halide / XLA): passes over the same rows run as one walk, so a row is
  loaded into cache once. Fused automatically when it is provably the same result: the same filter, no structural
  change, no read of another row the first pass writes (`Lookup<T>`), no per-pass result the second reads (`Reduce<T>`,
  `Emit<T>`), no `[Changed<T>]` / `[Removed<T>]` on what the first writes, the same cadence and parallel settings;
  within one system by declaration order, across systems when the schedule puts them next to each other. What stops a
  fusion is reported as a fixable reason (`audit-analyzer`), and the docs teach merging by hand where it cannot be
  proven. Proof: a benchmark of fused against separate passes in a world larger than L2.
- **Hot/cold layout split**: fields a pass never reads moved out of the column it walks (measured access).
- **Migration**: a code fix from object-oriented code (a class with update methods) to components and systems, and an
  AI migration guide in the public skill (`skills/easyecs/SKILL.md`).

## Vector loops

The vector loops (`Kernel*.cs`: what becomes SIMD, side effects, fixable reasons, proof) have their own skill:
`audit-vector-loops`.

## Speed: measure first

`GeneratorBenchmark`: the four generators over the battle server plus 40 generated modules (components of every
kind, buffers, systems, query loops); each case with `OneNamespace` false and true (10 cases, about 90 s each).

```bash
tools/bench.sh GeneratorBenchmark
```

- `Cold`: a first run (a build, the IDE opening the project), mostly emitting files.
- `EditMethodBody` / `EditUnrelatedFile`: one keystroke with the previous driver kept. **The number that matters.**
- `Compile` / `CompileAndAnalyze`: a build's diagnostics without and with the analyzer; the difference is the
  analyzer's cost.
- Setup throws if the code does not compile, the generators skip it, or the analyzer reports it: the modules are
  written as users should write them.
- Compare against the base back to back (the base's `src/EasyEcs.Generator` checked out, run, restored, run) with
  nothing else on the machine, both sides compiling the tip's input (names the base lacks mapped back). Keep a change
  only if time and allocations both improve beyond the error; numbers go into `decisions.mdx` (D62).
- Finding the cost: `--profiler EP` or `[EventPipeProfiler(EventPipeProfile.GcVerbose)]`, stacks aggregated by
  frame. Driver time under `AdditionalSourcesCollection.Add` is mostly GC: cut allocations, not files.

## Speed: incremental rules (each is checked)

- `ForAttributeWithMetadataName` or a syntax-only predicate first; no `SemanticModel` in predicates. Components are
  found by interface, so they use `CreateSyntaxProvider`.
- **No body binding in transforms**: `GetSymbolInfo` / `GetTypeInfo` inside a member body binds the whole body on
  every edit. Bind a type name alone with `Symbols.TypeNamed` (at the type's open brace, `Symbols.Outside`).
- **No display strings**: `Symbols.Named(symbol, "Ns.Type<T>")`, never `ToDisplayString() ==`.
- Models are equatable and symbol-free (`EquatableArray`, records of strings and ints).
- No `CompilationProvider` in a per-item pipeline; compilation-wide facts from the cheapest source
  (`ParseOptionsProvider`, `AssemblyName`).
- `WithTrackingName` on every stage; `GeneratorTests.EditsThatChangeNoModelHitTheCache` lists them (`TrackedStages`)
  and asserts `Cached` / `Unchanged` after edits elsewhere, in a method body and in a `[ForEach]` body.
- Implementation output for what user code never names (system code, `TypeRegistry`); source output for members it
  calls.
- No diagnostics from generators: the analyzer reports through the same `Symbols` checks.
- Deterministic output (sorted, culture-invariant), unique hint names, `[GeneratedCode("EasyEcs.Generator")]`.
- Template text in `Code.cs`; arity families from `ArityGenerator` templates, never by hand.
- Roslyn floor `Microsoft.CodeAnalysis.CSharp` 5.0.0 (C# 14 extension members); attributes ship in EasyEcs.Core, so
  no embedded attribute definitions.

## Newer Roslyn features (verdicts: D63)

- Interceptors: not used (D18): a `static readonly` filter already gives what intercepting a filter chain would, and
  `Get` / `Has` / `Set` / `CreateEntity` / `Index` fold their type's bit as a static readonly field. Re-evaluate only
  with a measured cost at a call site that the call's own arguments do not cause, or for boilerplate: scoped inline
  queries become generated jobs ("ECS without the burden" above) when they run as fast as a `[ForEach]` method.
- In use: C# 14 extension members, generic attributes (`[With<T>]`), FAWMN, implementation output.

## Done means (each new generated form, on top of `feature`)

- Its generated names (members, types, hint names) pass `audit-naming`.
- A generated-output test and a caching assertion in `GeneratorTests`.
- The analyzer reports what the generator skips (same `Symbols` / `Kernel` function); a code fix moves hand-written
  code to the form.
- A benchmark of the generated form against the hand-written one (`ForEntityBenchmark`, `ConsumeBenchmark`,
  `KernelBenchmark`, `ReduceBenchmark` are the pattern), and GeneratorBenchmark for the per-keystroke cost.
- `You write` / `The generator writes` from real output wherever the form appears in an example: guide page, README
  (`<details>`), docs home, getting started, battle server walkthrough (a docs to-do; `audit-docs` part 3). A change
  to a generated form lists every page showing its old output.
