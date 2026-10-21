---
name: audit-naming
description: Audit EasyEcs names (public Core API, generated members, analyzer descriptors, internals) for ambiguity, wrong meaning, ECS convention and consistency. Report first; public renames need the owner's approval.
---

# Naming audit

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
7. **Internals**: private and internal names that reuse a public term for something else (Runner, Passes, Groups,
   Forget, Rows, ChangeTick).
8. **Output**: a table per layer (current | problem | proposed | breaking | confidence | call site before → after),
   ranked by impact, split into clearly right vs owner's call, plus internal renames and a one-line
   "checked and fine" list.

## Applying renames

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
