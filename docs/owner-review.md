# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## Countdown timers as a generated form (Finish 1a scan, finish-scan.md item 2; owner, 2026-11-07: not built)

**Proposed by** the generated-members scan: a component that counts down every tick, the entity or the component
going when it runs out, appears at 21 sites (the template's and the demo's `LifetimeSystem`, GameLoop's `Simulation`,
BattleServer's Expire / Burn / Thaw / cooldown, the demo's Fade and Burst, and pages: structural-changes,
project-structure, getting-started, testing, change-tracking, cleanup-and-disable, diagnostics). The generated form
would be a field attribute (`record struct Lifetime([Expires(Then = Expire.Destroy)] int Ticks)`) and a generated
system the user still registers (`world.AddSystem<LifetimeExpiry>()`), about six lines saved per timer.

**Why not.** Only about 10 of the 21 do nothing else when the time runs out (Burn deals damage, Thaw enables two
components, the cooldown resets and attacks, Burst fires); the unit differs (ticks, or seconds from a `Clock.Dt`
singleton); the registration line and its place in the order stay the user's (D56; D120 declined a generated system
the schedule places by itself); and the hand form is already the fast one (a vector loop whose structural write marks
rows, D122, once the `--t.Ticks` value form vectorizes: backlog, vector-loop lane). The faster design is an absolute
deadline read every tick and written once (`samples/VoxelServer/Systems/Zombies.cs`: `clock.Tick < cooldown.Until`, no
write per row per tick): a docs lesson for the docs pass (`docs-todos.md`, "Sweep 2026-11-07"), which a generated
countdown would teach against.

What would change the verdict: users' code showing many timers that only destroy or remove when they run out.

## Machine-bound items of #159: thread pinning, the helpers' queue line (feat-later-research, 2026-11-03)

**Not built: this shared 4-core VM cannot show them.** Owner (2026-11-07): the entry stays here; it needs a quiet
machine with 8 or more cores, so it is not part of the owner's M4 run list (`docs/plans/owner-m4.md`). Two low-latency ideas of `borrowed-ideas.md` "#159 remainder":

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
