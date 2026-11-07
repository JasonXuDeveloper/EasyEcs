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
