---
name: audit-docs
description: Audit the EasyEcs docs (docs/content en+zh, README, rules file, samples) for stale API names, wrong or non-exemplary examples, missing best practices, duplication and en/zh drift. Use after code changes land and before merging to master.
---

# Docs audit

Read `docs/WRITING.md` (page anatomy, tone, numbers) and `docs/CLAUDE.md` first.

## Per page (en and zh)

1. **Names**: every API in prose and code exists now (grep the old names of every rename in the changelog's
   "Changes that need code changes"). History pages (changelog, migration, decisions) keep old names on purpose.
2. **Examples are exemplary** (`ecs-principles.mdx`): plain data components, one system per responsibility, stateless
   systems, generated members (`entity.Health`, `item.Health`, `SetX`) over generic calls, and no analyzer warning
   (Read for read-only loops, values at creation instead of Set, ...). DocCheck compiles fences, not their quality.
3. **Anatomy**: what / when / how / full example / fast / costs / pitfalls / best practices / related.
4. **Behavior matches the code**: after a fix, pages that describe the old behavior (pitfalls, troubleshooting,
   analyzer messages) are rewritten. Verify a claim with a throwaway test when unsure.
5. **Numbers**: no typed benchmark numbers outside `decisions.mdx` (the site reads `bench.json`).
6. **Changelog**: user-facing items only, no numbers.
7. **zh**: says the same as en; glossary in `tools/ApiDocs/zh/README.md` (stamp = 打 tick, filter = 过滤条件,
   tracking = 追踪, parent = 父实体, World stays World); `bun run check` runs `lint:terms`.

## Across pages

- One owner per explanation; other pages summarize in a sentence and link.
- Copies that must exist (analyzer.mdx, common-mistakes, troubleshooting, cheat-sheet, easyecs-rules.md, api.mdx
  complexity) agree with each other and with the code.
- The project's own docs never mention other products that use it.

Finish with DocCheck, ApiDocs and `bun run check`; `verify` runs `bun run build` (SEO, search expectations).
