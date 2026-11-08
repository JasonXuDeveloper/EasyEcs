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

## A rule for an IForSpan job that is one element loop (inner-loops.md section 6, backlog A15; not built in feat-analyzer-lane2)

**Proposed by** inner-loops.md (section 6, after WP4 shipped the lambda form of `ParallelFor`): an `IForSpan<T>` job
whose `Execute(int start, Span<T> items)` is one plain loop over `items` would get a vector loop as a lambda pass,
`world.ParallelFor(ref items, values..., static (ref T item, ...) => ...)`, so a rule could say "write it as a lambda
pass".

**Why not in this lane.** The analyzer's rule for SIMD written by hand reports a form only when its rewrite, put in
place in a copy of the compilation, gets the generator's loop (`HandSimd.Rewritten`), and a rule without that check
would advise rewrites that stay scalar (`ParallelForBenchmark.Heavy`'s inner `k < 8` loop is one). Here the rewrite is
not of a method but of the call site: the job is built where `ParallelFor(ref items, ref job)` is called, its fields
set there become the lambda's values, and the loop's body becomes the lambda's. Checking that rewrite needs
`LambdaPass.Read` and `Kernel.Vectorize` over a call rewritten in a copy of the compilation, a new entry in
`LambdaPass.cs`, which the vector-loop lane owns while it edits the lambda passes. The cause is the missing call-site
form of the check, not the rule.

What would change the verdict: the vector-loop lane adding a `LambdaPass` reader over a rewritten call (the job's
fields as values, its loop's body as the lambda); the rule is then a `HandSimd`-style form with its fix.

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
