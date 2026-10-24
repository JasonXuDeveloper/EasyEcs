---
name: audit-perf
description: "Look for the next performance win in EasyEcs - borrow from Burst, CUDA, stencil codes, OpenMP, MPI, OS schedulers, databases, HPC runtimes (Halide, Kokkos, TBB, Legion), LLM serving (vLLM) and newer .NET (10) features, find where the runtime or the generated code leaves speed on the table, and prove every change with BenchmarkDotNet. Use for a performance round, before a release, or when asked to \"optimize further\" or to research an idea. Triggers: \"faster\", \"optimize\", BenchmarkDotNet, A/B, regression, allocation, NativeMemory, mimalloc, 性能, 优化, 更快, 基准, 研究一下."
metadata:
  internal: true
---

# Performance research and audit

Performance is the first constraint (AGENTS.md); GC is part of it. This skill finds the next win and proves it. It owns
no files: a finding is built by the skill that owns its files (`audit-core`, `audit-generator`, `audit-vector-loops`) or in
its own worktree.

## Where wins come from

1. **The user's code, rewritten by the generator (Burst's idea).** The user writes the plain form; the generator
   emits the fast one. Look at what users write by hand that a generated form would run faster:
   - per-row scalar loops that `Kernel.Vectorize` could turn into `Vector<T>` loops (`audit-vector-loops` has what
     qualifies, side effects and the proof): count the EECS055 / EECS056
     reasons over `samples/`, `BattleServer.cs`, `WorldSimulation.cs` and the docs examples; the most frequent reason
     is the next form to support (mixed lane widths, struct fields, masks, wide rows are done: D68, D90);
   - work a parameter does per chunk (`Reduce<T>`, `Emit<T>`, `Scratch<T>`, `Lookup<T>`, `Tile<,>`, `EntityRandom`):
     the hand-written versions are what EECS086-089 report;
   - schedule choices the generator can make: `Parallel`, `ChunkRows`, `Slices`, `[UpdateInterval]`, `[Consume]`,
     one-entity jobs (`[ForEntity]`), runs of one system set across stages.
2. **Ideas from other fields**, each checked against what EasyEcs already does (decisions D67-D90):
   - **GPU / CUDA**: structure of arrays, coalesced loads (column loads, interleaved fields), warp-style masks (lane
     masks of passing rows), shared-memory tiles (`Tile<,>` with a halo), reductions in a fixed order;
   - **stencil codes**: halos, ping-pong buffers, sleeping tiles (`ChangedEdges`), tiled loops that stay in L1/L2;
   - **OpenMP**: `schedule(dynamic, n)` (chunk claiming, `ChunkRows`), `threadprivate` (`Scratch<T>`), `reduction`
     (`Reduce<T>`), `depend` (stages instead of a task graph, D76);
   - **MPI**: one-sided reads (`Lookup<T>`), moving owned data between ranks (`MoveEntities`, `SaveEntities`);
   - **OS schedulers and runtimes**: spin-then-park, few spinning threads (Go's scheduler), LIFO wake (warm caches),
     work stealing, futex-style waits, per-core state, false sharing (padding to 64 bytes);
   - **databases**: column stores, zone maps (`WordTicks` / `BlockTicks`: skip unwritten words and blocks), bulk ops
     by whole words (`SetAll`); vectorized execution (MonetDB/X100, DuckDB: batches of rows per call), morsel-driven
     parallelism (HyPer: small chunks claimed by threads, NUMA-aware), compiled queries (HyPer: the generator's job),
     radix-partitioned joins (sort lookups by target table before chasing them);
   - **LLM serving (vLLM and friends)**: paged memory (PagedAttention: fixed-size blocks and a block table, so
     variable-length data per entity, `DynamicBuffer<T>`, grows without fragmentation and blocks are shared
     copy-on-write by clones and prefabs), prefix sharing (one copy of identical values: shared or flyweight
     components), continuous batching (schedule per iteration: work that arrives mid-tick joins the next pass instead
     of waiting for the next tick), CUDA graphs (record a schedule once, replay it: the system set's switch, a cached
     stage plan), preemption and swapping (cold entities saved out and loaded back: `SaveEntities` for idle regions),
     FlashAttention (tiling and fusing passes that touch the same data: fuse systems over one table into one walk);
   - **HPC runtimes and compilers**: Halide / TVM (the algorithm apart from its schedule; autotuning the schedule:
     `ChunkRows`, `Parallel`, `Slices` chosen from measurements), Triton (block-level programs with autotuned block
     sizes), Kokkos / RAJA (layout and execution policy as parameters: one method, several layouts), Legion / Regent
     (tasks declare privileges on regions and the runtime finds the parallelism: our read/write sets), Charm++
     (over-decomposition and migratable objects for load balance: entities moved between worlds), TBB (work stealing,
     the affinity partitioner: the same chunk to the same thread each tick for warm caches), polyhedral loop
     optimizers (fusion and tiling of nested loops), GotoBLAS (cache blocking per level), ISPC (SPMD over SIMD lanes:
     the vector loops' model), NUMA first-touch placement, software prefetch for index chasing (`Lookup<T>`);
   - **low-latency systems**: LMAX Disruptor (ring buffers, one writer per sequence, mechanical sympathy), Seastar
     (shard-per-core, share nothing: a world per core), DPDK / io_uring (batching, busy polling), RCU (readers never
     wait: the copy-on-write archetype lists), mimalloc / jemalloc arenas (per-thread pools).
3. **.NET 10**: `Vector128/256/512` and `Avx512*` (compress, permute), `TensorPrimitives`, `InlineArray`,
   `UnsafeAccessor` (keeps PGO: checked), `AlternateLookup`, `FrozenDictionary`, `System.Threading.Lock`, `ref struct`
   generics (`allows ref struct`), C# 14 extension members, Native AOT (`IlcInstructionSet`, D108), dynamic PGO and
   guarded devirtualization. Read the runtime's source before assuming a behavior (thread pool spin, GC modes).
4. **The runtime's own overhead**: what a system costs per update apart from its rows (`SystemDispatchBenchmark`),
   what a pass costs per table and per word (`SparseBenchmark`), per chunk (`ParallelForEachBenchmark`), per stage
   (`ParallelSystemsBenchmark`), per structural change (`OperationBenchmark`), per world (`MultiWorldBenchmark`).

## Method

1. **Research first**: write what the idea is, where it comes from, what EasyEcs does today, what it would change and
   which benchmark shows it. Read the decisions and the roadmap's "Dropped" list: a dropped idea comes back only
   with a new, measured reason.
2. **Prototype in a worktree**; keep the public API unless the owner approves a change.
3. **Measure with BenchmarkDotNet only** (`feature`, part 6):
   - A/B copies as git worktrees outside the repo tree (scratchpad); two rounds, interleaved;
   - every run under `flock /tmp/bench.lock`, nothing else running (agents' builds and samples included): parallel
     rows swing by 2x on a loaded machine; take the load average with the results;
   - the feature's benchmark plus WorldSimulation, BattleServer, MultiWorld and the neighbors; 0 B in steady state;
   - a runtime change (threads, memory, intrinsics) also works on every platform (`audit-core`, Platforms): measure on
     x64, check arm64, AOT and the single-threaded WebAssembly demo still run;
   - new hot loops: Tier1 asm with `tools/jitdisasm.sh` (no call in the loop body, no unexpected range check);
   - `tools/bench.sh <classes> --sync` runs classes one process each (resumable, under the lock) and merges them into
     the site's numbers, listing rows that moved 15 % or allocate differently; `--site` runs every class the site
     shows, `--all` adds the A/B-only classes. A full run takes hours: run what the change touches.
4. **Ship only a net win** (time and allocations, no regression elsewhere). A loss or a wash is reverted and gets a
   decision record (what was tried, the numbers, why not); the roadmap's "Dropped" list names it.
5. A kept change gets: tests (behaviour, 0 B allocation test, bit-identical at 1 and 4 threads, vector equals scalar),
   a benchmark for what was not covered, a decision record, changelog, and the docs' "How to use it fast" / "What it
   costs" sections and `api.mdx` complexity rows when they change.

## Report

A table: idea | source | what EasyEcs does now | expected gain | benchmark | result (numbers) | verdict (build /
drop / later). Ideas for later go to `docs/content/roadmap.mdx`.
