---
name: audit-generator
description: Audit the EasyEcs source generators (ArityGenerator, ComponentGenerator, SystemGenerator, PartGenerator, and the vector loops of Kernel*.cs) for generator speed, incremental-generator best practices, caching, and use of newer Roslyn features such as interceptors. Use when changing a generator or reviewing IDE/build performance.
---

# Generator audit

Files: `src/EasyEcs.Generator` (netstandard2.0, loaded by every Roslyn host). Generator time is IDE latency on
every keystroke of every user, so it is performance like the runtime's.

The generators have three jobs, and an audit checks all three, not only their speed:

1. **Users write a thing once** (CLAUDE.md, definition of done 2): what they would otherwise write twice is
   generated: members per component (`entity.X`, `SetX`, `TryGetX`, `world.XIndex`), arity families, system code and
   the set per assembly, jobs and entry points (`[ForEntity]`), parts (`[Sum]`, `[IndexKey]`, `[Group]`), type
   setup (`[GroupBy]`, `[WhenTargetDestroyed]`, `[NotSaved]`), the save registry. Look for boilerplate still left in
   `samples/`, templates, `BattleServer.cs` and the docs examples (a hand-written job, a reset-then-accumulate
   singleton, a registry call, a copy per arity): each is a candidate for an attribute, a parameter or a generated
   member, with the analyzer rule and code fix that move users to it (`audit-analyzer`).
2. **Users write the plain form, the generator emits the fast one** (Burst's idea, D68 / D90): a `[ForEach]` method
   becomes a vector loop (`Kernel*.cs`); a parameter does per chunk what users would do by hand (`Reduce<T>`,
   `Emit<T>`, `Scratch<T>`, `Lookup<T>`, `Tile<,>`); the schedule follows declarations (`Parallel`, `ChunkRows`,
   `Slices`, `[UpdateInterval]`, `[Consume]`, stages from read/write sets). Look for forms users write that fall back
   to scalar or to a serial pass, rank them by how often they appear (EECS055 / EECS056 reasons over the samples and
   docs), and support the most frequent next. The research behind a new form belongs to `audit-perf`.
3. **The generators are fast**, because they run on every keystroke (below).

## Measure first

`benchmarks/EasyEcs.Benchmark/GeneratorBenchmark.cs` (class `GeneratorBenchmark`): the three generators over the
battle server plus 40 generated modules (components of every kind, buffers, systems, query loops), each case with
`OneNamespace` false and true: the four generators.

```bash
tools/bench.sh GeneratorBenchmark        # or: dotnet run ... -- --filter '*.GeneratorBenchmark.*' (with the namespace)
```

- `Cold`: a first run (a build, or the IDE opening the project). Mostly emitting files.
- `EditMethodBody` / `EditUnrelatedFile`: one IDE keystroke, the previous driver kept. **The number that matters**:
  every transform of every candidate re-runs on a new compilation, so this is the per-keystroke cost.
- `Compile` / `CompileAndAnalyze`: a build's diagnostics without and with `EasyEcsAnalyzer`; the difference is the
  analyzer's cost. Most of it is Roslyn binding method bodies for operation actions.
- Setup throws if the code does not compile, the generators skip it, or the analyzer reports it: keep the modules
  written as users should write them.
- The machine is noisy: compare a change against its base **back to back** (check the base's `src/EasyEcs.Generator`
  out, run, restore, run), with nothing else on the machine (no build either: about 90 s per case, 10 cases).
  Both sides compile the same input: the tip's `GeneratorBenchmark.cs` and `BattleServer.cs`, with the names the base
  does not have yet mapped back (an older base's own input may break a rule it gained since, and Setup throws). Keep a change only if both time and allocations improve beyond the error; numbers go into
  `decisions.mdx` ([D62](/en/docs/decisions#d62-generators-bind-outside-bodies)), not here.
- To find the cost: `--profiler EP` (CPU sampling) or `[EventPipeProfiler(EventPipeProfile.GcVerbose)]`, then
  aggregate the speedscope / nettrace stacks by frame. Driver time under `AdditionalSourcesCollection.Add` is mostly
  GC attributed there: cut allocations, not files.

## Best practices (each is checked)

- `ForAttributeWithMetadataName` or a syntax-only predicate first; no `SemanticModel` work in predicates.
  Components are found by interface, not attribute, so they use `CreateSyntaxProvider`.
- **No body binding in transforms.** `GetSymbolInfo` / `GetTypeInfo` (and speculative binding at a position) inside a
  member body binds the whole body and runs nullable analysis, on every edit. Bind a type name alone with
  `Symbols.TypeNamed` (speculative binding at the type's open brace, `Symbols.Outside`).
- **No display strings.** Compare symbols with `Symbols.Named(symbol, "Ns.Type<T>")`, never
  `ToDisplayString() ==` (it allocates per candidate per edit). `ToDisplayString` only for emitted text.
- Models are equatable and symbol-free (`EquatableArray`, records of strings and ints): no `ISymbol`,
  `SyntaxNode`, `Compilation`, `Location` in a model.
- No `CompilationProvider` combined into a per-item pipeline; compilation-wide facts from the cheapest source
  (`ParseOptionsProvider` for the language version, `AssemblyName` instead of a type lookup).
- `WithTrackingName` on every stage. `GeneratorTests.EditsThatChangeNoModelHitTheCache` asserts its list of stages
  is complete and that every stage and both outputs are `Cached` / `Unchanged` after an edit in another file, in a
  method body and in a `[ForEach]` body: a new stage goes into `TrackedStages`.
- Implementation output (`RegisterImplementationSourceOutput`) for what user code never names (system code,
  `TypeRegistry`); source output for members user code calls.
- No diagnostics from generators: the analyzer reports, through the same `Symbols` checks ("skipped by the
  generator" == "reported").
- Deterministic output (sorted, culture-invariant), unique hint names, `[GeneratedCode("EasyEcs.Generator")]`
  on generated classes.
- Template text shared in `Code.cs`; arity families come from `ArityGenerator` templates, never by hand.
- Roslyn floor: `Microsoft.CodeAnalysis.CSharp` 5.0.0 (the .NET 10 SDK; extension members need C# 14). Attributes
  ship in EasyEcs.Core, so no `AddEmbeddedAttributeDefinition` / post-initialization output.
- Analyzer side: `EnableConcurrentExecution`, `ConfigureGeneratedCodeAnalysis(None)`, symbols resolved once per
  compilation in `RegisterCompilationStartAction`, no per-node lists, `Symbols.Named` for type checks.

## Vector loops (`Kernel*.cs`)

- A `[ForEach]` method becomes a `Vector<T>` loop only through `Kernel.Vectorize`; the reason it returns otherwise is
  exactly what EECS055 / EECS056 report (one function for the generator and the analyzer).
- Each new form gets a test that the vector loop equals the scalar method (`KernelTests`), a `KernelBenchmark` case,
  and a look at its Tier1 asm (`tools/jitdisasm.sh`): no call in the loop body, no unexpected range check.
- A change to `Kernel` must leave the code generated for `WorldSimulation.cs` and `BattleServer.cs` identical, or
  ship with their BenchmarkDotNet numbers.

## Done means (each new generated form)

- A `GeneratorTests` generated-output test and a caching assertion (the stage in `TrackedStages`).
- The analyzer reports what the generator skips (same `Symbols` / `Kernel` function) and a code fix moves
  hand-written code to the form.
- Behaviour tests in `tests/EasyEcs.UnitTest`; for vector loops, equivalence with the scalar method; for parallel
  forms, bit-identical results at 1 and 4 threads; the 0 B allocation test.
- A benchmark of the generated form against the hand-written one (`ForEntityBenchmark`, `ConsumeBenchmark`,
  `KernelBenchmark`, `ReduceBenchmark` are the pattern), and GeneratorBenchmark for the per-keystroke cost.
- Docs: the guide page's `You write` / `The generator writes` tabs from real output, `easyecs-rules.md`.

## Newer features (verdicts: [D63](/en/docs/decisions#d63-roslyn-features))

- Interceptors: not used. Query calls ([D18](/en/docs/decisions#d18-no-interceptors-for-query-calls-filters-carry-no-references)):
  intercepting a whole filter chain only reaches what a `static readonly` filter gives. `Get` / `Has` / `Set`,
  `CreateEntity`, `Index`: the type's bit or signature is a static readonly field the JIT folds, so nothing is left
  to remove. Re-evaluate only with a measured cost at a call site that the call's own arguments do not cause.
- C# 14 extension members (the generated `entity.X`), generic attributes (`[With<T>]`), FAWMN, implementation
  output: in use. Embedded attribute definitions: not needed.
