# Plan: VoxelServer rewritten with every feature EasyEcs has and the least user code

Base: the tip `ba6da74` (refreshed 2026-10-27; the first plan was made on `2515638`, before L1 / L2, the call
inliner, the generator gaps, the prefetch gates and the distance field). Read-only: nothing here is built. The rewrite
is step 2a of Finish (backlog): after every feature is built, before docs and the benchmark sync, and after item 11
(+ 12a / 12b), so every tile system is written once in the ping-pong form (owner: "你得等你的 uv 交换先修好"). Pair
concepts follow feat-pairs-2's renames (block → cluster, reach → cutoff, window → cluster).

Evidence: a Release build of a copy (the generated `__loops` reasons), `--schedule`, one run at 50 and one at 2,000
players with `MeasureSystems` (single runs on a shared VM: a ranking, not numbers to quote).

## 0. Summary

- **Today**: 2,620 lines of `.cs`, 1,736 of them code (Benchmark/ excluded); 41 systems (the two generic scent
  systems counted per color) in 34 files. In Simulation, 33 stages hold 35 systems: only stage 4 (HeightMap,
  PlayerMove, ZombieTarget) and stage 6 (WaterFlow, ZombieChase) hold more than one; every other system runs alone
  (it takes the `World` or makes structural changes). One method is a vector loop: `PlayerMove.Walk`.
- **Hot systems at 2,000 players** (ms per tick, ranking only): WaterFlow 0.60, Float 0.55, ZombieChase 0.55,
  Physics 0.48, Pickup 0.39, SendEntities 0.27, ScentStep Black 0.26 / Red 0.26, ZombieTarget 0.25. At 50 players:
  Flow 0.19, Physics 0.17, Float 0.11, ScentStep 0.10 + 0.08. A tick is 4.9 ms of the 50 ms budget at 2,000 players;
  steady state 0 B.
- **The restructure**:
  1. Totals become one `[Accumulate, Sum] Stats`; Carried, Filled and TallySystem go.
  2. The outbox becomes a `[Drain]` method.
  3. The zombies' target becomes the nearest form of the cluster-pair method; `ChunkRing` goes.
  4. Pickup becomes a pair method that writes the neighbor (`[Neighbor] ref Item`).
  5. The bodies' tables take the space order those pairs infer, so the chunk-buffer reads of Physics, Float, Chase
     and Exposure come in row order (buffers about 17 MB at 2,000 players, past the 8 MB L2).
  6. Four bools get components of their own; five fire and death methods become vector loops with marked structural
     changes.
  7. The fire systems merge into one system.
  8. The AOI spawn and despawn passes drop the `World` for `Lookup`s.
  9. **After item 11**: water and scent written once in the ping-pong form (NextFluid, NextScent, the commit and
     settle systems, Red / Black and the generic systems go), then Cell methods (WP5 / WP5b).
  10. **After item 9**: the chunk lifecycle in the generated deferred load and save.
- **Expected**: about 380-450 lines fewer (about 15%), 41 → about 30 systems. About 120 lines are possible with what
  is merged today; the rest waits for items 9, 11, 8c, WP2, WP5 and WP5b.
- **Output**: identical for the reshapes, `[Accumulate]` / `[Drain]`, AOI, water and the fire merge. It
  changes for the pairs (ties by the lower `EntityId.Index`; every zombie looks every tick instead of in Slices), the
  inferred row order (spawn and pickup order), possibly the scent with `[GaussSeidel]`, and the chunk budget (item 9).
- **Prefetch never fires here**: at 2,000 players the world has about 8k entities (about 0.25 MB against the 8 MB
  L2). The memory that misses is the chunk buffers read at computed indexes, which no chase form sees: the lever is
  ordering rows by space, not prefetch.

## 1. Feature inventory

"Used" = used by the sample today. Gain: lines (L), speed (S), lesson (Le). "Blocked" names a running item, a backlog
item or a gap (section 4).

### 1a. Forms users write

| Feature | Used today (where) | Should use (where, why, gain) | Blocked by |
|---|---|---|---|
| `[ForEach]` per-row methods | everywhere | — | — |
| Vector loops from plain methods | `PlayerMove.Walk` (Movement.cs:14) | FireDamage (F1), ZombieDeath, Sunlight.Expose, Extinguish.Out, ChunkUnload, SendChunks, water Sleep / settle (S, Le) | bool splits, `[Accumulate]`; Sleep after item 11 |
| Enum lanes (G0) | automatic | Pickup's `Item.Kind` (becomes a pair anyway); FluidStep.Result | FluidStep has 6 fields (reshape after item 11) |
| Single-bool component lanes (G0) | none | `Body` → `OnGround` + `Blocked`; `Exposure` → `Sunlit` + `Wet`; `Input.Dig` → `Dig`; `Visible.Placed` → presence (S; Finish 2b) | none |
| Vector loops through the user's calls | automatic | `Rules.IsNight`, `Terrain.Cell` stay; the inherent reasons are lookups, `Mut` and the `World` | — |
| Narrow / wide / interleaved / paired-width lanes | Walk | SendChunks `long += int` widens | — |
| Marked-row structural changes in vector loops | none | Expose `SetBurning`, Out `RemoveBurning`, Die `Destroy`, Unload `SetUnloading`, Sleep `RemoveFlowing` (S) | bool splits, `[Accumulate]` |
| `Parallel = true`, `ChunkSize` | Walk, Physics, Float, Target, Chase, Exposure.Look, SendEntities, Flow, Commit, Footing, ScentStep | keep; pair passes gain `Parallel` (feat-pairs-2) except Pickup (writes the neighbor) | feat-pairs-2 |
| `Slices` | Unload (20), Pickup (2), Target (`LookEvery`) | Unload keeps them; Pickup and Target lose them as pair methods | GA8 / Q1 |
| `[UpdateInterval]`, groups | Spawn, fire, Flow, scent | keep (Le) | — |
| `Mut<T>` / `SetIfChanged` | Physics, Respawn, Flow, HeightMap, Footing, Commit, Settle, Step | keep in Physics and Respawn (the lesson); the tile step buffers go after item 11 | item 11 |
| `Maybe` / `ReadOnlyMaybe` | AoiView, Chase, SendEntities | keep | — |
| `Lookup<T>` | Physics, Float, Target, Chase, Exposure, Spawn, SendEntities, ScentStep | AoiSpawn, AoiDespawn, AoiChunk instead of the `World` (Le; S: shared stages) | none |
| `Reduce<T>` + `[Sum]` | Carried, Filled | folded into `Stats` | — |
| `[Accumulate]` | none | `[Accumulate, Sum] Stats` as `ref Reduce<Stats>` in every pass; Carried, Filled, Tally go (L ~-25; S: Die and SendChunks vectorize) | GA1, GA2 |
| `Emit<T>` | SendEntities | keep | — |
| `[Drain]` | none (Replication.cs:53-67 by hand) | `[Drain] static void Send(in EntityUpdate u, ref Stats s)` (L -8) | none |
| `EntityRandom` | Spawn, FakeClients | keep | — |
| `Scratch<T>` | none | none fits (small per-row stackallocs; tile scratch is the generator's after item 11) | — |
| `[Consume]` | none | the chunk requests through item 9's deferred form with a per-tick budget | item 9 |
| `[EntityMethod]`, lambda passes | none (lambda in Host only) | none fits | — |
| `[View]` | none | no fit: `Nearby` is a bundle of lookups | GA5 |
| `[Prefab]` declarations | none (fluent, Program.cs:190-209) | stays fluent (G3 declined) | — |
| `SetAbsent<T>` | none | Program.cs:195 `.Set(...).RemoveUnloading()` → `.SetAbsent<Unloading>()`; :206 → `.SetAbsent<Burning>()`; `Visible` absent until placed (L -2, Le) | none |
| `Reserve`, `World.ReserveBuffers` | `Reserve` (Program.cs:160-182); 9 `FillPool` lines | `ReserveBuffers(prefabs.Chunk, ...)` + `(prefabs.Player, ...)` (L -7); the `2 *` of field buffers stays until 8c | 8c |
| `MaxPooledLength` | Program.cs:65-69 | stays until GA3 | GA3 |
| Value / unique / ordered indexes, `IDirectMapped` | InChunk, ViewCenter, Footprint, Score / Dropped, Chunk / Ground / LoadRequest | keep (Le); `Ground` goes after 8c | 8c |
| Relations / hierarchy | none | none fits (an inventory is a buffer, cheaper; say why on the page) | — |
| GroupBy, `Disabled<T>` | none | none fits | — |
| Cleanup components | ViewCenter, Visible | keep; `Visible` loses `Placed` | — |
| Snapshots | Host/Steady.cs | keep (12d helps for free) | — |
| Save / Load, `SaveEntities` / `LoadEntities` | Program, ChunkRead / ChunkSave | keep (the lesson), through item 9's form | item 9 |
| `MoveEntities` | none | Q7 (item 7's ghost rows?) | item 7 |
| Tiles (`ITile`, `Tile<,>`, `Pad`, `Rows`, `ChangedEdges` / `Facing`) | Flow, Wake, Step, Settle, ScentWake | ping-pong, then `Cell<,>` (WP5 / 5b), `[GaussSeidel]` for the scent | 11, 12a, 12b, 8c, WP5 / 5b |
| `[GaussSeidel]` | none (red-black by hand: Red / Black, `ScentStepSystem<TColor>`, `ScentSettleSystem<TColor>`) | the scent (L ~-60, Le) | item 11 |
| Ping-pong (u / v, zero rows) | none (commit copies: Water/Commit.cs, Scent/Settle.cs) | water and scent (L ~-40; S) | 11, 12a, 12b |
| Element loops (WP2) | none (hand `Vector128`, HeightMap.cs:15-38) | HeightMap, Footing (L -8) | WP2 |
| `Cell<TCell,TTile>`, per-tile results | none (hand `Vector128`, Flow.cs:104-123, Step.cs) | Flow; the scent if as fast (L ~-60 / -80) | WP5, WP5b |
| Cluster pairs `[Neighbor]` (cutoff per row or one, two kinds, self masks, keep hint, nearest, `[Neighbor] ref`, parallel) | none | ZombieTarget (nearest), Pickup (writes j), optionally Attack (section 3) | feat-pairs-2, GA1, GA8 |
| `[OrderBy]` / ordered rows (L1) | none | inferred from the pair methods on `Position` (no `[OrderBy]`) (S) | pairs |
| Deferred load / save (G10) | none (Chunks.cs:19-84, 105-142 by hand) | item 9's form | item 9 |
| `DescribeSchedule`, `MeasureSystems` | Program.cs:15, Server.cs | keep | — |
| `FloatMode` | none | no float reduction or float loop beyond Walk | — |

### 1b. Automatic features: does the sample trigger them?

| Feature | Fires today? | Shape data for it? | Proof |
|---|---|---|---|
| Prefetch gates (row chases past L2; probes and vector chases past LLC; `DestroyEntities` ids) | **No**: gate `EntityCount × (16 + rowSize)` (LookupLanes.cs:89-90), ~8k entities ≈ 0.25 MB; twins exist (`Job0Chases` in Float, Chase) at one check per pass; `DestroyEntities` lists ≤ 256 ids | No: what misses is the chunk buffers (~17 MB) read through `Nearby`; the answer is rows ordered by space | MeasureSystems at 2,000 before / after the order; no 300k-entity case (Q9) |
| Ordered rows (L1) | No | Yes, inferred from the pairs: bodies in Morton order, re-sorted every 10 updates | TickBenchmark 50 / 2,000 A/B + Physics, Float, Chase times; kept anyway (the pairs need it) |
| Cluster-pair j prefetch | n/a | Position ~96 KB: never past a gate | — |
| Fusion | none fused | the merged Fire system: Hurt and Out share `With<Burning>` and may fuse as vector loops (check EECS093) | `--schedule`, TickBenchmark |
| Paired accumulators | no float Min / Max / Fast sums | none | — |
| `Lanes.Compact` / `Selection` / `Ballot` | via `CollectIds`, `Emit` | automatic for the new marked-row loops | — |
| `LookupLanes` gathers | none | none (every Lookup user also reads buffers through `Nearby`) | — |
| Tombstones | Pickup `Destroy`, ItemExpiry | keep | — |
| Change-tick skipping | `Changed<Position>`, `<Health>`, `<FluidStep>`, `ChangedAny<buffers>` | keep (a `ref` of a tracked component stamps every row, so Respawn keeps `Mut`) | — |
| Parallel stages | 2 shared of 33 | more systems without the `World` (AOI) and fewer structural changes; marked-row loops still run alone, `Reduce<Stats>` users conflict | GA2 |
| Copy-on-write buffers, incremental snapshots (12d), Emitted segments (12e) | automatic | nothing to write | — |

## 2. Per file and per system

L = lines saved (approximate). Output: same unless said.

### 2.1 Components

| File | Change | L |
|---|---|---|
| Bodies.cs:15 `Body(bool OnGround, bool Blocked)` | → `OnGround(bool On)`, `Blocked(bool On)` | +2 |
| Bodies.cs:27 `Visible(ChunkPos, bool Placed)` | → `Visible(ChunkPos)`, absent in every prefab; AoiSpawn sets it on first sight; AoiDespawn needs no `Placed` test | 0 |
| Mobs.cs:30 `Exposure(bool Sunlit, bool InWater)` | → `Sunlit(bool On)` + `Wet(bool On)` | +2 |
| Mobs.cs:16 `Target(EntityId Player)` | → `Target(EntityId Player, float Distance)`: the nearest form's hint (-1 = keep) | 0 |
| Players.cs:9 `Input(Vector2 Walk, bool Dig)` | → `Input(Vector2 Walk)` + `Dig(bool On)` | +2 |
| Chunks.cs:59 `Viewers(int Count, long EmptySince)` | `Count` → `long`: Unload becomes a vector loop with a marked `SetUnloading` | 0 |
| Singletons.cs:18-38 `Stats` | → `[Accumulate, Sum] partial record struct Stats(...)`; Carried (45-48), Filled (51-54), comment 40-42 deleted | -14 |
| Water.cs:21 `NextFluid` | deleted **after item 11** | -5 |
| Water.cs:40 `FluidStep` (6 fields) | **after 11 / 12b**: `FluidStep(StepResult Result, TileFaces Edges)` + count in `FluidCount`; filled blocks to `Reduce<Stats>` | -3 |
| Scent.cs:18 `NextScent`, 87-89 `Red` / `Black`, 58-72 `Ground` | **after 11 / 8c**: all go | -35 |
| Scent.cs:35-52 `Footing.CanStep` | stays (a precomputed mask) unless WP5 reads it per cell | 0 |

### 2.2 Chunks.cs (143)

| System | Lines | After | L |
|---|---|---|---|
| ChunkRead | 19-43 | **after item 9**: the plain `[ForEach]` request; the budget `Rules.MaxRequests` kept | ~-12 |
| ChunkGenerate | 47-84 | **after 9 + 11 / 8c**: `RemoveRed` / `RemoveBlack` (67-68) and the four `SetCount`s (75-78) go (GA10) | ~-18 |
| ChunkUnload | 90-98 | `Viewers.Count` long: vector loop, marked `SetUnloading` | 0 |
| ChunkSave | 105-142 | **after item 9**; the `keep` rule stays as data | ~-10 |
| comment 17-18 | | stale (only Load / Save are refused in a pass, EECS041): rewrite with item 9 | |

### 2.3 Movement.cs (98), Aoi.cs (144)

| System | Lines | After | L |
|---|---|---|---|
| PlayerMove.Walk | Movement.cs:11-20 | unchanged (vector loop) | 0 |
| Physics.Move | 31-82 | writes `OnGround` / `Blocked`; rows in space order; `Mut<Position>` stays (the lesson) | 0 |
| Grid.Track | 89-97 | drop `SimdMode = SimdMode.Off` (F7); check the reason stays Info | -1 |
| AoiView.Look / Left | Aoi.cs:27-74 | stats → `ref Reduce<Stats>`; the `World` stays (the lesson) | 0 |
| AoiChunk.Loaded | 77-89 | `Lookup<ViewCenter>` + `Lookup<InChunk>` (`Count`) + `Reduce<Stats>`: no longer alone | 0 |
| AoiSpawn.Moved | 94-109 | `Without<Visible>` `Appeared(...)` sets `Visible` on its own row; `Moved(in InChunk, ref Visible, ...)` handles crossings; `Aoi.ViewersOf` / `Watching` take lookups | +3 |
| AoiDespawn.Gone | 112-121 | lookups, no `Placed` test | -1 |
| `Aoi` helper | 124-143 | takes four lookups (a small ref struct like `Nearby`) | +2 |

**AOI as pairs: no.** Views are chunk squares (Chebyshev in chunks) counted in O(1) per chunk through the view-center
index; a pair method needs a Euclidean cutoff and would visit millions of pairs per tick in a crowd of 2,000 against
about 25 index counts per crossing. The page says why (count, do not search). GA6 declined.

### 2.4 Zombies.cs (216)

| System | Lines | After | L | Output |
|---|---|---|---|---|
| NightSpawn.Spawn | 14-39 | `Reduce<Stats>`; `World` stays (`Instantiate`) | 0 | changes with the player order (zombie ids) |
| ZombieTarget.Pick | 51-80 + Terrain.cs:191-205 | **nearest form, two methods**: `Keep` (Lookup<Position>, Lookup<Player>; target kept within Vision: `Distance = -1`, else +∞) and `Acquire` (`(in Position p, in Vision v, ref Target t, [Neighbor] in Position q, [Neighbor] in Player _, [Neighbor] Entity other)`, ties by the lower index); `ChunkRing` and the ViewCenter walk go | ~-24 | changes (ties, every tick: Q1) |
| ZombieChase.Chase | 92-125 | unchanged; rows in space order | 0 | same |
| ZombieAttack.Attack | 131-146 | A: keep (`World`), recommended. B: a pair writing `[Neighbor] ref Health` (cutoff 1.2 on X / Z), needs GA1; only if faster | 0 | same |
| Exposure.Look, Sunlight.Expose, FireDamage.Hurt, Extinguish.Out | 156-215 | **one `FireSystem`**, `[UpdateInterval(BurnEvery)]`: `Look` (parallel, writes `Sunlit` / `Wet`), `Expose`, `Hurt` (`[With<Burning>] (ref Health h) => h.Value--;`: F1, the pragma and the hand `Vector<int>` loop go), `Out`; Hurt / Out may fuse (EECS093) | ~-20 | same |
| ZombieDeath (Health.cs:6-16) | | `ref Reduce<Stats>`: `Destroy` on marked rows, a vector loop | 0 | same |

### 2.5 Items.cs (122), Health.cs (47)

| System | Lines | After | L | Output |
|---|---|---|---|---|
| Dig | Items.cs:11-34 | reads `in Dig dig`; `Reduce<Stats>`; `World` stays (the lesson) | 0 | same |
| Pickup | 43-69 | **pair, after feat-pairs-2**: players × items, `(in Position p, Mut<DynamicBuffer<ItemStack>> inventory, [Neighbor] in Position q, [Neighbor] ref Item item, [Neighbor] in Dropped dropped, in Clock clock)`; a vector loop destroys items at 0; the `ViewCenterIndex` walk goes. Needs `Mut<>` as a row parameter of a pair method (unverified) and GA1 | ~-8 | changes |
| Inventory | 72-103 | unchanged (WP2 may vectorize HasRoom) | 0 | same |
| ItemExpiry | 109-121 | OnUpdate writes the world's `Stats` value | 0 | same |
| Respawn | Health.cs:22-47 | `Reduce<Stats>`; `Mut` stays | 0 | same |
| Item merging | — | not built: a pair writing the neighbor's `Item` while reading its own is refused | — | GA4 / Q3 |

### 2.6 Replication.cs (97)

| System | Lines | After | L |
|---|---|---|---|
| SendChunks | 12-18 | `ref Reduce<Stats>`: a vector loop (widening sum) | 0 |
| ChunkSent | 22-26 | unchanged | 0 |
| SendEntities | 37-49 | unchanged (`Maybe` in parallel: inherent) | 0 |
| SendPackets | 53-67 | `[Drain] static void Send(in EntityUpdate u, ref Stats stats)` | -8 |
| Tally | 73-82 | deleted (`[Accumulate]`) | -12 |
| Clock | 89-96 | unchanged | 0 |

### 2.7 Water/ (283 lines; all **after item 11 + 12a + 12b**, the Cell form after WP5 / WP5b)

| System | Lines | After | L |
|---|---|---|---|
| HeightMap | HeightMap.cs:15-38 | **after WP2**: plain element loops | -8 |
| Wake | Wake.cs:16-38 | `EnsureCapacity` (36) goes; Dug and Spread stay or go with GA12 | -3 (-15 with GA12) |
| Flow | Flow.cs:34-124 | ping-pong (the generator swaps and zeroes skipped rows, item 11; edges from the step, 12b); then WP5b: `Shade` / `Gives` as one Cell method with `out Fluid`, FluidCount, filled and edges as per-tile results; filled → `Reduce<Stats>` | -60 |
| Sleep + Commit | Sleep.cs, Commit.cs | **merge** into `WaterSettleSystem` (no copy, no TrimExcess); a vector loop if FluidStep ≤ 4 fields | -25 |
| Float | Float.cs:13-39 | `Reduce<Stats>`; rows in space order | 0 |

Output identical (proof: check-sample and the water tests).

### 2.8 Scent/ (291 lines; **after item 11 (`[GaussSeidel]`) + 8c**, the Cell form after WP5)

| System | Lines | After | L |
|---|---|---|---|
| Step<TColor> + Settle<TColor> | Step.cs:30-167, Settle.cs:12-28 | one `[ForEach, GaussSeidel]` step: no colors, no generic system, no NextScent, no settle copy, settled tiles sleep; seeds become a cell buffer `Seed` written by Footprint; Trail a second out (WP5b); the hand doubling scan (Step.cs:111-120) only if the Cell form is slower (Q6) | -60 to -110 |
| Footing | Footing.cs:10-34 | **after WP2**: plain loop | -5 |
| Hunt | Hunt.cs:10-22 | `With<Ground>` → `With<Chunk>` (8c) | 0 |
| ScentWake, Footprint | Wake.cs:8-40 | Footprint writes `Seed` cells (changed buffer = wake); the Facing wake goes with GA12 | -10 |
| AddSystems | Program.cs:115-118 | four lines → one | -3 |

Output may change (multicolor order while the field settles).

### 2.9 Terrain.cs (222), Program.cs (211), Rules.cs, Host

- **Terrain.Generate** (33-55), hand `Vector128`: scalar if the generator vectorizes it (not a `[ForEach]` today);
  after WP5 a Cell method with `out Block`; otherwise kept with a comment (cold path). Q5.
- **Nearby** (91-168) stays (GA5 would fold its constructor). **ChunkStrip** (173-187) and **ChunkSquare** (208-222)
  stay for the AOI; **ChunkRing** (191-205) goes with the pair target.
- **Program.cs**: `ReserveBuffers` replaces 170-181 (-7); `SetAbsent` at 195, 206 (-2); `MaxPooledLength` (65-69)
  until GA3; the systems list loses about 10 lines.
- **Rules.cs**: `LookEvery` stays if `[UpdateInterval]` replaces the Slices.
- **Host/**: FakeClients reads `OnGround` / `Blocked` (Play may become a vector loop; the static `Directions[...]`
  read may keep it scalar). Steady, Server, RegionFiles, Support unchanged.

## 3. Cluster pairs: every place entities meet entities

| Meeting | Pair? | Rows × neighbors | Order key | Cutoff | Output | L |
|---|---|---|---|---|---|---|
| Zombie target | **yes**, nearest with the keep hint | zombies × players | inferred Morton of `Position` | per row `vision.Range²` | changes | -24 |
| Chase | no (the scent gives the direction) | — | — | — | — | 0 |
| Pickup | **yes**, writes j | players × items | same | `PickupRange²` | changes | -8 |
| Attack | optional (B) | zombies × players, writes `Health` | same | 1.2² on X / Z | same | 0 |
| Item merging | **yes** (GA4, owner Q3): symmetric, both sides written | items × items | same | merge range | changes | + |
| AOI | no (counted squares) | — | — | — | — | — |

Proof: TickBenchmark 50 / 2,000 A/B; Target (0.25 ms), Pickup (0.39 ms), Attack (0.14 ms) per system; parallel pairs
after feat-pairs-2. Order effects: player, zombie and item tables re-sorted every 10 updates; Physics, Float, Chase
and Exposure read chunk buffers in row order (the expected gain at 2,000 players); NightSpawn, Dig and Pickup order
changes, so the output changes deterministically; the j-cluster prefetch never fires.

## 4. Gaps: lines users would still write twice

| # | Gap | Saves / unlocks | Recommendation |
|---|---|---|---|
| GA1 | `Reduce<T>` / `[Accumulate]` in pair methods (PairForm refuses `ref` context parameters) | Pickup and Attack count into Stats without a second pass or EECS086 | build (small, after feat-pairs-2) |
| GA2 | Systems that only accumulate into one `[Accumulate]` singleton do not conflict (combined in registration order at the stage end) | Stats users share stages | build if `--schedule` shows a stage lost after step 2; else record |
| GA3 | `MaxPooledLength` inferred for a tile's cell buffers from `ITileShape` (or `ReserveBuffers(prefab, count, length)`) | Program.cs:65-69 | build with item 11 / 12a |
| GA4 | A symmetric pair (each unordered pair once, both sides written) | item merging, collisions | **build** (owner, Q3) |
| GA5 | A bundle of `Lookup<T>` as one parameter | `Nearby` built 5 times (-5) | declined (owner, Q8) |
| GA6 | Chebyshev / box cutoffs for pairs | AOI as pairs | declined (owner, Q8) |
| GA7 | EECS055 reason text: Chase prints `local 'cell' is a ??` (type name missing); Float says `LookupExtensions.TryFind, defined in another assembly` where a key probe has no vector form | correct reasons | **fix (a bug)** |
| GA8 | `Slices` on pair methods (rows sliced, neighbors whole) | the target's look spread over ticks | not built (owner, Q1: every tick) |
| GA9 | item 9's form carries a per-tick budget (`Rules.MaxRequests`) | the chunk cap kept | a requirement of item 9 |
| GA10 | A tile's cell buffers sized at instantiation from its shape (owner thread) | ChunkGenerate:75-78, Water Wake:36 (-6) | build with item 11 |
| GA12 | Tile sleep and wake owned by the tile pass (from the step's edges and `Changed<cell buffer>`), not `foreach (id in tile.Facing(e)) world.GetEntity(id).SetX()` | written 4 times (WaterWake.Dug / Spread, ScentWake, ScentSettle; Footprint.Wake): ~-20, and those systems stop running alone | build with item 11 (extend `[GaussSeidel]`'s sleep to Jacobi) |

## 5. What stays because it is the lesson

AOI (squares, strips, counts, ViewCenter as a cleanup component); the chunk lifecycle as data (requests, Fresh,
Unloading), also in item 9's form; `Mut` in Physics and Respawn; Dig and NightSpawn with the `World`; the ordered
indexes (expiry, leaderboard); save on stop and the region files; groups and `UpdateInterval`; `Emit` in
SendEntities; the two-step fire (the costly look in parallel, the presence flip after); `Footing.CanStep` as a
precomputed mask; Terrain's hand SIMD only per Q5.

## 6. Deterministic output

- Identical: bool splits, `SetAbsent`, FireDamage per row, the Fire merge, `[Accumulate]`, `[Drain]`,
  `ReserveBuffers`, AOI lookups, Viewers long, water ping-pong and Cell, HeightMap / Footing loops, Terrain scalar.
- Changes: the pair target (ties, cadence), the inferred order (spawn and item ids, pickup order), the pair pickup,
  possibly the `[GaussSeidel]` scent, the item 9 budget if it differs.
- When it changes, update together: the README output block, `docs/content/samples/voxel-server.mdx` + `.zh.mdx`
  (`## Output`, check-sample.sh), the walkthroughs of target, pickup, water and scent. Keep the `#region` names;
  delete `tally` and its include; add `fire` if burn and sunlight merge.

## 7. Rewrite steps

Each its own commit. Proof every step: TickBenchmark A/B at 50 and 2,000 (2 rounds, 0 B), `check-sample.sh`,
Host/Steady.cs 0 B, VoxelServerTests, `--schedule` diff, no EECS055-bool / EECS086 / EECS109 left (Finish 2b).

1. **Reshape, output identical**: bool splits (Body, Exposure, Input, Visible), Viewers long, `SetAbsent`,
   FireDamage per row + the Fire merge, F7, the Chunks.cs:17 comment.
2. **Totals, output identical**: `[Accumulate, Sum] Stats` (Carried, Filled, Tally deleted), SendPackets →
   `[Drain]`, `ReserveBuffers`; check stages (GA2).
3. **AOI without the `World`**, output identical.
4. **Pairs** (after feat-pairs-2, GA1): Keep + Acquire (`ChunkRing` deleted), Pickup with `[Neighbor] ref Item`, the
   order inferred; output blocks updated once at the end; Target, Pickup, Physics, Float, Chase measured per system.
5. **Water, after item 11 + 12a + 12b**: ping-pong, Commit and NextFluid deleted, Sleep + Commit merged (GA10,
   GA12); the Cell form after WP5b. Output identical.
6. **Scent, after item 11 + 8c**: one `[GaussSeidel]` step, Seed cells; Ground / Red / Black / NextScent / Settle
   deleted; Cell form after WP5 if not slower (Q6).
7. **Chunk lifecycle, after item 9**: Read, Generate, Save in the deferred form with the budget (GA9).
8. **Element loops after WP2**: HeightMap, Footing; Terrain per Q5.
9. Line count and `--schedule` before and after, recorded here; then the docs pass rewrites the page.

## 8. The first plan's items: status

| Old item | Status |
|---|---|
| F1, F2, F7, the comment | open (step 1 above) |
| F3 `[Consume]` | superseded by item 9 (G10, with a budget) |
| F5 Chase gather | obsolete (Chase reads the Trail through `Nearby`; the prefetch twin never fires at this scale) |
| F4 Terrain | open, Q5 |
| G0 | built; applied with the bool splits |
| G5, G6, G4 | built (`[Accumulate]`, `[Drain]`, `ReserveBuffers`); applied in step 2 |
| G2, G1 | open: WP2 / WP5 / WP5b in Later |
| G8, G9 | G8 built (nearest with hint, cutoff per row, two kinds); G9 (writes to j), parallel pairs and the cluster renames on feat-pairs-2 |
| "G10 declined" | contradicted: approved as backlog item 9 |
| G3, G11 declined, G7 not built | still true |

## 9. Owner answers (2026-10-27)

- Q1: every zombie looks every tick (no Slices on the pair target, no GA8).
- Q2: the output changes are accepted; the README and page output blocks are rewritten once at the end.
- Q3: item merging is added, as the symmetric pair that writes both sides: GA4 is built (backlog, with item 1).
- Q4: `Target` stays (the nearest pair with the keep hint is the lesson).
- Q5: Terrain generation becomes a Cell method with `out Block` after WP5 (the generator writes the SIMD); it
  replaces the hand `Vector128` only if not slower.
- Q6: if the plain `[GaussSeidel]` Cell form is slower than the hand doubling scan, that is a generator gap: the
  generator learns to write code as fast, then the scent is rewritten (no hand scan kept, no slower form accepted).
- Q7: ghost rows (item 7) get both examples: VoxelServer (two region worlds, `MoveEntities` at the border) and
  MultiWorld.
- Q8: GA5 (a bundle of lookups) and GA6 (box cutoffs) declined: decision records in the docs pass.
- Q9: no larger benchmark parameter for prefetch; the sample stays at its scale (ordered rows are its lever).

## 10. The questions as asked

1. Slices on the pair target: every zombie every tick, `[UpdateInterval(LookEvery)]`, or build GA8?
2. Accept the output changes from pairs and ordered rows (and possibly the scent), blocks rewritten once at the end?
3. Item merging (Minecraft merges dropped stacks) to show a pair that writes the neighbor (GA4), or not?
4. Keep `Target` at all? Zombies could attack any player in reach while the scent leads them (~-30 lines; changes
   the game).
5. Terrain generation: hand `Vector128` with a comment, scalar, or a Cell method with `out Block` after WP5?
6. The scent's doubling scan: if the plain `[GaussSeidel]` Cell form is slower, keep the hand scan with a comment,
   or accept the slower form?
7. VoxelServer as item 7's ghost-rows example (two region worlds, `MoveEntities` at the border), or another sample?
8. GA5 (a bundle of lookups) and GA6 (box cutoffs): decline and record in owner-review.md?
9. Prefetch never fires at this scale (~8k entities): leave the sample as is, or add a much larger benchmark
   parameter?
