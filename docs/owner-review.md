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
