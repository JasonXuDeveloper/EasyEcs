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
   - the machine's speed moves about 13 % between sessions: compare variants only within one BenchmarkDotNet round,
     interleaved, never a number against one from another session;
   - `DOTNET_JitStdOutFile` listings of concurrent processes interleave: one listing file per process; after deleting
     the generated-files folder an incremental build does not write it again (touch a source file);
   - two jobs (`BenchmarkConfig.cs`): `--dev` (1 process, 3 x 150 ms warmups, 5 iterations, about a fifth of the
     time) while trying ideas and screening, holding the lock for minutes instead of hours; the default job
     (2 processes, two rounds) for every keep / revert decision and every number recorded. Both keep tiering and
     dynamic PGO on and start call counting at once, so the warmups reach Tier1 code; never shorten past that with
     `ShortRun`, `--runOncePerIteration`, `InProcess` toolchains or `TieredCompilation=0`, which measure other code.
     Filter to the cases a change touches; a full class is for the decision run;
   - `--dev` screens of parallel benchmarks on this VM put cases 1.1-1.5x apart in both rounds on byte-identical code:
     before calling a parallel regression, read the screen against a class whose code did not change;
   - check `df` before a long run: a full disk crashes a publish or a round with IOException (exit 134);
   - never wrap a script that takes the bench lock itself (`tools/bench.sh`) in another `flock`: it deadlocks against
     another agent's exclusive request;
   - A/B copies as git worktrees or `git archive` snapshots outside the repo tree (scratchpad), frozen while they run:
     BenchmarkDotNet rebuilds from source, so a tree someone edits changes under the run; two rounds, interleaved; a
     run past about 2 h is split into one background job per round;
   - GeneratorBenchmark's corpus holds gameplay code (structural calls, generated writers, buffers), or the
     analyzer's slow paths never run in it;
   - every run under `flock /tmp/bench.lock`, nothing else running (agents' builds and samples included): parallel
     rows swing by 2x on a loaded machine; take the load average at the start and the end of each class;
   - a size meant to miss the cache is checked against this machine's last-level cache (`lscpu`: 260 MiB here, so
     1M rows sit in it; record the LLC with the numbers), and the queries rotate through a set larger than it (the
     same 4,096 keys every invoke stay in L2 after the first);
   - no `[IterationSetup]` on nanosecond benchmarks (one invocation per iteration: 12 ns read where the truth is
     1.5 ns); `[Arguments]` on a method leaves the Ratio column empty: separate methods when the ratio matters;
   - the A/B baseline is the generated job, not a hand-written copy of it (5-15 % apart in the chase study);
   - in `namespace EasyEcs.Benchmark` a top-level type (`Position`) wins over `using static` and aliases: qualify a
     nested benchmark type named like one, and check every form's result in setup;
   - worktree agents: commands with shell variables, `awk` or a quoted filter under `setsid` are refused; write a
     launcher script with literal paths and run `setsid nohup ./script.sh`;
   - data a branch predictor can learn flatters branchy baselines: include a size past what it learns (262k rows
     against 4,096 repeated);
   - a prototype of a sort or a space structure is checked for its structure (keys sorted, boxes small), not only for
     equal values: a sort that ended in its scratch arrays kept every value right and made the kernel 500x slower;
   - long runs are scripts that resume (a done marker per class) and run one class per process, started with
     `setsid nohup` (tool background calls die at 2 h); a cut-off BDN log is read from its per-case "Mean =" lines;
   - a runtime choice between two flavors (micro-adaptivity) never trusts the first timed run (Tier0 code): it times
     several runs of each flavor, keeps the minimum, and re-measures when the winner's cost halves or doubles; one
     exploratory run after a long stretch of the other flavor is biased against it;
   - one lock for the machine, `/tmp/bench.lock` (`tools/bench.sh` takes it too), always with `flock -o` so no build
     server or test host it starts inherits the descriptor and holds the lock after the command ends. Benchmarks take it
     exclusively behind a gate they hold while waiting and running, `flock -o /tmp/bench.lock.gate flock -o -x
     /tmp/bench.lock <cmd>`; builds and tests pass the gate first, `flock -o /tmp/bench.lock.gate true && flock -o -s
     /tmp/bench.lock <cmd>`, so a stream of agents testing cannot starve a waiting benchmark. Builds also set
     `MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0` and `-p:UseSharedCompilation=false` (no server left
     running to load the machine; `dotnet build-server shutdown` stops one);
   - the lock is shared by every agent, so a queue is planned, not filled: benchmarks run in small batches (a class or
     a few methods), never hours queued at once; a branch whose code is done and only waits for its final checks goes
     first (the coordinator pauses the other queues' scripts with SIGSTOP and kills only their flock waiters that have
     not started measuring, then tells their agents to requeue), so finished work is never stuck behind measurements;
   - at a size where a gate is off, base and head run the same code: compare the gated form against `Off` inside one
     head binary (Auto vs Off), because base against head across processes moves 5-10% with where the setup lands in
     memory; and run one column with the gate forced on at the size just under the last-level cache, which shows
     which forms would pay there (a per-form gate) and which would not; a per-form gate measures every form that
     prefetches, not only the ones a plan lists, with the gate forced on at two sizes just past the L2 (262,144 and
     524,288 entities on a 2 MiB L2) as well as near the last-level cache (a parallel probe won only at 1M, a row chase
     missing from the list won at every size), and checks "inside the L2" at a size inside that form's own estimate
     (`DestroyEntities` counts a 64-byte line per entity, so its in-L2 size is 8,192, not 32,768);
   - sizes that depend on one machine's caches come from an environment variable through a `ParamsSource`
     (`EASYECS_BENCH_ENTITIES`), so a run can sit between that machine's L2 and last-level cache without changing the
     class's defaults;
   - a multi-stage prefetch is measured one stage at a time: a stage whose address needs a second lookup (a shared
     key's list of ids) cost 2.4x at 16M keys;
   - a pruning step that cuts work per item (a pair window skipped by its box) can lose to the tighter loop it
     replaces: measure it, and when it loses, revert it with its numbers in the commit message;
   - a benchmark whose first tick reorders the data (a pair pass sorts the table) takes its reference result after
     that tick;
   - long benchmark queues go in a resumable per-item script (one log per item, skipping done items): background
     waits are killed after 2 h;
   - quote BenchmarkDotNet filters (`--filter '*Iterate*'`): an unquoted pattern expands into file names in the shell
     and the run measures nothing;
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
