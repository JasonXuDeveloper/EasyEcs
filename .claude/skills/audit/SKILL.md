---
name: audit
description: "Run an audit round of EasyEcs (core, generators, analyzer, performance, naming, docs) with parallel agents in worktrees - code first, docs last - then triage, merge and verify. Use when asked to review the codebase for redundancy, design problems, missing generated forms or analyzer rules, performance or doc gaps, or to \"do another round\". Triggers: \"another round\", \"review everything\", \"audit\", 再来一轮, 全面审查, 审一遍, 检查整个项目."
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
| `audit-perf` | no files: research, prototypes in its own worktree | the next performance win (Burst, CUDA, stencil, OpenMP, MPI, OS, databases, HPC runtimes, vLLM, .NET 10), proven with BenchmarkDotNet |
| `audit-naming` | every name: a report; internal, test, example and docs renames applied in their own commit | ambiguous, wrong or inconsistent names in code, examples and docs; public renames wait for the owner |

`audit-generator`, `audit-vector-loops` and `audit-analyzer` share `Symbols.cs`, and the analyzer reports the reasons
`Kernel.Vectorize` returns (EECS055 / EECS056): run them in different rounds or give the shared files to one. `audit-naming` reports only, so it runs beside
any of them.

## Order

Code first, then examples, docs last: the code audits (core, generator, vector loops, analyzer, code, perf) run and
merge; then the exemplar pass (`audit-code`, "Exemplar pass") rewrites every sample (VoxelServer included), the
demo, the templates, the battle server and the other headline benchmarks, the tests that model usage and every docs
fence in the newest recommended forms, once no code change that could change a recommendation is still running;
agents do not edit docs pages but report their docs to-dos (what each page must now say), which are collected in one
list; `audit-docs` then runs once over everything, with `tools/bench.sh --site --sync` on the final code. Every agent also reports
what it left undone, which goes on the same list (nothing is dropped silently).

## Starting an agent

- A new worktree may start on an old commit (`origin/master`), not on the work branch. The prompt must say:
  `git checkout -B <audit-branch> <work-branch>`, then check `git merge-base --is-ancestor <work-branch-tip> HEAD`.
- Give each agent: its skill, its owned paths, the paths it must not touch, the commit command (AGENTS.md: author =
  owner, committer = signing identity, no session links), "commit on your branch, do not push or merge".
- Decision numbers collide between parallel agents: give each a distinct range (e.g. next free D, next free D + 1).
- Docs edits from code audits stay minimal: XML docs with their zh entries and the `api.mdx` complexity row; the
  pages wait for the docs pass (the agent lists what they must say).
- Each agent's definition of done is the `feature` skill (docs as to-dos until the docs pass).
- At most three code agents at once (the owner's limit). Benchmarks run one at a time under the machine's lock
  (`audit-perf`), and every build and test waits while one runs, so pair an agent that measures with agents that need
  no BenchmarkDotNet (tests, analyzer, naming) instead of starting three that queue on the lock.
- Tell each agent: never end its turn while its own background command (a test run, an A/B) still runs; wait for it
  with Monitor or an until-loop. An agent that ends its turn early stalls until it is messaged again.
- Two agents whose files overlap (a file both edit, such as `GeneratorTests.cs` or `Kernel.cs`) do not run at once;
  a purely mechanical change to a shared file (a split, a rename) waits until the other branches merged.

## While they run

- When the work branch moves (another audit merged, master merged), tell the running agents to rebase onto its tip.
- Findings an agent reports outside its scope (a misleading exception message found by the docs audit) go to the
  agent that owns the file, not into a second edit.

## Triage and merge

1. Read each report. Reject changes that widen the API without need or regress a benchmark. Read the diff of every
   test an agent changed to make a flaky test pass: every assertion and every measured section must stay as it was;
   only the measurement's setup may change, and the report must say why that removes only noise (D128 is the model).
   Check what the agent's report does not: the Release build's warnings (0, even when the tests pass), and the docs a
   branch changed or regenerated (`git diff <tip> <branch> -- docs/content`, English and Chinese: nothing lost, no
   Chinese line replaced by English, no Chinese in an English page, en and zh saying the same).
   Both slipped through once: an RS1037 warning, and 23 reference pages turned English (`audit-docs`, zh).
2. Merge one branch at a time onto the work branch (rebase it onto the tip first, then fast-forward); after each:
   `dotnet build -c Release` (0 warnings), `dotnet test`. A branch that changes a hot path is merged only after a
   quiet A/B against the tip (two rounds, copies as worktrees outside the repo, under `flock /tmp/bench.lock`, nothing
   else running): agents' own numbers taken beside other agents' builds are not proof.
3. Conflicts are usually in `changelog`, `migration`, `decisions` (both sides appended): keep both, renumber a
   duplicated D and fix its anchors and links.
4. After the code audits are merged, run `audit-docs` once over everything with the collected docs to-dos.
5. Finish with `verify`, then fast-forward `master` (no PR unless asked) and push. The owner cuts the release.
6. Update the skills with what the round taught, then cross-check them (AGENTS.md, Workflows).
7. Remove finished worktrees (`git worktree remove`, `git worktree prune`): a stale worktree makes BenchmarkDotNet
   find two matching projects.
