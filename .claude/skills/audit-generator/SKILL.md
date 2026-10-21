---
name: audit-generator
description: Audit the EasyEcs source generators (ArityGenerator, ComponentGenerator, SystemGenerator) for generator speed, incremental-generator best practices, caching, and use of newer Roslyn features such as interceptors. Use when changing a generator or reviewing IDE/build performance.
---

# Generator audit

Files: `src/EasyEcs.Generator` (netstandard2.0, loaded by every Roslyn host). Generator time is IDE latency on
every keystroke of every user, so it is performance like the runtime's.

## Best practices (each is checked)

- `ForAttributeWithMetadataName` or a syntax-only predicate first; no `SemanticModel` work in predicates.
- Models are equatable and symbol-free (`EquatableArray`, records of strings and ints): no `ISymbol`,
  `SyntaxNode`, `Compilation`, `Location` in a model.
- No `CompilationProvider` combined into a per-item pipeline (it changes on every edit and re-runs everything);
  read compilation-wide facts once into a small equatable value.
- `WithTrackingName` on every stage, and a caching assertion in `GeneratorTests` per stage: after an unrelated
  edit every tracked step is `Cached` or `Unchanged`.
- No diagnostics from generators: the analyzer reports, through the same `Symbols` checks ("skipped by the
  generator" == "reported").
- Deterministic output (sorted, culture-invariant), unique hint names, `[GeneratedCode("EasyEcs.Generator")]`
  on generated classes.
- Template text shared in `Code.cs`; arity families come from `ArityGenerator` templates, never by hand.
- Analyzer side: `EnableConcurrentExecution`, `ConfigureGeneratedCodeAnalysis(None)`, symbols resolved once per
  compilation in `RegisterCompilationStartAction`, no per-node lists.

## Measure

Generator benchmarks run a `CSharpGeneratorDriver` with BenchmarkDotNet: a cold run and an incremental run after
a one-line edit, on a realistic compilation. Keep or revert each change by those numbers.

## Newer features

Evaluate before adopting, record the verdict with numbers in `decisions.mdx`:
- Interceptors (`[InterceptsLocation]`, versioned location data, `GetInterceptableLocation`): only where they
  remove a measured runtime cost at user call sites, work for every caller (calls from other assemblies fall
  back to the normal method) and keep the generator incremental.
- C# 14 extension members (used for the generated `entity.X` members), generic attributes, embedded attribute
  definitions.
