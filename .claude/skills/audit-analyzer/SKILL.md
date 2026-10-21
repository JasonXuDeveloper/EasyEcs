---
name: audit-analyzer
description: Audit the EasyEcs analyzer (EECS rules) and code fixes - overlaps, missing rules, false positives, shared detection logic. Use when reviewing or adding analyzer rules or code fixes.
---

# Analyzer audit

Files: `src/EasyEcs.Generator/EasyEcsAnalyzer.cs` (declaration and [ForEach] rules), `.Performance.cs` (code that
works but costs), `.Usage.cs` (code that compiles but does not do what it says), `Symbols.cs` (shared checks),
`Uses.cs`, `Threads.cs`; fixes in `src/EasyEcs.CodeFixes`. Rule list: `docs/content/guide/analyzer.mdx`.

## Principles

- **One mistake, one diagnostic** (D60): when two rules fire on the same code, the rule whose fix removes the
  other's case reports it; the other skips. Pin each pair in `GeneratorTests.OneMistakeIsReportedOnce`.
- **Detection lives in `Symbols.cs`** once (structural calls, [ForEach] context, filters via `Symbols.Requirements`,
  query loops, branch walks). The generators read the same helpers: what the generator skips is what the analyzer
  reports.
- Rule ids are stable; a removed id is never reused.
- Severity: Error only when the code cannot be right; Warning for cost and likely bugs.
- A fix is offered only when it is always correct (e.g. "make it static" only if no instance member is used and
  nothing calls the method); otherwise no fix, and the message says what to do.

## Look for

- Snippets that trigger two rules (write them; each is a test).
- Hand-rolled copies of a Symbols helper in a rule.
- False positives on: `ref readonly`, lambdas, generated members (`[GeneratedCode("EasyEcs.Generator")]`), loops
  with break/return, explicit types, code after a structural change.
- Mistakes users make that no rule catches (common-mistakes.mdx, troubleshooting.mdx are the source).
- Messages that no longer match runtime behavior.

## Done means

- Tests: one diagnostic test per rule case, a code-fix test per fix, the overlap test.
- Docs: analyzer.mdx + .zh (section + table row + fix line), changelog, decisions for a new rule or a dropped idea,
  `docs/public/easyecs-rules.md`, common-mistakes / troubleshooting if users meet it, the EECS range in AGENTS.md.
