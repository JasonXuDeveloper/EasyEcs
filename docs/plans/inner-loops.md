# Plan: element loops, `Cell<TCell,TTile>` and EECS103

> Internal plan, not published on the site (only `docs/content` is). It is the design agents build from, across
> sessions. When a work package ships, its reasons go into its decision record (D132-D134), the guide pages describe
> it, and its section here is deleted; the file is deleted with the last package.
>
> Status: WP6a (EECS103 v1) shipped: D134, `HandSimd.cs`. WP0b (jobs within the inliner's 32 IL locals) shipped:
> D135, `SystemGenerator.Loops.cs`. WP0-WP5 wait for the Lookup-gather work (D131) to merge,
> since they change `Kernel.cs`; WP6b/c follow WP5/WP2.


## 1. What exists today

### 1.1 The Kernel translation core (`src/EasyEcs.Generator`)
- **`Kernel.cs` (2,119 lines).**
  - Entry points:
    - `Kernel.Vectorize(IMethodSymbol, MethodDeclarationSyntax, SemanticModel, fieldOf, ct, out ScalarReason?)` (l.257), for [ForEach] methods.
    - `Vectorize(IMethodSymbol lambda, IAnonymousFunctionOperation, …)` (l.293), for lambda passes.
    - Both go to the private `Vectorize(Lane, IOperation root, fieldOf, …)` (l.314), then `Translate(Lane, body, fieldOf, ct)` (l.447).
  - `Translate` builds:
    - `_components`, a `Dictionary<IParameterSymbol,(Index, Fields, Ref)>` (l.126);
    - `_uniformParams` (keyed by `IParameterSymbol`), `_optionals`, `_reductions`, `_locals`;
    - the column loads (`Interleaved.Load2..4` / `V.LoadUnsafe(ref e{k}, row)`) and the stores.
  - It returns `Kernel.Code`: Setup, Body, Stores, MaskedBody, MaskedStores, Restore, Fold, Size, Mask, Lane, Interleaves, Bails, `FlatCode? Flat`, Cold, Wide, Statics, plus `StructuralChanges`, `VectorTail`, `Written`.
  - Statement translation:
    - `Statement` (l.894) handles block, declarations, expression statements, if (masks), return (`_alive`).
    - **Any loop falls to `default: throw Not(op)`.**
    - `Branch` (l.988) handles cold branches. `Assignment`, `Write`, `Reduces`/`Fold` handle Reduce<T>. `Expr`, `Binary`, `IntegerDivision`, `Call`, `Pow`, `Clamp`.
  - Uniforms: `IsUniform` (l.2006) treats **every local as non-uniform**. `Scalar` (l.2017) hoists only constants, static readonly fields, params and pure Math.
  - Lanes: `Lanes`/`Paths` (l.811/825).
    - Latent issue: an `[InlineArray(N)]` struct field is seen as one lane (`_first`). Today this is safe only because `IInlineArrayAccessOperation` is unsupported. WP1 fixes it first and adds a regression test.
  - Tokens: `Kernel.V`, `SquareRoot`, `LanesForm`, `Visited`, `Row`. One text gives both widths (Vector512 and Vector<T>).
- **Other `Kernel.*.cs` files:**
  - `Kernel.Narrow.cs`: byte/short lanes, int interval tracking. This answers D77's "byte promotes to int" objection.
  - `Kernel.Struct.cs`, `Kernel.Wide.cs` (AVX-512 permutes; AVX2 wide rows are planned), `Kernel.Pairs.cs`.
  - `Kernel.Views.cs`: desugars by binding a synthesized method in a copy of the compilation.
  - `Kernel.Structural.cs`.
- **`SystemGenerator.cs`:**
  - `EmitAccessor` (l.189): the `UnsafeAccessor` extern `{Name}_{index}`.
    - Tile methods get `NoInlining|NoOptimization` so the user method is its own JIT root (D77).
    - `OutOfLine` gives `NoInlining` for Scratch methods that loop or call (l.1174).
  - The job's `Execute` (l.620–690): `EmitFlatLoop` (l.820), `EmitVectorLoop` (l.742), `EmitTail` (l.796). They hard-code the names `rows`, `r{k}`, `e{k}` and `call`.
- **`SystemGenerator.Lambdas.cs` + `LambdaPass.cs`:** interceptors for `world.ForEach/ForEachParallel(values…, static lambda)`.
  - `LambdaPass.Copy` (Qualifier: qualifies names, inlines constants), `Unreachable` (EECS096), `Usings`.
  - `ArityGenerator.Lambdas()` writes `World.Lambdas.g.cs` stubs taking `Delegate body` that throw `NotCompiled()`.

### 1.2 Core
- **ForParallel** (`World.For.cs`):
  - `IForRange.Execute(int start, int end)`, `IForSpan<T>.Execute(int start, Span<T> items)`.
  - `ForParallel(int count, ref TJob, chunkRows)` and `ForParallel<T,TJob>(ref DynamicBuffer<T>, ref TJob, chunkRows)`.
  - One job copy per chunk, nothing merged back (D80). **The generator never sees `Execute` bodies.**
- **Buffers** (`DynamicBuffer.cs`): `AsReadOnlySpan()`, `GetEnumerator()` (ReadOnlySpan enumerator), `AsSpan(this ref)` (unshares the copy-on-write store, D124), `SetCount` returns a Span.
- **Tile** (`Tile.cs`):
  - `Tile<TCell,TTile>`: Pad, Beside, Cells, ChangedEdges, Facing. `PaddedTile`: Rows(TileReads), At(row), this[x,y,z].
  - `TileCursor`: `Load128/256/LoadVector<TLane>(dx,dy,dz)`, `this[dx,dy,dz]`, `Offset` with halo checks. There is no `Load512`.
  - `TileRow.Index`, `TileRows` (TZCNT walk), `TileMemory` (ThreadStatic texture, `Blank(fill)`), `TileLayout` (internal: Row, Layer, Cells).
  - `TileReads` doc: skipping quiet rows assumes f(0…)=0.
  - Generator: `ParamKind.Tile`, `TileModel`, and `TileNeighbors.Of` per entity.

### 1.3 Inner loops users write by hand today
| Where | What | Shape |
|---|---|---|
| `samples/VoxelServer/Systems/Water/Flow.cs` | `Shade`/`Gives` over `Vector128<byte>`, 11 `Load128` per row, per-row summaries (popcounts, first/last layer, edges moved) | Tile shader + reductions |
| `samples/VoxelServer/Systems/Water/HeightMap.cs` | per layer, a `Vector128<byte>` loop over 256 columns on a `stackalloc`, `ConditionalSelect` | buffer element loop inside an outer loop |
| `samples/VoxelServer/Systems/Zombies.cs` `FireDamageSystem.Hurt` | `Cast<Health,int>` → `Cast<int,Vector<int>>`, `lanes[i] -= One`, scalar tail | batch method with hand SIMD; the per-row `health.Value--` vectorizes today (EECS103 target) |
| `samples/VoxelServer/Terrain.cs` `Generate` | Vector128 per layer | a helper, not a pass: out of scope |
| `samples/VoxelServer/Systems/Items.cs`, `Inventory.HasRoom/Add` | searches with early exit | not a vector target |
| `benchmarks/.../TileBenchmark.cs` `TileSystems.Blur`, `WaveSystem`, Life (l.495), `HandBlurSystem` | `Load128` stencils, the Laplacian | Cell targets / baselines |
| `benchmarks/.../KernelBenchmark.cs` `Kernels.StepBatch` / `StepBatch512` (l.1024/1051) | `[ForEach]` batch methods with hand Vector<T> / Vector512 | baselines; EECS103 fires, so the benchmark project needs `NoWarn` |
| `benchmarks/.../ForParallelBenchmark.cs` `Light`/`Heavy` (IForSpan) | scalar element loops (Heavy has an inner `k<8` loop) | ForParallel body |
| `benchmarks/.../BattleServer.cs` `PlanningSystem.Plan` | `for i < 8` over the singleton `[InlineArray] WaypointArray`, argmin | constant-trip loop (still scalar for `Mut<Goal>`) |
| `BattleServer.LootValue` | buffer sum outside a pass | out of scope |
| `HistogramBenchmark` | `[InlineArray(1024)]` bins | scatter: not vectorizable |
| `demo/EasyEcs.Demo/Systems/MovementBatchSystem.cs`, `DragBatchSystem.cs` | Vector128 casts, odd-row tail | EECS103 (Drag has a user call `Units.Keep`, so no plain form and no report) |
| `docs/content/guide/grids.mdx` | 25 `Load128`/`Vector128` mentions | docs pass |
| tests: `TileTests.cs` | Load128 stencils | keep as tests of the cursor |

## 2. User-facing forms and what the generator writes

### 2.0 One core, thin wrappers
The shared lowering stays `Kernel.Translate`: Statement, Expr, Narrow, Struct, Pairs, Wide, LaneMath, Reductions. Every form is described as "streams + uniforms + reductions + optional lane index". WP0 adds this as a `Binding`:
- **Streams:** a symbol (parameter, foreach iteration local, or span local indexed by the loop index), its element type and fields, written or not, the text of a `ref` to element 0, and constant offsets.
- **Uniforms:** `ISymbol → text`. This adds outer locals and parameters that are not written inside the loop (`model.AnalyzeDataFlow(loop).WrittenInside`). In a body copy they are in scope by name.
- **Reductions:** `Reduce<T>` fields (today), plus outer locals updated as `x += e` (integers), `x = Math.Min/Max(x, e)`, `x |= e`, `&=`, `^=`.
- **Lane index:** a loop variable `i` used as a value becomes `{V}<int>.Indices + broadcast(i)`.

Plug-in points:
1. `Kernel.Vectorize(method)` builds `Binding.OfParameters` (today's code moved out of `Translate`).
2. `Kernel.VectorizeLoop(ILoopOperation, Lane, …)` (new, `Kernel.Loops.cs`) builds `Binding.OfLoop`.
3. `Kernel.VectorizeCells(method)` (new, `Kernel.Cells.cs`) builds `Binding.OfCells`.
4. `Statement`'s loop case unrolls constant-trip loops in place (WP1).

Emission:
- `EmitVectorLoop` / `EmitFlatLoop` get the names as parameters (`count`, `column(k)`, `call`, and a widths list).
- The body copy is `BodyCopy.cs`: `LambdaPass.Copy`/`Qualifier` moved out, plus a statement-replacement map and `#line` directives.
- `EmitAccessor` emits either today's `UnsafeAccessor` extern or the copied body under the **same name `{Name}_{index}`**, so the job's call site is unchanged.

### 2.1 Buffer and span loops (element loops) inside `[ForEach]` methods (per-row and batch) and lambda passes
You write:
```csharp
[ForEach(Parallel = true)]
static void Cool(ref DynamicBuffer<Heat> cells, in Climate climate)
{
    foreach (ref var cell in cells.AsSpan()) cell.Value = cell.Value * climate.Keep + climate.Ambient;
}

[ForEach, Changed<DynamicBuffer<Block>>]           // VoxelServer HeightMap, plain
static void Update(in DynamicBuffer<Block> blocks, Mut<DynamicBuffer<Surface>> surface)
{
    var solid = MemoryMarshal.Cast<Block, byte>(blocks.AsReadOnlySpan());
    Span<byte> top = stackalloc byte[Columns];
    for (int y = 1; y < Terrain.Height; y++)
    {
        var layer = solid.Slice(y * Columns, Columns);
        for (int i = 0; i < Columns; i++) if (layer[i] != 0) top[i] = (byte)y;
    }
    top.CopyTo(MemoryMarshal.Cast<Surface, byte>(surface.Write().SetCount(Columns)));
}

[ForEach, With<Burning>] static void Hurt(Span<Health> health)   // batch method, plain loop
{ for (int i = 0; i < health.Length; i++) health[i].Value--; }
```
Recognized loops:
- `foreach (ref|ref readonly|var x in S)`, where S is a span local, a parameter, `buffer`, `buffer.AsSpan()`/`AsReadOnlySpan()`, `MemoryMarshal.Cast<A,B>(…)` or `.Slice(…)`.
- `for (int i = a; i < n; i++)`, whose element accesses are `s[i]` or `s[i ± c]` (WP3) on span locals or parameters, or on arrays held in locals.

Requirements:
- No `break`, `continue`, `goto` or inner `return`.
- No calls except pure Math (the existing `Call` rules).
- No writes to outer locals other than the reduction shapes.

The generator writes (in `__ForEach`, replacing the `UnsafeAccessor` extern):
```csharp
[MethodImpl(MethodImplOptions.NoInlining)]   // its own JIT root (D77 lesson): the job's Execute stays small
internal static void Update_0(in DynamicBuffer<Block> blocks, Mut<DynamicBuffer<Surface>> surface)
{
#line (21, 9) - (30, 90) "Systems/Water/HeightMap.cs"
    var solid = global::System.Runtime.InteropServices.MemoryMarshal.Cast<global::VoxelServer.Block, byte>(blocks.AsReadOnlySpan());
    global::System.Span<byte> top = stackalloc byte[256];
    for (int y = 1; y < 32; y++)
    {
        var layer = solid.Slice(y * 256, 256);
        {   // for (int i = 0; i < 256; i++) …: a vector loop over the elements
            int i = 0, __n = 256;
            if (__n <= layer.Length && __n <= top.Length)        // else the loop as written throws where it does
            {
                ref byte e1 = ref MemoryMarshal.GetReference(layer);
                ref byte e2 = ref MemoryMarshal.GetReference(top);
                if (Vector512.IsHardwareAccelerated && __n - i >= Vector512<byte>.Count)
                {
                    var __p0 = Vector512.Create((byte)y);         // uniform: an outer local, not written in the loop
                    for (int last = __n - Vector512<byte>.Count; i <= last; i += Vector512<byte>.Count)
                    {
                        var __c1_0 = Vector512.LoadUnsafe(ref e1, (nuint)i);
                        var __c2_0 = Vector512.LoadUnsafe(ref e2, (nuint)i);
                        var __m0 = ~Vector512.Equals(__c1_0, Vector512<byte>.Zero);
                        __c2_0 = Vector512.ConditionalSelect(__m0, __p0, __c2_0);
                        Vector512.StoreUnsafe(__c2_0, ref e2, (nuint)i);
                    }
                }
                /* the same text with Vector / Vector<byte> (EmitVectorLoop's second width) */
            }
            for (; i < __n; i++) if (layer[i] != 0) top[i] = (byte)y;   // the elements left, as written
        }
    }
    top.CopyTo(...);
#line default
}
```
Rules:
- Spans are evaluated where the user evaluates them, so copy-on-write (`AsSpan` unshares), `Mut.Write()` stamps and `SetCount` keep their meaning.
- Private members the copy names outside the loop (constants are inlined) are reached through generated `UnsafeAccessor` stubs. Generic owners or methods, or what an accessor cannot reach, keep today's extern; the reason is Info.
- `SimdMode.Off` means no copy.
- `DescribeSchedule` lists each loop: "Update: rows scalar (it takes DynamicBuffer<Block>); loop HeightMap.cs(29): vector (Vector512 or Vector<byte>)".
- A loop that looks meant for SIMD (lane element type, no early exit, no user call) and stays scalar is reported by **EECS055 at the loop keyword**, using Kernel's reason (Warning if fixable). `SimdMode.Require` turns it into EECS056.

### 2.2 `[InlineArray]` loops: unrolled into the per-row loop (lanes = rows)
You write:
```csharp
[InlineArray(8)] public struct Bands { private float _b; }
public struct Spectrum : IComponent { public Bands Gain; }

[ForEach] static void Fade(ref Spectrum s, in Mixer mixer)
{ for (int b = 0; b < 8; b++) s.Gain[b] *= mixer.Fade; }

[ForEach(Slices = 4), Without<Dead>]                // BattleServer Plan's shape (ref Goal instead of Mut)
static void Plan(in Position p, ref Goal goal, in Waypoints waypoints)
{
    int next = goal.Waypoint; float nearest = float.MaxValue;
    for (int i = 0; i < Waypoints.Count; i++)
    {
        float dx = waypoints.Points[i].X - p.X, dy = waypoints.Points[i].Y - p.Y, d = dx * dx + dy * dy;
        if (d > 64 * 64 && d < nearest) (nearest, next) = (d, i);
    }
    goal = new Goal(next);
}
```
What the generator does:
- **`Paths`:** an `[InlineArray(N)]` field gives N lane paths (element k). This fixes the latent single-lane bug.
- **`Statement`:** a `for` with constant bounds, step 1 and N ≤ 16, or a `foreach` over an inline array, is translated N times. The loop variable is bound to the constant k (`_constants[i] = k`), and `IInlineArrayAccessOperation` with a constant index resolves to lane path k. On a uniform (singleton) it resolves to a uniform `Scalar` (`waypoints.Points[3].X`), broadcast once per run.
- **Choosing the loop:**
  - `Fade` is element-wise, so it takes the existing **flat loop** (`EmitFlatLoop`, `rows*8` floats, no deinterleave). `FlatCode`'s 2..4 limit is raised for inline arrays.
  - Mixed per-element use takes `Interleaved` (≤ 4 elements), or `Kernel.Wide` permutes (≤ 16 words, AVX-512, AVX2 once wide rows have it), or stays scalar with a reason.
  - `Plan`'s argmin is a loop-carried local per lane, so 8 iterations run in order: exact.
- The per-row `call` for tails is unchanged: no body copy needed.

### 2.3 ForParallel bodies: a lambda pass over a buffer's elements
You write:
```csharp
world.ForParallel(ref hits.Items, 0.999f, static (ref Hit hit, float keep) => hit.Amount = hit.Amount * keep + 1);
```
- `ArityGenerator.Lambdas()` adds `ForParallel<T, V1..Vn>(ref DynamicBuffer<T> items, V1 v1, …, Delegate body) => throw NotCompiled()`.
- `LambdaPass.IsCandidate/IsTarget` accept `ForParallel`.
- The element is treated as the lambda's only component (`Hit(int, float)`: two 4-byte lanes). `Kernel.Vectorize(lambda…)` runs unchanged. The interceptor runs a generated `IForSpan<T>` job:
```csharp
internal static void ForParallel3(this World world, ref DynamicBuffer<Hit> items, float v1, Delegate body)
{ var job = new LambdaJob3 { x1 = v1 }; world.ForParallel(ref items, ref job); }
file struct LambdaJob3 : IForSpan<Hit>
{
    public float x1;
    public void Execute(int start, Span<Hit> c1)
    {
        ref Hit r1 = ref MemoryMarshal.GetReference(c1); nint rows = c1.Length;
        /* EmitVectorLoop / EmitFlatLoop text, exactly as a [ForEach] job's */
        for (; i < rows; i++) __ForEach.Lambda_3(ref Unsafe.Add(ref r1, i), x1);
    }
}
```
- `Heavy`'s inner `for (k < 8)` unrolls through 2.2.
- Hand-written `IForSpan`/`IForRange` structs are **not** compiled; see §6.

### 2.4 Neighbor lists
- **1D offsets in element loops (WP3):**
  ```csharp
  for (int i = 1; i < a.Length - 1; i++) b[i].Value = (a[i-1].Value + a[i].Value + a[i+1].Value) * (1f/3);
  ```
  - This gives stream `a` with offsets {-1, 0, +1}, loaded as `LoadUnsafe(ref e1, (nuint)(i + off))`.
  - Bounds are proven from the loop range (start + minOff ≥ 0; the end condition `a.Length - 1` covers maxOff). Other streams get a pre-loop check, and a failed check runs the loop as written.
  - **Alias guard:** a stream read at offset ≠ 0 must not be written, and every written stream must have a root (buffer parameter, component span, `stackalloc` local; `Cast`/`Slice` of a root keeps the root and adds a known offset) proven distinct from the streams it reads. Unknown roots that are written leave the loop scalar ("it writes 'b' and reads 'a', which may overlap").
- **Tile neighbors:** see 2.5.
- **Index lists (gathers, MD-style `foreach (var j in near) sum += pos[j]`):** not vectorized (§6).

### 2.5 `Cell<TCell,TTile>`: the shader form (on 2.1 and 2.4)

**Goal (the owner's).** A general, high-performance stencil form: users write what one cell computes, and the
generator writes the real kernel. It is a goal, not an experiment that may be dropped: where the generated kernel is
slower than the hand-written `Load128`/`Load256` tile version of the same stencil, that is a generator task to fix,
not a reason to keep hand-written SIMD. D77 is rewritten when this ships (its objections are answered below).

General means:
- **Cells:** any unmanaged `TCell`, a single field or a struct of fields of one or several lane types (flat lanes,
  `Interleaved`, narrow and wide lanes from the existing core), `Vector2/3/4` fields;
- **Grids:** 2D and 3D shapes, any `Halo` width, every `TileBoundary`;
- **Several inputs and outputs:** more than one `Cell<,>` input (different grids or cell types of one grid), more
  than one `out` cell, plus the usual uniforms (singletons, values, constants) and component parameters of the tile's
  entity;
- **Control flow:** conditions as masks, cold branches, `FloatMode.Fast` math, as in per-row vector loops;
- **Per-tile results** (summaries such as counts, first/last layer, changed edges) through reductions in the same
  pass, so the VoxelServer water (`Flow.cs`) is written as cells too (WP5b).

The kernel the generator writes is held to the hand-written one:
- every width the hardware has (512/256/128), constant-folded for the tile's row width;
- each neighbor offset one unaligned load, identical loads shared (common subexpressions) across the terms of a
  stencil; rows in a sliding window where it measures faster;
- no `call` and no range check in the row loop (Tier1 checked with `tools/jitdisasm.sh`), the routine its own JIT
  root;
- quiet rows skipped as the hand version skips them, with the exact f0 fill below.

New core type in `Tile.cs`:
```csharp
public readonly ref struct Cell<TCell, TTile> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    public TCell Value { get; }                          // this[0,0,0]
    public TCell this[int dx, int dy, int dz] { get; }   // within Halo (constants)
    public int X { get; } public int Y { get; } public int Z { get; }
    [EditorBrowsable(Never)] public Cell(ref TCell at, int x, int y, int z);   // generated code only
}
```
`PaddedTile` gains `[EditorBrowsable(Never)]` row access for generated code: a ref to a row's first padded cell, plus the row and layer strides. The internal `TileLayout` cannot be named from user assemblies.

You write (replacing `TileSystems.Blur` and Life):
```csharp
[ForEach(Parallel = true, ChunkRows = 16)]
static void Step(Cell<Warmth, BlurPlot> w, out NextWarmth next) =>
    next = new((w[-1,0,-1].T + w[0,0,-1].T + w[1,0,-1].T + w[-1,0,0].T + w.Value.T + w[1,0,0].T
              + w[-1,0,1].T + w[0,0,1].T + w[1,0,1].T) * (1f / 9));

static void Step(Cell<Seed, LifePlot> s, out NextSeed next)
{
    int n = s[-1,0,-1].Alive + s[0,0,-1].Alive + s[1,0,-1].Alive + s[-1,0,0].Alive
          + s[1,0,0].Alive + s[-1,0,1].Alive + s[0,0,1].Alive + s[1,0,1].Alive;  // [0,8]: byte lanes (Kernel.Narrow)
    next = new((byte)(n == 3 || n == 2 && s.Value.Alive != 0 ? 1 : 0));
}
```
How the method is read:
- **Inputs:** `ParamKind.Cell` behaves like Tile for filters, access and EECS059, and shares neighbors per grid (`TileNeighbors.Of`).
- **Output:** `out TOut` is a new `ParamKind.CellOut`. It writes the entity's `DynamicBuffer<TOut>`, with a filter `With<>` and one stamp per entity. If TOut is a Cell input's cell type, that is EECS059.
- **Offsets** must be constants within `Halo`; otherwise the reason is fixable. `TileReads` is derived from them (dz≠0 → NorthSouth, dy>0 → Above, dy<0 → Below, both → Diagonals, always Self).

What the generator writes (one NoInlining routine per method, as the Tile accessor today):
```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
static void Step_0_Cells(Entity entity, in TileNeighbors n0, ref DynamicBuffer<NextWarmth> next, float __f0)
{
    var w = new Tile<Warmth, BlurPlot>(entity, in n0).Pad();          // whole tile, row masks
    var into = next.SetCount(TileLayout-public.Cells);                  // the out buffer
    into.Fill(new NextWarmth(__f0));                                    // rows whose stencil reads only zeros: f(0…), see below
    ref float o = ref Unsafe.As<NextWarmth, float>(ref MemoryMarshal.GetReference(into));
    foreach (var row in w.Rows(TileReads.Layer))                        // derived from the offsets
    {
        ref float c = ref Unsafe.As<Warmth, float>(ref w.RowStart(row)); // padded row, cell x = 0
        ref float d = ref Unsafe.Add(ref o, row.Index);
        int x = 0;                                                      // Width*4 = 64 bytes: widths 512, 256, 128 (constants fold)
        if (Vector512.IsHardwareAccelerated) { var s = Vector512.LoadUnsafe(ref c, (nuint)(x - 1 - S)) + … ; (s * __p0).StoreUnsafe(ref d, (nuint)x); x += 16; }
        /* Vector256, Vector128 copies of the same text */
        for (; x < 16; x++) d.. = Step(new Cell<…>(ref Unsafe.Add(ref c, x), x, row.Y, row.Z)) // scalar cells, as written
    }
}
```
How D77's three objections are answered:
1. **Byte promotion** is handled by `Kernel.Narrow` (intervals).
2. **Width:** element loops emit the widths 512/256/128 chosen from `Width * sizeof(TCell)` (from TTile's constant expression bodies when readable, else all three guarded). This needs a `Vector128`/`Vector256` replacement for the `Kernel.V` token and its `SquareRoot`/`AddSaturate` forms; check that each Narrow helper exists at 128/256.
3. **Per-row summaries** are per-tile results through reductions in the same pass (WP5b), so a stencil with
   summaries, like the voxel water, is written as cells too.

Quiet rows:
- Rows are skipped only when every Cell input is padded with masks and the method does not read X/Y/Z.
- f0 = the scalar method on Cells over `TileMemory.Blank(fill)`, computed once per pass into the job and passed as `__f0`. Skipped rows get f0, which is **exact without proof** and needs no "f(0)=0" assumption.

## 3. Exactness
The rule from `audit-vector-loops` is that results are bit-identical to the scalar method, at 1 and 4 threads, unless fast math is on.

| Form | Default (`FloatMode.Exact`) | Needs `FloatMode.Fast` |
|---|---|---|
| Element-wise buffer, span, InlineArray, ForParallel lambda, Cell bodies | exact: same IEEE op per lane in order (D68); integer division, Clamp and Abs guards bail to scalar (D127) | the D127 Fast lowerings (Pow, Log10, Tan…) as today |
| Constant-trip loops unrolled into the per-row loop | exact, including loop-carried locals and argmin (each lane runs the iterations in order) | — |
| Reductions over elements into outer locals | exact: integer `+ - & \| ^` (wrapping), integer Min/Max in any form, float `Math.Min`/`Math.Max` (associative and commutative incl. NaN and ±0) | float/double `+`/`*` sums; float `if (x < best) best = x` (ties between -0 and +0 depend on order; counterexample in the notes); the reason says "set FloatMode = FloatMode.Fast, or accumulate in an int/long" |
| 1D offset reads | exact given the alias guard (the scalar loop would read new values otherwise, so it stays scalar) | — |
| Cell rows and f0 fill | exact (f0 is computed by the method itself) | — |
| Out-of-range indexes | bounds are checked before the loop; a failure runs the loop as written, which throws at the same element | — |

Proof per form (`audit-vector-loops`):
- Random data plus edges (0, 1, Count−1, Count, Count+1, 1,003 elements; NaN, ±0, ±∞; an empty buffer) compared with the method row by row, bit for bit, at 1 and 4 threads.
- Byte forms over every pair of operands (Life: all 3^9 neighborhoods).
- `LaneMathTests` exhaustive only if a new lowering is added (none planned).

## 4. The analyzer: code that could run as a vector loop

Two rules point users to the vector form (the owner's rule: both cases warn):
- **Plain code that a small change would vectorize: EECS055** (exists, Warning when the reason is fixable, naming the
  statement and the change). Every form of this plan reports it where it stays scalar: element loops at the loop
  keyword, inline-array loops, Cell methods, lambda passes (EECS097), methods with Entity or Emit parameters (the
  `LooksEligible` gap, fixed with the Lookup-gather work). WP8 audits the reasons themselves.
- **Hand-written SIMD that the plain form would generate: EECS103** (below).

**WP8 (with each package): the fixable reasons.** List every reason `Kernel` returns; a reason a user can remove
by rewriting the method (a call that has a vector form under another name, a local written in one branch, a
`Math` overload, a field of mixed types that a split fixes, an early `return` that is a condition) is fixable, names
the rewrite, and gets a code fix where the rewrite is mechanical (as the split of EECS055 has). Scan `samples/`,
`benchmarks/`, `templates/`, `demo/` and the docs fences for methods left scalar and check each is reported with
the right class; a scalar method that a small change vectorizes and that gets no Warning is a bug of WP8.

### EECS103: SIMD written by hand that the generator writes from the plain form
v1 (batch `[ForEach]` methods, their casts, loads and stores) shipped: what qualifies, the guards, the code fix and
where this plan was wrong (no lambda passes: they take no spans; no accumulators; a span only read becomes `in`) are
D134, the detection `src/EasyEcs.Generator/HandSimd.cs`. v2 and v3 extend `HandSimd` and keep its rule: report only a
rewrite bound in a copy of the compilation that gets the generator's loop.

**v2 (after WP5):** a Tile method whose body is `Pad()` (whole tile), a `Rows(reads)` loop, an `x += Count` loop of `Load128/256/LoadVector` at constant offsets, and `.CopyTo(into.Slice(row.Index + x))` into `Mut<DynamicBuffer<TOut>>`. Report it with the Cell form (`TileBenchmark` Blur/Wave/Life fire; VoxelServer `Flow` does not: `Pad(from,to)` and summaries).

**v3:** element loops written with `VectorN.Create(span.Slice(i, n))` (HeightMap) → the plain `for`.

**Suppressions (v1):** `EasyEcs.Benchmark.csproj` `NoWarn` (KernelBenchmark's hand baselines); `#pragma` with a
reason on VoxelServer `FireDamageSystem.Hurt` and the demo's `MovementBatchSystem.Move` until WP7 rewrites them (the
demo's rewrite or keeping the batch form under its pragma is the owner's call).

## 5. Work packages in build order
Agents per the `audit` skill (at most three; pair the one that measures with ones that do not). WP6a touches only analyzer files and can run beside WP1. Kernel work should not run beside the AVX2 wide-rows work, since it touches the same files.

**WP0: Kernel `Binding`, `EmitVectorLoop` with names as parameters, `BodyCopy.cs` moved out of `LambdaPass`. No behavior change.**
- Tests: all `KernelTests`, `LambdaPassTests`, `GeneratorTests` (the generated text for `WorldSimulation.cs`, `BattleServer.cs` and samples identical), caching assertions.
- Benchmarks: `GeneratorBenchmark.EditMethodBody`, level.
- Risk: drift in the text the JIT sees (D126 lesson: even a text cleanup changed inlining). Gate: identical generated text, or identical Tier1 for `World:Masked` runners (`tools/jitdisasm.sh`, `DOTNET_TieredPGO=0`).

**WP0b: generated bodies sized for the JIT inliner: shipped (D135).** Every later form keeps to it: a job's methods
the runner inlines declare at most 32 IL locals (`SystemGenerator.InlinedLocals`, counted by `SystemGenerator.Locals`;
`GeneratorTests.*StayWithinTheInlinersLocals`, `tools/inlining.sh` for the runners). A larger form adds its loops
through `EmitLoops`, which splits them into methods when the count says so.

**WP1: `[InlineArray]` lanes and constant-trip unrolling in the per-row loop.**
- Tests: `KernelTests.InlineArrayElementsMatchTheMethodBitForBit` (Lengths; 4/8/16 elements; flat, interleaved, wide); `UnrolledLoopsKeepLoopCarriedLocals` (argmin, running sum); uniform inline arrays (Waypoints); a regression test for the single-lane `Paths` bug; EECS055 reasons (N > 16, variable bound); 0 B.
- Benchmarks: `KernelBenchmark` new `SpectrumScalar/Generated/HandWritten` (component of 8 floats, 1k/100k) and `NearestScalar/Generated` (8 waypoints, argmin).
- Regression: `CycleHolesGenerated`, `WorldSimulationBenchmark`, `BattleServerBenchmark`.
- Risk: **inlining budget** (D135): an unrolled body ×8 declares locals per element; past 32 IL locals `EmitLoops` moves each width's loop into a method, and a single loop past 32 is not split further, so `World.Masked` would call it per run or per 64-row word. Count the translated body's locals (`SystemGenerator.Locals`), cap N and fall back with a reason rather than lose; `tools/inlining.sh` checks the runners.

**WP2: element loops in `[ForEach]` methods (per-row with buffers, batch-method loops, lambda passes), through the body copy.**
- Tests:
  - new `tests/EasyEcs.UnitTest/ElementLoopTests.cs`: equivalence (Lengths, edges, empty buffer, a buffer shared copy-on-write written in the loop, `Mut.Write` stamps once); integer, Min/Max and float-Fast reductions; bounds failure throws at the same element; private helper reached through a generated accessor; 0 B; 1 vs 4 threads.
  - `GeneratorTests`: the copy, `#line`, caching.
  - `AnalyzerTests.Performance`: EECS055 at loops (fixable vs inherent), EECS056 for `Require`.
- Benchmarks: new `benchmarks/EasyEcs.Benchmark/ElementLoopBenchmark.cs`, at 10k/100k entities and Elements 4/64/1,024, each case Scalar (`SimdMode.Off`) vs Generated vs HandWritten:
  - `Decay` (element-wise buffer float);
  - `HeightMap` (the VoxelServer shape: the hand Vector128 version vs plain);
  - `SumInts` (reduction);
  - `SumFloatsFast`;
  - `BatchLoop` (the plain `for` in a batch method vs the per-row method vs hand).
  - Plus VoxelServer `TickBenchmark` before and after the HeightMap port.
- Risks:
  - the call per row for tiny buffers (Elements=4 must be within noise of scalar, else gate);
  - copy semantics (name binding, `using static`, extension members, nullable; `BodyCopy` reuses Qualifier + Usings);
  - build cost (`GeneratorBenchmark`);
  - debugging (`#line`).

**WP3: offset reads (1D neighbors) and the alias/bounds proofs.**
- Tests: `ElementLoopTests` for offsets −2..+2; written-and-read-at-offset stays scalar with the reason; aliasing through `Slice`/`Cast` of one buffer stays scalar; distinct buffers vectorize; bound proofs at start 0 and 1 and at the end.
- Benchmark: `ElementLoopBenchmark.Smooth3Scalar/Generated/HandWritten`.
- Risk: proof gaps. Prefer scalar with a reason over a guess.

**WP4: the `ForParallel` lambda over a buffer.**
- Tests: `ForParallelTests` (equivalence 1 vs 4 threads, chunk edges at 1,024 multiples, 0 B, EECS095/096/097 on the new call).
- Benchmark: `ForParallelBenchmark` gains `LightLambda` and `HeavyLambda` against `LightForParallel`/`HeavyForParallel` (hand jobs, scalar) and `*Serial`.
- Risk: low; the lambda infrastructure and the IForSpan runner exist.

**WP5: `Cell<TCell,TTile>` (built on WP2/WP3): the headline package.**
- Bar: every Cell case at or below its hand-written `Load128`/`Load256` twin, at 0 B; a case above it is a finding
  for the generator, fixed before the package merges.
- Core: `Cell<,>`, `PaddedTile` row access for generated code, the 128/256 width tokens.
- Generator: `ParamKind.Cell`/`CellOut`, `Kernel.Cells.cs`, emission in `SystemGenerator.cs` (or a new `SystemGenerator.Cells.cs`).
- Analyzer: EECS059 for `out` of an input's cell type; EECS073 for two Cell inputs of one type; reasons (offset beyond `Halo`, non-constant offset).
- Tests: new `tests/EasyEcs.UnitTest/CellTests.cs`:
  - Blur, Laplacian (Halo 2), Life over every 3^9 neighborhood, a 3D 7-point stencil;
  - every `TileBoundary`; the quiet-row f0 (f0 ≠ 0 case);
  - X/Y/Z reads disable skipping;
  - outputs equal to the `Load128` tile versions and to the method cell by cell; 1 vs 4 threads; 0 B.
- Benchmarks:
  - `TileStencilBenchmark` new `Blur9_Cell`, `Laplacian_Cell` against `Blur9_Tile`/`Laplacian_Tile` (target ≤ 1.0x) and `*_Hand`;
  - `TileActiveBenchmark` new `AlwaysOnCell` against `AlwaysOn`.
- Risks:
  - the generated tile routine must stay a NoInlining root with no `call` in the row loop (D77: as an inlined callee it called its loads, 2.9x slower). Check Tier1.
  - Whole-tile `Pad()` vs a partial range.
  - The new public `EditorBrowsable(Never)` surface needs `audit-naming`.

**WP5b: per-tile results and several inputs/outputs, then the voxel water as cells.**
- Generator: reductions from a Cell method into the tile entity's components (the `Reduce<T>` machinery per tile),
  several `Cell<,>` inputs and `out` cells; partial row ranges (`Pad(from, to)`) derived from the offsets and the
  rows the method reads.
- Port `samples/VoxelServer/Systems/Water/Flow.cs` to Cell methods; its output and `TickBenchmark` must match or beat
  the hand-written version.
- Tests: summaries against the scalar method per tile, 1 vs 4 threads, 0 B; the water's output unchanged.

**WP6: EECS103 and its code fix.**
- WP6a (v1, batch casts): shipped (D134).
- WP6b (Tile → Cell) after WP5; WP6c (element loops → plain) after WP2.
- Tests:
  - `AnalyzerTests.Forms.cs`: positives (FireDamage, StepBatch, StepBatch512, Movement demo shapes; each load/store idiom); every guard as a negative; the overlap test (no EECS055/EECS033 on the same method).
  - `CodeFixTests.cs`: before and after text for each positive.
- Benchmark: `GeneratorBenchmark` (analyzer cost) level.
- Risk: the cost of binding in a compilation copy. The prefilter keeps it to methods that name vector types.

**WP7: samples, docs to-dos, decision records** (written in the docs pass).
- Samples:
  - `HeightMap.cs` → plain loops;
  - `FireDamageSystem` → per-row (the EECS103 fix);
  - `TileBenchmark`'s Cell cases;
  - demo pragma or rewrite;
  - VoxelServer `Flow.cs` becomes Cell methods (WP5b).
- Docs: `systems.mdx` vector loops (inner loops), `dynamic-buffers.mdx`, `grids.mdx` (Cell form with "You write / The generator writes", Load128 examples rewritten), the ForParallel section, `analyzer.mdx` EECS103 (en+zh), the public skill, glossary (shader, stencil, halo, lane), `api.mdx` complexity rows, changelog, roadmap.
- Decision records:
  - D132: element loops share the core; the body is copied (considered: compilation-copy synthesis, intercepting IForSpan jobs, `Element<T>`, which the owner declined).
  - D133: `Cell<,>`, the general stencil form; D77 rewritten to the decision that is true then.
  - D134: EECS103.
- Update `audit-vector-loops` SKILL: a ForParallel job's Execute is compiled only in lambda form.

## 6. Drop or defer
- **Intercepting hand-written `IForSpan`/`IForRange` jobs:** dropped. D63/D18 allow interceptors only for lambda passes, and it would need field accessors into user structs. The lambda form (WP4) covers it. Possible later rule: an Execute that is one plain element loop → "write it as a lambda pass".
- **`ForParallel(count, values…, static (int i, …) =>)` over arrays:** deferred until a use asks (D80: no use case beyond buffers).
- **Gathers through index lists (MD neighbor lists, Lookup):** deferred to the owner's separate "retry vectorized reads, sorted by table, AVX-512" task. D90 measured 0.8–1.18x.
- **Early-exit loops (searches, `break`):** short, exactness of the first match plus side effects; not worth it.
- **Prefix sums / recurrences:** need reassociation or log-step shuffles; the owner declined `Scan<T>`.
- **Argmin with index across elements in element loops:** a lane-order fold breaks ties differently (the ±0 example). The per-row unrolled form (WP1) already covers the BattleServer shape.
- **Hook bodies (`OnUpdate`) with element loops:** run once per update; not hot.
- **Masked AVX-512 tails for element loops:** retried with every job inlined (D126, D135), the tail still lost to its copy in and out of a 64-row buffer; only worth trying again where a run is mostly tail (tables of a few rows) or the copy goes away. Measure later.
- **Pow strength reduction:** already settled by D127. Pow rewrites are not exact, so they are Fast-only, and that is done.
- **Temporal blocking, separable kernels, the double-buffered cell component:** D77 dropped these; they come back
  only with a measured case where the Cell kernel cannot reach the hand-written one without them.
- **One body for both widths (`ISimdVector`), our own polynomials:** stay out (D126/D127).

### Critical Files for Implementation
- /workspace/EasyEcs/src/EasyEcs.Generator/Kernel.cs (`Translate`, `Statement`, `IsUniform`/`Scalar`, `Paths`; plus new `Kernel.Loops.cs` / `Kernel.Cells.cs`)
- /workspace/EasyEcs/src/EasyEcs.Generator/SystemGenerator.cs (`EmitAccessor`, `EmitVectorLoop`, `EmitFlatLoop`, `ForEachModel.Simd`/`OutOfLine`, `TileModel`)
- /workspace/EasyEcs/src/EasyEcs.Generator/LambdaPass.cs and /workspace/EasyEcs/src/EasyEcs.Generator/SystemGenerator.Lambdas.cs (Copy/Qualifier → `BodyCopy.cs`; the ForParallel lambda)
- /workspace/EasyEcs/src/EasyEcs.Core/Tile.cs (new `Cell<,>`, `PaddedTile` row access) and /workspace/EasyEcs/src/EasyEcs.Core/World.For.cs
- /workspace/EasyEcs/src/EasyEcs.Generator/EasyEcsAnalyzer.Forms.cs (EECS103) with /workspace/EasyEcs/src/EasyEcs.CodeFixes/EasyEcsCodeFixes.Forms.cs
