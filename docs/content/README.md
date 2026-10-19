# EasyEcs documentation

The documentation is published at **https://ecs.xgamedev.net** (English: `/en/docs`, Chinese: `/zh/docs`), with
search, diagrams, benchmark charts and a Markdown copy of every page (`/llms.txt`, `/llms-full.txt`). This folder is
its source: each page is `<page>.mdx` (English) and `<page>.zh.mdx` (Chinese), and the sidebar order is in
`meta.json` / `meta.zh.json`. The [project README](../../README.md) has the pitch and the quick start.

## Learn

| Page | Source | Read it to |
|---|---|---|
| [What is ECS](https://ecs.xgamedev.net/en/docs/what-is-ecs) | [what-is-ecs.mdx](what-is-ecs.mdx) | Learn entities, components and systems from scratch. |
| [Features](https://ecs.xgamedev.net/en/docs/features) | [features.mdx](features.mdx) | See every feature on one page. |
| [Getting started](https://ecs.xgamedev.net/en/docs/getting-started) | [getting-started.mdx](getting-started.mdx) | Build a small simulation step by step. |
| [ECS principles](https://ecs.xgamedev.net/en/docs/ecs-principles) | [ecs-principles.mdx](ecs-principles.mdx) | Design features as data plus one-job systems. |
| [Example: a battle server](https://ecs.xgamedev.net/en/docs/example) | [example.mdx](example.mdx) | See every feature in one world (the code of `BattleServer.cs`). |
| [Cheat sheet](https://ecs.xgamedev.net/en/docs/cheat-sheet) | [cheat-sheet.mdx](cheat-sheet.mdx) | Look up the common calls. |
| [Glossary](https://ecs.xgamedev.net/en/docs/glossary) | [glossary.mdx](glossary.mdx) | Look up a term. |

## Guide

One page per concept, under [guide/](guide):

| Page | Source |
|---|---|
| [Structuring a real project](https://ecs.xgamedev.net/en/docs/guide/project-structure) | [guide/project-structure.mdx](guide/project-structure.mdx) |
| [Entities and components](https://ecs.xgamedev.net/en/docs/guide/entities-and-components) | [guide/entities-and-components.mdx](guide/entities-and-components.mdx) |
| [Prefabs and cloning](https://ecs.xgamedev.net/en/docs/guide/prefabs) | [guide/prefabs.mdx](guide/prefabs.mdx) |
| [Queries and filters](https://ecs.xgamedev.net/en/docs/guide/queries) | [guide/queries.mdx](guide/queries.mdx) |
| [Systems](https://ecs.xgamedev.net/en/docs/guide/systems) | [guide/systems.mdx](guide/systems.mdx) |
| [Singletons](https://ecs.xgamedev.net/en/docs/guide/singletons) | [guide/singletons.mdx](guide/singletons.mdx) |
| [Structural changes](https://ecs.xgamedev.net/en/docs/guide/structural-changes) | [guide/structural-changes.mdx](guide/structural-changes.mdx) |
| [`ref` and `await`](https://ecs.xgamedev.net/en/docs/guide/refs-and-await) | [guide/refs-and-await.mdx](guide/refs-and-await.mdx) |
| [Change tracking](https://ecs.xgamedev.net/en/docs/guide/change-tracking) | [guide/change-tracking.mdx](guide/change-tracking.mdx) |
| [Dynamic buffers](https://ecs.xgamedev.net/en/docs/guide/dynamic-buffers) | [guide/dynamic-buffers.mdx](guide/dynamic-buffers.mdx) |
| [Indexes and relations](https://ecs.xgamedev.net/en/docs/guide/indexes-and-relations) | [guide/indexes-and-relations.mdx](guide/indexes-and-relations.mdx) |
| [Hierarchy](https://ecs.xgamedev.net/en/docs/guide/hierarchy) | [guide/hierarchy.mdx](guide/hierarchy.mdx) |
| [Groups](https://ecs.xgamedev.net/en/docs/guide/groups) | [guide/groups.mdx](guide/groups.mdx) |
| [Cleanup and disable](https://ecs.xgamedev.net/en/docs/guide/cleanup-and-disable) | [guide/cleanup-and-disable.mdx](guide/cleanup-and-disable.mdx) |
| [Bulk changes](https://ecs.xgamedev.net/en/docs/guide/bulk) | [guide/bulk.mdx](guide/bulk.mdx) |
| [Snapshots](https://ecs.xgamedev.net/en/docs/guide/snapshots) | [guide/snapshots.mdx](guide/snapshots.mdx) |
| [Save and load](https://ecs.xgamedev.net/en/docs/guide/saves) | [guide/saves.mdx](guide/saves.mdx) |
| [Migration and unload](https://ecs.xgamedev.net/en/docs/guide/migration-and-unload) | [guide/migration-and-unload.mdx](guide/migration-and-unload.mdx) |
| [Threading](https://ecs.xgamedev.net/en/docs/guide/threading) | [guide/threading.mdx](guide/threading.mdx) |
| [Hosting in a .NET server](https://ecs.xgamedev.net/en/docs/guide/hosting) | [guide/hosting.mdx](guide/hosting.mdx) |
| [Testing systems](https://ecs.xgamedev.net/en/docs/guide/testing) | [guide/testing.mdx](guide/testing.mdx) |
| [Debugging and diagnostics](https://ecs.xgamedev.net/en/docs/guide/diagnostics) | [guide/diagnostics.mdx](guide/diagnostics.mdx) |
| [Analyzer rules](https://ecs.xgamedev.net/en/docs/guide/analyzer) | [guide/analyzer.mdx](guide/analyzer.mdx) |
| [Common mistakes](https://ecs.xgamedev.net/en/docs/guide/common-mistakes) | [guide/common-mistakes.mdx](guide/common-mistakes.mdx) |
| [Troubleshooting](https://ecs.xgamedev.net/en/docs/guide/troubleshooting) | [guide/troubleshooting.mdx](guide/troubleshooting.mdx) |

## Compare

| Page | Source |
|---|---|
| [Performance](https://ecs.xgamedev.net/en/docs/performance) | [performance.mdx](performance.mdx) |
| [Why it is fast](https://ecs.xgamedev.net/en/docs/why-fast) | [why-fast.mdx](why-fast.mdx) |
| [Comparison](https://ecs.xgamedev.net/en/docs/comparison) | [comparison.mdx](comparison.mdx) |
| [EasyEcs vs Arch](https://ecs.xgamedev.net/en/docs/vs-arch) | [vs-arch.mdx](vs-arch.mdx) |
| [EasyEcs vs Friflo](https://ecs.xgamedev.net/en/docs/vs-friflo) | [vs-friflo.mdx](vs-friflo.mdx) |
| [EasyEcs vs DefaultEcs](https://ecs.xgamedev.net/en/docs/vs-defaultecs) | [vs-defaultecs.mdx](vs-defaultecs.mdx) |
| [EasyEcs vs Unity Entities](https://ecs.xgamedev.net/en/docs/vs-unity-entities) | [vs-unity-entities.mdx](vs-unity-entities.mdx) |
| [Migrate from Arch](https://ecs.xgamedev.net/en/docs/migrate-from-arch) | [migrate-from-arch.mdx](migrate-from-arch.mdx) |
| [Migrate from Friflo](https://ecs.xgamedev.net/en/docs/migrate-from-friflo) | [migrate-from-friflo.mdx](migrate-from-friflo.mdx) |
| [Migrate from DefaultEcs](https://ecs.xgamedev.net/en/docs/migrate-from-defaultecs) | [migrate-from-defaultecs.mdx](migrate-from-defaultecs.mdx) |

## Reference

| Page | Source | Contents |
|---|---|---|
| [API reference](https://ecs.xgamedev.net/en/docs/reference) | [reference/](reference) | Every public type, generated by `tools/ApiDocs` (do not edit by hand). |
| [API and complexity](https://ecs.xgamedev.net/en/docs/api) | [api.mdx](api.mdx) | Every public API with its complexity (read by `tools/ApiDocs`). |
| [Architecture](https://ecs.xgamedev.net/en/docs/architecture) | [architecture.mdx](architecture.mdx) | Storage, entity lifecycle, query cache, systems, generators, threads. |
| [FAQ](https://ecs.xgamedev.net/en/docs/faq) | [faq.mdx](faq.mdx) | Short answers to common questions. |
| [Using EasyEcs with AI assistants](https://ecs.xgamedev.net/en/docs/ai) | [ai.mdx](ai.mdx) | `llms.txt`, Markdown copies and a rules file ([easyecs-rules.md](../public/easyecs-rules.md)). |
| [Design decisions](https://ecs.xgamedev.net/en/docs/decisions) | [decisions.mdx](decisions.mdx) | Why EasyEcs is the way it is. |
| [Roadmap](https://ecs.xgamedev.net/en/docs/roadmap) | [roadmap.mdx](roadmap.mdx) | What is planned and what was dropped. |
| [Changelog](https://ecs.xgamedev.net/en/docs/changelog) | [changelog.mdx](changelog.mdx) | What changed in each release. |
| [Migration guide](https://ecs.xgamedev.net/en/docs/migration) | [migration.mdx](migration.mdx) | What to change when upgrading: between 4.x releases, from v3 to v4 and from v2 to v3. |
