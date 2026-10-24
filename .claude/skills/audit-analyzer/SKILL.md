---
name: audit-analyzer
description: Audit the EasyEcs analyzer (EECS rules) and code fixes - missing rules for wrong, costly and hand-written code, overlaps, false positives, code fixes to the generated forms, shared detection logic, the analyzer's build cost. Use when reviewing or adding analyzer rules or code fixes.
---

# Analyzer audit

Files in `src/EasyEcs.Generator`: `EasyEcsAnalyzer.cs` (declarations and [ForEach] rules), `.Performance.cs` (code
that works but costs), `.Usage.cs` (code that compiles but does not do what it says), `.Systems.cs` (systems as
declarations), `.Parallel.cs` (code on several threads), `.Parts.cs` (generated parts), `.Forms.cs` (what the
generator declares for code written by hand), `.Parameters.cs` ([ForEach] parameters done by hand),
`UnusedSuppressor.cs`, `Symbols.cs` (shared with the generators), `Uses.cs`, `Threads.cs`; fixes in
`src/EasyEcs.CodeFixes`. Rule list: `docs/content/guide/analyzer.mdx` and the range in CLAUDE.md (EECS002-089).

## Coverage (`feature`, part 3)

Every misuse that compiles and every pattern that works but costs gets a rule, in three kinds:

- **Wrong (Error) or likely wrong (Warning)**: a structural change in a pass, a ref used after a change, shared
  writes from parallel code, filters no entity passes.
- **Costly or a better form exists (Warning: users miss Info)**: write access that only reads, per-row lookups, a `Query` loop that only
  reads (`Read`), per-entity structural changes (bulk calls), a method that could be a vector loop (EECS055 /
  EECS056, the exact reason from `Kernel.Vectorize`). Info is left for hints a user may not be able to act on
  (EECS055's reasons that come from the algorithm itself; the fixable ones become a Warning that names the fix).
- **Written by hand where the generator writes it** (EECS080-089): a hand-written job instead of `[ForEntity]`,
  destroying every visited row instead of `[Consume]`, a singleton accumulated row by row instead of `Reduce<T>`,
  other entities read through the World instead of `Lookup<T>`, records appended by hand instead of `Emit<T>`,
  hand-written parts instead of `[Sum]` / `[IndexKey]` / `[Group]`.

## Principles

- **One mistake, one diagnostic** (D60): when two rules fire on the same code, the rule whose fix removes the other's
  case reports it. Pin each pair in `GeneratorTests.OneMistakeIsReportedOnce`.
- **Detection lives in `Symbols.cs`** once (structural calls, [ForEach] context, `Symbols.Requirements`, query loops,
  branch walks); the generators read the same helpers, so what the generator skips is what the analyzer reports.
- Rule ids are stable; a removed id is never reused.
- The message names the type or member and what to write instead.
- **A code fix for every rule whose fix is mechanical**, including converting hand-written patterns to the generated
  or attribute form (solution-wide when call sites change). The analyzer computes the fix as diagnostic properties;
  a `CodeFixTests` case covers it. A fix is offered only when it is always correct; otherwise the message says what
  to do.
- **Cost**: rules run on every build (`CompileAndAnalyze` minus `Compile` in `GeneratorBenchmark`); a new rule must
  not move it beyond the error. `EnableConcurrentExecution`, `ConfigureGeneratedCodeAnalysis(None)`, symbols resolved
  once per compilation (`RegisterCompilationStartAction`), cheap syntax checks first, no per-node lists,
  `Symbols.Named` for type checks.

## Look for

- Snippets that trigger two rules (write them; each is a test).
- Hand-rolled copies of a `Symbols` helper in a rule.
- False positives on `ref readonly`, lambdas, generated members (`[GeneratedCode("EasyEcs.Generator")]`), loops with
  break/return, explicit types, code after a structural change.
- Mistakes no rule catches (`docs/content/guide/common-mistakes.mdx`, `guide/troubleshooting.mdx`).
- Messages that no longer match runtime behavior.
- New features without their rules: each attribute, parameter or API the changelog adds, checked for the misuse it
  invites and the hand-written pattern it replaces.
- Patterns in `samples/`, `BattleServer.cs`, `WorldSimulation.cs`, templates and docs examples that a rule should flag
  but does not (exemplary code reports nothing).

## Done means

- Tests: positive and negative cases per rule, false-positive guards, a code-fix test per fix, the overlap test.
- Docs (to-dos for the docs pass): `analyzer.mdx` + `.zh` (section, table row, fix line), changelog, a decision for a
  new rule or a dropped idea, `docs/public/easyecs-rules.md`, common-mistakes / troubleshooting if users meet it, the
  rule list and EECS range in CLAUDE.md's layout.
