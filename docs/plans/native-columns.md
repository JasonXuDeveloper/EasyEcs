# Plan: the core's unmanaged columns on mimalloc (internal, not published)

> The owner's choice (2026-11-07) on owner-review "The core's columns on native memory": option **(b), mimalloc as a
> native dependency**. A work item of the backlog's core lane, last in it (it touches every place a column is read).
> When it ships its reasons go into a decision record and the hosting / high-performance pages, and this file goes.

## 1. Why, and what the research found

`NativeColumnsBenchmark` (#159 remainder, `borrowed-ideas.md`) allocated a world's five unmanaged arrays (48 bytes a
row), wrote them once and freed them. `NativeMemory.AlignedAlloc` (glibc) took 1.4x the pinned arrays' time at 1M rows
and 2.4x at 4M (3-6x the minor faults: glibc returns large blocks to the kernel and faults them back; the GC keeps
freed memory resident), equal at 100,000. With `GC.AddMemoryPressure` the 1M case was sometimes 4x faster (memory
reused resident, 0 faults) and sometimes not (9.3 +- 7.1 ms): glibc's heuristics decide. The prize is the gen2
collections the pinned-heap budget starts while a fresh world fills (D28), at most a quarter of `WorldCreate` at 1M,
and worlds created and disposed often (a zone server spinning up instances; the pool of disposed worlds' columns in
`feat-page-faults` halved the bytes but not the faults, because the GC already reuses its memory: see the page-fault
record in docs-todos). mimalloc keeps freed segments resident and reuses them across worlds (its purge delay), aligns
to 64 bytes without padding, and maps large OS pages on request, where glibc does none of that predictably. Steady
ticks allocate nothing either way: this is about creation, growth, copies and disposal.

## 2. What changes

- **Columns off `Array`.** Only unmanaged columns (pinned `byte[]` today, `ComponentInfo.Create`, `HugePages.AllocatePinned`)
  move; managed columns (`T[]` of components holding references) stay on the GC heap. A column becomes a native block:
  a pointer and a capacity in the table (`Archetype.Columns` keeps one entry per component: an `Array` for managed, a
  small handle for native, or two parallel arrays; pick by Tier1 of `Archetype.Row0`, which every generated job and
  `Entity` read goes through).
- **Every reader of a column:** `Archetype.Row0<T>` (returns `ref Unsafe.AsRef<T>(ptr)`), `ComponentInfo` reads and
  copies (`CopyRow`, `Resize`, `Create`), `Entity.cs` lookups (`lookup.Column`), `World.Migration.cs`
  (`Row0<ChildOf>`), `ColumnSpares` and re-sorts (`Archetype.Permute`), `HugePages.Populate` (madvise the block, or
  mimalloc's large pages), `StreamingStores` (alignment of the head), snapshots (`WorldSnapshot`'s copies, `Capture` /
  `Restore` incremental blocks), saves (`World.Serialization.cs` writes spans: the span comes from the pointer), the
  generated jobs' spans (`SystemGenerator` emits `MemoryMarshal.CreateSpan(ref Row0..., count)` already through
  `Row0`: check that nothing generated takes an `Array`), `Lookup<T>` / `LookupLanes`, `TrimExcess`.
- **Freeing.** `World.Dispose` frees every native column; a finalizer frees them when a world is never disposed: the
  blocks are owned by one `CriticalFinalizerObject` per world (so the world itself has no finalizer and is never
  resurrected), released in `Dispose` with `GC.SuppressFinalize`. A column replaced by a resize is not freed at once:
  a `ref` held across a structural change (wrong already, EECS047) would read freed memory instead of a stale array,
  so a replaced block waits in the world's retired list until the structural gate closes and no pass is in flight
  (`StorageVersion` already bumps then), then is freed.
- **`GC.AddMemoryPressure(bytes)`** for every block allocated, `RemoveMemoryPressure` when freed, so the GC sees what
  the world holds (the research: without it, the GC never collects the worlds that hold the memory).
- **No new public API.** An `AppContext` switch for the fallback in tests (`EasyEcs.NativeColumns`, default on) is
  the one name, hidden (owner, 2026-11-07: hidden, and the native binary packed per RID with the automatic fallback
  when it does not load).

## 3. The binary per platform (NuGet `runtimes/` layout) and the fallback

- **Source:** mimalloc (MIT), one pinned release (v3.x or the 2.x line, decided by the first benchmark round: both),
  built from source in CI with CMake (`MI_OVERRIDE=OFF`, `MI_SECURE=OFF`, `MI_BUILD_SHARED=ON` / `STATIC` per target),
  never a downloaded binary; its license in the package's third-party notice.
- **Desktop and server:** `runtimes/linux-x64/native/libmimalloc.so`, `linux-arm64`, `linux-musl-x64`,
  `linux-musl-arm64`, `osx-arm64` and `osx-x64` (`libmimalloc.dylib`), `win-x64` and `win-arm64` (`mimalloc.dll`):
  the .NET host resolves them by RID; `[LibraryImport("mimalloc")]` with `mi_malloc_aligned`, `mi_free`,
  `mi_option_set` for the purge delay and large OS pages.
- **Native AOT:** static linking through a `buildTransitive` targets file of the package (`DirectPInvoke` for
  `mimalloc` and `NativeLibrary` of `libmimalloc.a` per RID), so a published AOT binary needs no file beside it.
- **WebAssembly (the live demo, Mono AOT):** `libmimalloc.a` built with Emscripten, added as a `NativeFileReference`
  for `browser-wasm` by the same targets file (needs the `wasm-tools` workload, which `build-demo.sh` installs). If
  mimalloc on WebAssembly is not faster than the Mono heap (one linear memory, no OS pages), the browser target keeps
  the pinned heap: measured, not assumed.
- **iOS / Android through Godot:** iOS forbids loading a non-system dylib outside a framework, so `libmimalloc.a` is
  linked statically (`ios-arm64`, through Godot's export of the .NET project); Android takes
  `runtimes/android-arm64/native/libmimalloc.so` (and `android-x64` for the emulator). The Godot sample's README says
  what an export needs.
- **Where it cannot load** (an unlisted RID, a musl without the file, a sandbox refusing it, `DllNotFoundException` /
  `EntryPointNotFoundException` at the first column): a one-time probe at the first native allocation falls back to the
  pinned heap, today's path, for the whole process (one static flag, read once per allocation, never per row). Both
  paths stay tested: the unit tests run once with the switch off.

## 4. Proof (`audit-perf`: two rounds, default job, 0 B in steady state; the bar is nothing slower)

- `NativeColumnsBenchmark` (Pinned against Mimalloc, with pressure; 100k / 1M / 4M) and `PageFaultBenchmark` (Create,
  Grow, Reserve, Load, Capture / Restore, InstantiateEach at 1M and 16M, with `[PageFaultDiagnoser]`).
- `WorldCreate` (NativeColumnsBenchmark's whole operation), `SnapshotBenchmark` / `FullCopyBenchmark` /
  `BufferSnapshotBenchmark` (Capture / Restore), `SaveComparison` (Save / Load), `ColdCreateComparison`, and the
  headline worlds: `BattleServerBenchmark`, `WorldSimulationBenchmark`, `MultiWorldBenchmark`, VoxelServer
  `TickBenchmark`.
- **Gen2 collections during creation are counted** (`[MemoryDiagnoser]`'s Gen2 column, and `GC.CollectionCount(2)`
  around a world's creation in a test), the number the change exists to lower.
- Tests: every column reader above against the pinned path (the switch off), a world never disposed freed by its
  finalizer (a `WeakReference` test with a forced collection), a resize during a pass that holds a span (the retired
  list keeps the old block), `RemoveTypes` and `TrimExcess` (blocks freed), the AOT program (static link), the demo
  (WebAssembly) and the Godot sample (the owner's export, `owner-m4.md`).
- ARM64: the owner's M4 run of the same classes (`owner-m4.md`), after the code.

## 5. Order

After the core lane's other items (they edit `World*.cs`, `Archetype.cs`, `ComponentInfo.cs` too) and after the
directory move (Finish 1b moves these files). One branch; the packaging (CI build of mimalloc, the `runtimes/` and
targets files) is its own commit set, CI separate from logic per AGENTS.md.
