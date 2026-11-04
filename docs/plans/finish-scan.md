# Finish 1a and 1b: the generated-members scan and the directory layout (internal, not published)

Read-only reports for the owner, made on `feat-finish-scan` at `29ea0dd` (the work branch). Nothing here is built.
Backlog: `docs/plans/backlog.md`, Finish, items 1a and 1b. Report 2 refreshes `docs/plans/layout.md` (the plan made at
`2515638`, with the coordinator's decisions of 2026-10-26) against the tip; when the move ships, both files go.

## Report 1: the generated-members scan (Finish 1a)

Scope: the public API (the generated members of `guide/entities-and-components.mdx`, the attributes, the `[ForEach]`
parameters), `benchmarks/EasyEcs.Benchmark/BattleServer.cs`, every sample, the templates, the live demo, and the C#
fences of `docs/content/guide/*.mdx` (English; the Chinese pages repeat the same fences), `example.mdx`,
`getting-started.mdx`, README and the public skill (7,083 fence lines extracted and counted by normalized line).
Settled records were read first and are not re-proposed: D26 (generated members only where they save lines, style
alone rejected), D51, D56 (no generated `AddSystems()`, no cached filters from attributes, no counting members, no
named `TryGetXRef`), D79 / D98 (streams of `EntityRandom` numbered by the author), D101 (declarations as attributes),
D102 (systems as ref structs), D109 / D111 (views, `[Prefab]`, no generated view over a declaration), D120 (no
`[Feature]` modules, no generated reader of records), D141 (`[Drain]`), voxel-rewrite.md GA2 / GA5 / GA6 (declined).

### Ranking

Lines saved per site times the sites that would change (user-facing code: samples, demo, templates, the battle server,
the English guide fences; the Chinese twins and the tests come on top and are named per finding).

| # | Pattern | Sites | Lines per site | Total | Verdict |
|---|---|---:|---:|---:|---|
| 1 | A tile's `Beside` (and its shape) written by hand | 10 | 7 (2D) to 9 (3D), shape 2-5 more | ~70 (+25 shape) | **build** (1a without a new name; 1b `[Tile]` owner's call, recommended) |
| 2 | Countdown timers (`--x.Ticks`, then `Destroy` / `RemoveX`) | ~10 of 21 | ~6 (a system and its method) | ~60 | not worth it |
| 3 | `[EcsSystem, UpdateInGroup<Simulation>]` on 34 VoxelServer systems | 34 | 0.5 | ~17 | not a generator item: the exemplar pass (Simulation is the default group) |
| 4 | `IDirectMapped<TKey>`'s `Slots` / `SlotOf` written as a power-of-two window | 5 | 2 | 10 | **build** with 1 |
| 5 | A reduction that keeps the row with the lowest key (argmin, MPI's `MINLOC`) | 2 | 3 (+ the method's vector loop) | 6 | **build** |
| 6 | A request entity finds its target by key in every request system | 6 | 2 | ~12 | not worth it |
| 7 | `Scratch<T>`: `??= new List<T>()`, then `Clear()` | 4 | 0.5 | 2 | not worth it (the missing-`Clear` rule goes to `audit-analyzer`) |
| 8 | `world.TryGetEntity(id, out var e) && Requests.X(e, ...)` | 3 | 0.5 | 1.5 | not worth it |
| 9 | `world.GetOrAddSingleton<Emitted<T>>().Items` before a count or a send | 8 | 0 | 0 | not worth it (style; D120) |
| 10 | `[ForEach(Parallel = true)]` (53 times) inferred from the body | 53 | 0 | 0 | not worth it (safety) |
| 11 | Generated members nobody uses | - | - | - | none found: every kind is used outside the tests |

Out of scope but found on the way (the exemplar pass, `audit-code`): three copies of the samples' `Checks` class
(`samples/GameLoop/Host/Checks.cs`, `samples/SaveLoad/Support.cs:10-20`, `samples/HotReload/HotReload.Host/Support.cs:6-16`,
each project stands alone on purpose, so they stay but should read the same); `if (!world.HasBonus) world.SetBonus(Points: 1)`
(`samples/HotReload/HotReload.Plugin/ScoringPlugin.cs:11-14`) is the generated `world.GetOrAddBonus(1)`; the HotReload
plugin's `Status` and the battle server's `LootValue` / `NearestPlayer` are host loops a `[ForEach]` entry point with a
reduction would replace; `Battle`'s per-method `battle.Replicated++` moves to `[Accumulate]` (backlog 2c, already planned).

### 1. Tile declarations: `Beside` inferred from the key, the shape as an attribute (build)

**Pattern.** Every tile writes the same neighbor function: step the key's coordinate by one per face.

- `docs/content/guide/grids.mdx:50-56`, `:279-287` (3D), `:358-364`, `:468-474`, `:547-553`
- `docs/content/guide/analyzer.mdx:2425`, `:2437` (a 1D `int` key: `face == TileFaces.West ? index - 1 : index + 1`)
- `samples/VoxelServer/Components/Chunks.cs:29-35`, `samples/VoxelServer/Components/Scent.cs:65-71` (identical)
- `benchmarks/EasyEcs.Benchmark/TileLayersBenchmark.cs:20` (the other tile benchmarks wrap: periodic grids)
- the Chinese twins of the five grids fences; about 40 tile types in `tests/EasyEcs.UnitTest/TileTests*.cs` and
  `tests/EasyEcs.Aot/Program.cs` (some periodic: those keep their own `Beside`)

Each comes with its shape, `static int ITileShape.Width => 16;` and the others (`grids.mdx:47-48`, `:273-277`,
`Chunks.cs:25-27`, `Scent.cs:62-63`, 4-8 per benchmark file), and the key's type named twice (`[LookupKey] PlatePos Pos`
and `ITile<PlatePos>`).

**1a. `Beside` inferred (no new name).** When a type declares `ITile<TKey>` without `Beside`, and `TKey` is `int`
(a row: West −1, East +1) or a struct whose integer members are named `X` and `Z` (and `Y` for layers), the
generator writes `Beside` into the part it already writes for the key (`PartGenerator`), with the faces of
`TileFaces` (North −z, South +z, West −x, East +x, Below −y, Above +y, as `Tile.cs:22-33` defines them):

```csharp
// Before (grids.mdx:45-57)
public partial record struct HeatPlate([LookupKey] PlatePos Pos) : IComponent, ITile<PlatePos>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;

    static PlatePos ITile<PlatePos>.Beside(PlatePos p, TileFaces face) => face switch
    {
        TileFaces.North => p with { Z = p.Z - 1 },
        TileFaces.South => p with { Z = p.Z + 1 },
        TileFaces.West => p with { X = p.X - 1 },
        _ => p with { X = p.X + 1 },
    };
}

// After 1a
public partial record struct HeatPlate([LookupKey] PlatePos Pos) : IComponent, ITile<PlatePos>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
}

// The generator writes, in the part beside the explicit Key
static PlatePos ITile<PlatePos>.Beside(PlatePos key, TileFaces face) => face switch
{
    TileFaces.North => key with { Z = key.Z - 1 },
    ...
};
```

A periodic grid (`TileBenchmark.cs`'s `p.Wrap(face)`) and any other key keep their own `Beside`: D77 made "a `Beside`
that wraps" the periodic grid, and that stays the user's.

**1b. The shape as an attribute (a new public name).** D101's rule, "a type declares what it is with attributes; the
generator writes the members", applied to tiles: one attribute gives `ITile<TKey>` (the key type from the type's
`[LookupKey]` / `[UniqueKey]` member), the shape members and `Beside`:

```csharp
// After 1b: nine lines to one; the VoxelServer chunk keeps its IDirectMapped (finding 4)
[Tile(Width = 16, Depth = 16)]
public partial record struct HeatPlate([LookupKey] PlatePos Pos) : IComponent;

[Tile(Width = 16, Depth = 16, Height = 16, Faces = TileFaces.All, Boundary = TileBoundary.Mirror)]
public partial record struct GasCube([LookupKey] CubePos Pos) : IComponent;
```

Every shape member is a compile-time constant already (`Terrain.Size` included), so an attribute argument can hold it;
the JIT folds the generated static members as it folds the hand-written ones (no runtime difference). The interface
stays for a shape computed from something an attribute cannot hold, as D101 kept `IReduction<T>`.
Names: `[Tile(...)]` (recommended: the type is a tile, the guide's word), `[TileShape(...)]` (says only half of what
it writes), `[Grid(...)]` (a grid is many tiles: wrong thing).

**Generator.** `PartGenerator` (it writes the key's part today; `Parts.cs` reads the declaration once for the generator
and the analyzer). 1b adds one `ForAttributeWithMetadataName` provider for `EasyEcs.TileAttribute`.

**Analyzer and code fix.** A new Info, "`Beside` / the shape is what the generator writes" (the EECS065 / EECS067 shape:
hand-written members an attribute or the key declares), with a fix that deletes them and adds `[Tile(...)]`; an
Error when a type declares `ITile<TKey>`, writes no `Beside` and its key is none of the inferred shapes (today the
compiler's CS0535, which does not say what would be inferred); EECS068's check extended to `[Tile]` on a type
without a key member.

**IDE and build cost.** Declaration-only: the transform reads the type's base list, the key member's type and that
type's member names (no body, no `GetSymbolInfo` in a body), and extends a part file that exists already, so no new
file per type for 1a. 1b's FAWMN is attribute-indexed (no work for files without it). A caching assertion per stage in
`GeneratorTests`, `GeneratorBenchmark` Edit cases before and after (expected equal within noise).

**Verdict: build** (1a at once; 1b once the owner approves the name). 10 user-facing sites, 7-9 lines each, and the
same for every tile users will declare; no runtime cost; the docs' grids page loses its most repeated block.

### 2. Countdown timers (not worth it)

**Pattern.** A component counts down every tick and the entity or the component goes when it runs out:
`templates/.../Systems/LifetimeSystem.cs:11-18`, `samples/GameLoop/Systems/Simulation.cs:25-34`,
`demo/EasyEcs.Demo/Systems/LifetimeSystem.cs:9-17`, `demo/.../FadeSystem.cs:9-18`, `BattleServer.cs:288-292` (Expire),
`:176-181` (Burn), `:183-190` (Thaw), `:150-151` (cooldown), `structural-changes.mdx:49`, `:289`,
`project-structure.mdx:163`, `:485`, `getting-started.mdx:157`, `:355`, `testing.mdx:235`, `change-tracking.mdx:468`,
`cleanup-and-disable.mdx:123`, `:268`, `diagnostics.mdx:384`, `demo/.../BurstSystem.cs:25`; 21 sites.

**The generated form** would be a field attribute (`record struct Lifetime([Expires(Then = Expire.Destroy)] int Ticks)`)
and a generated system the user still registers (`world.AddSystem<LifetimeExpiry>()`), about six lines saved per timer.

**Why not.** Only about 10 of the 21 do nothing else when the time runs out (Burn deals damage, Thaw enables two
components, the cooldown resets and attacks, Burst fires); the unit differs (ticks, or seconds from a `Clock.Dt`
singleton); the registration line and its place in the order stay the user's (D56, D120: a generated system the
schedule places by itself is what D120 declined); and the hand form is already the fast one (a vector loop whose
structural write marks rows, D122). The faster design, an absolute deadline read every tick and written once
(`samples/VoxelServer/Systems/Zombies.cs:137`, `:213`: `clock.Tick < cooldown.Until`, no write per row per tick), is a
docs lesson, and a generated countdown would teach the other one. Goes to `owner-review.md` with this reason; it would
change if users' code showed many timers that only destroy or remove.

### 3. The VoxelServer's 34 `UpdateInGroup<Simulation>` (not a generator item)

`samples/VoxelServer`: 34 systems carry `UpdateInGroup<Simulation>` while no system is in the default group
(`Host/Server.cs:38`: "no system in the default group: it advances EntityRandom's tick"). The group exists only to be
`Update<Simulation>()`. Moving the simulation into the default group removes all 34 (the input group then runs before
`world.Update()`, which still advances `EntityRandom`'s tick first). A generated default group per folder or namespace
would be a second way to say a system's group; the rewrite (voxel-rewrite.md, step 2a) takes it instead.

### 4. Direct-mapped keys as a window: `[UniqueKey(SlotsPerAxis = 64)]` (build, with 1)

**Pattern.** `static int IDirectMapped<ChunkPos>.Slots => 64 * 64;` and
`static int IDirectMapped<ChunkPos>.SlotOf(ChunkPos pos) => (pos.Z & 63) * 64 + (pos.X & 63);` at
`samples/VoxelServer/Components/Chunks.cs:23-24` and `Scent.cs:60-61` (identical), `docs/content/guide/grids.mdx:465-466`,
`indexes-and-relations.mdx:277-278` (a tuple key), `reference/idirectmapped.mdx:20` (generated from the XML doc).

```csharp
// Before (Chunks.cs:21-24)
public partial record struct Chunk([UniqueKey] ChunkPos Pos) : IComponent, IDirectMapped<ChunkPos>, ITile<ChunkPos>
{
    static int IDirectMapped<ChunkPos>.Slots => 64 * 64;
    static int IDirectMapped<ChunkPos>.SlotOf(ChunkPos pos) => (pos.Z & 63) * 64 + (pos.X & 63);

// After (with finding 1)
[Tile(Width = Terrain.Size, Depth = Terrain.Size, Height = Terrain.Height)]
public partial record struct Chunk([UniqueKey(SlotsPerAxis = 64)] ChunkPos Pos) : IComponent;
```

The generator adds `IDirectMapped<TKey>` with `Slots` = n to the power of the key's axes and `SlotOf` = the row-major
index of each coordinate masked by n − 1 (n a power of two: `&` is exact for negative coordinates; anything else is an
error with its reason). Same key shapes as finding 1 (`int`, members `X` / `Z` / `Y`), plus the tuple's `Item1..3`.

**Names** (a property of the attribute that already makes the key unique, since a direct map needs a unique key):
`SlotsPerAxis` (recommended: the vocabulary of `Slots` / `SlotOf`, and it says the cost, n² or n³ slots),
`Window` (the loaded area's side, the doc's own picture, but hides the memory), a separate `[DirectMapped(64)]`
(a second attribute on the member). **Generator**: `PartGenerator`, as for `[UniqueKey]` today. **Analyzer**: Info for
a hand-written `Slots` / `SlotOf` that equals the window form, fix to the property; Error for a non-power-of-two value.
**Cost**: none beyond the part it writes today. **Verdict: build**: 2 lines per site, but the formula (mask, order of
axes, slot count matching the mask) is where hand-written ones go wrong, and it is the same at every site.

### 5. A reduction that keeps the row with the lowest key: `[MinBy]` / `[MaxBy]` (build)

**Pattern.** `BattleServer.cs:384-388`, written by hand because its fields "combine together", and so its method runs
scalar (`:403`, `SimdMode.Off`); `threading.mdx:382-383` names the same case ("the busiest cell and its count") as the
one the attributes cannot say; `BattleServer.cs:686-698` (`NearestPlayer`) is the same reduction as a host loop.
MPI has it as `MINLOC` / `MAXLOC`; .NET's LINQ as `MinBy` / `MaxBy`.

```csharp
// Before
public record struct Weakest(EntityId Player, int Health) : ISingletonComponent, IReduction<Weakest>
{
    public static Weakest Identity => new(EntityId.Null, int.MaxValue);
    public static Weakest Combine(Weakest left, Weakest right) => right.Health < left.Health ? right : left;
}

// After: the marked field decides, the others travel with it; a tie keeps the earlier row (the left side)
public partial record struct Weakest(EntityId Player, [MinBy] int Health) : ISingletonComponent;
```

**Generator.** `PartGenerator` writes `Identity` (the marked field at `MaxValue` / +∞, the others `default`) and
`Combine`; `Kernel` gets a vector form, since the comparison is now declared: per lane a compare and a select of every
carried field, the lanes folded left to right so ties keep the earlier row (the same result on any thread count).
**Names**: `[MinBy]` / `[MaxBy]` (recommended: .NET's word for "the element with the smallest key"),
`[ArgMin]` / `[ArgMax]` (the math name, reads as a function), `[Lowest]` / `[Highest]`. **Analyzer**: EECS064's "a
field no attribute combines" accepts the carried fields of a `[MinBy]` type (exactly one `[MinBy]` / `[MaxBy]`, else an
error); EECS065's fix learns the `right.K < left.K ? right : left` shape. **Cost**: a part per type, as for `[Min]`.
**Verdict: build**: few sites, but the identity and the tie rule are where it goes wrong, and it turns a scalar pass
of the battle server into a vector loop (to prove with `ReduceBenchmark`; if the vector form loses, the attribute still
ships with the scalar loop and the reason goes to EECS055).

### 6. A request finds its target by key (not worth it)

`samples/GameLoop/Systems/Requests.cs:16`, `:35`, `:56`, `:76`; `samples/Actors/Messages.cs:39`, `:49`. A parameter
such as `[By(nameof(MoveRequest.Client))] Entity player` would save the `if` and its braces. Not worth it: one of the
four acts when the key is missing (`JoinSystem`), so the parameter needs a skip-or-act choice; the target is written
or destroyed, which a lookup of another entity may not do in a pass (`Lookup<T>` is read-only, D75); and the bundle of
lookups as one parameter was declined (GA5).

### 7. `Scratch<T>` created and cleared by hand (not worth it; a rule instead)

`BattleServer.cs:228-229`, `threading.mdx:461`, `analyzer.mdx:1921`, `:1937`. Creating the value in the generated job
(`new T()`) would save `??= new List<T>()`, but takes away the capacity users pass (`new List<int>(32)`) and changes
`Scratch<T>`'s contract ("`default` at first"). What is worth having is the rule the docs say is missing
(`threading.mdx:1068`: "A `Scratch<T>` list used without clearing it ... Code review"): a Warning for a scratch
collection added to in a method that never clears it, with a fix inserting `Clear()` at the method's start. That is
`audit-analyzer`'s, not a generated member.

### 8. An `[EntityMethod]` entry point by id (not worth it)

`BattleServer.cs:595-596`, `hosting.mdx:271-272`: `world.TryGetEntity(id, out var unit) && Requests.TakeHit(unit, damage)`.
A generated `Requests.TakeHit(World, EntityId, int)` saves half a line, doubles every method's entry points, and
collides with a method whose first value is itself an `EntityId`.

### 9. `Emitted<T>`'s list behind a generated member (not worth it)

`BattleServer.cs:353`, `samples/VoxelServer/Systems/Replication.cs:58`, `samples/Actors/Host/Storage.cs:30`,
`threading.mdx:619`, `:810`, `common-mistakes.mdx:1059`, `:1076`, `analyzer.mdx:2360`. A shorter spelling saves no line
(D26: style alone is rejected), the walks that clear are `[Drain]` already, and D120 declined the reader parameter. One
docs finding for the docs pass: three pages read `GetSingleton<Emitted<T>>()`, which throws before the first pass
emitted anything; `GetOrAddSingleton` (as the samples write it) does not.

### 10. `Parallel = true` inferred (not worth it)

The generator knows when a body is safe for the runtime (EECS060 / EECS061), not whether the code it calls is (a static
field, a log, an order of side effects): the attribute is the user's statement that the body may run on several
threads. D22 already makes `Parallel = true` cost nothing on small tables.

### 11. Generated members nobody uses (none)

Counted outside the unit tests (samples, demo, templates, benchmarks, guide pages): `GetOrAddX` 51, `SetXIfChanged` 21,
`TryGetX` 15, `UnlinkX` 6, `LinksX` 6, prefab `RemoveX` 6, `IsXDisabled` 4, `DisableX` 43, `WriteX` and `RemoveX` in the
hundreds. Every generated kind has users; nothing to remove.

### Questions for the owner (Report 1)

1. **`[Tile(...)]`** (finding 1b): add it, and the name: `[Tile]` (recommended), `[TileShape]`, `[Grid]`.
2. **The direct-mapped window** (finding 4): `[UniqueKey(SlotsPerAxis = 64)]` (recommended), `[UniqueKey(Window = 64)]`,
   `[DirectMapped(64)]`.
3. **The argmin reduction** (finding 5): `[MinBy]` / `[MaxBy]` (recommended), `[ArgMin]` / `[ArgMax]`, `[Lowest]` /
   `[Highest]`.
4. **2D keys named `(X, Y)`** (finding 1a): infer `Y` as the depth axis of a one-layer tile, or infer only `X` / `Z`
   (/ `Y` for layers) and let the analyzer say why. Recommended: only `X` / `Z`, since `TileFaces` names z the depth and
   a guess the other way would step the wrong axis without an error.
5. **Timers** (finding 2): confirm "not worth it" (recorded in `owner-review.md` with the docs lesson of absolute
   deadlines), or ask for the narrow form (destroy / remove at zero, ticks only).

## Report 2: the directory layout (Finish 1b)

The base is `docs/plans/layout.md` and the coordinator's decisions recorded at its end (2026-10-26): the files of four
projects go into folders by concern, the same names in Core, the tests and the benchmarks; tests and benchmarks move
too; each project's map is updated in its own move commit; `tools/agents.sh --check` is made recursive and extended
to the tests and the benchmarks first; a Rider `.DotSettings` per moved project turns off the namespace-vs-folder hint;
namespaces stay. This refresh re-checks it at `29ea0dd`: 75 files were added since `2515638` (Core 10, Generator 19,
tests 17, benchmarks 29), none was removed, and every path reference was grepped again.

### 2.1 The top level (nothing moves)

```
EasyEcs/
├── src/                    the package: EasyEcs.Core, EasyEcs.Generator, EasyEcs.CodeFixes
├── tests/                  EasyEcs.UnitTest, EasyEcs.Aot, EasyEcs.Aot.Library
├── benchmarks/             EasyEcs.Benchmark (+ Locality/), EasyEcs.Benchmark.Plugin, EasyEcs.Comparison
├── samples/                runnable lessons, in learning order
├── templates/              the EasyEcs.Templates package (dotnet new)
├── demo/                   EasyEcs.Demo, the site's WebAssembly demo
├── docs/                   the site (Cloudflare root): content/, app/, src/, scripts/, public/; plans/ and
│                           owner-review.md (internal)
├── tools/                  ApiDocs/, DocCheck/, Inlining/, LoopBound/, agents.sh, bench.sh, jitdisasm.sh,
│                           loopbound.sh/.py, inlining.sh
├── skills/easyecs/         the public skill
├── .claude/skills/ (+ .agents/skills), .github/
└── AGENTS.md, CLAUDE.md, README.md, LICENSE, Directory.Build.props, EasyEcs.slnx, global.json
```

This is the layout .NET repositories use (`src/`, `tests/`, `benchmarks/`, `samples/`, `docs/`, `tools/`; dotnet/runtime
adds `eng/` for build scripts, which this repository keeps in `tools/` and `.github/scripts/`). `layout.md` section 5
judged each entry; the verdicts still hold at the tip:

| Path | Verdict | Why (re-checked at `29ea0dd`) |
|---|---|---|
| `samples/` | stays | `ci.yml:34-40,114` and `check-sample.sh:49` (the slug from the folder name), 152 `<include>`s in `docs/content/samples/*.mdx` (en + zh), 85 GitHub links to `samples/` in pages |
| `demo/` | stays | `docs/scripts/build-demo.sh:14`, `ci.yml:44`, 15 includes in `demo.mdx` and 15 in `demo.zh.mdx`; under `docs/` CI's filter (`ci.yml:6,9`: `!docs/**`) would stop building it |
| `templates/` | stays | its own package (`release.yml:98`, `templates/smoke.sh:13`) |
| `skills/easyecs/` | stays | the path `npx skills add` finds; `docs/public/_redirects` and `ai.mdx` point at its raw URL |
| `.claude/skills/`, `.agents/skills`, `.github/` | stay | where the agents and GitHub look |
| `docs/` | stays | Cloudflare Pages' root directory |
| `docs/plans/`, `docs/owner-review.md` | stay (owner's call, question 8) | see 2.6 |
| `tools/` | stays (question 9) | 10 entries, each tool its own folder or script; 107 references to the performance scripts and their projects (`bench.sh`, `jitdisasm.sh`, `loopbound.*`, `inlining.sh`, `Inlining/`, `LoopBound/`) in AGENTS.md, skills, `ci.yml:27,72,82,84` and the decisions pages would change for a `tools/perf/` grouping that no reader asked for |
| `src/EasyEcs.CodeFixes`, `tests/EasyEcs.Aot*`, `benchmarks/EasyEcs.Comparison`, `benchmarks/EasyEcs.Benchmark.Plugin` | stay flat | 7, 1, 2, 13 and 1 source files |
| `src/EasyEcs.Core/build/` | stays | `Directory.Build.props:10`, `EasyEcs.Core.csproj:43` (packed as `build/` and `buildTransitive/`) |
| `benchmarks/EasyEcs.Benchmark/Locality/` | stays | linked into the unit tests (`EasyEcs.UnitTest.csproj:42`, `Locality\*.cs`); no benchmark class may go into it |

### 2.2 Directories: old → new

Every new directory is inside a project that does not move (the projects' paths, the `.slnx`, every `ProjectReference`
and CI command stay valid). Old: the project's root, flat.

| Project | New folders (files) |
|---|---|
| `src/EasyEcs.Core` (89) | root 2 (`World.cs`, `WorldOptions.cs`), `Components/` 9, `Diagnostics/` 4, `Entities/` 5, `Grids/` 4, `Indexes/` 5, `Lookups/` 4, `OrderedTables/` 5, `Parallel/` 4, `Parameters/` 6, `Queries/` 4, `Relations/` 4, `Saves/` 8, `Storage/` 10, `Systems/` 9, `VectorLoops/` 6 |
| `src/EasyEcs.Generator` (66) | `Analyzer/` 22, `Arity/` 1, `Declarations/` 7, `Kernel/` 17, `Shared/` 4, `Systems/` 15 |
| `tests/EasyEcs.UnitTest` (87) | root 5, `Analyzer/` 14, `Components/` 6, `Diagnostics/` 1, `Entities/` 2, `Generator/` 4, `Grids/` 3, `Indexes/` 2, `Lookups/` 2, `OrderedTables/` 4, `Parallel/` 5, `Parameters/` 5, `Queries/` 4, `Relations/` 3, `Saves/` 5, `Storage/` 3, `Systems/` 8, `VectorLoops/` 11 |
| `benchmarks/EasyEcs.Benchmark` (96 at its root; `Locality/` 10 unchanged) | root 11, `Components/` 5, `Diagnostics/` 1, `Entities/` 3, `Generator/` 1, `Grids/` 9, `Indexes/` 3, `Lookups/` 5, `OrderedTables/` 6, `Parallel/` 5, `Parameters/` 9, `Queries/` 6, `Relations/` 2, `Saves/` 6, `Storage/` 7, `Systems/` 8, `VectorLoops/` 9 |

Why: a feature's files sit together under the name its guide page uses, the same name in the code, its tests and its
benchmarks (`Grids/` holds `Tile.cs`, `TileSteps.cs`, `Cell.cs`, `CellRows.cs`; the tile tests; the nine `Tile*Benchmark`s).
Today the four projects hold 338 files flat (Core 89, Generator 66, tests 87, benchmarks 96), and the only index is
each AGENTS.md map. The `World.*.cs` partials go to the concern they implement (`World.Indexes.cs` in `Indexes/`); the
entry (`World.cs`, `WorldOptions.cs`) stays at Core's root; files another project, a page or a link reaches by path stay
at their project's root (section 2.5).

The 75 files new since `layout.md` and where they go (the rest keep `layout.md`'s placement):

- **Core**: `Cell.cs`, `CellRows.cs`, `TileSteps.cs` → `Grids/` (the tile's cell form and steps); `Pairs.cs`,
  `PairHalves.cs` → `OrderedTables/` (pair methods walk an ordered table's clusters); `IndexPrefetch.cs` → `Indexes/`;
  `Prefetching.cs` → `Lookups/` (beside `CacheSizes.cs` and the chases' prefetches); `HugePages.cs` → `Storage/` (the
  tables' arrays); `StreamingStores.cs`, `Recorded.cs` → `VectorLoops/` (the stores of generated loops; the facts the
  generator records for vector loops in other assemblies).
- **Generator**: `Kernel.Blank/Cells/Loops/Neighbors/Streams/Switches.cs`, `Recorder.cs`, `Recorded.cs` → `Kernel/`
  (what other assemblies' vector loops read); `SystemGenerator.Cells/Elements/Pairs/TileLoads.cs`, `TileLoads.cs`,
  `PairForm.cs`, `BodyCopy.cs` → `Systems/` (the system generator and the models it shares with the analyzer, as
  `Fusion.cs` and `LambdaPass.cs`); `EasyEcsAnalyzer.Members/Pairs/Tiles.cs`, `PairRewrite.cs` → `Analyzer/` (EECS107's
  fix, read by the analyzer and its code fix, as `HandSimd.cs`).
- **Tests**: `AnalyzerTests.Pairs/Tiles.cs` → `Analyzer/`; `TileTests.Cells/Steps.cs` → `Grids/`; `NeighborTests.cs`,
  `SymmetricPairTests.cs`, `SymmetricPairParallelTests.cs` → `OrderedTables/`; `ElementLoopTests.cs`,
  `ForeignBodyTests.cs`, `InlineArrayTests.cs`, `StreamingStoreTests.cs`, `SwitchLoopTests.cs` → `VectorLoops/`;
  `CopyEntitiesTests.cs` → `Saves/` (beside `MoveTests.cs`); `DrainTests.cs` → `Parameters/` (beside `EmitTests.cs`);
  `HugePagesTests.cs` → `Storage/`; `MaxRowsTests.cs` → `Systems/` (beside `ConsumeTests.cs`); `VoxelServerTests.cs`
  stays at the root (a scenario test, like `BattleServerTests.cs`; `docs-todos.md:1074` names its path).
- **Benchmarks**: the eight `Tile*Benchmark`s → `Grids/`; `PairHalvesBenchmark`, `PairListsBenchmark` →
  `OrderedTables/`; `ElementLoop`, `FlagLanes`, `LateHoles`, `StreamingStore`, `StreamingStoreGate` → `VectorLoops/`;
  `FullCopy`, `GhostCopy`, `StreamingCopy` → `Saves/` (snapshot and migration copies); `HugePageTouch`, `PrefaultTouch`,
  `PageFault`, `NativeColumns` → `Storage/`; `Accumulate`, `Drain` → `Parameters/`; `MaxRows` → `Systems/`;
  `PrefetchForms` → `Lookups/`; `GeneratorCorpus.cs` stays at the root (linked into the unit tests,
  `EasyEcs.UnitTest.csproj:37`); `PageFaultDiagnoser.cs` and `RooflineBenchmark.cs` stay at the root with the harness
  (`BenchmarkConfig.cs`, `IsaColumn.cs`): they measure the machine, not a feature.

### 2.3 Every file

Generated from the working tree at `29ea0dd` (338 files, none missing); "(new)" marks a file `layout.md` did not
place. Line counts are today's.

#### `src/EasyEcs.Core` (89 files: root 2, Components/ 9, Diagnostics/ 4, Entities/ 5, Grids/ 4, Indexes/ 5, Lookups/ 4, OrderedTables/ 5, Parallel/ 4, Parameters/ 6, Queries/ 4, Relations/ 4, Saves/ 8, Storage/ 10, Systems/ 9, VectorLoops/ 6)

| Old | New | Lines |
|---|---|---:|
| `World.cs` | stays | 722 |
| `WorldOptions.cs` | stays | 64 |
| `Cleanup.cs` | `Components/Cleanup.cs` | 14 |
| `ComponentInfo.cs` | `Components/ComponentInfo.cs` | 507 |
| `Contracts.cs` | `Components/Contracts.cs` | 54 |
| `Declarations.cs` | `Components/Declarations.cs` | 202 |
| `Disabled.cs` | `Components/Disabled.cs` | 30 |
| `DynamicBuffer.cs` | `Components/DynamicBuffer.cs` | 710 |
| `TypeIndex.cs` | `Components/TypeIndex.cs` | 217 |
| `World.Defaults.cs` | `Components/World.Defaults.cs` | 111 |
| `World.Singletons.cs` | `Components/World.Singletons.cs` | 185 |
| `EntityDebugView.cs` | `Diagnostics/EntityDebugView.cs` | 17 |
| `ScheduleInfo.cs` | `Diagnostics/ScheduleInfo.cs` | 184 |
| `SystemStats.cs` | `Diagnostics/SystemStats.cs` | 61 |
| `World.Introspection.cs` | `Diagnostics/World.Introspection.cs` | 193 |
| `Entity.cs` | `Entities/Entity.cs` | 426 |
| `EntityId.cs` | `Entities/EntityId.cs` | 55 |
| `EntityMeta.cs` | `Entities/EntityMeta.cs` | 21 |
| `Prefab.cs` | `Entities/Prefab.cs` | 95 |
| `World.DestroyLog.cs` | `Entities/World.DestroyLog.cs` | 101 |
| `Cell.cs` | `Grids/Cell.cs` (new) | 167 |
| `CellRows.cs` | `Grids/CellRows.cs` (new) | 253 |
| `Tile.cs` | `Grids/Tile.cs` | 1912 |
| `TileSteps.cs` | `Grids/TileSteps.cs` (new) | 854 |
| `IndexPrefetch.cs` | `Indexes/IndexPrefetch.cs` (new) | 151 |
| `OrderedIndex.cs` | `Indexes/OrderedIndex.cs` | 27 |
| `UniqueIndex.cs` | `Indexes/UniqueIndex.cs` | 118 |
| `ValueIndex.cs` | `Indexes/ValueIndex.cs` | 984 |
| `World.Indexes.cs` | `Indexes/World.Indexes.cs` | 352 |
| `CacheSizes.cs` | `Lookups/CacheSizes.cs` | 186 |
| `Lookup.cs` | `Lookups/Lookup.cs` | 251 |
| `LookupLanes.cs` | `Lookups/LookupLanes.cs` | 238 |
| `Prefetching.cs` | `Lookups/Prefetching.cs` (new) | 45 |
| `PairHalves.cs` | `OrderedTables/PairHalves.cs` (new) | 248 |
| `Pairs.cs` | `OrderedTables/Pairs.cs` (new) | 1243 |
| `RowClusters.cs` | `OrderedTables/RowClusters.cs` | 269 |
| `RowOrder.cs` | `OrderedTables/RowOrder.cs` | 440 |
| `World.Order.cs` | `OrderedTables/World.Order.cs` | 184 |
| `SpinGate.cs` | `Parallel/SpinGate.cs` | 53 |
| `Workers.cs` | `Parallel/Workers.cs` | 303 |
| `World.For.cs` | `Parallel/World.For.cs` | 169 |
| `World.Parallel.cs` | `Parallel/World.Parallel.cs` | 634 |
| `Emit.cs` | `Parameters/Emit.cs` | 440 |
| `EntityRandom.cs` | `Parameters/EntityRandom.cs` | 112 |
| `Maybe.cs` | `Parameters/Maybe.cs` | 220 |
| `Mut.cs` | `Parameters/Mut.cs` | 182 |
| `Reduce.cs` | `Parameters/Reduce.cs` | 61 |
| `Scratch.cs` | `Parameters/Scratch.cs` | 60 |
| `EntitySpan.cs` | `Queries/EntitySpan.cs` | 87 |
| `FilterOverflow.cs` | `Queries/FilterOverflow.cs` | 116 |
| `Query.cs` | `Queries/Query.cs` | 1528 |
| `QuerySlice.cs` | `Queries/QuerySlice.cs` | 68 |
| `Entity.Relations.cs` | `Relations/Entity.Relations.cs` | 54 |
| `Hierarchy.cs` | `Relations/Hierarchy.cs` | 27 |
| `Relations.cs` | `Relations/Relations.cs` | 286 |
| `World.Hierarchy.cs` | `Relations/World.Hierarchy.cs` | 328 |
| `Collectible.cs` | `Saves/Collectible.cs` | 72 |
| `ComponentSet.cs` | `Saves/ComponentSet.cs` | 80 |
| `TypeRegistry.cs` | `Saves/TypeRegistry.cs` | 356 |
| `World.Migration.cs` | `Saves/World.Migration.cs` | 623 |
| `World.Serialization.cs` | `Saves/World.Serialization.cs` | 690 |
| `World.Snapshots.cs` | `Saves/World.Snapshots.cs` | 231 |
| `World.Types.cs` | `Saves/World.Types.cs` | 173 |
| `WorldSnapshot.cs` | `Saves/WorldSnapshot.cs` | 32 |
| `Archetype.cs` | `Storage/Archetype.cs` | 2139 |
| `BitMask.cs` | `Storage/BitMask.cs` | 295 |
| `HugePages.cs` | `Storage/HugePages.cs` (new) | 210 |
| `MarkedRows.cs` | `Storage/MarkedRows.cs` | 126 |
| `StructuralGate.cs` | `Storage/StructuralGate.cs` | 155 |
| `World.Archetypes.cs` | `Storage/World.Archetypes.cs` | 422 |
| `World.Bulk.cs` | `Storage/World.Bulk.cs` | 521 |
| `World.Components.cs` | `Storage/World.Components.cs` | 458 |
| `World.Groups.cs` | `Storage/World.Groups.cs` | 136 |
| `World.Trim.cs` | `Storage/World.Trim.cs` | 176 |
| `Annotations.cs` | `Systems/Annotations.cs` | 33 |
| `EcsSystem.cs` | `Systems/EcsSystem.cs` | 55 |
| `ForEachAttributes.cs` | `Systems/ForEachAttributes.cs` | 272 |
| `SystemAccess.cs` | `Systems/SystemAccess.cs` | 65 |
| `SystemCode.cs` | `Systems/SystemCode.cs` | 322 |
| `SystemGroups.cs` | `Systems/SystemGroups.cs` | 60 |
| `SystemHandle.cs` | `Systems/SystemHandle.cs` | 156 |
| `SystemOrder.cs` | `Systems/SystemOrder.cs` | 34 |
| `World.Systems.cs` | `Systems/World.Systems.cs` | 746 |
| `Interleaved.Wide.cs` | `VectorLoops/Interleaved.Wide.cs` | 121 |
| `Interleaved.cs` | `VectorLoops/Interleaved.cs` | 795 |
| `LaneMath.cs` | `VectorLoops/LaneMath.cs` | 321 |
| `Lanes.cs` | `VectorLoops/Lanes.cs` | 401 |
| `Recorded.cs` | `VectorLoops/Recorded.cs` (new) | 56 |
| `StreamingStores.cs` | `VectorLoops/StreamingStores.cs` (new) | 179 |

#### `src/EasyEcs.Generator` (66 files: Analyzer/ 22, Arity/ 1, Declarations/ 7, Kernel/ 17, Shared/ 4, Systems/ 15)

| Old | New | Lines |
|---|---|---:|
| `BodyFacts.cs` | `Analyzer/BodyFacts.cs` | 72 |
| `EasyEcsAnalyzer.Forms.cs` | `Analyzer/EasyEcsAnalyzer.Forms.cs` | 829 |
| `EasyEcsAnalyzer.Fusion.cs` | `Analyzer/EasyEcsAnalyzer.Fusion.cs` | 107 |
| `EasyEcsAnalyzer.Lambdas.cs` | `Analyzer/EasyEcsAnalyzer.Lambdas.cs` | 119 |
| `EasyEcsAnalyzer.Layout.cs` | `Analyzer/EasyEcsAnalyzer.Layout.cs` | 240 |
| `EasyEcsAnalyzer.Members.cs` | `Analyzer/EasyEcsAnalyzer.Members.cs` (new) | 351 |
| `EasyEcsAnalyzer.Pairs.cs` | `Analyzer/EasyEcsAnalyzer.Pairs.cs` (new) | 105 |
| `EasyEcsAnalyzer.Parallel.cs` | `Analyzer/EasyEcsAnalyzer.Parallel.cs` | 406 |
| `EasyEcsAnalyzer.Parameters.cs` | `Analyzer/EasyEcsAnalyzer.Parameters.cs` | 474 |
| `EasyEcsAnalyzer.Parts.cs` | `Analyzer/EasyEcsAnalyzer.Parts.cs` | 613 |
| `EasyEcsAnalyzer.Performance.cs` | `Analyzer/EasyEcsAnalyzer.Performance.cs` | 2041 |
| `EasyEcsAnalyzer.Records.cs` | `Analyzer/EasyEcsAnalyzer.Records.cs` | 92 |
| `EasyEcsAnalyzer.Systems.cs` | `Analyzer/EasyEcsAnalyzer.Systems.cs` | 278 |
| `EasyEcsAnalyzer.Tiles.cs` | `Analyzer/EasyEcsAnalyzer.Tiles.cs` (new) | 425 |
| `EasyEcsAnalyzer.Usage.cs` | `Analyzer/EasyEcsAnalyzer.Usage.cs` | 1041 |
| `EasyEcsAnalyzer.Views.cs` | `Analyzer/EasyEcsAnalyzer.Views.cs` | 308 |
| `EasyEcsAnalyzer.cs` | `Analyzer/EasyEcsAnalyzer.cs` | 923 |
| `HandSimd.cs` | `Analyzer/HandSimd.cs` | 1054 |
| `PairRewrite.cs` | `Analyzer/PairRewrite.cs` (new) | 484 |
| `Threads.cs` | `Analyzer/Threads.cs` | 265 |
| `UnusedSuppressor.cs` | `Analyzer/UnusedSuppressor.cs` | 37 |
| `Uses.cs` | `Analyzer/Uses.cs` | 229 |
| `ArityGenerator.cs` | `Arity/ArityGenerator.cs` | 1847 |
| `ComponentGenerator.cs` | `Declarations/ComponentGenerator.cs` | 981 |
| `PartGenerator.cs` | `Declarations/PartGenerator.cs` | 133 |
| `Parts.cs` | `Declarations/Parts.cs` | 533 |
| `PrefabGenerator.cs` | `Declarations/PrefabGenerator.cs` | 73 |
| `Prefabs.cs` | `Declarations/Prefabs.cs` | 78 |
| `ViewGenerator.cs` | `Declarations/ViewGenerator.cs` | 72 |
| `Views.cs` | `Declarations/Views.cs` | 137 |
| `Kernel.Blank.cs` | `Kernel/Kernel.Blank.cs` (new) | 394 |
| `Kernel.Cells.cs` | `Kernel/Kernel.Cells.cs` (new) | 195 |
| `Kernel.Inlining.cs` | `Kernel/Kernel.Inlining.cs` | 1449 |
| `Kernel.Lookups.cs` | `Kernel/Kernel.Lookups.cs` | 203 |
| `Kernel.Loops.cs` | `Kernel/Kernel.Loops.cs` (new) | 505 |
| `Kernel.Narrow.cs` | `Kernel/Kernel.Narrow.cs` | 426 |
| `Kernel.Neighbors.cs` | `Kernel/Kernel.Neighbors.cs` (new) | 92 |
| `Kernel.Pairs.cs` | `Kernel/Kernel.Pairs.cs` | 338 |
| `Kernel.Streams.cs` | `Kernel/Kernel.Streams.cs` (new) | 84 |
| `Kernel.Struct.cs` | `Kernel/Kernel.Struct.cs` | 371 |
| `Kernel.Structural.cs` | `Kernel/Kernel.Structural.cs` | 232 |
| `Kernel.Switches.cs` | `Kernel/Kernel.Switches.cs` (new) | 463 |
| `Kernel.Views.cs` | `Kernel/Kernel.Views.cs` | 90 |
| `Kernel.Wide.cs` | `Kernel/Kernel.Wide.cs` | 613 |
| `Kernel.cs` | `Kernel/Kernel.cs` | 2594 |
| `Recorded.cs` | `Kernel/Recorded.cs` (new) | 196 |
| `Recorder.cs` | `Kernel/Recorder.cs` (new) | 555 |
| `Code.cs` | `Shared/Code.cs` | 71 |
| `EquatableArray.cs` | `Shared/EquatableArray.cs` | 42 |
| `Polyfills.cs` | `Shared/Polyfills.cs` | 10 |
| `Symbols.cs` | `Shared/Symbols.cs` | 2120 |
| `BodyCopy.cs` | `Systems/BodyCopy.cs` (new) | 271 |
| `Chases.cs` | `Systems/Chases.cs` | 213 |
| `Fusion.cs` | `Systems/Fusion.cs` | 267 |
| `LambdaPass.cs` | `Systems/LambdaPass.cs` | 253 |
| `PairForm.cs` | `Systems/PairForm.cs` (new) | 525 |
| `SystemGenerator.Cells.cs` | `Systems/SystemGenerator.Cells.cs` (new) | 355 |
| `SystemGenerator.Elements.cs` | `Systems/SystemGenerator.Elements.cs` (new) | 313 |
| `SystemGenerator.Fusion.cs` | `Systems/SystemGenerator.Fusion.cs` | 557 |
| `SystemGenerator.Kernels.cs` | `Systems/SystemGenerator.Kernels.cs` | 123 |
| `SystemGenerator.Lambdas.cs` | `Systems/SystemGenerator.Lambdas.cs` | 206 |
| `SystemGenerator.Loops.cs` | `Systems/SystemGenerator.Loops.cs` | 432 |
| `SystemGenerator.Pairs.cs` | `Systems/SystemGenerator.Pairs.cs` (new) | 277 |
| `SystemGenerator.TileLoads.cs` | `Systems/SystemGenerator.TileLoads.cs` (new) | 141 |
| `SystemGenerator.cs` | `Systems/SystemGenerator.cs` | 1863 |
| `TileLoads.cs` | `Systems/TileLoads.cs` (new) | 168 |

#### `tests/EasyEcs.UnitTest` (87 files: root 5, Analyzer/ 14, Components/ 6, Diagnostics/ 1, Entities/ 2, Generator/ 4, Grids/ 3, Indexes/ 2, Lookups/ 2, OrderedTables/ 4, Parallel/ 5, Parameters/ 5, Queries/ 4, Relations/ 3, Saves/ 5, Storage/ 3, Systems/ 8, VectorLoops/ 11)

| Old | New | Lines |
|---|---|---:|
| `AllocationTests.cs` | stays | 560 |
| `BattleServerTests.cs` | stays | 580 |
| `Fixtures.cs` | stays | 167 |
| `LocalityTests.cs` | stays | 332 |
| `VoxelServerTests.cs` | stays (new) | 158 |
| `AnalyzerTests.Declarations.cs` | `Analyzer/AnalyzerTests.Declarations.cs` | 445 |
| `AnalyzerTests.Forms.cs` | `Analyzer/AnalyzerTests.Forms.cs` | 303 |
| `AnalyzerTests.Pairs.cs` | `Analyzer/AnalyzerTests.Pairs.cs` (new) | 204 |
| `AnalyzerTests.Parallel.cs` | `Analyzer/AnalyzerTests.Parallel.cs` | 104 |
| `AnalyzerTests.Parts.cs` | `Analyzer/AnalyzerTests.Parts.cs` | 47 |
| `AnalyzerTests.Performance.cs` | `Analyzer/AnalyzerTests.Performance.cs` | 321 |
| `AnalyzerTests.Systems.cs` | `Analyzer/AnalyzerTests.Systems.cs` | 66 |
| `AnalyzerTests.Tiles.cs` | `Analyzer/AnalyzerTests.Tiles.cs` (new) | 257 |
| `AnalyzerTests.Usage.cs` | `Analyzer/AnalyzerTests.Usage.cs` | 381 |
| `AnalyzerTests.cs` | `Analyzer/AnalyzerTests.cs` | 77 |
| `CodeFixTests.cs` | `Analyzer/CodeFixTests.cs` | 1012 |
| `FormTests.cs` | `Analyzer/FormTests.cs` | 221 |
| `HotColdTests.cs` | `Analyzer/HotColdTests.cs` | 98 |
| `UnusedTests.cs` | `Analyzer/UnusedTests.cs` | 76 |
| `CleanupTests.cs` | `Components/CleanupTests.cs` | 125 |
| `ComponentTests.cs` | `Components/ComponentTests.cs` | 302 |
| `DisableTests.cs` | `Components/DisableTests.cs` | 148 |
| `DynamicBufferTests.cs` | `Components/DynamicBufferTests.cs` | 911 |
| `PresenceTests.cs` | `Components/PresenceTests.cs` | 301 |
| `SingletonTests.cs` | `Components/SingletonTests.cs` | 71 |
| `IntrospectionTests.cs` | `Diagnostics/IntrospectionTests.cs` | 354 |
| `EntityTests.cs` | `Entities/EntityTests.cs` | 389 |
| `InstantiateTests.cs` | `Entities/InstantiateTests.cs` | 420 |
| `GeneratorTests.cs` | `Generator/GeneratorTests.cs` | 2729 |
| `PartTests.cs` | `Generator/PartTests.cs` | 353 |
| `PrefabDeclarationTests.cs` | `Generator/PrefabDeclarationTests.cs` | 179 |
| `ViewTests.cs` | `Generator/ViewTests.cs` | 534 |
| `TileTests.Cells.cs` | `Grids/TileTests.Cells.cs` (new) | 1284 |
| `TileTests.Steps.cs` | `Grids/TileTests.Steps.cs` (new) | 1377 |
| `TileTests.cs` | `Grids/TileTests.cs` | 1618 |
| `IndexTests.cs` | `Indexes/IndexTests.cs` | 986 |
| `OrderedIndexTests.cs` | `Indexes/OrderedIndexTests.cs` | 126 |
| `LookupTests.cs` | `Lookups/LookupTests.cs` | 328 |
| `PrefetchTests.cs` | `Lookups/PrefetchTests.cs` | 719 |
| `NeighborTests.cs` | `OrderedTables/NeighborTests.cs` (new) | 1145 |
| `OrderTests.cs` | `OrderedTables/OrderTests.cs` | 833 |
| `SymmetricPairParallelTests.cs` | `OrderedTables/SymmetricPairParallelTests.cs` (new) | 347 |
| `SymmetricPairTests.cs` | `OrderedTables/SymmetricPairTests.cs` (new) | 612 |
| `ConcurrencyTests.cs` | `Parallel/ConcurrencyTests.cs` | 184 |
| `ParallelForTests.cs` | `Parallel/ParallelForTests.cs` | 144 |
| `ParallelTests.cs` | `Parallel/ParallelTests.cs` | 720 |
| `RunnerTests.cs` | `Parallel/RunnerTests.cs` | 188 |
| `WorkersTests.cs` | `Parallel/WorkersTests.cs` | 201 |
| `DrainTests.cs` | `Parameters/DrainTests.cs` (new) | 275 |
| `EmitTests.cs` | `Parameters/EmitTests.cs` | 649 |
| `EntityRandomTests.cs` | `Parameters/EntityRandomTests.cs` | 189 |
| `ReduceTests.cs` | `Parameters/ReduceTests.cs` | 662 |
| `ScratchTests.cs` | `Parameters/ScratchTests.cs` | 201 |
| `ChangedTests.cs` | `Queries/ChangedTests.cs` | 1052 |
| `QueryTests.cs` | `Queries/QueryTests.cs` | 404 |
| `RemovedTests.cs` | `Queries/RemovedTests.cs` | 447 |
| `RunTests.cs` | `Queries/RunTests.cs` | 143 |
| `HierarchyTests.cs` | `Relations/HierarchyTests.cs` | 150 |
| `RelationTests.cs` | `Relations/RelationTests.cs` | 115 |
| `TargetPolicyTests.cs` | `Relations/TargetPolicyTests.cs` | 133 |
| `CopyEntitiesTests.cs` | `Saves/CopyEntitiesTests.cs` (new) | 403 |
| `MoveTests.cs` | `Saves/MoveTests.cs` | 364 |
| `SerializationTests.cs` | `Saves/SerializationTests.cs` | 638 |
| `SnapshotTests.cs` | `Saves/SnapshotTests.cs` | 343 |
| `UnloadTests.cs` | `Saves/UnloadTests.cs` | 167 |
| `BulkTests.cs` | `Storage/BulkTests.cs` | 107 |
| `GroupTests.cs` | `Storage/GroupTests.cs` | 129 |
| `HugePagesTests.cs` | `Storage/HugePagesTests.cs` (new) | 242 |
| `ConsumeTests.cs` | `Systems/ConsumeTests.cs` | 231 |
| `EntityMethodTests.cs` | `Systems/EntityMethodTests.cs` | 161 |
| `ForEachSystemTests.cs` | `Systems/ForEachSystemTests.cs` | 683 |
| `FusionTests.cs` | `Systems/FusionTests.cs` | 648 |
| `GenericTests.cs` | `Systems/GenericTests.cs` | 181 |
| `LambdaPassTests.cs` | `Systems/LambdaPassTests.cs` | 384 |
| `MaxRowsTests.cs` | `Systems/MaxRowsTests.cs` (new) | 507 |
| `SystemTests.cs` | `Systems/SystemTests.cs` | 477 |
| `ElementLoopTests.cs` | `VectorLoops/ElementLoopTests.cs` (new) | 535 |
| `ForeignBodyTests.cs` | `VectorLoops/ForeignBodyTests.cs` (new) | 123 |
| `HelperLoopTests.cs` | `VectorLoops/HelperLoopTests.cs` | 272 |
| `InlineArrayTests.cs` | `VectorLoops/InlineArrayTests.cs` (new) | 65 |
| `KernelLookupTests.cs` | `VectorLoops/KernelLookupTests.cs` | 287 |
| `KernelTests.cs` | `VectorLoops/KernelTests.cs` | 2458 |
| `LaneMathTests.cs` | `VectorLoops/LaneMathTests.cs` | 569 |
| `LanesTests.cs` | `VectorLoops/LanesTests.cs` | 127 |
| `StreamingStoreTests.cs` | `VectorLoops/StreamingStoreTests.cs` (new) | 358 |
| `StructuralLoopTests.cs` | `VectorLoops/StructuralLoopTests.cs` | 338 |
| `SwitchLoopTests.cs` | `VectorLoops/SwitchLoopTests.cs` (new) | 351 |

#### `benchmarks/EasyEcs.Benchmark` (96 files: root 11, Components/ 5, Diagnostics/ 1, Entities/ 3, Generator/ 1, Grids/ 9, Indexes/ 3, Lookups/ 5, OrderedTables/ 6, Parallel/ 5, Parameters/ 9, Queries/ 6, Relations/ 2, Saves/ 6, Storage/ 7, Systems/ 8, VectorLoops/ 9)

| Old | New | Lines |
|---|---|---:|
| `BattleServer.cs` | stays | 767 |
| `BattleServerBenchmark.cs` | stays | 35 |
| `BenchmarkConfig.cs` | stays | 35 |
| `GeneratorCorpus.cs` | stays (new) | 200 |
| `IsaColumn.cs` | stays | 29 |
| `MicroBenchmarks.cs` | stays | 356 |
| `PageFaultDiagnoser.cs` | stays (new) | 118 |
| `Program.cs` | stays | 12 |
| `RooflineBenchmark.cs` | stays (new) | 158 |
| `WorldSimulation.cs` | stays | 255 |
| `WorldSimulationBenchmark.cs` | stays | 96 |
| `BufferSharingBenchmark.cs` | `Components/BufferSharingBenchmark.cs` | 138 |
| `CleanupBenchmark.cs` | `Components/CleanupBenchmark.cs` | 85 |
| `DynamicBufferBenchmark.cs` | `Components/DynamicBufferBenchmark.cs` | 357 |
| `HotColdBenchmark.cs` | `Components/HotColdBenchmark.cs` | 72 |
| `SparseBenchmark.cs` | `Components/SparseBenchmark.cs` | 71 |
| `IntrospectionBenchmark.cs` | `Diagnostics/IntrospectionBenchmark.cs` | 37 |
| `DestroyLogBenchmark.cs` | `Entities/DestroyLogBenchmark.cs` | 90 |
| `PrefabBenchmark.cs` | `Entities/PrefabBenchmark.cs` | 87 |
| `SingleEntityBenchmark.cs` | `Entities/SingleEntityBenchmark.cs` | 236 |
| `GeneratorBenchmark.cs` | `Generator/GeneratorBenchmark.cs` | 93 |
| `TileBenchmark.cs` | `Grids/TileBenchmark.cs` | 854 |
| `TileExchangeBenchmark.cs` | `Grids/TileExchangeBenchmark.cs` (new) | 62 |
| `TileHaloBenchmark.cs` | `Grids/TileHaloBenchmark.cs` (new) | 172 |
| `TileInPlaceBenchmark.cs` | `Grids/TileInPlaceBenchmark.cs` (new) | 201 |
| `TileLayersBenchmark.cs` | `Grids/TileLayersBenchmark.cs` (new) | 104 |
| `TileRestartBenchmark.cs` | `Grids/TileRestartBenchmark.cs` (new) | 323 |
| `TileSettleBenchmark.cs` | `Grids/TileSettleBenchmark.cs` (new) | 195 |
| `TileTallyBenchmark.cs` | `Grids/TileTallyBenchmark.cs` (new) | 130 |
| `TileWokenBenchmark.cs` | `Grids/TileWokenBenchmark.cs` (new) | 96 |
| `BatchProbeBenchmark.cs` | `Indexes/BatchProbeBenchmark.cs` | 72 |
| `IndexBenchmark.cs` | `Indexes/IndexBenchmark.cs` | 228 |
| `OrderedIndexBenchmark.cs` | `Indexes/OrderedIndexBenchmark.cs` | 100 |
| `LookupBenchmark.cs` | `Lookups/LookupBenchmark.cs` | 104 |
| `LookupChaseBenchmark.cs` | `Lookups/LookupChaseBenchmark.cs` | 285 |
| `LookupGatherBenchmark.cs` | `Lookups/LookupGatherBenchmark.cs` | 226 |
| `LookupOrderBenchmark.cs` | `Lookups/LookupOrderBenchmark.cs` | 109 |
| `PrefetchFormsBenchmark.cs` | `Lookups/PrefetchFormsBenchmark.cs` (new) | 282 |
| `NearestPlayerBenchmark.cs` | `OrderedTables/NearestPlayerBenchmark.cs` | 63 |
| `NearestZombieBenchmark.cs` | `OrderedTables/NearestZombieBenchmark.cs` | 77 |
| `PairHalvesBenchmark.cs` | `OrderedTables/PairHalvesBenchmark.cs` (new) | 67 |
| `PairListsBenchmark.cs` | `OrderedTables/PairListsBenchmark.cs` (new) | 84 |
| `PairsBenchmark.cs` | `OrderedTables/PairsBenchmark.cs` | 221 |
| `RowOrderBenchmark.cs` | `OrderedTables/RowOrderBenchmark.cs` | 100 |
| `ChunkRowsBenchmark.cs` | `Parallel/ChunkRowsBenchmark.cs` | 101 |
| `ParallelForBenchmark.cs` | `Parallel/ParallelForBenchmark.cs` | 101 |
| `ParallelPassBenchmark.cs` | `Parallel/ParallelPassBenchmark.cs` | 114 |
| `ParallelRunnerBenchmark.cs` | `Parallel/ParallelRunnerBenchmark.cs` | 103 |
| `ParallelSystemsBenchmark.cs` | `Parallel/ParallelSystemsBenchmark.cs` | 79 |
| `AccumulateBenchmark.cs` | `Parameters/AccumulateBenchmark.cs` (new) | 105 |
| `DrainBenchmark.cs` | `Parameters/DrainBenchmark.cs` (new) | 81 |
| `EmitBenchmark.cs` | `Parameters/EmitBenchmark.cs` | 187 |
| `EntityRandomBenchmark.cs` | `Parameters/EntityRandomBenchmark.cs` | 123 |
| `HistogramBenchmark.cs` | `Parameters/HistogramBenchmark.cs` | 188 |
| `MaybeBenchmark.cs` | `Parameters/MaybeBenchmark.cs` | 56 |
| `ReduceBenchmark.cs` | `Parameters/ReduceBenchmark.cs` | 400 |
| `ScratchBenchmark.cs` | `Parameters/ScratchBenchmark.cs` | 145 |
| `ViewBenchmark.cs` | `Parameters/ViewBenchmark.cs` | 116 |
| `AccessBenchmark.cs` | `Queries/AccessBenchmark.cs` | 324 |
| `CallSiteBenchmark.cs` | `Queries/CallSiteBenchmark.cs` | 62 |
| `ChangeTrackingBenchmark.cs` | `Queries/ChangeTrackingBenchmark.cs` | 348 |
| `CountBenchmark.cs` | `Queries/CountBenchmark.cs` | 41 |
| `SlicesBenchmark.cs` | `Queries/SlicesBenchmark.cs` | 116 |
| `TimerWheelBenchmark.cs` | `Queries/TimerWheelBenchmark.cs` | 71 |
| `HierarchyBenchmark.cs` | `Relations/HierarchyBenchmark.cs` | 50 |
| `RelationBenchmark.cs` | `Relations/RelationBenchmark.cs` | 63 |
| `FullCopyBenchmark.cs` | `Saves/FullCopyBenchmark.cs` (new) | 62 |
| `GhostCopyBenchmark.cs` | `Saves/GhostCopyBenchmark.cs` (new) | 127 |
| `MigrationBenchmark.cs` | `Saves/MigrationBenchmark.cs` | 115 |
| `SnapshotBenchmark.cs` | `Saves/SnapshotBenchmark.cs` | 142 |
| `StreamingCopyBenchmark.cs` | `Saves/StreamingCopyBenchmark.cs` (new) | 66 |
| `UnloadBenchmark.cs` | `Saves/UnloadBenchmark.cs` | 42 |
| `BulkBenchmark.cs` | `Storage/BulkBenchmark.cs` | 59 |
| `GroupBenchmark.cs` | `Storage/GroupBenchmark.cs` | 107 |
| `HugePageTouchBenchmark.cs` | `Storage/HugePageTouchBenchmark.cs` (new) | 153 |
| `NativeColumnsBenchmark.cs` | `Storage/NativeColumnsBenchmark.cs` (new) | 88 |
| `PageFaultBenchmark.cs` | `Storage/PageFaultBenchmark.cs` (new) | 156 |
| `PrefaultTouchBenchmark.cs` | `Storage/PrefaultTouchBenchmark.cs` (new) | 74 |
| `TrimBenchmark.cs` | `Storage/TrimBenchmark.cs` | 99 |
| `ConsumeBenchmark.cs` | `Systems/ConsumeBenchmark.cs` | 85 |
| `EntityMethodBenchmark.cs` | `Systems/EntityMethodBenchmark.cs` | 98 |
| `FusionBenchmark.cs` | `Systems/FusionBenchmark.cs` | 224 |
| `LambdaPassBenchmark.cs` | `Systems/LambdaPassBenchmark.cs` | 91 |
| `MaxRowsBenchmark.cs` | `Systems/MaxRowsBenchmark.cs` (new) | 98 |
| `OrderingBenchmark.cs` | `Systems/OrderingBenchmark.cs` | 66 |
| `RunnerBenchmark.cs` | `Systems/RunnerBenchmark.cs` | 78 |
| `SystemDispatchBenchmark.cs` | `Systems/SystemDispatchBenchmark.cs` | 164 |
| `CompactBenchmark.cs` | `VectorLoops/CompactBenchmark.cs` | 133 |
| `CompressBenchmark.cs` | `VectorLoops/CompressBenchmark.cs` | 61 |
| `ElementLoopBenchmark.cs` | `VectorLoops/ElementLoopBenchmark.cs` (new) | 375 |
| `FlagLanesBenchmark.cs` | `VectorLoops/FlagLanesBenchmark.cs` (new) | 92 |
| `KernelBenchmark.cs` | `VectorLoops/KernelBenchmark.cs` | 1254 |
| `LateHolesBenchmark.cs` | `VectorLoops/LateHolesBenchmark.cs` (new) | 67 |
| `StreamingStoreBenchmark.cs` | `VectorLoops/StreamingStoreBenchmark.cs` (new) | 45 |
| `StreamingStoreGateBenchmark.cs` | `VectorLoops/StreamingStoreGateBenchmark.cs` (new) | 102 |
| `StructuralLoopBenchmark.cs` | `VectorLoops/StructuralLoopBenchmark.cs` | 107 |

### 2.4 Path references that must change (grepped at `29ea0dd`)

The move is `git mv` plus these edits; no C# line changes. Each was found by grepping the moved paths and the tools that
list a directory.

| File:line | Today | Change | Commit |
|---|---|---|---|
| `tools/agents.sh:57-63` | `glob.glob(f"{d}/*.cs")`: only the files directly in the project; after the move it finds 2 Core files and 0 Generator files and **passes while checking nothing** | take the `.cs` files from the `git ls-files` list it already builds (line 15) under `d + "/"`, skipping `bin/` and `obj/`; match the path relative to the project (`Storage/Archetype.cs`), not the basename; fail on a map line naming a file that does not exist; add `tests/EasyEcs.UnitTest` → `tests/AGENTS.md` and `benchmarks/EasyEcs.Benchmark` → `benchmarks/AGENTS.md` (coordinator's decision) | 1 (tools) |
| `docs/vite.config.ts:148-150` (`analyzerRules`) | `readdirSync(dir)` over `src/EasyEcs.Generator`, not recursive: after the move it finds no `.cs` and throws "No analyzer rules found" (`:156`), so `bun run build` fails on Cloudflare | `readdirSync(dir, { recursive: true })` (Node 22, `.node-version`) with `path.join(dir, file)`; the count must equal today's | 1 |
| `docs/src/lib/site.ts:58` | names `src/EasyEcs.Core.csproj` (wrong today: the file is `src/EasyEcs.Core/EasyEcs.Core.csproj`) | the real path | 1 |
| `.claude/skills/audit-naming/SKILL.md:30` | `grep -n "^\s*public " src/EasyEcs.Core/*.cs` (finds 2 files after the move) | `grep -rn --include='*.cs' --exclude-dir=obj --exclude-dir=bin "^\s*public " src/EasyEcs.Core` | 1 |
| `.claude/skills/audit-naming/SKILL.md:76` | `grep -n '...' src/EasyEcs.Generator src/EasyEcs.CodeFixes` (a directory without `-r`: broken today) | `grep -rn --include='*.cs'` | 1 |
| `src/EasyEcs.Core/AGENTS.md:10-272` | a flat map of file names | sections per folder, each line `Folder/File.cs`; lines that describe two files now in different folders split (`EcsSystem.cs` / `SystemHandle.cs` / `SystemCode.cs` stay together in `Systems/`; `World.Parallel.cs` / `World.For.cs` in `Parallel/`; `Hierarchy.cs` / `World.Hierarchy.cs` in `Relations/`; `World.Types.cs` / `Collectible.cs` in `Saves/`; `Interleaved.cs` / `Interleaved.Wide.cs` in `VectorLoops/`) | 2 |
| `src/EasyEcs.Generator/AGENTS.md:3` and its map (`:5-316`) | `> Read when: ... the vector loops (`Kernel*.cs`) or the analyzer`; a flat map | "the generators (`Arity/`, `Declarations/`, `Systems/`), the vector loops (`Kernel/`) or the analyzer (`Analyzer/`)"; sections per folder | 3 |
| `AGENTS.md:40` | the index line generated from the Generator map's Read-when | rerun `tools/agents.sh` | 3 |
| `AGENTS.md:49-52` (Layout) | "src/EasyEcs.Core/ net10.0 library, namespace EasyEcs: entities, tables, ..." | name the folders, one line per project (`layout.md` section 4's text, with `Grids/` and `OrderedTables/` holding the new files) | 3 |
| `AGENTS.md:65-68` (Layout) | "tests/ EasyEcs.UnitTest (NUnit, one file per feature) ..."; "benchmarks/ EasyEcs.Benchmark (one class per feature ...)" | add "in folders named as Core's, plus `Generator/` and `Analyzer/`" | 4, 5 |
| `tests/AGENTS.md:24-52` | a flat list in a code block | sections per folder, each `.cs` named (the check of commit 1 reads it) | 4 |
| `benchmarks/AGENTS.md:10-63` | prose list of classes | sections per folder, each `.cs` named | 5 |
| `.claude/skills/audit/SKILL.md:16-19` (scopes) | owners by file name (`Kernel*.cs`, `EasyEcsAnalyzer*.cs`, `Code.cs`, ...) | add the folders: `audit-core` `src/EasyEcs.Core/**`; `audit-generator` `Generator/{Arity,Declarations,Systems,Shared}/`; `audit-vector-loops` `Generator/Kernel/`, `Core/VectorLoops/`, `Core/Lookups/`; `audit-analyzer` `Generator/Analyzer/` | 6 (docs) |
| `.claude/skills/audit-analyzer/SKILL.md:10` | "Files in `src/EasyEcs.Generator`: `EasyEcsAnalyzer.cs` ..." | "Files in `src/EasyEcs.Generator/Analyzer`: ..." | 6 |
| `.claude/skills/audit-generator/SKILL.md:10` | "Files: `src/EasyEcs.Generator` ... `ArityGenerator`, `ComponentGenerator`, ..." | name the folders | 6 |
| `docs/plans/borrowed-ideas.md:133, 149, 204, 417` | `src/EasyEcs.Core/CacheSizes.cs`, `src/EasyEcs.Generator/Chases.cs`, `src/EasyEcs.Core/IndexPrefetch.cs`, `src/EasyEcs.Core/Emit.cs` | `Lookups/CacheSizes.cs`, `Systems/Chases.cs`, `Indexes/IndexPrefetch.cs`, `Parameters/Emit.cs` | 6 |
| `docs/plans/inner-loops.md:366, 419, 422, 656-660` | `HandSimd.cs`, `ElementLoopTests.cs`, `ElementLoopBenchmark.cs`, `Kernel.cs`, `SystemGenerator.cs`, `LambdaPass.cs`, `SystemGenerator.Lambdas.cs`, `Tile.cs`, `World.For.cs`, `EasyEcsAnalyzer.Forms.cs` with their old paths (`:493` names a `CellTests.cs` that was never written: drop it) | the new paths | 6 |
| `docs/plans/docs-todos.md:650` | `src/EasyEcs.Core/Pairs.cs` | `OrderedTables/Pairs.cs` | 6 |
| `docs/plans/generated-text.md:196` | `tests/EasyEcs.UnitTest/GeneratorTests.cs` | `Generator/GeneratorTests.cs` | 6 |
| `docs/plans/layout.md`, this file | the plan | deleted with the last move commit (its reasons go to a decision record, AGENTS.md "Plans that span sessions") | 6 |
| new: `src/EasyEcs.Core/EasyEcs.Core.csproj.DotSettings` and the same for Generator, UnitTest and Benchmark | none | the folders marked as not namespace providers (coordinator's decision): Rider's "namespace does not match the file location" hint off | with each move |

### 2.5 Checked: no change needed

| What | Where | Why it holds |
|---|---|---|
| csproj item globs | every project (SDK default `**/*.cs`) | recursive; no project lists a moved file |
| Linked and embedded sources | `EasyEcs.UnitTest.csproj:35,37,38,42` (`BattleServer.cs`, `GeneratorCorpus.cs`, the embedded `BattleServer.cs`, `Locality\*.cs`), `EasyEcs.Aot.csproj:23`, `EasyEcs.Comparison.csproj:26-27` (`WorldSimulation.cs`, `BenchmarkConfig.cs`), `EasyEcs.Benchmark.csproj:32` (embedded `BattleServer.cs`) | each linked file stays at its project's root (section 2.2) |
| `ProjectReference`s, `EasyEcs.slnx` | 28 project files, the solution's 24 entries | no project moves |
| CI and release | `ci.yml:21-124`, `release.yml:96-98` | they name projects and samples, not files inside the moved projects |
| `.github/scripts` | `check-sample.sh` (sample folders, `docs/content/samples/<slug>.mdx`), `release-notes.sh` (the changelog) | nothing inside the moved projects |
| `tools/` scripts and projects | `bench.sh` (project dirs and class names, `docs/src/data/bench.json`), `inlining.sh` + `Inlining/Program.cs` and `loopbound.sh` + `LoopBound/Program.cs` (benchmark types by name), `jitdisasm.sh` (method names), `DocCheck` (`docs/content`, `README.md`), `ApiDocs` (the built assembly and its XML; zh entries keyed by doc id) | class and type names do not change; none reads a source folder |
| Docs includes | 21 regions of `benchmarks/EasyEcs.Benchmark/BattleServer.cs` (`example.mdx:97-296`, `example.zh.mdx:88-261`), `samples/**`, `demo/EasyEcs.Demo/**` | none moves |
| GitHub links in pages | `example(.zh).mdx:14-16` (`BattleServer.cs`, `BattleServerTests.cs`), `migrate-from-{arch,friflo,defaultecs}(.zh).mdx` and `comparison(.zh).mdx` (`WorldSimulation.cs`, the Comparison files), `performance(.zh).mdx` (`BenchmarkConfig.cs`), `guide/prefabs(.zh).mdx:348/321` (`AllocationTests.cs`) | those files stay |
| Comments naming paths | `benchmarks/EasyEcs.Comparison/{ComparisonBenchmark,Shared,EasyEcsSimulation}.cs` (`WorldSimulation.cs`), `tests/EasyEcs.UnitTest/LocalityTests.cs:8` (`Locality/`) | those stay |
| The docs site's other scripts | `build-demo.sh:14` (the demo project), `sync-benchmarks.ts` (the Comparison csproj, BenchmarkDotNet reports), `lint-links.ts` (pages only) | outside the moved folders |
| Templates and the demo build | `EasyEcs.Templates.csproj:28` (`content\**`), `docs/scripts/build-demo.sh` | unrelated to the moved projects |
| Generated hint names | `AddSource` names come from symbols; `SystemGenerator.Lambdas.cs` hashes the *user's* file path (a moved test or benchmark gets a new `*.Lambdas.g.cs` name, asserted nowhere); fusion pairs systems of one file, and whole files move | tests matching `.Fusion.g.cs`, `__EasyEcsSystems.g.cs`, `EasyEcs.TypeRegistry.g.cs` still match |
| Files read at run time | no test or benchmark reads a source file (only `/proc` in `LookupChaseBenchmark.cs:236` and `PageFaultDiagnoser.cs:87`) | - |
| `InternalsVisibleTo` | `EasyEcs.Core.csproj:37` | by assembly name |
| Skills naming files without a path | `Kernel*.cs`, `EasyEcsAnalyzer*.cs`, `World.Parallel.cs`, `GeneratorCorpus.cs` (`audit-analyzer:166`, with its path, stays) | the names do not change |
| The package | `dotnet pack src/EasyEcs.Core` | the same files at the same package paths; only Source Link paths in the symbols change. Proof: the `.nupkg` file lists equal before and after, ApiDocs regenerates with no diff |

### 2.6 `docs/plans/` and `docs/owner-review.md` (owner's call)

They are internal and never published (`source.ts` reads `content/` only), but they live under Cloudflare's root, whose
build watch path is `docs/*` (docs/AGENTS.md, "Cloudflare Pages"): every plan commit rebuilds and redeploys the site
with nothing changed. CI does not run for them (`ci.yml:6,9`: `!docs/**`).

- **Stay, and exclude them in Cloudflare's watch paths** (recommended): one dashboard setting (exclude
  `docs/plans/*` and `docs/owner-review.md`), recorded in docs/AGENTS.md's table; no path changes.
- **Move to a root `plans/`** (`owner-review.md` inside it): 78 references in 33 files change (AGENTS.md, 3 skills,
  `benchmarks/AGENTS.md`, `tests/AGENTS.md`, comments in 12 benchmark files, `samples/VoxelServer/Systems/Zombies.cs`,
  `demo/.../MovementBatchSystem.cs`, `EasyEcs.UnitTest.csproj`, `decisions(.zh).mdx`, the plans themselves), and
  `ci.yml:6,9` must add `!plans/**`, or every plan commit runs the full CI.
- **Move to `.plans/`** (hidden): the same edits, and the files drop out of most editors' trees.

### 2.7 Order

As `layout.md` section 7, with the coordinator's decisions: every running branch merged first (backlog "Running":
`feat-inner-loops`, `feat-later-small`, and any worktree under `.claude/worktrees/`), then in one sitting:

| # | Commit | Gate |
|---|---|---|
| 1 | CI/tools: `agents.sh` recursive with relative paths and the tests' and benchmarks' maps; `vite.config.ts` recursive; `site.ts:58`; `audit-naming` commands | on today's flat layout: `tools/agents.sh --check` passes (after writing the tests' and benchmarks' maps as flat lists), `analyzerRules()` gives today's count |
| 2 | Core into folders + its map + its `.DotSettings` | `dotnet build -c Release` 0 warnings, `dotnet test`, `tools/agents.sh --check`, ApiDocs no diff, DocCheck unchanged |
| 3 | Generator into folders + map + root index + `.DotSettings` | the same, plus `bun run check` and `tools/inlining.sh` |
| 4 | Unit tests into folders + `tests/AGENTS.md` + `.DotSettings` | `dotnet test`, the same test count |
| 5 | Benchmarks into folders + `benchmarks/AGENTS.md` + `.DotSettings` | `--list flat` lists the same classes; `tools/bench.sh --site` resolves every class |
| 6 | Docs: skills, plans, root Layout; delete `layout.md` and this file's Report 2 | `tools/agents.sh --check` |

Then the full `verify` gate and the package comparison. Commits 2-5 are pure renames (`git diff -M --stat`: 100 %
similarity for every `.cs`).

### Questions for the owner (Report 2)

6. **Folder names** (`audit-naming`; `layout.md` question 1, still open): Generator `Kernel/` against
   `VectorLoops/` elsewhere (recommended: keep `Kernel/`, the files' own prefix and the skills' trigger, `Kernel*.cs`);
   `OrderedTables/` now also holds pair methods (`Pairs.cs`, `PairHalves.cs`, the pair tests and benchmarks;
   alternatives `Neighbors/`, or `Pairs/` apart; recommended `OrderedTables/`, since pairs walk an ordered table's
   clusters); `Lookups/` holds the prefetches (`Prefetching.cs`, `CacheSizes.cs`; alternative `Prefetch/`; recommended
   `Lookups/`, the guide's word, the prefetches serve its chases).
7. **Placements of the new files that could go either way**: Core `Recorded.cs` (`VectorLoops/` or `Systems/` beside
   `Annotations.cs`), Generator `Recorder.cs` / `Recorded.cs` (`Kernel/` or `Shared/`), `PairRewrite.cs` (`Analyzer/` or
   `Systems/`), `RooflineBenchmark.cs` (root or `VectorLoops/`), `GeneratorCorpus.cs` (root, or `Generator/` with
   `EasyEcs.UnitTest.csproj:37` and `audit-analyzer:166` edited). Recommended: as in 2.2.
8. **`docs/plans/` and `owner-review.md`** (2.6): stay with a Cloudflare exclude (recommended), `plans/`, or `.plans/`.
9. **`tools/`**: stays flat (recommended), or `tools/perf/` for the performance scripts and projects (107 references).
