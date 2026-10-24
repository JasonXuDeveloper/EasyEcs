---
name: audit-naming
description: Audit every name in EasyEcs - public API, generated members, attributes, analyzer descriptors, internal and private code (types, methods, fields, locals, files, projects), tests and benchmarks, samples, templates, the demo, docs prose, headings, anchors and examples' entities and components, and the Chinese terms - for ambiguity, wrong meaning, ECS convention and consistency. Use in every round, and on every new feature before it merges. Report first; public renames need the owner's approval.
metadata:
  internal: true
---

# Naming audit

A name is the first doc a reader sees. This audit covers every name, not only the public API:

| Layer | Where |
|---|---|
| Public API | `src/EasyEcs.Core` public types and members, attributes, options, exceptions' messages |
| Generated | members and types the generators write (`entity.X`, `SetX`, jobs, `__X_SystemCode`, hint names) |
| Analyzer | descriptor field names, ids' titles, messages, code-fix titles |
| Internal code | internal and private types, methods, fields, locals, parameters, files and folders, projects |
| Tests and benchmarks | fixture components, test names (say what is checked), benchmark classes, methods, `[Params]` |
| Examples | samples, templates, the demo, `BattleServer.cs`, `WorldSimulation.cs`, docs fences: components, systems, entities, locals read as the game or server they model, one name per concept across examples |
| Docs | page titles, headings, anchors, file names, glossary terms, prose naming an API exactly as the code does |
| Chinese | one term per concept (`tools/ApiDocs/zh/README.md` glossary), the same term in pages, ApiDocs and the public skill |

1. **Read what is settled**: CLAUDE.md, `docs/content/glossary.mdx`, `ecs-principles.mdx`, `decisions.mdx` (naming
   records and "Not added" records) and the renames in `migration.mdx`. Never re-propose a settled name without a
   new, concrete reason.
2. **See the generated names**: `dotnet build tests/EasyEcs.UnitTest -p:EmitCompilerGeneratedFiles=true`, then read
   `obj/**/generated/EasyEcs.Generator/` for each kind of component (tracked, tag, singleton, indexed, unique,
   grouped, buffer, relation). ArityGenerator families are under `src/EasyEcs.Core/obj`.
3. **Dump the public surface**: `grep -n "^\s*public " src/EasyEcs.Core/*.cs`, keep members of public types, and
   cross-check `docs/content/api.mdx`.
4. **For every name, ask**:
   - **Ambiguity**: is one word used for two things (group, index, pass, slice, tick, since, run, restore, forget,
     rows, into, destroyed/alive)? Are two names used for one thing?
   - **Behavior**: does the name say its cost, whether it stamps a tick, whether it is a structural change, and
     whether it throws? The convention: `Get` throws, `TryGet` returns false, `Find` enumerates.
   - **ECS vocabulary**: does it match DOTS, Bevy, Flecs, Arch and Friflo where the behavior is the same, and
     differ where the behavior differs?
   - **Call sites**: does it read well in `samples/`, `BattleServer.cs` and the guide examples?
5. **Families**:
   - verbs: Set/Add/Create/Instantiate; Remove/Destroy/Unlink; Get/Read/TryGet/Find; Save/Load/Capture/Restore/Move;
   - suffixes: -All, -Entities, -With/-Without/-Keeping/-Into, *Extensions, *Info;
   - booleans: Is/Has/Can, and no mutator named like a predicate;
   - options: a capacity says Capacity.
6. **Analyzer**: every DiagnosticDescriptor (field name, title, message) in `EasyEcsAnalyzer*.cs` matches its rule.
7. **New since the last round**: every public name the changelog adds (attributes, parameters, generated members,
   options) gets the questions above first; it is cheapest to change before a release.
8. **zh**: the Chinese term of each name follows the glossary in `tools/ApiDocs/zh/README.md` (one term per concept).
9. **Internals**: private and internal names that reuse a public term for something else (Runner, Passes, Groups,
   Forget, Rows, ChangeTick); abbreviations a reader must decode; a name that no longer matches what the code does
   after a refactor; files whose name is not the type or concern inside.
10. **Examples and docs**: an example's names model its domain (`Health`, `Damage`, not `Comp1`, `Foo`); the same
   concept has the same name in every example and page; a heading names the feature as the API does; an anchor
   survives a rename only through a redirect or a link fix.
11. **Output**: a table per layer (current | problem | proposed | breaking | confidence | call site before → after),
   ranked by impact, split into clearly right vs owner's call, plus internal renames and a one-line
   "checked and fine" list.

## Applying renames

- **Internal names, tests, benchmarks, examples and docs** are renamed without approval (they are not released API),
  in their own commit; a benchmark rename keeps the site's numbers (`docs/src/data/bench.json` keys, `benchmarks.ts`).

- **Owner's approval**: public renames need it first. Pre-1.0 there are no `[Obsolete]` aliases.
- **Generated members**: some analyzer checks match them by name prefix, so update those first:
  `grep -n 'StartsWith("\|EndsWith("' src/EasyEcs.Generator src/EasyEcs.CodeFixes`
  (covers `Symbols.IsGeneratedWriter` and the EECS033/038/047 paths).
- **Every public rename then needs**:
  - migration, changelog, README "What's new";
  - `api.mdx`, and `tools/ApiDocs` zh entries;
  - a decision record;
  - analyzer and code-fix strings, samples, templates, the demo.
- **Stale names**: `grep -rn "<old>" docs/content CLAUDE.md README.md samples templates demo` must show only the
  history pages (changelog, migration, decisions).
