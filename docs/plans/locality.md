# Plan: layout instead of gathers (internal, not published)

The owner's goal: redesign the layers under the vector loops after the HPC and database work that avoids irregular
access instead of paying for it - GROMACS's cluster pairs (Páll & Hess 2013, "A flexible algorithm for calculating
pair interactions on SIMD architectures"; the GROMACS pair-list docs) and Polychroniou, Raghavan & Ross, SIGMOD 2015
("Rethinking SIMD vectorization for in-memory databases"). Nothing here is built yet; phase 0 decides what is.

## 1. What the sources say, in our terms

| Source | The idea | Where it lands here |
|---|---|---|
| GROMACS | Per-atom Verlet lists make each j a gather; grouping atoms into spatial clusters of 4/8 and listing **cluster pairs** makes each j-cluster one contiguous load, the i x j work a dense 4x4 / 4x8 block with a cutoff mask | a pair form over spatially ordered rows (section 3.2) |
| GROMACS | The list is rebuilt every `nstlist` steps with a buffer radius (rlist > rc), so the sort's cost spreads over many steps | rows re-sorted on a cadence, with a margin (3.1) |
| GROMACS | Bounding boxes per cluster prune pairs before any distance math | a box per block of rows, kept with the sort (3.1) |
| Polychroniou | Selective store (compress) and selective load turn a filter into dense output without branches | row selection in the core: marked rows, structural changes from a vector mask, Emit (3.3) |
| Polychroniou | Vertical vectorization of hash probes: one key per lane, gathers of buckets, lanes that finish are refilled | batch lookups of ValueIndex / UniqueIndex (3.3) |
| Polychroniou | Radix partitioning with vector histograms and conflict-free scatters, buffered per partition | the re-sort of 3.1, GroupBy's bulk moves (3.3) |

What we already have that matches: columns are SoA per table and pinned (the layout GROMACS needs inside a cluster);
`Tile` / `Cell<,>` keep regular grids gather-free; `GroupBy` splits tables by a key. What is missing: an **order of
rows inside a table**, **blocks with bounds**, a **pair form** that uses them, and the database operators in the core.

## 2. Where we pay for irregular access today

- `Lookup<T>` reads (D131): vector loops gather only when a vector's targets share a table; random targets over many
  tables run row by row (1.0-1.05x at 64 tables).
- Spatial searches written by hand: `ZombieTargetSystem.Pick` (VoxelServer: rings of chunks through a value index,
  then `positions.Read(id)` per candidate: scattered reads, scalar), `BattleServer.NearestPlayer` (a linear scan),
  the AOI systems. Every server with "who is near me" writes one of these.
- Value index probes: one key at a time.
- Filters that produce rows (marked rows, structural changes inside a pass, Emit): branchy, one row at a time.

## 3. Design

### 3.1 Ordered rows and blocks (core)
- A table can keep its rows in the order of a key the user declares on a component (a spatial cell key: Morton /
  Hilbert of a quantized position; any `IComparable` key for non-spatial uses). Provisional name `[OrderBy]` on the
  key member; naming audit before it ships. Usually inferred from a pair method instead (3.4).
- The order is restored on a cadence, not on every write (GROMACS's nstlist): a pass that re-sorts a table whose
  rows drifted, by an LSD radix partition of (key, row) pairs (Polychroniou), then one permutation applied to every
  column under the structural gate (`Archetype.FillHoles` already rewrites rows and slots; the re-sort is the same
  kind of move and bumps `StorageVersion`).
- Blocks: every 8 rows (one 256-bit float vector) get an axis-aligned box, written by the re-sort and widened by the
  margin; the box stays valid until the next re-sort as long as no row moves farther than the margin (checked from
  the displacement since the sort, as GROMACS does, or enforced by re-sorting earlier).
- Side effect that matters without any new form: `Lookup` targets ordered in space sit in few tables and near rows,
  so D131's gathers hit one table more often and the cache more often.

### 3.2 The pair form (generator)
- The user writes what one pair does, as with `Cell<,>` for a grid cell; the generator writes the cluster-pair
  kernel: for each i-block, the j-blocks whose boxes are within the cutoff, then an 8 x 8 block (i in lanes, j
  broadcast or rotated through permutes), the cutoff and self pairs masked, sums into i's lanes.
- Forms: an interaction (sum over neighbors: separation, repulsion, damage in an area) and a nearest (argmin over
  neighbors within a range: targeting, `NearestPlayer`). The nearest form keeps the tie order of the row loop or
  states the order it uses (the exactness rule of `inner-loops.md` section 3).
- Writes go to i only; writing j (Newton's third law, half the pairs) is a later step with its own proof.
- Parallel: i-blocks split across threads; j is read-only.
- The pair list itself is cached between re-sorts, as GROMACS's list is (rebuilt with the boxes).

### 3.4 Automatic: the user's plain code adapts (the owner's rule, 2026-10-26)

Users do not learn cluster pairs or compress. They write the code they would write anyway; the generator and the core
pick the layout and the kernel.

- **The order is inferred, not declared.** When a pair method (below) compares the distance of two rows' positions
  against a cutoff, the generator registers an order for the tables it walks: the Morton key of that position
  component, quantized by the cutoff, and a re-sort cadence from the cutoff and the margin. `[OrderBy]` stays only as
  an override (another key, a non-spatial order); without a pair method a table keeps today's order and pays nothing.
  The core therefore needs an order a system can request at setup (internal API the generated setup calls), not only
  an attribute.
- **The pair form is the code users already write.** Recognized shapes, each turned into the blocked kernel when
  the body passes the vector-loop rules, else kept as written with the EECS055 reason:
  1. a `[ForEach]` method with a second row (`in Position other`, the "other" side of the pair) and a distance test
     against a cutoff (constant, singleton field, parameter): the interaction form;
  2. the same with a running best (`if (d < best) { best = d; target = other; }`): the nearest form (tie order of
     the row loop kept);
  3. a nested walk the user wrote by hand inside a `[ForEach]`: `foreach` over a query or view of the same
     components with that distance test. Same set of pairs, so the generator rewrites it automatically;
  4. a hand-written grid search (cell keys into `ValueIndex` / `Lookup` + distance test): not provably the same set,
     so an analyzer rule with a code fix to shape 1 or 2.
  Self pairs are skipped when the body says `other != self` or the distance test excludes zero; otherwise the
  generator keeps them, as the row loop does.
- **Compress has no API.** Every place a vector mask becomes rows uses it: the generated loops (marked rows,
  structural writes after the pass, tag writes, `Emit`, `[Consume]`, value conditions) and the core (`Lanes.Compact`
  callers, query filters). The generated code calls one core helper, so it picks AVX-512 CompressStore / AVX2 table /
  ARM64 TBL at run time and no generated text changes per ISA.
- Docs say what is recognized and what keeps a method on the row loop, like the vector-loop list.

### 3.3 Database operators in the core
- Selective store: a vector mask to a list of rows with `Avx512F.Compress` where present, a permutation table on
  AVX2 (4 / 8 lanes); used where the core turns conditions into rows.
- Batch probes: `ValueIndex` / `UniqueIndex` lookups of many keys at once (the zombie's chunk keys, relation targets
  in bulk), vertical: one key per lane.
- Radix partition: the re-sort of 3.1; `GroupBy`'s bulk moves when many rows change group in one tick.

## 4. Phase 0: prove it before building it (one agent, benchmarks only)

Hand-written kernels in `benchmarks/` first; no core or generator change until a bar is met.

1. **Pairs:** N entities in 2D/3D, cutoff r, density of a crowd and of a sparse map (1k / 10k / 100k). Four
   versions: the row loop over a hand-written grid (what users write today), the same with per-entity neighbor
   lists, a hand-written cluster-pair kernel over Morton-sorted rows (8 x 8, boxes), and the same with the sort's
   cost included at 1, 10 and 20 ticks per sort. Bar: the cluster kernel with its sort amortized at 10 ticks is
   >= 2x the grid row loop at 10k and 100k, 0 B per tick.
2. **Nearest:** `ZombieTargetSystem.Pick`'s shape and `NearestPlayer`, hand-written against a block-box search.
   Bar: >= 1.5x at the sample's sizes.
3. **Lookup locality:** `LookupGatherBenchmark` with targets ordered by key against random, scalar and D131's
   gathers. Bar: the 64-table case beats row by row once ordered (it is the case D131 loses).
4. **Operators:** compress against the branchy row list at 10 / 50 / 90 % selectivity; batch probe against one by
   one at 1k / 100k keys. Bar: 1.3x, else that operator is dropped.

Each result goes into section 6 with its numbers; the failed ones go to `docs/owner-review.md`.

## 5. Work packages after phase 0 (only those whose bar was met)

- **L1** ordered rows + blocks: built (branch `feat-locality-core`, D135; what L2 reads is in 6.5).
- **L2** pair form (generator + Kernel, EECS055 reasons for bodies it cannot take, a code fix from a hand-written
  grid search where one is recognizable), tests bit for bit against the row loop.
- **L3** compress: built (branch `feat-locality-core`, D136). Batch probes are not built (6.3).
- **L4** examples: `ZombieTargetSystem`, `NearestPlayer`, the AOI rewritten in the new forms (exemplar pass), the
  battle server page.
- Decision records: one per package (ordered rows, the pair form, the operators), with what phase 0 dropped.

## 6. Results

Phase 0 (branch `research-locality`): `benchmarks/EasyEcs.Benchmark/Locality/` holds the prototypes, and the classes
`PairsBenchmark`, `NearestZombieBenchmark`, `NearestPlayerBenchmark`, `CompressBenchmark` and `BatchProbeBenchmark`
measure them; `tests/EasyEcs.UnitTest/LocalityTests.cs` checks every version against its plain form and 0 B per
tick. 4.3 (Lookup locality) was left to the agent changing `Lookup`.

Setup: BenchmarkDotNet 0.15.2, the default job (2 launches x 8 iterations of 250 ms, 6 warm-ups), two rounds over a
frozen snapshot, one process per class, round 2 in reverse class order. Xeon 2.10 GHz VM, 4 cores, AVX-512F/CD/BW/
DQ/VL/VBMI, .NET 10.0.13. Other agents benchmark on the same machine, so the load average is logged at the start of
each class. Every method of every class allocates 0 B. Means are per tick (pairs, zombies), per query (NearestPlayer,
probes) or per call (compress).

Load average (1 / 5 / 15 min) at the start of each class:

| Class | Round 1 | Round 2 |
|---|---|---|
| Pairs | 2.06 / 1.83 / 1.89 | 8.83 / 4.08 / 2.76 (1.05 / 1.40 / 1.87 at the end) |
| NearestZombie | 2.12 / 1.63 / 1.59 | 1.76 / 1.80 / 1.80 |
| NearestPlayer | 1.67 / 1.71 / 1.64 | 1.17 / 1.61 / 1.87 |
| Compress | 2.27 / 1.84 / 1.64 | 2.07 / 2.27 / 2.10 |
| BatchProbe | 2.69 / 1.84 / 1.69 | 2.32 / 1.93 / 1.91 |

### 6.1 Pairs (4.1): bar met

`Crowd.cs`: Position and Velocity in 2D, cutoff r = 1, a separation push summed into i (`(r^2 - d^2) * (dx + 0.5 dv)`
for every j within r). Crowd: 24 neighbors per entity on average; sparse map: 2. Versions:

- **GridLookups** (baseline), what a user writes today: a counting sort of ids into a uniform grid of cell side r
  (two query loops), then a `[ForEach]` over the rows reading the 3 x 3 cells' entities through `Lookup<Position>` /
  `Lookup<Velocity>`.
- **GridCopies**: the same grid with positions and velocities copied into the cell arrays, so the inner loop reads no
  lookups: the best a user writes by hand without reordering the world.
- **NeighborLists10**: per-entity lists within 1.1 r, rebuilt every 10 ticks (build included), read through `Lookup`.
- **Clusters**: the cluster-pair kernel alone on rows sorted beforehand: blocks of 8 Morton-ordered rows with boxes,
  a CSR list of block pairs within 1.1 r, 8 x 8 blocks in `Vector256` (i in lanes, j broadcast), cutoff and self
  masked.
- **ClustersSort1 / 10 / 20**: the kernel with the sort (keys, LSD radix sort, copies, boxes, the pair list) paid every
  1, 10 or 20 ticks.
- **ClustersWithCopies10**: Sort10 plus the copies the prototype pays every tick in a live world (positions and
  velocities scattered into the sorted arrays, the pushes written back).

| Method | Entities | Density | Mean r1 | Ratio r1 | Mean r2 | Ratio r2 | Allocated |
|---|---|---|---|---|---|---|---|
| GridLookups | 1000 | Crowd | 756.52 us | 1.00 | 835.00 us | 1.00 | 0 B |
| GridCopies | 1000 | Crowd | 439.10 us | 0.58 | 430.22 us | 0.52 | 0 B |
| NeighborLists10 | 1000 | Crowd | 323.50 us | 0.43 | 319.66 us | 0.38 | 0 B |
| Clusters | 1000 | Crowd | 55.95 us | 0.07 | 57.29 us | 0.07 | 0 B |
| ClustersSort1 | 1000 | Crowd | 157.70 us | 0.21 | 156.66 us | 0.19 | 0 B |
| ClustersSort10 | 1000 | Crowd | 98.17 us | 0.13 | 99.67 us | 0.12 | 0 B |
| ClustersSort20 | 1000 | Crowd | 99.51 us | 0.13 | 100.86 us | 0.12 | 0 B |
| ClustersWithCopies10 | 1000 | Crowd | 67.03 us | 0.09 | 68.66 us | 0.08 | 0 B |
| GridLookups | 1000 | Sparse | 123.02 us | 1.03 | 105.02 us | 1.00 | 0 B |
| GridCopies | 1000 | Sparse | 103.13 us | 0.87 | 100.91 us | 0.96 | 0 B |
| NeighborLists10 | 1000 | Sparse | 35.13 us | 0.29 | 34.66 us | 0.33 | 0 B |
| Clusters | 1000 | Sparse | 28.81 us | 0.24 | 28.57 us | 0.27 | 0 B |
| ClustersSort1 | 1000 | Sparse | 106.68 us | 0.90 | 111.41 us | 1.07 | 0 B |
| ClustersSort10 | 1000 | Sparse | 53.12 us | 0.45 | 51.34 us | 0.49 | 0 B |
| ClustersSort20 | 1000 | Sparse | 48.67 us | 0.41 | 49.84 us | 0.48 | 0 B |
| ClustersWithCopies10 | 1000 | Sparse | 39.93 us | 0.34 | 39.47 us | 0.38 | 0 B |
| GridLookups | 10000 | Crowd | 9,953.08 us | 1.00 | 10,234.39 us | 1.00 | 0 B |
| GridCopies | 10000 | Crowd | 5,591.84 us | 0.56 | 5,460.28 us | 0.53 | 0 B |
| NeighborLists10 | 10000 | Crowd | 4,565.64 us | 0.46 | 4,641.19 us | 0.45 | 0 B |
| Clusters | 10000 | Crowd | 674.75 us | 0.07 | 654.76 us | 0.06 | 0 B |
| ClustersSort1 | 10000 | Crowd | 2,212.10 us | 0.22 | 2,177.69 us | 0.21 | 0 B |
| ClustersSort10 | 10000 | Crowd | 1,234.94 us | 0.12 | 1,226.61 us | 0.12 | 0 B |
| ClustersSort20 | 10000 | Crowd | 1,130.10 us | 0.11 | 1,183.39 us | 0.12 | 0 B |
| ClustersWithCopies10 | 10000 | Crowd | 916.08 us | 0.09 | 930.91 us | 0.09 | 0 B |
| GridLookups | 10000 | Sparse | 2,154.62 us | 1.00 | 2,234.80 us | 1.01 | 0 B |
| GridCopies | 10000 | Sparse | 1,717.12 us | 0.80 | 1,669.17 us | 0.75 | 0 B |
| NeighborLists10 | 10000 | Sparse | 623.53 us | 0.29 | 606.09 us | 0.27 | 0 B |
| Clusters | 10000 | Sparse | 333.22 us | 0.16 | 338.79 us | 0.15 | 0 B |
| ClustersSort1 | 10000 | Sparse | 1,773.10 us | 0.82 | 1,916.02 us | 0.86 | 0 B |
| ClustersSort10 | 10000 | Sparse | 684.33 us | 0.32 | 665.11 us | 0.30 | 0 B |
| ClustersSort20 | 10000 | Sparse | 601.80 us | 0.28 | 617.68 us | 0.28 | 0 B |
| ClustersWithCopies10 | 10000 | Sparse | 579.79 us | 0.27 | 577.48 us | 0.26 | 0 B |
| GridLookups | 100000 | Crowd | 153,351.54 us | 1.00 | 156,118.02 us | 1.01 | 0 B |
| GridCopies | 100000 | Crowd | 63,972.46 us | 0.42 | 60,455.16 us | 0.39 | 0 B |
| NeighborLists10 | 100000 | Crowd | 77,651.76 us | 0.51 | 77,603.24 us | 0.50 | 0 B |
| Clusters | 100000 | Crowd | 7,264.69 us | 0.05 | 7,070.86 us | 0.05 | 0 B |
| ClustersSort1 | 100000 | Crowd | 29,467.70 us | 0.19 | 28,697.66 us | 0.19 | 0 B |
| ClustersSort10 | 100000 | Crowd | 9,794.10 us | 0.06 | 9,440.51 us | 0.06 | 0 B |
| ClustersSort20 | 100000 | Crowd | 8,225.62 us | 0.05 | 8,118.82 us | 0.05 | 0 B |
| ClustersWithCopies10 | 100000 | Crowd | 10,813.76 us | 0.07 | 11,336.06 us | 0.07 | 0 B |
| GridLookups | 100000 | Sparse | 34,698.55 us | 1.00 | 31,789.64 us | 1.00 | 0 B |
| GridCopies | 100000 | Sparse | 21,482.21 us | 0.62 | 20,902.67 us | 0.66 | 0 B |
| NeighborLists10 | 100000 | Sparse | 9,521.86 us | 0.28 | 10,698.49 us | 0.34 | 0 B |
| Clusters | 100000 | Sparse | 3,492.73 us | 0.10 | 3,644.62 us | 0.11 | 0 B |
| ClustersSort1 | 100000 | Sparse | 20,914.99 us | 0.61 | 21,095.56 us | 0.66 | 0 B |
| ClustersSort10 | 100000 | Sparse | 5,722.35 us | 0.17 | 5,436.12 us | 0.17 | 0 B |
| ClustersSort20 | 100000 | Sparse | 4,491.66 us | 0.13 | 4,497.66 us | 0.14 | 0 B |
| ClustersWithCopies10 | 100000 | Sparse | 6,905.63 us | 0.20 | 7,061.84 us | 0.22 | 0 B |

Bar (Sort10 >= 2x GridLookups at 10k and 100k, 0 B): **met** in both rounds. Sort10 against the baseline (r1 / r2):
10k crowd 8.1x / 8.3x, 10k sparse 3.1x / 3.4x, 100k crowd 15.7x / 16.5x, 100k sparse 6.1x / 5.8x. The kernel alone
is 13x to 22x the baseline in crowds and 3.7x to 10x on sparse maps. Against the best hand-written grid (GridCopies)
Sort10 is still 2.5x (10k sparse) to 6.5x (100k crowd).

Correctness: every version within `1e-5 * (1 + sum of |terms|)` of a double-precision sum over the same pairs
(`PairVersionsAgree`, 1k and 3k entities, both densities). The test also checks that the keys are sorted and the
block boxes stay near the side of the square 8 rows cover: that check caught a radix sort ending in its scratch
arrays (sorted by the low 24 bits only), which kept every value right while the boxes spanned the map.

What the prototype copies that the core version would not: the sort copies Position and Velocity into sorted side
arrays (`ClusterRows.PX/PY/VX/VY`), keeps `Order` / `Slot` (sorted slot to entity and back), and writes Push back to
the entities after the kernel (`WriteBack`); ClustersWithCopies10 also refreshes the side arrays every tick. The core
(L1) permutes the table's own columns and entity slots once per sort, so the kernel reads the columns in place: no
per-tick copies, no `Order` / `Slot`, no write-back. The prototype moves only the two columns the kernel reads; the
core moves every column of the table, a cost per sort this benchmark does not have (wide rows pay more per re-sort).

Notes:
- The sort is costly: about 1.5 ms at 10k and 17-22 ms at 100k (Sort1 - Clusters), most of it the block-pair search
  (a walk of the box tree per block) and the copies, not the radix sort. L1 should build the pairs from a grid of
  blocks. At 1k the sort pays off only from Sort10 on.
- At 1k and 10k, Sort10 and Sort20 are slower than the kernel plus a tenth (twentieth) of Sort1: about 0.4 ms per
  tick more at 10k crowd, while ClustersWithCopies10, the same work plus copies, is below Sort10. At 100k the sum
  holds. Not pinned down (the benchmark method's loop, inlining or PGO around the sort's call); it does not change
  the verdict, and it understates the cluster versions.
- Per-entity neighbor lists beat the lookup grid (2x to 3.7x) but stay far behind the cluster kernel: each j is still
  a scattered read.

### 6.1a Pairs on ARM64 (owner's run, Apple M4, NEON, .NET 10.0.0, default job, one round)

Ratio against `GridLookups` (lower is faster), 4-wide `Vector128` kernel (eedfeb0):

| Entities / density | GridCopies | NeighborLists10 | Clusters | ClustersSort1 | ClustersSort10 | ClustersSort20 | ClustersWithCopies10 |
|---|---|---|---|---|---|---|---|
| 1k crowd | 0.67 | 0.84 | 0.37 | 0.52 | 0.49 | 0.47 | 0.42 |
| 1k sparse | 0.86 | **0.30** | 0.78 | 1.55 | 0.91 | 0.78 | 0.90 |
| 10k crowd | 0.57 | 0.48 | 0.13 | 0.27 | 0.15 | 0.14 | 0.16 |
| 10k sparse | 0.85 | **0.29** | 0.42 | 1.06 | 0.50 | 0.48 | 0.61 |
| 100k crowd | 0.52 | 0.46 | 0.15 | 0.32 | 0.14 | 0.12 | 0.13 |
| 100k sparse | 0.83 | **0.40** | 0.31 | 1.40 | 0.48 | 0.40 | 0.60 |

The NEON kernel measured here is a direct port (the 8-row block run as two 4-lane halves, the x86 layout and order
kept), not a kernel tuned for NEON: the gap on sparse maps is first an optimization still to do, not a limit of
ARM64. L2 tunes it as it tunes the x86 one (4-row blocks, FMA, the j-loop order for 128-bit registers, llvm-mca on
the Tier1 loop).

Reading: crowded maps meet the 2x bar on ARM64 too (sort on a cadence: 2-8x). Sparse maps do not with the 8-row
blocks: ClustersSort10 is 1.1-2.1x and per-row neighbor lists win (2.5-3.4x), because a 128-bit vector covers half a
block and a sparse block's box admits many empty pairs. What L2 must check on ARM64 before it picks the kernel:
blocks of 4 rows (GROMACS's 4x4 NEON kernel: boxes tighter, one vector per block) against the 8-row block run as two
halves, and the neighbor-list form for sparse maps (the choice by measured density, as D130 chooses chunks).
The owner's call (2026-10-26): sparse maps on ARM64 are still faster than the grid (1.1-2.1x), so cluster pairs ship
there too and get optimized; the 2x bar decides whether a design is built, not whether a faster case is kept. What
must never ship is a slower case: re-sorting every tick on a sparse map (ClustersSort1: 1.06-1.55x slower) is avoided
by the cadence the generator picks (from the cutoff, the margin and the measured drift), and each kernel choice is
measured against the row loop it replaces. Compress on ARM64: section 6.3.

### 6.2 Nearest (4.2): met for the packed start and NearestPlayer, not met on a spread map

`Nearest.cs`, `NearestZombieBenchmark`: the shape of `ZombieTargetSystem.Pick` (VoxelServer): 3 zombies per player,
vision 35, chunk 16. The baseline walks rings of chunks through a `[LookupKey]` index (`Find`) and reads each
candidate's position, as the sample does. Packed: players on a spawn grid (16 wide, 3 apart), the sample's start;
spread: uniform over 64 x sqrt(players). The block search sorts the players (Morton, blocks of 8, a box tree with
fan-out 8) and descends with the best distance so far as the bound; SortOnce sorts once, SortPerTick every tick
(cost included). Ties: the ring walk keeps the first player found, the block search the lowest entity index; the
test compares distances (`ZombieSearchesAgree`, 50 / 400 players, both layouts).

| Method | Players | Layout | Mean r1 | Ratio r1 | Mean r2 | Ratio r2 | Allocated |
|---|---|---|---|---|---|---|---|
| RingWalk | 50 | Packed | 51.43 us | 1.00 | 49.42 us | 1.00 | 0 B |
| BlocksSortOnce | 50 | Packed | 13.80 us | 0.27 | 13.35 us | 0.27 | 0 B |
| BlocksSortPerTick | 50 | Packed | 31.22 us | 0.61 | 30.15 us | 0.61 | 0 B |
| RingWalk | 50 | Spread | 36.34 us | 1.00 | 34.48 us | 1.00 | 0 B |
| BlocksSortOnce | 50 | Spread | 12.23 us | 0.34 | 11.74 us | 0.34 | 0 B |
| BlocksSortPerTick | 50 | Spread | 27.80 us | 0.77 | 27.22 us | 0.79 | 0 B |
| RingWalk | 2000 | Packed | 4,528.92 us | 1.00 | 4,377.27 us | 1.00 | 0 B |
| BlocksSortOnce | 2000 | Packed | 2,307.37 us | 0.51 | 2,283.92 us | 0.52 | 0 B |
| BlocksSortPerTick | 2000 | Packed | 2,891.08 us | 0.64 | 2,851.79 us | 0.65 | 0 B |
| RingWalk | 2000 | Spread | 3,917.35 us | 1.01 | 3,475.94 us | 1.00 | 0 B |
| BlocksSortOnce | 2000 | Spread | 2,851.72 us | 0.73 | 2,771.53 us | 0.80 | 0 B |
| BlocksSortPerTick | 2000 | Spread | 3,351.69 us | 0.86 | 3,277.70 us | 0.94 | 0 B |

`NearestPlayerBenchmark`: BattleServer's `NearestPlayer` (a scan over every player), 100,000 units of which a third
are players on a 4096 map, 64 queries per invoke, mean per query. Blocks: the block search on a sort done once;
BlocksWithSort: one sort per 64 queries included (`NearestPlayerSearchesAgree`).

| Method | Mean r1 | Ratio r1 | Mean r2 | Ratio r2 | Allocated |
|---|---|---|---|---|---|
| Scan | 73,423.9 ns | 1.000 | 75,483.8 ns | 1.000 | 0 B |
| Blocks | 378.9 ns | 0.005 | 390.9 ns | 0.005 | 0 B |
| BlocksWithSort | 17,612.8 ns | 0.240 | 17,786.5 ns | 0.236 | 0 B |

Bar (>= 1.5x at the sample's sizes): **met** for the packed start with the sort paid every tick (1.65x / 1.64x at 50
players, 1.57x / 1.53x at 2000) and for NearestPlayer (194x on a sorted world, 4.2x with a sort per 64 queries);
**not met** on a spread map with the sort every tick (1.31x / 1.27x at 50, 1.17x / 1.06x at 2000). With the sort
amortized (SortOnce) every case is 1.25x to 3.7x. Where the time goes: about 30 box tests per search in a fan-out-8
tree of jagged arrays, descending without nearest-first order. A flat grid of blocks searched in rings would test
fewer boxes, and the nearest form would re-sort on L1's cadence instead of every tick.

### 6.3 Operators (4.4): compress met, batch probe not met

`Operators.cs`, `CompressBenchmark`: a float column, the rows below a limit (10 / 50 / 90 % selected) written to a row
list. Branchy: `if (v < limit) list[n++] = i`; Branchless: always store, advance by the condition; PermutationTable:
AVX2 compare, a 256-entry table of `Vector256<int>` shuffles (`PermuteVar8x32`), one store per 8 rows; Compress512:
`Avx512F.CompareLessThan` + `CompressStore` per 16 rows; CoreCompact: the core's `Lanes.Compact` per 64-row mask.

| Method | Rows | Percent | Mean r1 | Ratio r1 | Mean r2 | Ratio r2 | Allocated |
|---|---|---|---|---|---|---|---|
| Branchy | 4096 | 10 | 2,335.7 ns | 1.00 | 2,339.2 ns | 1.00 | 0 B |
| Branchless | 4096 | 10 | 6,243.7 ns | 2.67 | 6,196.9 ns | 2.65 | 0 B |
| PermutationTable | 4096 | 10 | 771.9 ns | 0.33 | 710.0 ns | 0.30 | 0 B |
| Compress512 | 4096 | 10 | 444.9 ns | 0.19 | 396.3 ns | 0.17 | 0 B |
| CoreCompact | 4096 | 10 | 916.0 ns | 0.39 | 936.6 ns | 0.40 | 0 B |
| Branchy | 4096 | 50 | 3,609.3 ns | 1.02 | 3,507.2 ns | 1.01 | 0 B |
| Branchless | 4096 | 50 | 6,172.7 ns | 1.74 | 6,143.0 ns | 1.77 | 0 B |
| PermutationTable | 4096 | 50 | 687.9 ns | 0.19 | 680.7 ns | 0.20 | 0 B |
| Compress512 | 4096 | 50 | 380.5 ns | 0.11 | 391.7 ns | 0.11 | 0 B |
| CoreCompact | 4096 | 50 | 1,024.6 ns | 0.29 | 1,023.2 ns | 0.29 | 0 B |
| Branchy | 4096 | 90 | 2,981.7 ns | 1.00 | 2,978.7 ns | 1.00 | 0 B |
| Branchless | 4096 | 90 | 6,145.5 ns | 2.06 | 6,094.9 ns | 2.05 | 0 B |
| PermutationTable | 4096 | 90 | 652.5 ns | 0.22 | 717.4 ns | 0.24 | 0 B |
| Compress512 | 4096 | 90 | 394.5 ns | 0.13 | 390.0 ns | 0.13 | 0 B |
| CoreCompact | 4096 | 90 | 1,079.5 ns | 0.36 | 1,108.2 ns | 0.37 | 0 B |
| Branchy | 262144 | 10 | 588,531.4 ns | 1.00 | 559,658.4 ns | 1.00 | 0 B |
| Branchless | 262144 | 10 | 406,865.4 ns | 0.69 | 401,499.1 ns | 0.72 | 0 B |
| PermutationTable | 262144 | 10 | 53,304.3 ns | 0.09 | 53,325.2 ns | 0.10 | 0 B |
| Compress512 | 262144 | 10 | 32,840.6 ns | 0.06 | 32,298.7 ns | 0.06 | 0 B |
| CoreCompact | 262144 | 10 | 93,091.5 ns | 0.16 | 85,506.0 ns | 0.15 | 0 B |
| Branchy | 262144 | 50 | 1,499,957.7 ns | 1.00 | 1,492,388.3 ns | 1.00 | 0 B |
| Branchless | 262144 | 50 | 409,029.8 ns | 0.27 | 404,170.2 ns | 0.27 | 0 B |
| PermutationTable | 262144 | 50 | 53,002.7 ns | 0.04 | 53,398.8 ns | 0.04 | 0 B |
| Compress512 | 262144 | 50 | 36,713.5 ns | 0.02 | 35,865.7 ns | 0.02 | 0 B |
| CoreCompact | 262144 | 50 | 82,495.7 ns | 0.06 | 80,985.1 ns | 0.05 | 0 B |
| Branchy | 262144 | 90 | 537,423.8 ns | 1.00 | 493,350.7 ns | 1.00 | 0 B |
| Branchless | 262144 | 90 | 420,023.1 ns | 0.78 | 396,788.7 ns | 0.81 | 0 B |
| PermutationTable | 262144 | 90 | 55,251.8 ns | 0.10 | 52,783.3 ns | 0.11 | 0 B |
| Compress512 | 262144 | 90 | 43,095.6 ns | 0.08 | 46,043.7 ns | 0.09 | 0 B |
| CoreCompact | 262144 | 90 | 87,366.7 ns | 0.16 | 88,286.4 ns | 0.18 | 0 B |

Compress bar (1.3x at 10 / 50 / 90 %): **met** everywhere. Compress512 is 5.2x to 9.5x at 4096 rows and 10.7x to 42x
at 262,144; the AVX2 permutation table 3.0x to 5.3x and 9.3x to 28x. The core's per-64 `Lanes.Compact` is about 2x
slower than a direct `CompressStore` loop. The branchy loop looks good at 4096 rows only because the benchmark repeats
the same 4096-row pattern and the branch predictor learns it (Branchless, which spills the condition through the
stack, loses there); at 262,144 rows the predictor cannot learn it and the branchy loop shows its mispredictions.

ARM64 (owner's Apple M4, NEON, default job, 2026-10-26; PermutationTable and Compress512 fall back to the 128-bit
shuffle table there, so the three rows are one form):

| Method | Rows | % | Mean | Ratio |
|---|---|---|---|---|
| Branchless | 4096 | 10 / 50 / 90 | 1,037 / 1,040 / 1,142 ns | 0.76 / 0.38 / 0.48 |
| ShuffleTable (TBL) | 4096 | 10 / 50 / 90 | 798 / 803 / 971 ns | 0.58 / 0.29 / 0.41 |
| CoreCompact | 4096 | 10 / 50 / 90 | 764 / 2,006 / 2,128 ns | 0.56 / 0.74 / 0.90 |
| Branchless | 262144 | 10 / 50 / 90 | 85.2 / 86.7 / 89.3 us | 0.52 / 0.10 / 0.48 |
| ShuffleTable (TBL) | 262144 | 10 / 50 / 90 | 66.8 / 69.8 / ~68 us | 0.41 / 0.08 / ~0.37 |
| CoreCompact | 262144 | 10 / 50 / 90 | 79.6 / 158.2 / ~146 us | 0.48 / 0.19 / ~0.8 |

The TBL table meets the bar on ARM64 (1.7x to 12x over branchy, 1.25-1.3x over branchless). The core's
`Lanes.Compact` does not: off AVX-512, a mask of more than `Sparse` bits goes to `Advance` (one scalar store per
element), 2-2.5x slower than the table at 50 / 90 % and slower than branchless. L3's core helper takes the table
forms on every ISA (NEON TBL, AVX2 permutation, AVX-512 compress) and must not lose any cell, the 10 % one included.

`BatchProbeBenchmark`: an open-addressing int -> int table (linear probing, capacity the power of two above 2x the
keys, 90 % of the queries hit), 4096 lookups per invoke, mean per lookup. Vertical: 8 keys per `Vector256`, AVX2
gathers of keys and values, the vector waits for its slowest lane; VerticalRefill: lanes that finish take the next
key (AVX-512VL `Expand` / `CompressStore`), Polychroniou's form.

| Method | Keys | Mean r1 | Ratio r1 | Mean r2 | Ratio r2 | Allocated |
|---|---|---|---|---|---|---|
| OneAtATime | 1000 | 1.552 ns | 1.01 | 1.586 ns | 1.01 | 0 B |
| Vertical | 1000 | 2.348 ns | 1.52 | 2.442 ns | 1.55 | 0 B |
| VerticalRefill | 1000 | 5.117 ns | 3.32 | 5.246 ns | 3.33 | 0 B |
| OneAtATime | 100000 | 1.923 ns | 1.00 | 1.890 ns | 1.00 | 0 B |
| Vertical | 100000 | 2.452 ns | 1.28 | 2.432 ns | 1.29 | 0 B |
| VerticalRefill | 100000 | 5.984 ns | 3.11 | 6.321 ns | 3.35 | 0 B |

Batch probe bar (1.3x): **not met**, dropped; both vertical forms are slower (1.3x to 3.4x). The scalar loop's
probes are independent, so the out-of-order core already overlaps several; the gathers on this CPU cost more than the
scalar loads they replace; and the 100k-key table (2 MB of slots) stays in the last-level cache, so there is no miss
latency for a batch to hide. What would change it: tables far past the cache (16M keys) where misses dominate,
measured against scalar group prefetching (`borrowed-ideas.md` item 1) rather than gathers.

**Re-measured at 16M keys (owner's call, 2026-10-26): vertical probes dropped again.** The table of 16M keys holds 32M
slots (128 MiB of keys, 128 MiB of values, past the 260 MiB last-level cache with the rest of the process); each
invoke takes the next 4,096 of 4M queries, so the lines an invoke touches are not left in cache by the invokes
before. Added: `Prefetched8/16/32`, one key at a time with the key and value slots of the query k ahead prefetched
(`Sse.Prefetch0`; nothing on ARM64, which has no prefetch in .NET 10 but SVE's). Default job, two rounds, same frozen
tree; load average 3.79 / 4.53 / 3.84 at the start of round 1 (another agent's run had just ended), 1.08 / 1.37 / 2.05
at round 2's.

| Method | Keys | Mean r1 | Ratio r1 | Mean r2 | Ratio r2 | Allocated |
|---|---|---|---|---|---|---|
| OneAtATime | 1000 | 1.518 ns | 1.00 | 1.513 ns | 1.00 | 0 B |
| Prefetched8 | 1000 | 2.105 ns | 1.39 | 2.098 ns | 1.39 | 0 B |
| Prefetched16 | 1000 | 1.941 ns | 1.28 | 2.103 ns | 1.39 | 0 B |
| Prefetched32 | 1000 | 2.370 ns | 1.57 | 2.146 ns | 1.42 | 0 B |
| Vertical | 1000 | 2.258 ns | 1.49 | 2.352 ns | 1.56 | 0 B |
| VerticalRefill | 1000 | 4.893 ns | 3.23 | 4.683 ns | 3.10 | 0 B |
| OneAtATime | 100000 | 1.626 ns | 1.00 | 1.638 ns | 1.00 | 0 B |
| Prefetched8 | 100000 | 2.328 ns | 1.43 | 2.215 ns | 1.35 | 0 B |
| Prefetched16 | 100000 | 2.192 ns | 1.35 | 2.346 ns | 1.44 | 0 B |
| Prefetched32 | 100000 | 2.225 ns | 1.37 | 2.285 ns | 1.40 | 0 B |
| Vertical | 100000 | 4.980 ns | 3.07 | 4.912 ns | 3.00 | 0 B |
| VerticalRefill | 100000 | 5.543 ns | 3.41 | 5.821 ns | 3.56 | 0 B |
| OneAtATime | 16000000 | 41.949 ns | 1.00 | 39.637 ns | 1.00 | 0 B |
| Prefetched8 | 16000000 | 33.660 ns | 0.80 | 33.272 ns | 0.84 | 0 B |
| Prefetched16 | 16000000 | 31.272 ns | 0.75 | 29.597 ns | 0.75 | 0 B |
| Prefetched32 | 16000000 | 32.896 ns | 0.79 | 31.458 ns | 0.79 | 0 B |
| Vertical | 16000000 | 52.224 ns | 1.25 | 52.067 ns | 1.32 | 0 B |
| VerticalRefill | 16000000 | 61.332 ns | 1.47 | 64.341 ns | 1.63 | 0 B |

Verdict: the vertical forms lose at every size, by more where misses dominate (1.25-1.63x slower at 16M): a gather
waits for its slowest lane and issues its eight loads no sooner than the out-of-order core issues eight scalar probes,
and refill adds its compress / expand work per step. Scalar group prefetching is the form that pays at 16M (1.33x at
k = 16, both rounds: bar met there) and costs 1.3-1.6x where the table is in cache (1k, 100k: the prefetch's hash and
two instructions per key with nothing to hide). Neither is built: `ValueIndex` / `UniqueIndex` have no batch probe
(each `TryFind` / `Find` / `Get` is one key whose successor the index cannot see), so a prefetching probe needs keys
known ahead, which only generated code has (a `[ForEach]` method calling `lookup.TryFind(key.Field)` with the key in
a component: the generator would prefetch the probe slot of row i + 16, as for lookups in `borrowed-ideas.md` item 1),
and it must be gated by the table's size (slots past the last-level cache) or it slows every small index. What would
change the verdict: an index probed by keys of a component in a pass over millions of rows (a join), measured with the
gate; the vertical forms stay dropped.

### 6.4 What the met bars need beyond the prototypes

- **L1 (ordered rows + blocks):** the permutation of every column and the entity slots under the structural gate with
  a `StorageVersion` bump, the block boxes kept with the table, the block pairs built from a grid of blocks (the
  prototype's tree walk is most of the sort's cost), the drift check against the margin, and the sort's cost on wide
  tables measured.
- **L2 (pair form):** the generator writes the 8 x 8 kernel from a method over one pair (the prototype's
  `ClusterRows.Pair` is the body it would inline), masks for cutoff and self, sums into i; the nearest form for
  `NearestPlayer` and the zombie search on the same blocks, the zombie case only with the sort on L1's cadence.
- **L3 (compress only):** `CompressStore` where AVX-512VL is present and the permutation table on AVX2 where the core
  turns a mask into rows (marked rows, structural changes from a vector mask, Emit), replacing the per-64
  `Lanes.Compact` walk. Batch probes are not built.

### 6.5 What L2 builds on (L1 and L3 as built)

- **The order.** The pair form's setup calls `World.OrderRows<T, TOrder>(axes, cell, interval, margin)` (internal)
  for the component it reads positions from: `TOrder : IRowOrder<T>` returns `RowKeys.Morton` of the position
  quantized by the cutoff and the point the boxes cover. The generator writes that struct; `[OrderBy]` only overrides.
- **Blocks.** `RowBlocks.Size` rows (8 with `Vector256` hardware, 4 otherwise), so the kernel is one register per
  axis on every machine: L2 writes the kernel over `Size`, never a fixed 8. `world.Blocks<T>()` / `EntitySpan.Blocks`
  give the boxes, a block's rows, the columns, ids and presence; `SortedRows` bounds the blocks, and rows past it
  (created since the sort) need the row loop.
- **Block pairs.** `RowBlocks.Pairs(reach, into)` / `Pairs(other, reach, into)` (a sweep over the boxes) rebuilt on
  the sort's cadence; the margin keeps them valid between sorts.
- **The kernel.** `benchmarks/EasyEcs.Benchmark/Locality/CoreClusters.cs` is the body L2 generates: the 8 x 8 step
  over the table's own columns in sorted order, within 0-15 % of the prototype over copied side arrays, and 11-14x
  (crowd) / 5-6x (sparse) faster than phase 0's grid through lookups with the sort and the pairs every 10 ticks.
- **Row lists.** `Lanes.Rows(mask, first, destination)` for any row list the generated code needs.

## 7. Order against the work already running

- Starts after **gather-always** reports (it changes `Lookup`/`LookupLanes`, and 3.1 changes what it measures) and
  after **WP0b** merges (L2 adds Kernel forms; the 32-IL-locals limit applies to the pair kernel too).
- `inner-loops.md` section 6 deferred "gathers through index lists (MD neighbor lists)": this plan replaces that item.
- #164 (columns in NativeMemory) is independent; L1's permutation is measured on whichever columns are current.
