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
  key member; naming audit before it ships.
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

- **L1** ordered rows + blocks (core, `StorageVersion`, saves keep or drop the order, the analyzer's rule for a key
  that is not a plain value), tests: order after churn, refs across a re-sort, parallel passes, recycled ids.
- **L2** pair form (generator + Kernel, EECS055 reasons for bodies it cannot take, a code fix from a hand-written
  grid search where one is recognizable), tests bit for bit against the row loop.
- **L3** operators (core), each one where its benchmark said.
- **L4** examples: `ZombieTargetSystem`, `NearestPlayer`, the AOI rewritten in the new forms (exemplar pass), the
  battle server page.
- Decision records: one per package (ordered rows, the pair form, the operators), with what phase 0 dropped.

## 6. Results

(phase 0 fills this in)

## 7. Order against the work already running

- Starts after **gather-always** reports (it changes `Lookup`/`LookupLanes`, and 3.1 changes what it measures) and
  after **WP0b** merges (L2 adds Kernel forms; the 32-IL-locals limit applies to the pair kernel too).
- `inner-loops.md` section 6 deferred "gathers through index lists (MD neighbor lists)": this plan replaces that item.
- #164 (columns in NativeMemory) is independent; L1's permutation is measured on whichever columns are current.
