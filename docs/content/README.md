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
| [Cheat sheet](https://ecs.xgamedev.net/en/docs/cheat-sheet) | [cheat-sheet.mdx](cheat-sheet.mdx) | Look up the common calls. |
| [Glossary](https://ecs.xgamedev.net/en/docs/glossary) | [glossary.mdx](glossary.mdx) | Look up a term. |

## Samples

One page per runnable project of [samples/](../../samples), its files included, under [samples/](samples), and the
battle server, whose sidebar entry is in the same section:

| Page | Source | Read it to |
|---|---|---|
| [Samples](https://ecs.xgamedev.net/en/docs/samples) | [samples/index.mdx](samples/index.mdx) | Pick a runnable sample, in learning order. |
| [Example: a battle server](https://ecs.xgamedev.net/en/docs/example) | [example.mdx](example.mdx) | See every feature in one world (the code of `BattleServer.cs`). |
| [HelloWorld](https://ecs.xgamedev.net/en/docs/samples/hello-world) | [samples/hello-world.mdx](samples/hello-world.mdx) | The smallest program. |
| [GameLoop](https://ecs.xgamedev.net/en/docs/samples/game-loop) | [samples/game-loop.mdx](samples/game-loop.mdx) | A fixed-tick server fed by a network thread. |
| [SaveLoad](https://ecs.xgamedev.net/en/docs/samples/save-load) | [samples/save-load.mdx](samples/save-load.mdx) | Saves and migration between worlds. |
| [MultiWorld](https://ecs.xgamedev.net/en/docs/samples/multi-world) | [samples/multi-world.mdx](samples/multi-world.mdx) | One world per thread. |
| [HotReload](https://ecs.xgamedev.net/en/docs/samples/hot-reload) | [samples/hot-reload.mdx](samples/hot-reload.mdx) | New logic in a running world. |
| [Actors](https://ecs.xgamedev.net/en/docs/samples/actors) | [samples/actors.mdx](samples/actors.mdx) | Entities that answer messages, with persistence. |
| [VoxelServer](https://ecs.xgamedev.net/en/docs/samples/voxel-server) | [samples/voxel-server.mdx](samples/voxel-server.mdx) | A Minecraft-style server. |
| [Godot](https://ecs.xgamedev.net/en/docs/samples/godot) | [samples/godot.mdx](samples/godot.mdx) | EasyEcs in a Godot 4 game. |
| [Live demo](https://ecs.xgamedev.net/en/docs/demo) | [demo.mdx](demo.mdx) | Run a world in the browser (WebAssembly). |

## Guide

One page per concept, under [guide/](guide):

| Page | Source |
|---|---|
| [Structuring a real project](https://ecs.xgamedev.net/en/docs/guide/basics/project-structure) | [guide/basics/project-structure.mdx](guide/basics/project-structure.mdx) |
| [Entities and components](https://ecs.xgamedev.net/en/docs/guide/basics/entities-and-components) | [guide/basics/entities-and-components.mdx](guide/basics/entities-and-components.mdx) |
| [Prefabs and cloning](https://ecs.xgamedev.net/en/docs/guide/basics/prefabs) | [guide/basics/prefabs.mdx](guide/basics/prefabs.mdx) |
| [Queries and filters](https://ecs.xgamedev.net/en/docs/guide/basics/queries) | [guide/basics/queries.mdx](guide/basics/queries.mdx) |
| [Systems](https://ecs.xgamedev.net/en/docs/guide/basics/systems) | [guide/basics/systems.mdx](guide/basics/systems.mdx) |
| [Singletons](https://ecs.xgamedev.net/en/docs/guide/basics/singletons) | [guide/basics/singletons.mdx](guide/basics/singletons.mdx) |
| [Structural changes](https://ecs.xgamedev.net/en/docs/guide/changes/structural-changes) | [guide/changes/structural-changes.mdx](guide/changes/structural-changes.mdx) |
| [ref and await](https://ecs.xgamedev.net/en/docs/guide/changes/refs-and-await) | [guide/changes/refs-and-await.mdx](guide/changes/refs-and-await.mdx) |
| [Change tracking](https://ecs.xgamedev.net/en/docs/guide/changes/change-tracking) | [guide/changes/change-tracking.mdx](guide/changes/change-tracking.mdx) |
| [Dynamic buffers](https://ecs.xgamedev.net/en/docs/guide/data/dynamic-buffers) | [guide/data/dynamic-buffers.mdx](guide/data/dynamic-buffers.mdx) |
| [Indexes and relations](https://ecs.xgamedev.net/en/docs/guide/data/indexes-and-relations) | [guide/data/indexes-and-relations.mdx](guide/data/indexes-and-relations.mdx) |
| [Hierarchy](https://ecs.xgamedev.net/en/docs/guide/data/hierarchy) | [guide/data/hierarchy.mdx](guide/data/hierarchy.mdx) |
| [Groups](https://ecs.xgamedev.net/en/docs/guide/data/groups) | [guide/data/groups.mdx](guide/data/groups.mdx) |
| [Cleanup and disable](https://ecs.xgamedev.net/en/docs/guide/changes/cleanup-and-disable) | [guide/changes/cleanup-and-disable.mdx](guide/changes/cleanup-and-disable.mdx) |
| [Bulk changes](https://ecs.xgamedev.net/en/docs/guide/changes/bulk) | [guide/changes/bulk.mdx](guide/changes/bulk.mdx) |
| [Snapshots](https://ecs.xgamedev.net/en/docs/guide/saves/snapshots) | [guide/saves/snapshots.mdx](guide/saves/snapshots.mdx) |
| [Save and load](https://ecs.xgamedev.net/en/docs/guide/saves/saves) | [guide/saves/saves.mdx](guide/saves/saves.mdx) |
| [Migration and unload](https://ecs.xgamedev.net/en/docs/guide/saves/migration-and-unload) | [guide/saves/migration-and-unload.mdx](guide/saves/migration-and-unload.mdx) |
| [Threading](https://ecs.xgamedev.net/en/docs/guide/performance/threading) | [guide/performance/threading.mdx](guide/performance/threading.mdx) |
| [Hosting in a .NET server](https://ecs.xgamedev.net/en/docs/guide/production/hosting) | [guide/production/hosting.mdx](guide/production/hosting.mdx) |
| [Testing systems](https://ecs.xgamedev.net/en/docs/guide/production/testing) | [guide/production/testing.mdx](guide/production/testing.mdx) |
| [Debugging and diagnostics](https://ecs.xgamedev.net/en/docs/guide/production/diagnostics) | [guide/production/diagnostics.mdx](guide/production/diagnostics.mdx) |
| [Analyzer rules](https://ecs.xgamedev.net/en/docs/guide/help/analyzer) | [guide/help/analyzer.mdx](guide/help/analyzer.mdx) |
| [Common mistakes](https://ecs.xgamedev.net/en/docs/guide/help/common-mistakes) | [guide/help/common-mistakes.mdx](guide/help/common-mistakes.mdx) |
| [Troubleshooting](https://ecs.xgamedev.net/en/docs/guide/help/troubleshooting) | [guide/help/troubleshooting.mdx](guide/help/troubleshooting.mdx) |

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
| [Using EasyEcs with AI assistants](https://ecs.xgamedev.net/en/docs/ai) | [ai.mdx](ai.mdx) | The agent skill ([skills/easyecs](../../skills/easyecs/SKILL.md), `npx skills add JasonXuDeveloper/EasyEcs`), `llms.txt` and Markdown copies. |
| [Design decisions](https://ecs.xgamedev.net/en/docs/decisions) | [decisions.mdx](decisions.mdx) | Why EasyEcs is the way it is. |
| [Roadmap](https://ecs.xgamedev.net/en/docs/roadmap) | [roadmap.mdx](roadmap.mdx) | What is planned and what was dropped. |
| [Changelog](https://ecs.xgamedev.net/en/docs/changelog) | [changelog.mdx](changelog.mdx) | What changed in each release. |
| [Migration guide](https://ecs.xgamedev.net/en/docs/migration) | [migration.mdx](migration.mdx) | What to change when upgrading: between 4.x releases, from v3 to v4 and from v2 to v3. |
