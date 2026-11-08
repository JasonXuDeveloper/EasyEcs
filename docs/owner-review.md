# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## The generated ping-pong step against step + commit by hand (feat-tiles-pingpong, merged 2026-10-29)

**Owner (2026-10-29): merge it, then fix the reads.** After the Tier1 review (LoadPlane, an unrolled plane Find,
nint cursor offsets, a Facing table, parallel dense swaps, a woken walk per color with the `TileWalk.Share` gate) the
`ref Tile` step beats the hand-written form past the last-level cache (Blur9 512x512: 198 against 255-260 ms at 1
thread, 72 against 88-92 ms at 4) and in sparse fields (about 2x at every share of awake tiles), and still loses in
cache (Blur9 32x32 529 against 473-505 us, Laplacian 580 against 436-476 us at 1 thread; one `--dev` round each). The
gap is the Tile API's checked column loads, which the by-value form pays too: backlog "Tile reads checked once".
VoxelServer TickBenchmark base against head, 4 interleaved rounds: within noise, 0 B.

What would change the verdict: nothing left to decide; the read fix is scheduled.

## The tile handed out by Enter: one thread-static read less per tile (feat-owner-calls (e), 2026-10-31)

**Built and reverted** (80fbdcb, reverted in the same branch). `TileSteps.Enter(tile, out Tile stepped)` read the
thread's frame once and handed it to the method's tile, `Sweep` and `Leave`, the untried design of the reverted
Begin/Frame split. Tier1 listings of TileActive's PingPong pass, base against head:

| Method (Tier1) | Base | Head |
| --- | --- | --- |
| `TileSteps.Enter` | 709 B, 173 instructions, 8 stack operands | 802 B, 196 instructions, 15 stack operands |
| `TileSteps.Leave` | 541 B, 154 instructions, 10 calls | 469 B, 136 instructions, 7 calls |
| `StepJob0:Step` (the job) | 1187 B, 291 instructions, 18 calls | 1173 B, 288 instructions, 17 calls |
| `WokenRun.Execute` (the walk over awake tiles) | 2509 B, 569 instructions | 2436 B, 542 instructions |
| `GETDYNAMIC_GCTHREADSTATIC_BASE` calls in Enter + Leave + job | 3 | 2 |

```
; head, Enter: `stepped = default` zeroes the 48-byte out tile, then the tile is built on the stack and copied out
mov      qword ptr [rbx], rsi          ; ... six stores to [rbx] .. [rbx+0x28]
mov      gword ptr [rbp-0x60], r15     ; the frame into the stack copy
vmovdqu  ymm0, ymmword ptr [rbp-0x60]
vmovdqu  ymmword ptr [rbx], ymm0       ; copied out to the caller's tile
vmovdqu  xmm0, xmmword ptr [rbp-0x40]
vmovdqu  xmmword ptr [rbx+0x20], xmm0
```

What it removes is one thread-static base read per stepped tile (a helper call of a few nanoseconds against a step of
microseconds per tile); what it adds is the out tile zeroed, built on the stack and copied through memory (an out
parameter is a pointer, never a register), seven more stack operands in Enter. TileActive PingPong and TileWoken Woken in two interleaved rounds flipped sign between rounds (a wash within the
machine's noise). The plain `Enter(tile)` stays. What would change the verdict: a pass whose tiles step in well under
a microsecond (a 4x4 tile of a cheap rule), where one helper call per tile would be a measurable share; the
generator could then pass the frame without the out tile (a field of the job read once per chunk).

## Restart and Sweep: two names for the owner (feat-field-gaps, 2026-10-30)

**Proposed by** backlog item 8c. Settling VoxelServer's scent with `[GaussSeidel]` needed two things the step did not
have: start each step from nothing (a scent whose player left otherwise lingers and fades a step per sweep: count to
infinity) and know which sweep runs (turn the row order every other sweep: a field crosses the tile both ways in two
sweeps). Built as two members of `Tile<TCell, TTile>`, provisional names, each one identifier to rename:

| Name | What | Considered | Why this one |
| --- | --- | --- | --- |
| `tile.Restart(TCell value = default)` | the first sweep starts from `value` in the tile's own cells, the halo kept | `Reset`, `Clear`, `StartFrom`, `ExchangeHalo(..., restart: true)`, `[GaussSeidel(FromZero = true)]` | the relaxation literature restarts an iteration; an attribute could not carry a TCell (a minimum restarts from `int.MaxValue`); a parameter of ExchangeHalo would grow its four optional parameters to five |
| `tile.Sweep` (int) | 0 in the first sweep of a step, one more after each | `Sweeps`, `SweepIndex`, `FirstSweep` (bool) | Gauss-Seidel's own word (`MaxSweeps` counts the same thing); a bool cannot turn the direction |

What would change the verdict: the owner prefers other names (rename before the release; nothing shipped).

## Not built in 8c: a lane-shift helper, Rows() turning by itself (feat-field-gaps, 2026-10-30)

- **A lane-shift helper** (`FromEast(v, n)` / `FromWest(v, n)` of the scent's in-row scan). Dropped: it shifts a vector
  the method computed, not cells the tile holds (those are `Load128(x ± n)` already), so it is a general SIMD helper,
  one `Vector128.Shuffle(v, Vector128<byte>.Indices ± n)` (PSHUFB, NEON TBL), not tile or ECS code; it saves the sample
  two one-line helpers. What would change the verdict: a second sample or a docs example writing the same shifts.
- **`Rows()` walking odd sweeps backwards by itself** in a `[GaussSeidel]` step. **Built, measured and reverted**
  (feat-owner-calls (f), e58fb0b, reverted in the same branch). An internal switch turned odd sweeps of `Rows()` from
  the last row back; `TileTurnBenchmark` settled a glow written with `Rows()` while its springs move, forwards against
  turned, and in a third process set up as the turned one but walking forwards (Control). Rows and sweeps per update
  counted on 1 thread: 4 springs visit 9 % (hop 1) and 18 % (hop 8) fewer rows turned (sweeps 281 to 262, 1116 to
  922); 256 springs visit the same rows in the same sweeps both ways (every row stays active, about 7-8 sweeps a
  step). Three rounds (us, 1 thread, forwards / turned / control in the third):

  | Springs, hop | Round 1 | Round 2 | Round 3 |
  | --- | --- | --- | --- |
  | 4, 1 | 262 / 271 | 260 / 263 | 273 / 270 / 277 |
  | 4, 8 | 1016 / 958 | 994 / 952 | 1026 / 955 / 1013 |
  | 256, 1 | 976 / 1028 | 951 / 1082 | 982 / 1037 / 1015 |
  | 256, 8 | 2225 / 2303 | 2226 / 2396 | 2203 / 2462 / 2175 |

  Turned wins 4-7 % where it saves rows and loses 3-14 % where it saves none; Control measures as Forwards, so the
  loss is the turned walk, not where the setup put the tables. The Tier1 walk differs by one predicted branch and an
  `lzcnt` for a `tzcnt` per row, and the work counted is identical, which leaves the descending order of the rows'
  addresses within a sweep against the hardware prefetcher; this VM has no hardware counters to show it. A method that
  pays for turning already turns with `tile.Sweep` (the scent). What would change the verdict: a field written with
  `Rows()` whose sweeps fall by far more than the 18 % here when turned, or a machine with counters showing the loss
  is not the access order.

## Page faults: what was tried and not kept (feat-page-faults, 2026-10-31)

The owner asked for fewer page faults on top of huge pages. Counted first (PageFaultBenchmark with the
`PageFaultDiagnoser` columns: `minflt` / `stime` of the process, `thp_fault_alloc` and the other THP counters of the
machine, per op). Kept: the arrays of a user's reservation are faulted in at the reservation (`MADV_POPULATE_WRITE`, see the
decision draft in `docs/plans/docs-todos.md`, "feat-page-faults"). Not kept, each with the cause found:

- **Prefault to cut the cost of huge-page faults.** One `MADV_POPULATE_WRITE` call costs what the first touch costs
  with 2 MiB pages: 128 MiB of memory freed 4 s earlier, 536 ms touched against 572 ms populated (PrefaultTouchBenchmark,
  the spread 41-50 ms). The time is the host's, not the guest's: the reference VM (Firecracker, virtio-balloon free
  page reporting at order 9) hands free 2 MiB blocks back to its host, a huge page needs exactly such a block, and the
  host faults it back 4 KiB at a time (stime 187 ms of the 536). With 4 KiB pages the same touch takes 67 ms (most
  4 KiB allocations come from blocks never reported), and populating them saves the trap per page (53 ms, 1.27x). So
  prefault moves the faults (what is kept: out of the tick that fills a reservation) but cannot make a huge page
  cheaper on such a VM. What would change the verdict: a host that does not report free pages (bare metal, most
  cloud VMs), where a huge page faults at memory speed and nothing is left to cut.
- **Prefaulting every sized array** (copies included: a load's, a restore's and a snapshot's tables, the rows of
  `CreateEntities`). A load at 1M entities with huge pages was 1.04-1.06x slower in two rounds: populating zeroes the
  whole array before the copy writes it, out of the cache by then, where a first-touch fault zeroes each page right
  before the copy writes it. Nothing is gained either, since the same call writes every row: the faults stay in
  the call. Kept instead: only a user's reservation (rows written later) is faulted in. The resident-window walk
  (populating memory the GC handed back resident) was the other cost found: skipped through `mincore`.
- **Keeping a disposed world's columns for the next world** (a process-wide spare list of exact lengths, 1 GiB at most,
  given at `Dispose`, taken by `ComponentInfo.Create`). The allocations halved (Create 16M 732 → 366 MB) but the faults
  did not move (31,252 against 31,257 per op) and the ops were no faster (Create 16M 1.06x slower, InstantiateEach 16M
  1.08x slower, `--dev`): the GC already hands a freed large array's memory back resident (a 128 MiB pinned array
  allocated again after a collection faults 0 pages), so the spares only replaced the GC's own reuse and added a
  clear. The faults left come from the other arrays (entity records, a table's entity rows, the free-id list: other
  types, which a typed spare cannot serve) and from the GC committing a new region while the previous world is not yet
  collected. Reusing a column inside one world (its next resize) never meets a spare of the size: doubling only grows.
  What would change the verdict: a workload that disposes and recreates worlds of one shape faster than the GC
  collects (a match server whose matches are past the large object heap's budget), measured.
- **The GC's settings** (host choices, never set by the library; written up for users in the docs to-dos): at 16M
  entities `GCRetainVM` changed no count (the faults are not memory the GC decommitted), `GCgen0size` none (the
  arrays are on the large and pinned heaps), `GCConserveMemory=5` added 20-65 % faults and made Grow, Load, Capture
  and InstantiateEach 1.2-1.6x slower, the segments GC (`GCName=libclrgc.so`) tripled the faults of a load and an
  InstantiateEach (it releases large segments at once) and made Capture 1.5x and InstantiateEach 1.2x slower
  (`--dev`, one launch each).

## `[Accumulate]` users sharing a stage (backlog Later, voxel-rewrite.md GA2; feat-later-accumulate, 2026-11-02)

**Not built: no stage is lost to an `[Accumulate]` singleton.** The proposal: systems that only add into one
`[Accumulate]` singleton do not conflict, share a stage, and their pass results are combined into the singleton in
registration order when the stage ends. The backlog's condition was the VoxelServer's `--schedule` showing stages lost
to its `Stats`. It shows none (784260d, `dotnet run -c Release --project samples/VoxelServer -- --schedule`):

- 45 systems in 42 stages; 29 systems run alone (an `Entity` or `World` parameter, an `OnUpdate`, a `[Consume]` or
  `MaxRows` method, no `[ForEach]`), and every one of them that writes `Stats` runs alone for those reasons, not
  for `Stats` (AoiView, AoiChunk, AoiSpawn, AoiDespawn, ChunkRead, ChunkGenerate, ChunkSave, ZombieDeath, PlayerRespawn,
  Dig, Pickup, ItemExpiry, NightSpawn, ZombieAttack, SendPackets, Tally).
- The three systems that add to a total without running alone are WaterCommit (`Filled`), Float (`Carried`) and
  SendChunks (`Stats`). Their neighbours in registration order run alone (WaterSleep, ZombieAttack, PlayerRespawn,
  AoiDespawn, ChunkSent) or conflict on components (Float before Physics: `Position`, `Velocity`). Among the
  "registration order only" pairs, none names `Stats`, `Filled` or `Carried`.
- After the rewrite (step 2 of voxel-rewrite.md: `Filled` and `Carried` folded into `[Accumulate] Stats`), the same
  holds: WaterCommit is deleted by the ping-pong step, Float still sits between a system that runs alone and one it
  conflicts with on components, and ZombieDeath, which the rewrite turns into a vector loop with marked rows, runs alone
  as such a loop does. BattleServer's `Battle` is written by one non-alone system
  (Replication) and by `OnUpdate` / `Entity` methods.

So the cause of the VoxelServer's serial stages is systems that run alone, not totals; a shared-total rule would merge
no stage there. What would change the verdict: a world where two consecutive systems that do not run alone conflict
only on one `[Accumulate]` singleton (the schedule's "Registration order only" line names just that singleton), for
example a sample whose per-kind counters move from separate singletons into one total. The design stays as GA2 states
it (each pass's result kept per system, combined in registration order at the stage end, so the total is the bits of
the serial run).

## Machine-bound items of #159: thread pinning, the helpers' queue line (feat-later-research, 2026-11-03)

**Not built: this shared 4-core VM cannot show them.** Two low-latency ideas of `borrowed-ideas.md` "#159 remainder":

- **The helpers' queue counter apart from its gate** (`Workers`: `_queued`, which spinning helpers read, shares a
  cache line with the `SpinGate` every `Post` and `Take` compare-and-swaps). The bound is one or two cache-line
  transfers per post and per take, about 0.1-0.3 µs of a pass that wakes helpers (5 µs and more, D22 / D130), under
  1 %, where parallel classes on this VM move 10-40 % between runs of the same code.
- **Pinning the helper threads to cores** (`sched_setaffinity`, `isolcpus`): needs cores no other process runs on.

What would change the verdict: a quiet machine with 8 or more cores (no SMT sharing, or with it, to see both). Run
`ParallelForBenchmark` (LightParallelFor at 10,000 records) and `ParallelSystemsBenchmark` with `--launchCount 6` on
base and on base plus the queue counter on a line of its own (a padded struct holding `Gate` at one line and `_queued`
at the next); a gain past the spread of the six launches ships. Pinning needs an option the owner would name
(`WorldOptions` has no thread settings today), so it is a public API question first.

## The core's columns on native memory (NativeMemory, mimalloc; feat-later-research, 2026-11-03)

**Not built: the C runtime's allocator is slower for large arrays, and an allocator that would be faster only helps
world creation.** `NativeColumnsBenchmark` (borrowed-ideas.md "#159 remainder", item 2) allocated a world's five
unmanaged arrays (48 bytes a row), wrote them once and freed them: `NativeMemory.AlignedAlloc` (glibc) took 1.4x the
pinned arrays' time at 1M rows and 2.4x at 4M (3-6x the minor faults: glibc returns large blocks to the kernel and
faults them back; the GC keeps freed memory resident), equal at 100,000. With `GC.AddMemoryPressure` (which a library
holding native memory owes the GC) the 1M case was sometimes 4x faster (memory reused resident, 0 faults) and
sometimes not (9.3 +- 7.1 ms): glibc's heuristics decide. The prize is the gen2 collections the pinned-heap budget
starts while a fresh world fills (D28), at most a quarter of `WorldCreate` at 1M; steady ticks allocate nothing
either way. Moving the columns means every column stops being an `Array` (snapshots, saves, the generated jobs'
spans, `Archetype.Row0`), freed by `Dispose` and a finalizer.

What would change the verdict: a workload that creates and disposes large worlds often (a zone server spinning up
instances), measured with `NativeColumnsBenchmark` and `PageFaultBenchmark`; then the options are (a) an arena of
EasyEcs's own that keeps freed column memory resident across worlds (the measured upside without a native
dependency), (b) mimalloc as a native dependency (one binary per platform: x64, arm64, WebAssembly, iOS / Android
through Godot), (c) leave the pinned heap and document `GCSettings` / `GCConserveMemory` for such hosts.

## Atan2 and Asin lowerings: not built (feat-inner-loops-rest, 2026-11-07)

**Not built: .NET 10 has no vector form of them, and D127 rules out a polynomial of the generator's own.** The roadmap
item of D90 asked for Atan2 / Asin / Pow. `Pow` of floats by any exponent ships under `FloatMode.Fast` as
`Exp(y * Log(x))` in doubles through .NET's vector `Exp` / `Log` (`LaneMath.Pow`, within 2 ulps over the tested
inputs; lanes with x <= 0, an infinite x or y, or NaN run the method), which stays inside D127. `Atan2` and `Asin` have
no such composition from the vector functions .NET 10 has (`Sin`, `Cos`, `Exp`, `Log`, `Log2`, `Hypot`): the
inverses need an argument reduction and a minimax polynomial, which is the code D127 says the generator does not
write (a precision contract EasyEcs would own on every ISA, ARM64 included).

What would change the verdict: a .NET release with `Vector512.Atan2` / `Asin` (the generator then lowers them as it
does `Sin`), or the owner accepting a polynomial of EasyEcs's own under `FloatMode.Fast` with a stated ulp bound (the
exhaustive `LaneMathTests` sweep would prove it).

## VoxelServer's Dig and Gone stay scalar (feat-inner-loops-rest WP8, 2026-11-07)

**Inherent, no rewrite vectorizes them.** `ItemSystem.Dig` (Items.cs) finds the chunk under the player through
`world.ChunkIndex.TryGet` (an index probe, scalar by D131), writes another entity's buffer (`chunk.WriteBlock()`),
instantiates an item with values computed per row and sets an indexed component (`SetScore`): each is a side effect
the vector loop does not take, and the rows are the players that dig this tick (a handful). `AoiDespawnSystem.Gone`
(Aoi.cs) counts the viewers of a chunk through the same index probe (`Aoi.ViewersOf`); its unconditional
`RemoveVisible` would be a marked structural change, but the probe keeps the method scalar, and the rows are the
entities despawned this tick. Splitting the remove into a `[Consume]`-like pass would move one bulk remove for a
second walk over those few rows: nothing to win. The schedule names another reason for `Gone` (and `Moved`): "field
Visible.Placed is a bool among 3 fields: a vector loop takes a bool in a component of its own". It is the reason
the translation meets first (Visible is read as lanes before the `World` parameter is weighed), and it carries no
split advice (WP8 (e) defers that advice past an inherent reason), but it is not a fix either: with `Placed` split out
the method would stop at "it takes the World". Naming the inherent parameter first for every lane reason would change
the reported reason of many methods (and their tests); left as is, recorded here.

What would change the verdict on the order: users splitting a component on the advice of a schedule line and finding
another reason behind it.

What would change the verdict: index probes in vector loops (D131 keeps them scalar: a probe per lane is a hash walk
per lane), or a workload where thousands of rows dig or despawn per tick.

## A masked AVX-512 tail for flat element loops: dropped (feat-inner-loops-rest (b), 2026-11-08)

**Built, measured, reverted: slower at every short length.** The form ran a flat element loop's elements past its last
whole vector through masked loads and stores (`Interleaved.LoadPart` / `StorePart` over `Vector512.LoadUnsafe` under a
mask) inside the `__Elements{k}` helper, and let a loop shorter than one vector enter the helper where Vector512 is
hardware. `FlatTailBenchmark` (sparks of two floats scaled and offset in place, the generated form, BenchmarkDotNet
A/B on this machine, AVX-512):

| Elements | Base (loop as written past the last vector) | Masked tail |
|---|---|---|
| 4 | 64 µs | 167 µs |
| 12 | 80 µs | 183 µs |
| 20 | 102 µs | 217 µs |
| 64 | 256 µs | 248 µs |

The 4-element case, where the masked block never ran (8 floats, half of one Vector512), lost as much as the others, so
the cost is not the masked moves but the call: with the guard widened to any length every row now calls the
NoInlining helper and pays its frame (pinned locals, the Vector512 setup and the mask computed per call), where the base
build's guard keeps a short buffer in the loop as written, inline. Kept from the work: the fix that puts the helpers'
`#line hidden` on a line of its own (a method body of one line produced CS1040), with its test.

What would change the verdict: elements long enough that most rows already enter the helper (then only the tail's
cost counts: 64 elements came out even), or a helper the JIT inlines into the job (the frame goes with the call).

## Masked words of wide rows: not built (feat-inner-loops-rest (g), 2026-11-08)

**Not attempted in this branch: the code is in the halo work's files.** D90's roadmap item lists masked words for
narrow lanes and for wide rows. Narrow lanes ship (`Interleaved.Lanes8` / `Lanes16` and their In512 forms, 5x faster in
`VectorFormsBenchmark.MaskedNarrow`). A wide component's loop (`Kernel.Wide.cs`, `Interleaved.Wide.cs`) runs its
masked words through the method today; giving it lane masks means changing both files, which the concurrent
halos branch owns. What would change the verdict: the halos branch merged, then the same lane masks over the wide
loop's rows (4 per 128-bit vector on ARM64, a permute tree's rows on AVX-512), measured on a wide component with holes.
