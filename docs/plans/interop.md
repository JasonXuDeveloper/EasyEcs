# EasyEcs from Node.js, Rust and Go (feasibility research, 2026-11-07)

> Status: research done, **waiting for the owner's decisions** (section 4; ledger `owner-decisions.md`, "Waiting for the
> owner"). Owner's request: "看看能不能导出给node用，或者rust和go，你研究一下可行性". Prototype (session scratchpad, not
> committed): `interop/` with `EcsNative/` (C# Native AOT library), `c/`, `rust/ecs_rs/`, `go/`, `node/` (koffi, Bun),
> `wasm/run.mjs`.

**Short answer: yes, built and run here.** A Native AOT shared library holding a World, with its components and
systems written in C#, works today from C, Rust, Go (cgo), Node (koffi) and Bun (`bun:ffi`), with no change to
EasyEcs. It is 2.2 MB and needs only libc and libm. The update runs at full speed behind one call (the vector loop
survives AOT); a call costs about 7 ns from C or Rust, 45 ns from Node, 75 ns from Go. The WebAssembly bundle the docs
demo ships also runs unchanged in Node.

The limit in both routes: **components and the fast systems are written in C#**, because they come from Roslyn
generators no Node, Rust or Go user can run over their own language. Hosts can add systems as one callback per run of
rows (zero-copy column pointers, the flecs model), but those loops are the host compiler's. A user who never writes C#
needs components registered at run time (shape 6), a core redesign.

Numbers below are loop timers on the shared 4-core VM (Xeon 2.10 GHz, AVX-512): orders of magnitude, **not
BenchmarkDotNet**. If the owner says go, `InteropBenchmark` gives the real ones.

## 1. Prototype results

- `EcsNative`: `PublishAot`, `NativeLib=Shared`; `Position`/`Velocity`, a `Clock` singleton, a `[ForEach]` Move system;
  14 `[UnmanagedCallersOnly(EntryPoint = "ecs_*")]` exports: world create/destroy, spawn N from arrays (returns ids),
  destroy, count, update / update n, get/set position by id, a per-run callback (`Runs.Visit`: a C function pointer
  per run with column pointers and count), a bulk copy, and three timing probes.
- `nm -D` lists exactly the 14 symbols; `ldd`: libc, libm. Correct from every language ((5,0) + (1,2) -> (6,2)).
  `Loops` reports Move as a vector loop inside the AOT library. No AOT or trim warnings.
- Toolchains used: .NET SDK 10.0.402, clang 18, gcc, node 22.22, bun, rustc 1.97 (edition 2024: `unsafe extern`),
  go 1.24.7; only koffi was installed.

### Finding: `[StructLayout(Sequential)]` turns the vector loop off

The usual interop habit put `[StructLayout(LayoutKind.Sequential)]` on the components; the generator then reported
"Move: scalar (component Position sets its layout ([StructLayout]))" and the update was 1.6x slower than plain C.
Without it (C# structs are sequential already) the update took 2.2 µs at 10k entities against 5.4 µs for C `-O2`.
A binding generator must not emit it, the header takes layout from `Unsafe.SizeOf` and field offsets, and the
generator could treat a bare `Sequential` (no `Pack`, no `Size`) as the default layout (backlog G19).

### Numbers

| Measurement | C | Rust | Go | Node (koffi) | Bun |
|---|---|---|---|---|---|
| Empty call | 6.8 ns | 7.6 ns | 75 ns | 46 ns | 8.2 ns |
| Get position by id | 12 ns | 16 ns | 82 ns | 605 ns (struct by value from a JS object) | |
| Callback from C# into the host | 6.5 ns | 6.7 ns | 110 ns | 144 ns | 126 ns |
| Update 1M, timed inside C# | 0.63 ms | 0.69 ms | 0.64 ms | 0.72 ms | 0.66 ms |
| Update 1M, one call per update | 0.66 ms | 0.66 ms | 0.63 ms | 0.63 ms | 0.62 ms |
| Move in the host language, one callback per run | 0.67 ms | 0.64 ms | 1.21 ms | 3.18 ms | 3.15 ms |
| Spawn 1M from arrays | 46 ms | 35 ms | 30 ms | 34 ms | |
| Bulk copy 1M positions | 0.66 ms | | | 0.67 ms | |

- One call per tick is free; batching updates buys nothing.
- Per-entity calls are the trap (from Node one read by id costs ~2,700x the per-entity update): bindings are
  batch-first (spawn from arrays, read and write runs).
- Host systems: LLVM (Rust) matched EasyEcs; Go 2x slower (no auto-vectorization); JS over `Float32Array` 5x.
  They lose vector loops, change ticks, `Mut<T>`, parallel chunks and the analyzer.
- Zero copy everywhere: koffi `koffi.view`, Bun `toArrayBuffer`, Rust `slice::from_raw_parts_mut`, Go `unsafe.Slice`.

### WebAssembly in Node

The committed demo bundle (Mono AOT, SIMD, trimmed) loads in Node 22 through `dotnet.create()`: start-up
180–210 ms; 8.6 MB (`dotnet.native.wasm` 7.0 MB, 2.3 MB gzipped); `Vector128` only; single-threaded; a `[JSExport]`
call ~300 ns; zero-copy reads through WASM memory work. Node and the browser only (Rust and Go would need WASI,
experimental in .NET 10).

## 2. Shapes

1. **C-ABI shared library via Native AOT (proven).** The user writes components and systems in C#; a generator writes
   the exports per component, the C header and thin bindings (Node/TypeScript over koffi or N-API, a Rust `-sys` crate
   plus a safe wrapper, a Go package). Fixed exports: world create/destroy, update, update group, count, destroy,
   is-alive, last error, run callback; generated per component: get/set by id, runs, spawn, singletons. Exports must
   be generated into the root assembly (Native AOT exports only from it); a Roslyn generator cannot write files, so the
   header and bindings come from an MSBuild target. A schema file (TOML/JSON) would be a second source of truth: the
   C# struct already is the schema.
   - Threading: the world has no thread-identity check, so calls into one world are serialized (Node: main thread or
     one world per worker; Rust: `Send`, not `Sync`; Go: a mutex). Parallel `[ForEach]` uses the .NET pool inside and
     works; host callbacks must run serially (Node takes no synchronous calls from foreign threads).
   - An exception escaping an export kills the process: every export catches and returns a status, plus
     `easyecs_last_error()`.
   - Pointers: stable during a callback or until the next structural call (EECS047's contract); under mimalloc a
     stale pointer reads a retired block, not freed memory. Managed components do not cross.
   - Limits: one Native AOT library per process; one build per RID; no hot reload; SIMD level chosen at publish.
   - Effort: 2–4 weeks for generator, packaging and one binding; ~1 week per further binding; CI per RID.
2. **WebAssembly npm package (proven with the demo).** Same C# plus generated `[JSExport]`s; one artifact for browser
   and Node; slower, single-threaded, 8.6 MB. ~1 week.
3. **Host-language systems as per-run callbacks (proven; companion to 1).** New core API: `World.ForEachRuns` over a
   run-time `ComponentSet` with an access mask, and registration in the schedule. 1–2 weeks.
4. **Port to Rust or Go: no.** Core 27.1k lines, generator 31.4k, code fixes 2.7k, tests 38.6k; Rust proc macros have
   no semantic model; Go has no auto-vectorization. A person-year and two codebases.
5. **Others:** flecs registers components at run time and runs callbacks over `ecs_iter_t` (hence its C++, C#, Rust,
   JS bindings); Bevy reaches other languages through scripting layers; Unity DOTS stays in C#/Burst. Libraries built
   on compile-time generation expose the finished world (shape 1).
6. **Run-time components in the core (the flecs model).** The only shape where users write no C#. Type indices,
   `ComponentInfo<T>`, change ticks, saves, indexes and all generated code are keyed by `T`, and Native AOT cannot make
   new value-type generic instantiations at run time: an untyped twin of every path. Months.

## 3. Recommendation

1. Shape 1 + 3 as an opt-in `EasyEcs.Native` layer, Rust and Node first, then Go. The runtime does not depend on it.
2. Shape 2 if the browser matters.
3. Shape 6 only on an explicit "no C#" goal.
4. Shape 4: no.

## 4. Owner decisions (open)

1. Scope: a C# world driven from other languages (1+3), or users who never write C# (6)?
2. Package form: a new package `EasyEcs.Native`, or a generator mode switched on by a property?
3. ABI: prefix (`ecs_` collides with flecs; `easyecs_`?), an ABI version call and its semver promise, status codes plus
   last error.
4. Distribution: users build their own library (no prebuilt binaries); publish npm, crates.io (`easyecs-sys`), a Go
   module? Each has upkeep and a CI matrix.
5. Schema format: suggested no (unless 6).
6. The WebAssembly npm package: ship it, under which name?
7. New core API `ForEachRuns` for host systems.

## 5. Work packages if the owner says go

- Records: exports generated into the root assembly; no `[StructLayout]`; calls serialized, host callbacks serial; no
  exception crosses; pointer lifetime; one AOT library per process.
- WP1 fixed world ABI (tests through `NativeLibrary`; CI publishes and runs a C smoke test beside `tests/EasyEcs.Aot`).
- WP2 per-component exports (spawn from arrays, get/set, singletons, runs for declared queries such as
  `[assembly: ExportQuery<Position, Velocity>]`, bulk copy); a caching test; analyzer rules for components that cannot
  be exported; `InteropBenchmark` (per call, per run, bulk copy, against the in-process update).
- WP3 header and bindings writer (MSBuild target: `easyecs.g.h`, `.d.ts` + `index.mjs`, `lib.rs`, `easyecs.go`;
  `_Static_assert(sizeof/offsetof)`; a smoke test per language in CI).
- WP4 host systems (`ForEachRuns`, schedule placement, a gate throw in a callback caught; fragmented-archetype bench).
- WP5 (optional) WebAssembly npm package.
- WP6 docs: `guide/interop.mdx` + zh, `samples/Interop`, migration, README, the public skill.
- Order: after `native-columns.md` (pointer lifetime); WP1 -> WP2 -> WP3 minimum; CI commits separate.

Sources: learn.microsoft.com native-aot/interop; ericsink.com "native_aot/objects_gotcha"; dotnet/runtime#97952;
flecs-ecs-rs; flecs-polyglot; bevy_mod_scripting; bevy_mod_js_scripting; componentize-dotnet.
