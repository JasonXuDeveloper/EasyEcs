# Finish 1a: the generated-members scan (internal, not published)

Read-only report for the owner, made on `feat-finish-scan` at `29ea0dd` (the work branch). Nothing here is built.
Backlog: `docs/plans/backlog.md`, Finish, item 1a. (The directory layout, item 1b, shipped as D142 and its plan is gone.)

## Report 1: the generated-members scan (Finish 1a)

Scope: the public API (the generated members of `guide/basics/entities-and-components.mdx`, the attributes, the `[ForEach]`
parameters), `benchmarks/EasyEcs.Benchmark/BattleServer.cs`, every sample, the templates, the live demo, and the C#
fences of `docs/content/guide/**/*.mdx` (English; the Chinese pages repeat the same fences), `example.mdx`,
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

- `docs/content/guide/performance/grids.mdx:50-56`, `:279-287` (3D), `:358-364`, `:468-474`, `:547-553`
- `docs/content/guide/help/analyzer.mdx:2425`, `:2437` (a 1D `int` key: `face == TileFaces.West ? index - 1 : index + 1`)
- `samples/VoxelServer/Components/Chunks.cs:29-35`, `samples/VoxelServer/Components/Scent.cs:65-71` (identical)
- `benchmarks/EasyEcs.Benchmark/Grids/TileLayersBenchmark.cs:20` (the other tile benchmarks wrap: periodic grids)
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
`samples/VoxelServer/Components/Chunks.cs:23-24` and `Scent.cs:60-61` (identical), `docs/content/guide/performance/grids.mdx:465-466`,
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

## Owner's answers (2026-11-07)

Report 1:
- Tile declarations: build `[Tile]` (`[Tile(Width = 16, Depth = 16)]` writes `ITile<TKey>`, the shape and `Beside`),
  and infer `Beside` from an `int` key or integer `X` / `Z` (/ `Y` as height) members. A 2D key named `(X, Y)` is
  not inferred: an Error explains it and the user writes `Beside` (`TileFaces` makes z the depth; a guess the other
  way would step the wrong axis silently).
- Direct-mapped windows: build `[UniqueKey(SlotsPerAxis = N)]` generating `Slots` / `SlotOf`.
- The lowest-key reduction: build `[MinBy]` / `[MaxBy]` (MPI's MINLOC; ties keep the earlier row), prove the vector
  form of `FindWeakest` with `ReduceBenchmark`.
- Countdown timers: not built; the record goes to `owner-review.md` with the absolute-deadline (`Until`) lesson for
  the docs pass.
