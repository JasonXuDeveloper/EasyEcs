---
name: verify
description: The full EasyEcs gate before merging to master - build, tests, doc fences, API reference, docs site, samples, templates, live demo. Use before any merge or push to master, and after merging audit branches.
---

# Verify

```bash
export PATH=$PATH:/root/.dotnet:/root/.dotnet/tools:/root/.bun/bin DOTNET_ROOT=/root/.dotnet   # cloud sessions
dotnet build -c Release                          # 0 warnings (warnings are treated as real problems)
dotnet test                                      # all NUnit tests, generator caching and allocation tests included
dotnet run -c Release --project tools/DocCheck   # every C# fence of the docs compiles: "0 problems"
dotnet run --project tools/ApiDocs               # reference pages + Chinese entries: "0 problem(s)"
(cd docs && bun run check && bun run build)      # links, anchors, icons, zh terms; SEO and search expectations
.github/scripts/check-sample.sh samples/HelloWorld   # and SaveLoad, HotReload, Actors, VoxelServer (output == README + docs page)
dotnet run --no-build -c Release --project samples/GameLoop -- --ticks 30
dotnet run --no-build -c Release --project samples/MultiWorld -- --updates 50
bash templates/smoke.sh                          # dotnet new templates pack, build, run, test
dotnet publish tests/EasyEcs.Aot -c Release -r linux-x64 -o <scratch>/aot && <scratch>/aot/EasyEcs.Aot   # no AOT warning, checks pass
```

- `samples/Godot` runs only where a Godot .NET editor is installed (CI: `check-sample.sh samples/Godot --ticks 120`
  with `GODOT` set); locally, build it with `dotnet build samples/Godot`.

- Changed `src/EasyEcs.Core`, the generator or `demo/`? Rebuild the live demo: `bash docs/scripts/build-demo.sh`
  and commit `docs/public/demo` (Cloudflare has no .NET).
- Changed a public XML doc comment? ApiDocs reports the stale Chinese entry in `tools/ApiDocs/zh/`; fix its text and hash.
- Renamed a heading anchor? `bun run build` runs `lint:search`: update `docs/scripts/search-expectations.json`.
- Changed a hot path? `tools/bench.sh <classes> --sync` (its classes and WorldSimulation, BattleServer, the
  neighbors): `--sync` lists the rows that moved 15 % or allocate differently. Each slower row or new allocation is
  run against `origin/master` (two rounds, the copy outside the repo) and fixed or explained before the numbers ship. Every BenchmarkDotNet run goes through `flock /tmp/bench.lock`;
  numbers taken while builds, tests or samples run outside the lock are noise (parallel rows swing by 2x): rerun them
  on a quiet machine.
- Long commands (`bun run build`, the demo, BenchmarkDotNet) run in the background.
- Before a release: `tools/bench.sh --site --sync` on the final code (every class the site shows), the README
  numbers (headline, tables) from the new `bench.json`, changelog / README "What's new" / migration complete, the
  roadmap updated; the GitHub description's numbers are the owner's to change (say so).

## Landing

- Commits: logic, docs and CI separate (CLAUDE.md). Merge `origin/master` first if it moved (owner PRs), then
  `git checkout master && git merge --ff-only <work-branch> && git push origin master`.
- Never trigger a release.
