---
name: audit
description: Run an audit round of EasyEcs (core, generators, analyzer, docs) with parallel agents in worktrees, then triage, merge and verify. Use when asked to review the codebase for redundancy, design problems, missing analyzers or doc gaps, or to "do another round".
---

# Audit round

One round = parallel audits, each in its own worktree and branch, then triage and merge on the work branch.

## Scopes (one agent each, never two on the same files)

| Skill | Owns | Looks for |
|---|---|---|
| `audit-core` | `src/EasyEcs.Core`, its tests and benchmarks | redundancy, non-DRY code, coupling, bugs, hot-path cost |
| `audit-generator` | `src/EasyEcs.Generator` generators: Arity, Component, System, Part (+ `Parts.cs`), `Kernel*.cs`, `Code.cs`, `EquatableArray.cs` | generator speed, incremental best practices, newer Roslyn features |
| `audit-analyzer` | `EasyEcsAnalyzer*.cs`, `UnusedSuppressor.cs`, `Symbols.cs`, `Uses.cs`, `Threads.cs`, `src/EasyEcs.CodeFixes` | overlapping rules, missing rules and fixes, false positives |
| `audit-docs` | `docs/content`, README, `docs/public/easyecs-rules.md`, samples READMEs | stale names, examples, best practices, duplication, en/zh |
| `audit-perf` | no files: research, prototypes in its own worktree | the next performance win (Burst, CUDA, stencil, OpenMP, MPI, OS, .NET 10), proven with BenchmarkDotNet |
| `audit-naming` | no files: a report | ambiguous or wrong names; public renames wait for the owner |

`audit-generator` and `audit-analyzer` share `Symbols.cs` (and the analyzer reads `Kernel.Vectorize` for EECS055 /
EECS056): run them in different rounds or give the shared files to one. `audit-naming` reports only, so it runs beside
any of them.

## Order

Code first, docs last: the code audits (core, generator, analyzer, perf) run and merge; agents do not edit docs
pages but report their docs to-dos (what each page must now say), which are collected in one list; `audit-docs`
then runs once over everything, with `tools/bench.sh --site --sync` on the final code. Every agent also reports
what it left undone, which goes on the same list (nothing is dropped silently).

## Starting an agent

- A new worktree may start on an old commit (`origin/master`), not on the work branch. The prompt must say:
  `git checkout -B <audit-branch> <work-branch>`, then check `git merge-base --is-ancestor <work-branch-tip> HEAD`.
- Give each agent: its skill, its owned paths, the paths it must not touch, the commit command (CLAUDE.md: author =
  owner, committer = signing identity, no session links), "commit on your branch, do not push or merge".
- Decision numbers collide between parallel agents: give each a distinct range (e.g. next free D, next free D + 1).
- Docs edits from code audits stay minimal: XML docs with their zh entries and the `api.mdx` complexity row; the
  pages wait for the docs pass (the agent lists what they must say).
- Each agent's definition of done is CLAUDE.md's: core, generator, analyzer, code fix, tests, benchmarks, docs
  to-dos, separate commits.

## While they run

- When the work branch moves (another audit merged, master merged), tell the running agents to rebase onto its tip.
- Findings an agent reports outside its scope (a misleading exception message found by the docs audit) go to the
  agent that owns the file, not into a second edit.

## Triage and merge

1. Read each report. Reject changes that widen the API without need or regress a benchmark.
2. Merge one branch at a time onto the work branch (rebase it onto the tip first, then fast-forward); after each:
   `dotnet build -c Release` (0 warnings), `dotnet test`. A branch that changes a hot path is merged only after a
   quiet A/B against the tip (two rounds, copies as worktrees outside the repo, under `flock /tmp/bench.lock`, nothing
   else running): agents' own numbers taken beside other agents' builds are not proof.
3. Conflicts are usually in `changelog`, `migration`, `decisions` (both sides appended): keep both, renumber a
   duplicated D and fix its anchors and links.
4. After the code audits are merged, run `audit-docs` once more: code changes leave stale pages behind.
5. Finish with `verify`, then fast-forward `master` (no PR unless asked) and push. The owner cuts the release.
6. Remove finished worktrees (`git worktree remove`, `git worktree prune`): a stale worktree makes BenchmarkDotNet
   find two matching projects.
