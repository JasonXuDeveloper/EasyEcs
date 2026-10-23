---
name: audit-generator
description: Audit the EasyEcs source generators (ArityGenerator, ComponentGenerator, SystemGenerator) for generator speed, incremental-generator best practices, caching, and use of newer Roslyn features such as interceptors. Use when changing a generator or reviewing IDE/build performance.
---

# Generator audit

Files: `src/EasyEcs.Generator` (netstandard2.0, loaded by every Roslyn host). Generator time is IDE latency on
every keystroke of every user, so it is performance like the runtime's.

## Measure first

`benchmarks/EasyEcs.Benchmark/GeneratorBenchmark.cs` (class `GeneratorBenchmark`): the three generators over the
battle server plus 40 generated modules (components of every kind, buffers, systems, query loops).

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

## Newer features (verdicts: [D63](/en/docs/decisions#d63-roslyn-features))

- Interceptors: not used. Query calls ([D18](/en/docs/decisions#d18-no-interceptors-for-query-calls-filters-carry-no-references)):
  intercepting a whole filter chain only reaches what a `static readonly` filter gives. `Get` / `Has` / `Set`,
  `CreateEntity`, `Index`: the type's bit or signature is a static readonly field the JIT folds, so nothing is left
  to remove. Re-evaluate only with a measured cost at a call site that the call's own arguments do not cause.
- C# 14 extension members (the generated `entity.X`), generic attributes (`[With<T>]`), FAWMN, implementation
  output: in use. Embedded attribute definitions: not needed.
