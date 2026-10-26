# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## Lookup gathers under `SimdMode.Auto` (feat-gather-always)

**Proposed:** gathers faster than the method row by row in every case, then on by default. **Not done:** on this
machine (Sapphire Rapids, AVX-512, .NET 10) no form beats the row-by-row method where a vector's targets spread over
many tables and the method does little per row; D131 has the measurements. Two prototypes (D131,
Considered) stayed out:

- **A per-lane walk** (each lane's table resolved in registers, its column's pinned address as a 64-bit lane, two
  `vpgatherqd` per 8 lanes): wins with a one-field body over 64 or 1,000 tables, but resolving a lane's table costs as
  many loads as the method's own lookup, so with a bearing body it runs 1.17 to 1.36 times the row-by-row time, and
  its one-table path ran slower than D131's.
- **A micro-adaptive choice** (Vectorwise) over the walk: each job, per thread, times 4 runs of each form, takes the
  cheaper one for 64 to 1,024 runs, measures again. It removes most of the spread-case loss, but its measuring and its
  loop cost the one-table case (one-field body, two rounds: 0.64 / 0.64 against 0.52 / 0.52 at 1k, 0.65 / 0.49
  against 0.45 / 0.43 at 1M), and with the bearing body over 64 tables it only reaches 1.04 / 1.03 (shipped: 1.15 / 1.12).

**What would change the verdict (the owner's call):** a per-entity table id the gathers can read (a field of `EntityMeta`, or an opt-in
array beside it) so a lane's table is one more gather, not a chain of loads; an AVX-512 gather in .NET; or WP0b's
split bringing the lookup job under the JIT's 32 locals, after which the adaptive choice can be measured again
against the one-table case.
