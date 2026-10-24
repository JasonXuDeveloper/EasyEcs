---
name: audit
description: Run an audit round of EasyEcs (core, generators, analyzer, performance, naming, docs) with parallel agents in worktrees - code first, docs last - then triage, merge and verify. Use when asked to review the codebase for redundancy, design problems, missing generated forms or analyzer rules, performance or doc gaps, or to "do another round".
metadata:
  internal: true
---

# Audit round

One round = parallel audits, each in its own worktree and branch, then triage and merge on the work branch.

## Scopes (one agent each, never two on the same files)

| Skill | Owns | Looks for |
|---|---|---|
| `audit-core` | `src/EasyEcs.Core`, its tests and benchmarks | redundancy, non-DRY code, coupling, bugs, hot-path cost |
| `audit-generator` | `src/EasyEcs.Generator` generators: Arity, Component, System, Part (+ `Parts.cs`), `Code.cs`, `EquatableArray.cs` | what users still write twice, plain forms to emit fast, generator speed |
| `audit-vector-loops` | `Kernel*.cs`, the vector-loop emission, `Interleaved.cs` / `Lanes.cs` | methods left scalar, conditions, side effects, structural writes as bulk ops, fixable reasons |
| `audit-analyzer` | `EasyEcsAnalyzer*.cs`, `UnusedSuppressor.cs`, `Symbols.cs`, `Uses.cs`, `Threads.cs`, `src/EasyEcs.CodeFixes` | missing rules (wrong, costly, hand-written code), code fixes, overlaps, false positives |
| `audit-code` | `tools/`, `tests/` structure, `benchmarks/` harness, `samples/`, `templates/`, `demo/`, `.github/` and `docs/scripts/`, the seams between projects | coupling, non-DRY code, dead code, god files, layering, unclear names and comments |
| `audit-docs` | `docs/content`, README, `skills/easyecs/SKILL.md` (the public skill), samples READMEs | every part of each feature page, stale names, examples, duplication, en/zh |
| `audit-perf` | no files: research, prototypes in its own worktree | the next performance win (Burst, CUDA, stencil, OpenMP, MPI, OS, .NET 10), proven with BenchmarkDotNet |
| `audit-naming` | every name: a report; internal, test, example and docs renames applied in their own commit | ambiguous, wrong or inconsistent names in code, examples and docs; public renames wait for the owner |

`audit-generator` and `audit-analyzer` share `Symbols.cs` (and the analyzer reads `Kernel.Vectorize` for EECS055 /
EECS056): run them in different rounds or give the shared files to one. `audit-naming` reports only, so it runs beside
any of them.

## Order

Code first, docs last: the code audits (core, generator, analyzer, code, perf) run and merge; agents do not edit docs
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
- Each agent's definition of done is the `feature` skill (docs as to-dos until the docs pass).

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
4. After the code audits are merged, run `audit-docs` once over everything with the collected docs to-dos.
5. Finish with `verify`, then fast-forward `master` (no PR unless asked) and push. The owner cuts the release.
6. Remove finished worktrees (`git worktree remove`, `git worktree prune`): a stale worktree makes BenchmarkDotNet
   find two matching projects.
