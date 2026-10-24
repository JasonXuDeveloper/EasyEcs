---
name: audit-docs
description: Audit and write the EasyEcs docs (docs/content en+zh, README, the public skill, samples) - every part of each feature page (when to use it, the recommended generated form, the fast form, costs and complexity, pitfalls, best practices), every place a feature must appear, stale names, exemplary examples, duplication, en/zh drift. Use once the code is final (the docs pass) and before merging to master.
metadata:
  internal: true
---

# Docs audit

Read `docs/WRITING.md` (page anatomy, tone, numbers) and `docs/CLAUDE.md` first.

## Per page (en and zh)

1. **Names**: examples model their domain and use one name per concept across pages; headings and terms match the
   API and the glossary (en and zh; `audit-naming`). Every API in prose and code exists now (grep the old names of every rename in the changelog's
   "Changes that need code changes"). History pages (changelog, migration, decisions) keep old names on purpose.
2. **Examples are exemplary** (`ecs-principles.mdx`): plain data components, one system per responsibility, stateless
   systems, generated members (`entity.Health`, `item.Health`, `SetX`) over generic calls, and no analyzer warning
   (Read for read-only loops, values at creation instead of Set, ...). DocCheck compiles fences, not their quality.
3. **Anatomy** (WRITING.md "Page anatomy"; a feature's guide page has every part, in this order):
   - **what it is**: the problem it solves, in two to four sentences;
   - **when to use it**: an `idea` Callout (use it for) and a `warn` Callout (do not use it for: what to use instead);
   - **how to use it**: the recommended form first, the one the generator supports (an attribute, a `[ForEach]`
     parameter, a generated member: `entity.Health`, `SetX`, `[Sum]`, `Reduce<T>`), each step with a complete example
     that compiles; where the generator writes code, Tabs `You write` / `The generator writes` show both (real
     generated output, checked against `obj/**/generated`); a hand-written alternative only when it is the right
     choice somewhere, saying where;
   - **what the generator writes, everywhere it writes it**: not only guide pages. Every headline example whose code
     the generator turns into something faster (README, the docs home, getting started, the battle server walkthrough,
     samples pages, cheat sheet) shows it: on the site `You write` / `The generator writes` Tabs; in the README (GitHub
     has no tabs) a `<details><summary>What the generator writes</summary>` block under the example. The generated
     side is real output from `obj/**/generated`, trimmed to what matters (the vector loop, the bulk call, the job),
     with a comment per step saying why it is faster (lanes per instruction, whole words, no per-row call), and it is
     refreshed whenever the generator changes that form;
   - **full example**: a short program that runs (the "Full example" section, `doccheck=example` fences);
   - **how to use it fast**: the high-performance form with an example (vector loop eligible, `Parallel`, `ChunkRows`,
     `Read` over `Query`, bulk calls, `Mut<T>`, values at creation), each choice with its benchmark through
     BenchTable from `bench.json`;
   - **what it costs**: memory per entity or row, time per operation, what is free when unused; the same complexity in
     `docs/content/api.mdx` / `.zh.mdx` (O(...) per call, rerun ApiDocs);
   - **pitfalls**: Mistake | What happens | Caught by (the EECS rule and its code fix);
   - **best practices**: what the battle server and samples do and why;
   - **related**: two to four Cards.
4. **Behavior matches the code**: after a fix, pages that describe the old behavior (pitfalls, troubleshooting,
   analyzer messages) are rewritten. Verify a claim with a throwaway test when unsure.
5. **Numbers**: no typed benchmark numbers outside `decisions.mdx` (the site reads `bench.json`).
6. **Changelog**: user-facing items only, no numbers.
7. **zh**: says the same as en; glossary in `tools/ApiDocs/zh/README.md` (stamp = 打 tick, filter = 过滤条件,
   tracking = 追踪, parent = 父实体, World stays World); `bun run check` runs `lint:terms`.
8. **One meaning per word**: "group" is a system group (`ISystemGroup`) or a table group (`IGroup` / `GroupBy`); the
   `ChangedAny` / `RemovedAny` OR is a set (zh 集合), and what `SaveEntities` writes is "a save of entities" (zh
   实体存档; never "group save" / 分组存档, which reads as an `IGroup`). "Destroyed" means freed; an entity kept for its
   cleanup components is retired (zh 退休). The public XML docs generated from `src/EasyEcs.Generator` templates
   count too (zh entry + ApiDocs).
9. **Renames leave code ragged**: realign the trailing `//` comments of fence lines whose names got shorter or longer.

## Across pages

- One owner per explanation; other pages summarize in a sentence and link.
- Copies that must exist (analyzer.mdx, common-mistakes, troubleshooting, cheat-sheet, the public skill, api.mdx
  complexity) agree with each other and with the code.
- The project's own docs never mention other products that use it.
- **Every feature is everywhere it belongs** (`feature`, part 7): its guide page; `features.mdx` and
  the comparison tables when peers differ; `cheat-sheet`, `glossary`, `api.mdx` (complexity) and the generated
  `reference/` (ApiDocs, zh entries); `analyzer.mdx` for its rules; `skills/easyecs/SKILL.md` (the public skill); changelog, README
  "What's new", migration for a breaking change; a decision record for every non-obvious choice and dropped idea;
  the roadmap; the battle server (`BattleServer.cs` regions + `example.mdx` walkthrough) and the samples where it
  belongs, as exemplary code; CLAUDE.md's layout.
- **Inspiration is named where it helps** (OpenMP `threadprivate` for `Scratch<T>`, `reduction` for `Reduce<T>`,
  stencil halos for `Tile<,>`, Burst for vector loops): one clause in "what it is", the comparison in decisions.
- **Docs come last**: while code still changes, agents collect docs to-dos instead of editing pages; one full pass
  at the end, with `tools/bench.sh --site --sync` on the final code (numbers come from `bench.json` only).

Finish with DocCheck, ApiDocs and `bun run check`; `verify` runs `bun run build` (SEO, search expectations).
