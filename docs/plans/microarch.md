# Plan: generated code tuned for the pipeline, the caches and the branch predictor (internal, not published)

The owner's rule: users write plain methods and never think about instruction-level parallelism, cache use or branch
prediction; the code the generators write takes care of them. This plan lists what the generated code already does,
what it does not, how to find where a kernel is bound, and the work packages. The rule itself lives in
`audit-generator` ("Tuned for the machine").

## 1. How to see where a loop is bound on this machine

- **No hardware counters here**: the VM exposes no `cpu` PMU (`/sys/bus/event_source/devices` has none), so no
  `perf stat` for IPC, branch misses or cache misses, and BenchmarkDotNet's hardware counters do not work on Linux.
- **Static: `llvm-mca` (installed, LLVM 18)** on the Tier1 listing of a job's loop (`tools/jitdisasm.sh`, the loop
  body between its label and its back edge, AT&T or Intel syntax with `-x86-asm-syntax=intel`), `-mcpu=icelake-server`
  (and `znver4` for the other vendor): it reports the bound of a loop (throughput per iteration, the critical
  dependency chain, port pressure). A loop whose bound is the chain (latency) gains from independent accumulators or
  unrolling; one bound by ports gains only from fewer instructions.
- **Dynamic: BenchmarkDotNet with controlled data**: branch cost shows as sorted against shuffled input of the same
  values; cache cost as 1k (L1) / 100k (L2/L3) / 1M+ (memory) rows of the same method; a chain as the time of one
  accumulator against two in a hand-written copy.

## 2. What the generated code already does

| Concern | Already there |
|---|---|
| Branches | conditions become masks in vector loops (no branch per row); a cold branch bails out of line (`NoInlining`); a masked vector tail; presence bits walked by words; data instead of flags keeps branches out of the rows (tags split tables) |
| Pipeline | vector loops; the 32-IL-locals budget so the JIT inlines (WP0b); permutation tables hoisted; no `ref` field across rows (a store per row, `SystemGenerator.cs` comment) |
| Caches | SoA columns aligned to 64 bytes; pass fusion (each row loaded once for several methods); chunks of 64-row multiples cut by measured cost (D130); per-thread scratch |
| Front end | cold paths out of line; WP0b moves each loop of an oversized Execute into its own method |

## 3. Candidates (each: the llvm-mca or data evidence first, then BenchmarkDotNet, two rounds, 0 B)

**Pipeline**
1. **Independent accumulators for reductions.** A `Reduce<T>` / sum in a vector loop adds into one vector per field:
   a chain of one `vaddps` (4 cycles) per vector while two can issue per cycle. Two to four accumulators per field,
   folded at the end, cut the chain. Exactness: lanes already reassociate a float sum (D68); more accumulators are
   the same kind of change, fixed by the code's shape (never by the thread count, D125), and the Precise / Fast rule
   of `audit-vector-loops` decides whether floats get it or integers only. Bar: a sum-bound method 1.5x at 100k.
2. **Unrolling short vector loops by two.** The JIT does not unroll; a body of a few instructions pays the loop
   overhead and one chain per vector. Two vectors per iteration where llvm-mca says the loop is latency-bound and the
   locals stay within 32 (WP0b's budget). Bar: 1.2x on the short kernels of `KernelBenchmark`, nothing slower.
3. **Hoisting what the JIT reloads.** Constants and table addresses the inlined runner reloads each iteration
   (no CSE across the big body, seen in the copy-asm work): written once into locals before the loop, within the
   locals budget. Proof: the Tier1 listing, then BDN.

**Branches**
4. **Scalar rows without branches.** A method that stays scalar with `if (c) x = a;` shapes: written as a select the
   JIT turns into `cmov` (its if-conversion takes only simple shapes); proven on shuffled against sorted data.
5. **Uniform masks.** Where a mask is all-true or all-false for long runs (a tag-like condition on a field), the
   "any lane" test before a masked block is a predictable branch; where it flips every vector it costs a miss per
   vector: measure both, and drop the test where the block is cheaper than a miss.
6. **The analyzer for conditions that should be data.** A method whose body is one `if` on a component's bool or
   enum field says to make it a tag (the ECS principle "data instead of flags") so the rows that do not match are not
   walked: check whether a rule exists (`audit-analyzer`), add it if not.

**Caches**
7. **Prefetching chases** (`borrowed-ideas.md` 1) for `Lookup` reads and relation walks.
8. **Streaming stores** (`borrowed-ideas.md` 5) for write-only columns of passes much larger than the cache.
9. **Wide components** already get the split suggestion (EECS055/091); check the walk of a fused group does not
   exceed the hardware prefetcher's streams (columns read at once), and split the walk if it does.

## 4. Work packages and order

- **M0** (one agent, after WP0b merges; Kernel files): llvm-mca over every kernel shape of `KernelBenchmark`,
  `BattleServer` and `WorldSimulation` jobs; a table "job, bound (chain / ports / memory), cycles per vector" in
  section 5; the method added to `audit-vector-loops`.
- **M1** candidates 1-3 (Kernel), **M2** 4-6 (generator + analyzer), **M3** 7-9 (core + Kernel, with
  `borrowed-ideas.md`). Each with its decision record; a failed bar goes to `docs/owner-review.md`.

## 5. Results

(M0 fills this in)
