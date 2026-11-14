# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## Two analyzer rules: a component removed to pause it, a flag where a tag would do (feat-analyzer-lane2, backlog A14)

**Proposed by** the analyzer sweep (2026-11-07, `analyzer.md` 3): (a) `entity.Remove<T>()` in one system and a
`Set<T>` / `Add<T>` of the same T on that entity in another, to pause what T drives, where `Disable<T>` hides
the component, keeps its value and moves no row; (b) `if (!c.Flag) return;` as a `[ForEach]` method's first
statement, where a tag and `[With<T>]` skip the rows without reading them.

**Why not.** (a) No signal in the code tells a pause from an end and a new start: a status effect removed when it
expires and set again by the next hit is the same pair of calls, and finding the pair needs the flow of one entity
across systems, which the analyzer does not follow (one entity per call is not a fact of the syntax). Every report it
could make would be a guess; the cleanup-and-disable page teaches `Disable<T>`. (b) One mistake, one fix: since e45aac7
EECS055 reports a bool the method uses alone among its component's fields as a Warning with the split (a one-bool
component is a byte column the vector loop reads as a mask), so that code already gets a fix that keeps every row in
place. A tag is faster only when the flag rarely flips (each flip is a structural change, a row moving between
archetypes), and how often it flips is not in the code. A second rule would contradict the first on the same line.

What would change the verdict: (a) a runtime count of Remove-then-Set of one type on one entity within a few ticks
(introspection could report it, the analyzer cannot); (b) the same kind of runtime count of how often a bool field
flips per row, which would let a profile-guided hint choose between the split and the tag.

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
machine with 8 or more cores, so it is not part of the owner's M4 run list (`docs/plans/owner/owner-m4.md`). Two low-latency ideas of `borrowed-ideas.md` "#159 remainder":

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

## An element-loop form for a tile cursor's indexer: not built (feat-lane4-generator, backlog G6, 2026-11-08)

**Proposed by** generator.md ("In areas owned", Settle's loops): a `for (int x = 0; x < n; x++)` loop of a Tile
`[ForEach]` method reading `c[x + dx, dy, dz]` through a TileCursor (or a Tile, a column walk) could become an element
loop, each offset a stream like `s[i - 1]` over a span.

**Why not.** Each indexer call is a checked offset into the padded buffer (dy and dz bounds, the halo), not a span: an
element loop would have to prove the column's range once per loop, which is what the tile loads already do
(`c.Load256(x + dx, dy, dz)`, checked once per loop by the TileLoads interceptor), and the step as a whole is what a
cell method (`Cell<TCell, TTile>`) writes, rows as vector loops with the halo, blank rows skipped and the shape's
constants folded; EECS103 v2 (HandStencil) rewrites a cursor stencil written by hand into that cell method. A third
form would vectorize the same loops a cell method already does, without its blank-row proof or halo reads in place.
What ships instead: EECS055's reason at such a loop names both forms ("it reads a tile's cells at its index 'x' ...:
write the step as a cell method (Cell<TCell, TTile>), whose rows get vector loops, or load whole vectors in the loop").

What would change the verdict: a stencil a cell method cannot express (writing its own tile at other cells than the
row's, or reading cells at offsets computed per row) that users write with the indexer in a hot pass.

## Doc comments per arity as `inheritdoc`: not done (feat-lane4-rest, backlog G18, 2026-11-08)

**Proposed by** generated-text.md: the arity variants ArityGenerator writes (World.Queries, World.Lambdas) repeat the
same XML doc comment per arity; `/// <inheritdoc cref="..."/>` pointing at one variant would shorten the text.

**Why not.** The text is not generated in users' builds: it is EasyEcs.Core's own source, compiled once into the
package, so the saving is only in our build (World.Queries carries 220,842 bytes of doc comments, World.Lambdas
19,165) and in no user's keystroke. What the comments feed is the public surface: IntelliSense reads the package's XML
file, where `inheritdoc` is resolved only by tools that support it (the compiler writes it through, VS resolves it,
some editors show the bare tag), and tools/docs/ApiDocs would have to resolve it and keep a Chinese entry per arity
in step with the variant it points to. The cost lands on users and on the API reference for a size win nobody sees.
What ships instead: ComponentGenerator's members and the view's `TryGetX` (the parts users' builds generate) got
their text shortened (the alias block, one lookup).

What would change the verdict: ApiDocs resolving `inheritdoc` itself (one zh entry for a family) and the compiler's
XML output resolving it for every editor, or doc comments showing up in a profile of the package's build.

## The rest of the generated-text cleanups (G17): not done beyond the marks (feat-lane4-rest, 2026-11-08)

**Proposed by** generated-text.md step 5: hoisted scalar setup, `this.v0`, pair broadcasts, struct-local aliases,
dead narrow temps, unused masks, `ids, 0`, discards for unused fields, the identity copies, one body per width.

**Why not.** The Tier1 listings read for this branch (the runners of the draws and halves programs) show the JIT
dropping locals never read, forwarding identity copies and hoisting invariant broadcasts, so these change the C#
text, not the machine code, while each needs its own printer path and test. The one with machine-code weight, the structural
change's mark (`var __d0 = Zero` then `|=`), shipped (361fbeb: StructuralLoopBenchmark level or better at 0 B, see the
backlog). GeneratedTextTests keeps counting the rest, so a regression shows.

What would change the verdict: a Tier1 listing where one of them survives (a copy or a broadcast the JIT keeps in a
loop), or the generator's own time on these lines showing in GeneratorBenchmark.

## Changing the ClosedSystems stage: not done (feat-lane4-rest, backlog G11 9c, 2026-11-08)

**Proposed by** generator.md 9c: `ClosedSystems` (the syntax provider that finds closed generic systems named by
AddSystem<G<P>>, [UpdateAfter<G<P>>] and the other system APIs) runs on every keystroke; measure it before changing it.

**Why not.** Measured with GeneratorBenchmark, the stage on against its predicate returning false (the corpus names no
closed generic system, so the output is the same), three BenchmarkDotNet runs per side: the ranges overlap and the
sign of the difference changes between runs and between the benchmark's two namespace variants (EditMethodBody
506-626 ms with the stage against 475-595 ms without), and the allocation is the same to 0.15 MB. Its predicate is a type test per node of the edited tree and its transform
binds only a generic name inside a system API's type arguments; nothing in it shows above the benchmark's noise, so
there is nothing to buy with a change (the backlog item has the numbers).

What would change the verdict: a corpus whose files name many closed generic systems (the transform then binds each
on every edit of its file), measured with this stage's tracked step.

## A ground cell sampling its tile's layer: not built (feat-ground-cells, 2026-11-14)

**Proposed by** the inner-loops leftover (a): a cell method over a `Ground<TTile>` tile. The plain form (every cell of the
ground's shape) already ran and now has its tests and TileGroundBenchmark (the backlog item has the numbers). What a
ground is for, though, is a second field beside the 3D tile's cells, and its stencils read that tile too:
`GroundHeatSystem` (TileTests.Steps.cs) spreads heat over `Ground<RockChunk>` except where the chunk's top layer is
rock, reading `rocks[x, 3, row.Z]`.

**Why not.** A cell method's inputs must share one shape (Symbols.StepProblem, EECS115: "cells of different tiles"),
because each input is a stream at a constant offset from the driver's cell and their rows line up. A ground row and
a row of the 3D tile line up in x and z, but the sampled layer is an absolute layer of the other shape (3), not an
offset within its halo: `Cell<Rock, RockChunk>` has no way to say it, and its exchange would copy every layer of the
3D tile for one. It needs a public form (a layer pick on the sampled cell, or an attribute naming the layer) and
the exchange of one layer of another shape: an API for the owner to choose.

What would change the verdict: the owner choosing that form; then Kernel.CellFormOf reads the pick as a stream
of the other tile's row at that layer, and the pass exchanges that layer alone (`ExchangeHalo(layer, layer)`).
