---
name: audit-analyzer
description: Audit the EasyEcs analyzer (EECS rules) and code fixes - overlaps, missing rules, false positives, shared detection logic. Use when reviewing or adding analyzer rules or code fixes.
---

# Analyzer audit

Files in `src/EasyEcs.Generator`: `EasyEcsAnalyzer.cs` (declaration and [ForEach] rules), `.Performance.cs` (code that
works but costs), `.Usage.cs` (code that compiles but does not do what it says), `.Systems.cs` (systems as
declarations), `.Parallel.cs` (code on several threads), `.Parts.cs` (generated parts), `.Forms.cs` (what the
generator declares for code written by hand), `.Parameters.cs` ([ForEach] parameters done by hand),
`UnusedSuppressor.cs`, `Symbols.cs` (shared checks), `Uses.cs`, `Threads.cs`; fixes in `src/EasyEcs.CodeFixes`. Rule
list: `docs/content/guide/analyzer.mdx` and the range in CLAUDE.md (EECS002-089).

## Principles

- **One mistake, one diagnostic** (D60): when two rules fire on the same code, the rule whose fix removes the
  other's case reports it; the other skips. Pin each pair in `GeneratorTests.OneMistakeIsReportedOnce`.
- **Detection lives in `Symbols.cs`** once (structural calls, [ForEach] context, filters via `Symbols.Requirements`,
  query loops, branch walks). The generators read the same helpers: what the generator skips is what the analyzer
  reports.
- Rule ids are stable; a removed id is never reused.
- **Coverage** (CLAUDE.md, definition of done 3): every misuse that compiles and every pattern that works but
  costs gets a rule. Three kinds:
  - wrong code (Error) and likely wrong code (Warning): a structural change in a pass, a ref used after a change,
    shared writes from parallel code, filters no entity passes;
  - code that costs (Info, a performance hint): write access that only reads, per-row lookups, a `Query` loop that
    only reads (`Read`), per-entity structural changes (bulk calls), a method that could be a vector loop (EECS055 /
    EECS056 with the exact reason from `Kernel.Vectorize`);
  - code written by hand that the generator writes (EECS080-089): a hand-written job instead of `[ForEntity]`,
    destroying every visited row instead of `[Consume]`, a singleton accumulated row by row instead of `Reduce<T>`,
    other entities read through the World instead of `Lookup<T>`, records appended by hand instead of `Emit<T>`,
    hand-written parts instead of `[Sum]` / `[IndexKey]` / `[Group]`.
- Severity: Error when the code is wrong, Warning when it is likely wrong, Info for performance hints.
- The message names the type or member and the fix (what to write instead).
- **Code fix for every rule whose fix is mechanical**, converting hand-written patterns to the generated or attribute
  form (solution-wide when the call sites change too); the analyzer computes the fix as diagnostic properties, and a
  `CodeFixTests` case covers it. A fix is offered only when it is always correct (e.g. "make it static" only if no
  instance member is used and nothing calls the method); otherwise no fix, and the message says what to do.
- **Cost**: rules run on every build. `GeneratorBenchmark.CompileAndAnalyze` minus `Compile` is the analyzer's
  cost; a new rule must not move it beyond the error (symbols resolved once per compilation, no body binding the
  generator would not do, cheap syntax checks first).

## Look for

- Snippets that trigger two rules (write them; each is a test).
- Hand-rolled copies of a Symbols helper in a rule.
- False positives on: `ref readonly`, lambdas, generated members (`[GeneratedCode("EasyEcs.Generator")]`), loops
  with break/return, explicit types, code after a structural change.
- Mistakes users make that no rule catches (`docs/content/guide/common-mistakes.mdx` and `guide/troubleshooting.mdx`
  are the source).
- Messages that no longer match runtime behavior.
- New features without their rules: each new attribute, parameter or API added since the last round (changelog) is
  checked for the misuse it invites and for the hand-written pattern it replaces.
- Patterns in `samples/`, `BattleServer.cs`, `WorldSimulation.cs`, templates and docs examples that a rule should
  flag but does not (build them with the analyzer and read what it says: exemplary code reports nothing).

## Done means

- Tests: one diagnostic test per rule case, a code-fix test per fix, the overlap test.
- Docs: analyzer.mdx + .zh (section + table row + fix line), changelog, decisions for a new rule or a dropped idea,
  `docs/public/easyecs-rules.md`, common-mistakes / troubleshooting if users meet it, the rule list and EECS range in
CLAUDE.md (layout and definition of done).
