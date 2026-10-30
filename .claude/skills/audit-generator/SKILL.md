---
name: audit-generator
description: "Audit the EasyEcs source generators (ArityGenerator, ComponentGenerator, SystemGenerator, PartGenerator; the vector loops are audit-vector-loops) on their three jobs - generate what users would write twice, turn the plain form users write into the fast one (vector loops, per-chunk parameters, schedule), and stay fast on every keystroke (incremental best practices, caching, newer Roslyn features). Use when changing a generator, adding a generated form, or reviewing IDE/build performance. Triggers: SystemGenerator, ComponentGenerator, ArityGenerator, PartGenerator, *.g.cs, slow IDE, boilerplate, interceptors, attributes, too many attributes, overlapping declarations, 源生成器, 生成代码, 样板代码, 少写代码, 标签, 属性, 标签太多, 重复的标签."
metadata:
  internal: true
---

# Generator audit

Files: `src/EasyEcs.Generator` (netstandard2.0, loaded by every Roslyn host): `ArityGenerator`, `ComponentGenerator`,
`SystemGenerator`, `PartGenerator` (+ `Parts.cs`), `Code.cs`, `EquatableArray.cs`; `Symbols.cs` is
shared with the analyzer (`audit-analyzer`).

## The three jobs

1. **Users write a thing once** (`feature`, part 2). Generated today: members per component (`entity.X`, `SetX`,
   `TryGetX`, `world.XIndex`), arity families, system code and the set per assembly, jobs and entry points
   (`[ForEntity]`), parts (`[Sum]`, `[LookupKey]`, `[Group]`), type setup (`[GroupBy]`, `[WhenTargetDestroyed]`,
   `[NotSaved]`), the save registry. Look for boilerplate still left in `samples/`, templates, `BattleServer.cs` and
   the docs examples (a hand-written job, a reset-then-accumulate singleton, a registry call, a copy per arity): each
   is a candidate for an attribute, a parameter or a generated member, with the rule and code fix that move users to
   it.
   The owner's rule (2026-10-27): whatever lets users write less code, less boilerplate or think less about how to
   make it fast is done by the generator without users noticing (vector loops, component splits, schedules): if we
   can do it, we do it. A gap is declined only when it saves users neither lines nor thought.
2. **Users write the plain form, the generator emits the fast one** (Burst's idea, D68 / D90). A `[ForEach]` method
   becomes a vector loop (`audit-vector-loops`); a parameter does per chunk what users would do by hand (`Reduce<T>`,
   `Emit<T>`, `Scratch<T>`, `Lookup<T>`, `Tile<,>`); the schedule follows declarations (`Parallel`, `ChunkRows`,
   `Slices`, `[UpdateInterval]`, `[Consume]`, stages from read/write sets). Look for forms that fall back to scalar
   or to a serial pass, rank them by how often they appear (the EECS055 / EECS056 reasons over the samples and docs),
   and support the most frequent next. The research behind a new form belongs to `audit-perf`.
3. **The generators are fast**: they run on every keystroke of every user (below).

## The declaration surface (attributes, marker interfaces, parameters)

Users pay twice for declarations: the code they write, and the choice between declarations that look alike. Both
count. Each round lists every public attribute, marker interface (`IComponent`, `ITracked`, `IIndexed<K>`, ...),
`[ForEach]` parameter type, attribute property and `WorldOptions` member (the in-flight branches' new ones too), and
for each one answers:

- **What code does it remove?** A before / after a user would write. A declaration that removes nothing is a
  candidate to drop.
- **Could the generator infer it** from code users already write (a method that destroys every row it visits is
  `[Consume]`; a parameter's type already says read or write)? Prefer inference, then a parameter type, then an
  attribute; an attribute the analyzer can suggest from the code is a sign it could be inferred.
- **Does it overlap another?** Two ways to say one thing (an attribute and an interface, an attribute and an option,
  a family that could be one attribute with a value, filters that compose into another) are merged into one: one way
  per meaning.
- **Is the family consistent?** Siblings share a shape (`With<T>` / `Without<T>` / `WithAny<T>`), names (`audit-naming`)
  and the same placement (type, method, parameter).
- **Is access declared by the type?** A parameter's type says read or write and the generator never infers it from the
  body: `in T` / `ref T`, `ReadOnlySpan<T>` / `Span<T>`, `ReadOnlyMaybe<T>` / `Maybe<T>`, `Mut<T>`, `in` / `ref`
  singletons. The read-only type exists so the compiler stops a write and the schedule sees a reader; a write type that
  only reads is EECS025 with a fix to the read-only one. Every new parameter kind that can write comes with its
  read-only form; one read form may serve several write forms (`in T` is the read of both `ref T` and `Mut<T>`, so
  there is no read-only `Mut<T>`: `Mut<T>` only changes how writes are stamped, and a `Mut<T>` never written is
  EECS025 with a fix to `in T`).
- **What is still written twice?** Boilerplate in samples, templates, `BattleServer.cs` and docs examples that a new
  declaration (or inference) would remove (job 1 above).

Report adds, merges, drops and inferences to the owner with the before / after and how many call sites in samples
and docs change; a public change is a breaking change (migration entry, code fix, analyzer rule from the old form).
The docs carry one "which declaration" table by task (`audit-docs`), and the analyzer suggests the right declaration
when code uses a weaker one (`audit-analyzer`).

## ECS without the burden

ECS's usual costs are business logic scattered across components and systems, boilerplate, and repeated code. The
goal is that users write ordinary code and get the ECS form and its speed without writing it. Each approach below is
a candidate; weigh it against the decisions (D18 interceptors, D63 Roslyn features), build it with numbers, and
record a dropped one.

- **Scoped inline queries** (C# interceptors): a lambda or `foreach` over a query inside a method becomes a generated
  job, its captured locals the job's fields, eligible for vector loops and `Parallel`. D18 dropped interceptors for
  speed; this reason is boilerplate, so it reopens only with that reason and a benchmark showing the job is as fast.
- **Views / aspects** (`[View]` structs, Unity `IAspect`): several components of one entity grouped with business
  methods (`player.TakeDamage(n)`), generated accessors over the row; logic stays in one place.
- **Archetype as a class** (`[Entity]`): a type lists its components; the generator writes the prefab, the creation
  call, the query filter and the systems from its methods.
- **Feature modules** (`[Feature]`): components, systems, groups and setup of one feature registered together.
- **Data events**: an event type declared once; the generator writes the buffer, the emit call and the reader system.
- **Schedule apart from the algorithm** (Halide): `ChunkRows`, `Parallel`, `Slices` chosen from `SystemStats`
  measurements instead of by hand (autotuning).
- **Pass fusion** (FlashAttention, loop fusion in Halide / XLA): passes over the same rows run as one walk, so a row is
  loaded into cache once. Fused automatically when it is provably the same result: the same filter, no structural
  change, no read of another row the first pass writes (`Lookup<T>`), no per-pass result the second reads (`Reduce<T>`,
  `Emit<T>`), no `[Changed<T>]` / `[Removed<T>]` on what the first writes, the same cadence and parallel settings;
  within one system by declaration order, across systems when the schedule puts them next to each other. What stops a
  fusion is reported as a fixable reason (`audit-analyzer`), and the docs teach merging by hand where it cannot be
  proven. Proof: a benchmark of fused against separate passes in a world larger than L2.
- **Hot/cold layout split**: fields a pass never reads moved out of the column it walks (measured access).
- **Migration**: a code fix from object-oriented code (a class with update methods) to components and systems, and an
  AI migration guide in the public skill (`skills/easyecs/SKILL.md`).

## Vector loops

The vector loops (`Kernel*.cs`: what becomes SIMD, side effects, fixable reasons, proof) have their own skill:
`audit-vector-loops`.

## Tuned for the machine (the owner's rule)

Users write plain methods and never think about the pipeline, the caches or the branch predictor; the code the
generators write does. Every generated form is checked for:

- **Chains**: a loop-carried dependency (one accumulator, a running value) is the bound when its latency exceeds the
  loop's work; independent accumulators or two vectors per iteration break it, within the 32-IL-locals budget
  (`audit-vector-loops`) and the exactness rules.
- **Branches**: conditions become masks or selects; what is rare goes out of line (`NoInlining`); a test before a
  masked block stays only where its outcome is predictable; conditions that pick rows belong in data (tags), and the
  analyzer says so.
- **Caches**: each row loaded once (fusion), columns walked in order, chunks on 64-row multiples, chases prefetched,
  write-only streams past the cache not read for ownership.
- **Front end**: cold code out of line, bodies within the inliner's budget.
- **A form that pays only past a size is a twin, chosen per pass**: never replace the job for every pass. Emit a
  second job and let the call site pick it from a gate read once per pass (the VectorTail `Rows` twin, the
  prefetching `Job{i}Chases` behind the last-level cache gate). The in-cache case then runs byte-identical code, and
  its A/B only has to show the gate check costs nothing. Replacing the job made parallel heavy rows 3x slower at 1k.
- **An owned form picks its path by use** (owner, 2026-10-28): a form that replaces what users write by hand (ping-pong
  instead of step + commit, a sleeping pass) must win where the idea wins; a loss is the generated code's fault, not
  the idea's. Read its Tier1 listing next to the hand-written form's, line by line (calls, range checks, per-unit
  loads and branches, lookups, spills, dead loads), and choose the path from use: dense walks every unit linearly,
  sparse walks the work set, picked per pass from a measured gate or by the generator when the declaration says.
  Never drop the form on one loss before the listing explains it.
- **An extra job per method skips methods in a fusion group**: they never run alone, so the twin is never built
  (CS0649 on its fields) and the method's schedule line would claim a form it does not run.

Patterns the Tier1 listings of the tile passes found (feat-tiles-pingpong), each a helper call or an extra
instruction per use:
- a `static readonly` field of a generic class is read through a static-base helper (`call rax`) where the JIT does not
  fold it: write a shape's constants as expression properties;
- an InlineArray of object references on the stack stores through `CHECKED_ASSIGN_REF` each: keep the references in
  ref locals;
- `Unsafe.Add(ref, dx + constant)` with an `int` sign-extends per load: widen alone, `(nint)dx + constant`, so the
  constant folds into the load's displacement;
- a thread-static is a helper call on every read: read it once and pass the frame;
- a `ref` into an element of `int[][]` (covariant arrays of arrays) adds `LDELEMA_REF`: take the ref only on growth;
- an `[UnsafeAccessor]` stub runs at MinOpts (about 1 % in a profile; a call, not inlined).
Tier1 listings of a benchmark method: `--inProcess` with `DOTNET_TC_CallCountingDelayMs=0`.

How to see the bound without hardware counters (the VM has none): `llvm-mca` on the Tier1 loop
(`tools/jitdisasm.sh`), then BenchmarkDotNet with controlled data (sorted against shuffled, 1k / 100k / 1M rows).
The open work is `docs/plans/microarch.md`.

- **An out-of-line method of the job** (a `NoInlining` loop such as the streaming loop) that takes a ref struct of
  the run (`in MaybeColumn<T>`) together with `ref` column arguments fails CS8350 (ref safety): exclude `Maybe<T>`
  from such a method, or pass what it needs differently.

## The generated text (rules for every template; read it every round)

Generated code is code users read: Go To Definition opens it, the docs' "The generator writes" tabs show it, and its
size is build time. These rules hold when a template is **written** (a new form, a new parameter, a fix), not only
when it is audited; `feature` part 2 points here. The survey that found each kind, with counts and template
locations: `docs/plans/generated-text.md`.

Write it like hand-written code:

- **Short names through one alias block.** Every type the body names more than once goes through a file-level
  `using` alias with a reserved `__` name (`using __V512i = global::System.Runtime.Intrinsics.Vector512<int>;`), one
  per closed type the file uses; the body never spells `global::System...` (about half of SystemGenerator's text
  was qualified names). Never a plain `using System.Numerics;`: a user type named `Vector` would win.
- **No line that does nothing**: no identity copy (`var __c = __s;`), no temporary that no aliasing needs (`__t`),
  no local nobody reads (deinterleaved fields, broadcasts, pair masks, accumulator holds a body never uses), no empty
  method (an empty `Create` / `Destroy` hook: interface defaults instead), no `x = default` the constructor already
  gives, no `(object __owner)` parameter nobody reads.
  A copy is not free in IL: each local counts toward the JIT's limit of 32 per inlined method (`audit-vector-loops`,
  "The JIT never inlines a method that declares more than 32 IL locals"); the loads' `out __s` -> `__c` copy alone
  took Move3's masked `Execute` from 24 to 36 locals and out of the runner. Every generated method a runner inlines
  stays at 32 IL locals or fewer: the generator counts what it declares and splits before the limit, one method per
  width (`__Loop512`, `__Loop`, `__MaskedLoop512`, ...; D135); `tools/inlining.sh` proves it on the benchmarks.
- **Say a value once**: a constant is `.Zero` / `.One` / a literal in place, not a local made per width
  (`var __u1 = Create<int>((0))`); setup that does not depend on the width (singleton refs, `Identity`,
  `this.v0 = default`) is written once above the width blocks; a mark written once is assigned, not zeroed then OR-ed
  (`__d0 = Zero; __d0 |= x` -> `var __d0 = x`); a table or broadcast with the same value is declared once per type.
- **Parentheses only where precedence needs them**: no `((0))`, no parentheses around a whole right-hand side or
  argument (the printer adds them where an operator binds weaker).
- **Names a reader can follow**: locals after the parameter and field they hold (`__hp_Value`, not `__c1_0`; `hp`,
  not `r1`/`e1`); the PDB is the only place local names live, so this costs nothing.
- **Generate once, refer to it**: a form that needs code another file already generates names that code (a fused
  pair runs the systems' own jobs; it does not copy them: fusion files were 76 % copies). The same body per vector
  width is emitted from one template (`Kernel.V` token); the output copies stay while .NET has no public
  `ISimdVector` (a wrapper of our own turns `a + b * c` into calls); per-width methods are WP0b's answer to the
  inliner (`audit-vector-loops`), not to the text.
- **Layout**: indentation and line breaks as a formatter would leave them (ArityGenerator output had 270 lines off).

Prove a text change by what it changes:

- **Text only** (names, aliases, parentheses, layout): method-body IL identical between base and head (build both,
  diff the IL of the benchmark assembly); no benchmark needed.
- **IL changes** (a copy, a local's order, a hoisted value, anything near the inliner's budget): Tier1 listings with
  `DOTNET_TieredPGO=0` of the Move3, Cycle, CycleHoles, WorldSimulation, BattleServer and MultiWorld runners
  identical (`tools/jitdisasm.sh`, `audit-vector-loops`), else BenchmarkDotNet decides; no `call ...Execute` appears.
- `GeneratedTextTests` (to build first, `docs/plans/backlog.md`) runs the generators over the Kernel, Fusion and LambdaPass corpora and fails on unused locals
  and identity copies (Roslyn data flow; D126's copy allow-listed), constant broadcasts, a Zero OR-ed once, doubled
  or redundant parentheses, duplicate tables, empty methods and `global::` outside the alias block. A new form adds
  its sources to that corpus. GeneratorBenchmark reports output bytes next to time.

Reading the output: build with `-p:EmitCompilerGeneratedFiles=true`, and read only files the build just wrote:
`obj/**/generated` keeps files of forms the generator no longer writes (filter by time, or delete the folder first).

- **Reading a method's body for calls**: a C# 14 extension-member call reports the extension block as its
  `ContainingType`, so match it by the block's outer type (`Chases.IsKeyLookup`); and any body analysis that looks
  for key, lookup or member calls reads the lambdas inside the body too (missing them made a lambda pass that found a
  non-unique key throw "holds no index").

- **A ref-struct job called in a loop is copied to a local first**: through a `ref` the JIT reloads it every
  iteration (that alone made `[ForEachRecord]` 2x slower than the hand-written walk).
- **A new tracked stage needs input in GeneratorTests' code**, or its caching assertion has nothing to check.

## Speed: measure first

`GeneratorBenchmark`: the four generators over the battle server plus 40 generated modules (components of every
kind, buffers, systems, query loops); each case with `OneNamespace` false and true (10 cases, about 90 s each).

```bash
tools/bench.sh GeneratorBenchmark
```

- `Cold`: a first run (a build, the IDE opening the project), mostly emitting files.
- `EditMethodBody` / `EditUnrelatedFile`: one keystroke with the previous driver kept. **The number that matters.**
- `Compile` / `CompileAndAnalyze`: a build's diagnostics without and with the analyzer; the difference is the
  analyzer's cost.
- Setup throws if the code does not compile, the generators skip it, or the analyzer reports it: the modules are
  written as users should write them.
- Compare against the base back to back (the base's `src/EasyEcs.Generator` checked out, run, restored, run) with
  nothing else on the machine, both sides compiling the tip's input (names the base lacks mapped back). Keep a change
  only if time and allocations both improve beyond the error; numbers go into `decisions.mdx` (D62).
- Finding the cost: `--profiler EP` or `[EventPipeProfiler(EventPipeProfile.GcVerbose)]`, stacks aggregated by
  frame. Driver time under `AdditionalSourcesCollection.Add` is mostly GC: cut allocations, not files.

## Speed: incremental rules (each is checked)

- `ForAttributeWithMetadataName` or a syntax-only predicate first; no `SemanticModel` in predicates. Components are
  found by interface, so they use `CreateSyntaxProvider`.
- **A transform reruns on every compilation change for every match** (FAWMN and `CreateSyntaxProvider` both hand it
  the new compilation), and a rerun that returns an equal model still costs its work; only the steps after it are
  `Cached`. So a transform reads declarations only (signature, attributes, parameter kinds, owner); facts that need a
  method body (vector loop, stability, parallel problems) are computed in a pipeline that feeds
  `RegisterImplementationSourceOutput` only, bound once per body, with every fact derived from that one
  `IOperation` tree. An entry point users call never depends on a body fact. (Phase 1 measured 300-350 ms per
  keystroke on the unit tests, any edit anywhere, from this.)
  A transform that must bind every file it matches (the `Recorder`, which writes what other assemblies read) caches
  its facts per `CompilationUnitSyntax` root in a `ConditionalWeakTable`: an unchanged tree keeps its root across
  compilations, so only the edited file is read again (a plain syntax provider cost 5.3 MB per edit, the cache 0).
  A stage tolerated in `AnEditRerunsOnlyTheStepsItChanges` reruns on every edit: measure it in `GeneratorBenchmark`.
- **Declaring-side output skips EasyEcs.Core**: Core runs its own generator, so a feature that writes something for
  other assemblies to read must not run on Core, or Core's API changes how user code vectorizes.
- netstandard2.0 has no `System.Index` / `System.Range`: no list patterns or `^1` over arrays in the generator.
- **No body binding in transforms**: `GetSymbolInfo` / `GetTypeInfo` inside a member body binds the whole body on
  every edit. Bind a type name alone with `Symbols.TypeNamed` (at the type's open brace, `Symbols.Outside`; the end of
  the file for top-level statements). With nullable off a bind costs the enclosing statement only, with nullable on it
  runs nullable analysis of the whole member: a test that guards against body binding enables nullable
  (`AnEditElsewhereBindsNoBody` passed falsely without it).
- **Speculative models, never compilation copies**: to bind code the user did not write (an inlined callee, a
  desugared view), host it in a speculative model of the method body (a new local function can change the method's
  parameters). A copy of the compilation per method cost +80 ms and +19 MB per keystroke for 40 view methods.
- **Body facts belong in implementation-only stages** (`SystemLoops`, merged back by `SystemCode`): what a method
  body vectorizes to is never computed in the declaration transform, which reruns on every IDE keystroke.
- **Measure the analyzer too**: when more methods vectorize, report CompileAndAnalyze − Compile next to the
  generator numbers; a full translation costs more than an early bail.
- **No display strings**: `Symbols.Named(symbol, "Ns.Type<T>")`, never `ToDisplayString() ==`.
- Models are equatable and symbol-free (`EquatableArray`, records of strings and ints).
- No `CompilationProvider` in a per-item pipeline, and no `Combine` with a whole-compilation value: `Select` the
  narrow fact first (`CompilationProvider.Select((c, _) => c.AssemblyName)`, `ParseOptionsProvider`), which is cached
  by value. `Collect()` only small models, and only into an output that needs all of them (the system set) or to drop
  duplicates of small models; everything else is one output per item. Several attribute providers that feed one model
  per type key the model on the type's first attribute (PartGenerator) instead of a Collect/Combine regrouping.
- **Strings**: no `SyntaxNode.ToString()` / `ToFullString()` / `NormalizeWhitespace()` in transforms (identifier
  text and symbol names only); emit through one `StringBuilder` per output, `SourceText.From(text, Encoding.UTF8)`,
  one file per model (no file that grows with the whole compilation).
- `cancellationToken.ThrowIfCancellationRequested()` in loops of transforms and emitters that walk members, bodies or
  rows of templates.
- `WithTrackingName` on every stage; `GeneratorTests.EditsThatChangeNoModelHitTheCache` lists them (`TrackedStages`)
  and asserts the cache after edits elsewhere, in a method body and in a `[ForEach]` body. `Unchanged` is not a cache
  hit: it is what a transform that reran and returned an equal model reports, so the test counts reruns (the step's
  input reasons, or a test-only counter) and runs once more with `IncrementalGeneratorOutputKind.Implementation`
  disabled (what VS does while typing). GeneratorBenchmark has the same IDE-mode case next to `EditMethodBody`.
- Implementation output for what user code never names (system code, `TypeRegistry`); source output for members it
  calls.
- No diagnostics from generators: the analyzer reports through the same `Symbols` checks.
- Deterministic output (sorted, culture-invariant), unique hint names, `[GeneratedCode("EasyEcs.Generator")]`.
- Template text in `Code.cs`; arity families from `ArityGenerator` templates, never by hand.
- Roslyn floor `Microsoft.CodeAnalysis.CSharp` 5.0.0 (C# 14 extension members); attributes ship in EasyEcs.Core, so
  no embedded attribute definitions.

## Newer Roslyn features (verdicts: D63)

- Interceptors: not used (D18): a `static readonly` filter already gives what intercepting a filter chain would, and
  `Get` / `Has` / `Set` / `CreateEntity` / `Index` fold their type's bit as a static readonly field. Re-evaluate only
  with a measured cost at a call site that the call's own arguments do not cause, or for boilerplate: scoped inline
  queries become generated jobs ("ECS without the burden" above) when they run as fast as a `[ForEach]` method.
  Used where a call site has a measured cost of its own: lambda passes (`SystemGenerator.Lambdas.cs`) and tile loads
  in a provable column loop (`SystemGenerator.TileLoads.cs`, one range check per loop). An interceptor of a hot call
  is `AggressiveInlining` (or the caller's inline budget leaves the call in the loop); a generic `this in` receiver
  works, and its arity is the containing type's plus the method's. The JIT puts a multi-use complex argument in a
  temporary, so `(x + k) + r` costs an add per use where the hand form folds `k` into the displacement.
- In use: C# 14 extension members, generic attributes (`[With<T>]`), FAWMN, implementation output.

## Done means (each new generated form, on top of `feature`)

- Its generated names (members, types, hint names) pass `audit-naming`.
- A generated-output test and a caching assertion in `GeneratorTests`; a form over components also compiles them
  from a referenced assembly (`ForeignLibrary()`) and generates the same text as from source.
- Field-to-property checks go through `Symbols.Associated` / `MemberName` / `BackingField`, never
  `IFieldSymbol.AssociatedSymbol` directly: Roslyn leaves it null on metadata (`<Name>k__BackingField`), so a
  component of another assembly loses its lanes.
- A stage that reads method bodies catches its own exceptions and turns them into that method's reason (Kernel
  does): one exception escaping a stage drops every generated file of the project (CS8785, then hundreds of CS7036).
  Catch everything but `OperationCanceledException`, word it through `Kernel.FailedTo(what, e)`, and keep the plain
  form (8f: chases, pair cutoffs, fusion sides).
- A stage that maps operations to the method's parameters checks the parameter belongs to the method: local
  functions and lambdas in the body have their own parameters, whose ordinals collide (`Chases.Owns`). A partial
  method's body binds the implementing part's parameters, other symbols than the declaring part's: a map keyed by
  parameter symbol holds both parts (`PairForm.ReadCutoff`).
- When the generator and the analyzer share a check, both call one function that returns the reason; two calls drift
  (EECS106 ignored the cutoff failures the generator fell back on).
- Comparing generated output: build with `EmitCompilerGeneratedFiles` and `--no-incremental` into a clean folder;
  the generated folder keeps files of earlier builds.
- The analyzer reports what the generator skips (same `Symbols` / `Kernel` function); a code fix moves hand-written
  code to the form.
- A benchmark of the generated form against the hand-written one (`ForEntityBenchmark`, `ConsumeBenchmark`,
  `KernelBenchmark`, `ReduceBenchmark` are the pattern), and GeneratorBenchmark for the per-keystroke cost.
- `You write` / `The generator writes` from real output wherever the form appears in an example: guide page, README
  (`<details>`), docs home, getting started, battle server walkthrough (a docs to-do; `audit-docs` part 3). A change
  to a generated form lists every page showing its old output.
