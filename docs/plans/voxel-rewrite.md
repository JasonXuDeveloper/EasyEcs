# Plan: VoxelServer with every high-performance feature and the least user code

Scope: `samples/VoxelServer` (base: master `2515638`). Request: rewrite the sample to use every performance feature
the library has, with as little user code as possible, while it stays an exemplary lesson (ecs-principles.mdx).
This plan is read-only. Nothing here has been built or measured; each step below names the proof it needs.

## 0. Summary

- **Size today**: 2,164 lines of `.cs` (excluding `Benchmark/`), 1,444 of them code. 35 systems, plus FakeClients.
- **The sample already uses most features.** These are in place: `[ForEach]` row methods, parallel runs, `Lookup`,
  `Mut`, `Maybe`, `Emit`, `Reduce`, `Scratch`, `EntityRandom`, `[Prefab]`, `[Consume]`, ordered indexes, tiles, groups
  with `UpdateInterval`, snapshots and region saves. Only one method compiles to a vector loop today
  (`PlayerMove.Walk`); the others stay scalar for the reasons in section 2.
- **Cuts possible now** (F1-F7): about 35 lines. One of them, F3, may change the output and needs checking.
- **Cuts that need generator work** (G0-G11): about 120 lines more. The largest are:
  - G1: per-tile results from `Cell`, which would simplify Water Flow.
  - G5: reductions that add to a total instead of replacing it.
  - G8: the L2 nearest form for ZombieTarget.
- **Total**: about 150-160 lines saved, about 7%. The sample is already close to the least code it can have. Most of
  what remains is the lesson itself (section 4).
- **Output**: G8 (L2) changes the deterministic output, and F3 may. Everything else must leave the output identical.

## 1. Per file and per system

Notation: "Lines" means the system's line range today. "Unused" means features it could use but does not.
Exemplary means it still follows ecs-principles after the change.

### 1.1 Chunks.cs (132 lines)

| System | Lines | What it does | Unused | Before → after | Saved | Exemplary |
|---|---|---|---|---|---|---|
| ChunkRead | 18-42 | gathers the ids of chunks to read from region files (`CollectIds`), then loads them outside the pass | none usable: `LoadEntities` is refused in a pass (EECS041) | stays | 0 | yes, it is the lesson "load outside the pass" |
| ChunkGenerate | 46-73 | instantiates the chunk prefab per `LoadRequest` without a save, capped at 256 per tick | `[Consume]` allows `Instantiate` in the pass (F3) | hand loop → `[ForEach, Without<Saved>, Consume] static void Generate(in LoadRequest request, in Prefabs prefabs, ref Stats stats, World world)` | ~10 | yes |
| ChunkUnload | 79-87 | tags chunks no player sees | vector loop: blocked by "lane types int and long differ" | widen one field, or keep (G0 notes) | 0 | yes |
| ChunkSave | 94-131 | saves unloading chunks to region files on stop or unload | none (I/O at the edge is the lesson) | stays | 0 | yes |

Comment at Chunks.cs:16 is stale. It says `Instantiate` may not run in a pass, but only `LoadEntities` /
`SaveEntities` are refused. Fix it with F3.

F3 caveats:
- The cap of 256 per tick goes away. The fake-client start then generates every chunk in one tick.
- Chunk ids are no longer recycled in the order of the requests.
- The deterministic output may change. Run `check-sample.sh` before accepting.
- Alternative: keep the cap with a `Reduce`/budget singleton. That costs back about 4 lines.

### 1.2 Movement.cs (98 lines)

| System | Lines | What it does | Unused | Before → after | Saved | Exemplary |
|---|---|---|---|---|---|---|
| PlayerMove | 11-20 | applies input to velocity | already a vector loop (`Walk`) | stays | 0 | yes |
| Physics | 31-82 | gravity, collision with the voxel grid, `OnGround` | vector loop blocked by `Mut<Position>` and a bool field | stays scalar (collision reads blocks); G0 lets the bool lane through but `Mut` and block reads keep it scalar | 0 | yes |
| Grid | 89-97 | updates the tile cell of moved bodies (`SetInChunkIfChanged`) | F7: check whether `SimdMode.Off` is still needed | drop the attribute if the generator no longer tries | 1 | yes |

### 1.3 Aoi.cs (144 lines)

| System | Lines | What it does | Unused | Before → after | Saved | Exemplary |
|---|---|---|---|---|---|---|
| AoiView | 27-74 | per player, the chunk square in view; sends enter and leave | L2 pairs could replace the square walk, but the view is chunk-keyed, not distance-keyed | stays | 0 | yes, AOI is the lesson |
| AoiChunk | 77-89 | marks chunks seen | vector blocked by int/long lanes | stays | 0 | yes |
| AoiSpawn / AoiDespawn | 94-109 / 112-121 | entity enter and leave per viewer | none | stays | 0 | yes |
| Aoi helper | 124-143 | shared chunk-square math | none | stays | 0 | yes |

### 1.4 Zombies.cs (201 lines)

| System | Lines | What it does | Unused | Before → after | Saved | Exemplary |
|---|---|---|---|---|---|---|
| NightSpawn | 14-39 | spawns zombies at night near players | none | stays | 0 | yes |
| ZombieTarget | 51-80 | nearest player within vision, through `Terrain.ChunkRing` | L2 nearest form (G8) | ring walk → `[ForEach] static void Target(ref Target target, in Position p, Nearest<Player> nearest)` style form, exact shape per locality.md 3.4 | ~30 incl. `ChunkRing` (Terrain.cs:371-385) | yes, if the cutoff is per row |
| Chase | 87-110 | moves toward the target through a `Lookup<Position>` | F5: try `SimdMode.Require` for the gather | an attribute; if it compiles to a vector loop, measure | 0 | yes |
| Attack | 116-131 | hits a player in reach | takes the `World`, which keeps it scalar | stays | 0 | yes |
| Exposure / Sunlight | 141-153 / 155-163 | sunlit test, sets burning | bool lane (G0) | stays, vectorizes with G0 | 0 | yes |
| FireDamage | 173-188 | hand-written span loop under `#pragma warning disable EECS103` | the generated form EECS103 asks for (F1) | → `[ForEach, With<Burning>] static void Hurt(ref Health health) => health.Value--;` | ~10, and the pragma goes | yes, and better: a hand loop the analyzer warns about is not something users should copy |
| Extinguish | 192-200 | clears burning by day | none | stays | 0 | yes |

### 1.5 Items.cs (122 lines)

| System | Lines | What it does | Unused | Before → after | Saved | Exemplary |
|---|---|---|---|---|---|---|
| Dig | 11-34 | removes the block, spawns an item | bool `Input.Dig` (G0) | stays | 0 | yes, lesson |
| Pickup | 43-69 | player picks items in reach | L2 writes to j (G9) | later: pairs form | ~10 (G9) | yes |
| Inventory | 72-103 | tallies carried items | accumulating `Reduce` (G5) | part of G5 | see G5 | yes |
| ItemExpiry | 109-121 | expires items through the ordered index | none | stays (ordered index is the lesson) | 0 | yes |

### 1.6 Health.cs (47 lines)

| System | Lines | Unused | Change | Saved |
|---|---|---|---|---|
| ZombieDeath | 6-16 | `stats.X++` on a ref singleton keeps it scalar | accumulating Reduce (G5) | ~2 |
| PlayerRespawn | 22-47 | `Mut<Health>` | stays (lesson: Mut) | 0 |

### 1.7 Replication.cs (97 lines)

| System | Lines | Unused | Change | Saved |
|---|---|---|---|---|
| SendChunks | 12-18 | `stats.X++` | G5 | ~1 |
| ChunkSent | 22-26 | none | stays | 0 |
| SendEntities | 37-49 | `Maybe` in a parallel run keeps it scalar | stays | 0 |
| SendPackets | 53-67 | gets, loops and clears `Emitted<T>` by hand | generated consumer (G6) | ~6 |
| Tally | 73-82 | sums counts into stats by hand | G5 | ~8 |
| Clock | 89-96 | none | stays | 0 |

### 1.8 Water/ (Commit 22, Float 39, Flow 124, HeightMap 38, Sleep 22, Wake 38)

- **Flow (34-124)**:
  - Hand-written `Vector128` `Shade`/`Gives` plus per-row summaries.
  - G1 (WP5/5b `Cell<,>` with per-tile results) would turn this into a tile method with one result per tile, saving
    about 50 lines.
  - Vector loops are blocked today by "lanes int and byte".
  - The output must be identical, so prove it with check-sample.sh and the Flow tests.
- **HeightMap (15-38)**:
  - Hand-written `Vector128` over a generic `DynamicBuffer`.
  - G2 (WP2 element loops) saves about 6 lines.
- **Float**: uses a Lookup, so it stays scalar. No change.
- **Commit, Sleep, Wake**: no change.

### 1.9 Terrain.cs (210), Program.cs (192), Rules.cs (79), Components (253), Groups (16), Host (290)

- **Terrain.Generate (225-247)**:
  - Hand-written `Vector128` noise on a cold path (chunk generation).
  - F4 writes it scalar and saves about 11 lines. This is the owner's call: it is the sample's one hand-SIMD lesson.
- **Terrain.ChunkRing (371-385)**: goes away with G8. `Nearby` (283-348), `ChunkStrip` and `ChunkSquare` stay.
- **Program.cs prefabs (174-190)**:
  - `.Set(new Fresh(), new Flowing(), new Unloading()).RemoveUnloading()` (179) and `.SetBurning(0).RemoveBurning()` (187).
  - F2 replaces these with `.SetAbsent<Unloading>()` / `.SetAbsent<Burning>()`. This saves about 2 lines and states
    the intent.
- **Program.cs capacity (151-166, 65-69)**:
  - Five `MaxPooledLength` settings and five `FillPool` calls.
  - G4: `Reserve(prefab)` filling the buffer pools saves about 8 lines.
- **[Prefab] declarations**: G3 would need buffer fields (EECS102 today) and absent fields. It adds about 15 lines
  instead of saving any, so it is not part of this plan.
- **Host/**: FakeClients 24, RegionFiles 96, Server 111, Steady 48, Support 11. This is plumbing and stays. G11 (a
  steady-state check helper) is probably declined.

## 2. Why methods stay scalar today (from the generated reasons, obj/Release)

| Reason | Methods | Fix |
|---|---|---|
| a `bool` field | Visible.Placed, Input.Dig, Exposure.Sunlit, Body.OnGround | G0 |
| an enum field | FluidStep.Result (StepResult), Item.Kind (Block) | G0 |
| lane types differ (int/long, int/byte) | ChunkUnload, AoiChunk, Flow | widen in user code, or mixed-width lanes |
| a `Lookup` | Float, NightSpawn, Chase, Target | F5 tries the gather form for Chase |
| `Mut<T>` | Physics, Respawn | none (lesson) |
| `stats.X++` on a ref singleton | SendChunks, ZombieDeath | G5 |
| spans | FireDamage | F1 |
| generic `DynamicBuffer` | HeightMap | G2 |
| `SetInChunkIfChanged` | Grid | stays |
| `Maybe` in a parallel run | SendEntities | stays |
| takes the `World` | Aoi, Attack | stays |

G0 is the cheapest generator gap. `Kernel.cs:929` rejects bool and enum fields as lanes. `Kernel.Wide.cs:149-154`
already maps enums and bools to their sizes. Letting them through as byte or underlying-type lanes unblocks four
systems' reasons. Those systems still need their other blockers removed before they vectorize.

## 3. Hand-written code the generator should write

| # | Where | What users write by hand | Generated form | Saved |
|---|---|---|---|---|
| G0 | Kernel.cs:929 | nothing, but bool and enum fields block vector loops | bool/enum lanes | 0 (speed) |
| G1 | Water/Flow.cs 34-124 | `Vector128` shading plus per-row summaries | `Cell<,>` with per-tile results (inner-loops WP5/5b) | ~50 |
| G2 | Water/HeightMap.cs 15-38 | `Vector128` over a buffer | element loops (WP2) | ~6 |
| G4 | Program.cs 65-69, 151-166 | pool sizes and fills per buffer | `Reserve(prefab)` fills pools | ~8 |
| G5 | Tally, Inventory (Carried, Filled), ZombieDeath, SendChunks | sums into singletons by hand (Reduce replaces, it does not add) | accumulating reductions | ~25 |
| G6 | Replication.cs:58; also BattleServer.cs:353, samples/Actors/Host/Storage.cs:30 | get `Emitted<T>`, loop, clear | generated consumer of emitted records | ~6 (here) |
| G7 | Pickup / Chase | a second Lookup of another component of the found entity | keyed read of the found entity | ~3 |
| G8 | Zombies.cs:51-80 + Terrain.cs:371-385 | nearest player through a ring of chunks | L2 nearest form (locality.md 3.4) | ~30 |
| G9 | Items.cs:43-69 | pairs with a write to j | L2 writes to j (after G8) | ~10 |
| G10 | ChunkRead/ChunkSave | load/save outside the pass | deferred consume | probably decline (it is the lesson) |
| G11 | Host/Steady.cs | steady-state allocation check | helper | probably decline |

G3 (`[Prefab]` with buffer and absent fields) adds lines here, so this plan leaves it out.

## 4. What must stay because it is the lesson

These stay:
- AOI (view square, enter and leave).
- The chunk lifecycle: read, generate, unload, save.
- `CollectIds` and load outside the pass (ChunkRead).
- `Mut` (Physics, Respawn).
- Dig.
- The ordered index (item expiry, leaderboard).
- Save on stop.
- Groups with `UpdateInterval`.
- The region files.

Terrain's hand SIMD is the only lesson that is in question (F4).

## 5. Deterministic outputs

- `.github/scripts/check-sample.sh` compares the program's output with the README's expected block and with
  `docs/content/samples/voxel-server.mdx` / `.zh.mdx`.
- F1, F2, F4, F5, F7, G0, G1, G2, G4, G5 and G6 must keep the output byte-identical. That is their proof.
- F3 may change the output, through the generation order and the cap.
- G8 changes the output: tie order, and which player a zombie targets.
- When the output changes, update together:
  - `samples/VoxelServer/README.md` (output block);
  - `voxel-server.mdx` and `.zh.mdx` (output blocks, and the walkthrough of ZombieTarget);
  - the stale "not used: parallel physics and the hunt" text, which is wrong today (both run in parallel). Fix it in
    the next docs pass regardless.

## 6. Totals and order of work

| Step | Content | Depends on | Saved | Output |
|---|---|---|---|---|
| 1 | F1 FireDamage, F2 SetAbsent, F7, Chunks.cs:16 comment | nothing | ~13 | same |
| 2 | F3 ChunkGenerate with `[Consume]` | nothing | ~10 | check |
| 3 | F5 Chase gather, measured | prefetch work merged (the prefetch is automatic for row and vector forms on x86; remeasure only) | 0 | same |
| 4 | F4 Terrain scalar | owner's call | ~11 | same |
| 5 | G0 bool/enum lanes | generator change (feature skill) | 0 | same |
| 6 | G5 accumulating Reduce, G6 emitted consumer, G4 Reserve | generator/core features | ~39 | same |
| 7 | G2 element loops, G1 Cell results | inner-loops WP2, WP5/5b | ~56 | same |
| 8 | G8 nearest, then G9 | L2 cluster pairs (feat-locality-pairs) | ~40 | changes |

Total: about 150-160 of 2,164 lines (about 7%).
- The steps that need nothing else (1-4) save about 35 lines.
- The prefetch work needs no code here. The sample's working set fits in cache, so the effect may be zero. Measure it.

Proof for every step:
- TickBenchmark A/B (`samples/VoxelServer/Benchmark`) with BenchmarkDotNet: no regression, 0 B steady state.
- `check-sample.sh`.
- The VoxelServer steady-state check in `Host/Steady.cs`.

## 7. Open questions for the owner

1. **F3**: accept losing the cap of 256 generated chunks per tick (and maybe a changed output), or keep a budget?
2. **F4**: keep Terrain's hand `Vector128` as the sample's one hand-SIMD lesson, or write it scalar?
3. **G8 (L2 nearest)**:
   - Must the form support a per-row cutoff (Vision)?
   - Must it support pairs across two tables (zombies × players)?
   - Must it support "keep the target while it is in reach"?
   - Accept the new tie order in the output?
4. Which generator gaps to build:
   - G0, G5 and G6 are general and small. G6 repeats in three places.
   - G4 is optional.
   - G10, G11 and G3 are proposed as declined. Record them in owner-review.md.
5. Is about 7% worth a rewrite? The alternative is to take steps 1-2 now and let the rest land with the features that
   enable them.

## 8. Coordinator's decisions (2026-10-26)

- Every rewrite step waits for the finish phase (owner: "先把代码全改完"); the generator gaps are built now as code
  items (backlog Later): G0, G4, G5, G6, G7.
- G8 / G9 requirements were given to L2 (feat-locality-pairs): a cutoff per row, pairs across two tables, keep the
  target while in reach (read from a component), writes to j; ties by the lower EntityId. The output may change.
- F3: keep a budget per tick if `[Consume]` can carry one (a cap is a lesson of the sample); otherwise keep today's
  form and fix the stale comment. F4: write Terrain scalar if the generator vectorizes it (EECS103's rule: no hand
  SIMD the plain form would generate); keep it only if it stays faster, and say why in the code.
- G3, G10, G11: declined, recorded in `docs/owner-review.md`.
