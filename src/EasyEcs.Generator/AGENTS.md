# EasyEcs.Generator: file map

> Read when: reading or changing the generators, the vector loops (`Kernel*.cs`) or the analyzer.

Roslyn generators + analyzer (netstandard2.0, loaded by every Roslyn host). `audit-generator`, `audit-vector-loops`
and `audit-analyzer` audit this directory; code fixes live in `src/EasyEcs.CodeFixes` (root AGENTS.md).

When you add, rename or delete a file here, update its line (`verify` checks every file is listed).

```
ArityGenerator.cs            T1..T9 families of EasyEcs.Core (Entity.Set/Remove, World.CreateEntity/CreateEntities/
                             DestroyAll, Instantiate with components, IForEach/IForEachEntity/IForEachBatch/
                             IForEachMasked, ForEach/ForEachEntity/ForEachBatch/ForEachMasked/ForEachParallel/
                             ForEachEntityParallel/ForEachBatchParallel with their row loops, CollectIds,
                             ForEachCollected (MaxRows: ids collected, the job run on copies outside any pass, Entity.RunCollected),
                             Query/Count/Any, QueryItem/QueryEnumerator, Filters.g.cs: QueryFilter.WithAny /
                             ChangedAny / RemovedAny and their attributes), one template each; World.Lambdas.g.cs:
                             World.ForEach / ForEachParallel(values..., Delegate), the calls lambda passes intercept
SystemGenerator.cs           [ForEach] -> struct jobs (per entity or per run of rows; INoStructuralChanges when
                             the method cannot change structure; Parallel -> chunked jobs; [Changed<T>] /
                             [Removed<T>] -> Since(LastRunTick + 1) and a read of T; Mut<T> -> a write stamped per
                             write; Slices -> the slice of the run; ChunkSize -> the chunkSize argument; Lookup reads at an id of an in component, key probes (TryFind / Find / Count at an in component's key) and reads at the ids of an in buffer's elements (Chases), no vector loop -> a twin job (Job{i}Chases) over runs of rows prefetching each chase's stages k rows apart (records 2k and rows k ahead; probes by the index's representation), which the pass runs past the last-level cache (EmitChases, EmitPass; parallel: the twin is a batch job per chunk, ForEachBatchParallel); [Consume] -> DestroyAll of
                             the filter after a pass without structural changes, a destroy per row otherwise; MaxRows -> world.ForEachCollected
                             over the method's IForEachEntity job (no vector loop, chase twin or fusion);
                             [EntityMethod] -> the IForEachEntity job and an entry point Owner.Method(entity, values)
                             over entity.Run; [Drain] -> an IDrain<T> job run by
                             Emitted<T>.Consume (stage RecordMethods, joined to the methods of the updates); a ref Tile -> a stepping pass (the method's colors struct, TileSteps.Of per pass, Enter / the sweeps / Leave per tile, a walk per color until Next: the tiles that woke when the pass sleeps and the method has no filter of its own, through the job's ITileStep.Step, else the query; the wakes of what else it reads; a write of its cells); a closed Tile of any kind -> TileCells.Pool in a module
                             initializer of the type's file (__TilePools: the pool of the cells' buffers sized from the shape); [EcsSystem] -> the code struct __X_SystemCode (hooks, then the
                             methods in declaration order; [UpdateInterval] -> its Interval / Phase), and per
                             assembly the set __EasyEcsSystems: one Add per system and per closed generic system any
                             API names (AddSystem<G<P>>, [UpdateAfter<G<P>>], ...), a switch per hook; no system
                             needs partial, its methods are reached through UnsafeAccessor);
                             SystemGenerator.Fusion.cs: pass fusion, consecutive [ForEach] methods of a system as one
                             walk (one fused job per group: per row, per block of FusedBlock rows, per word of a
                             masked walk) and pairs of systems declared or ordered one after the other as one job
                             (__A__B_Fusion, run by the set's RunFused switch when both are adjacent, enabled and due); a walk with a chasing
                             method gets a twin walk (..._FusedNChases) of its methods' twins, picked past the cache;
                             the type models (systems, other types' [ForEach] / [Drain], [EntityMethod]) are built from
                             declarations alone by the transform of the node holding the type's first attribute (no
                             Collect); entry points are source output calling a partial pass whose body the
                             implementation output writes;
                             SystemGenerator.Kernels.cs: what a method's body decides (vector loop, stable, parallel,
                             identities, a Lookup's key, chases, pair job, out of line, fusion side: BodyModel) in
                             stages only implementation output reads (ForEachBodies, RecordBodies, EntityMethodBodies,
                             put back into each type's model by SystemCode / ForEachCode / RecordCode / EntityMethodCode),
                             so an IDE typing never binds a body and a body's edit changes no source output;
                             SystemGenerator.Loops.cs: the loops of a job's Execute Kernel translated (each width's
                             vector loop, the flat loops, the vector tail), in Execute while it declares at most 32 IL
                             locals (Locals counts them), else each in an AggressiveInlining method of the job (D135);
                             StreamLoop: a NoInlining method of the job (__Stream) streaming the write-only columns,
                             called once per run past the last-level cache (StreamingStores.Pays)
SystemGenerator.Pairs.cs     a pair method ([Neighbor] parameters, PairModel): the pass (world.ForPairs), the order
                             struct (Morton key of the compared component over the axes the test names), the job
                             (the cutoff from the test, per row or one; Width; Rows: the method per pair, over the row's
                             list of neighbors when the run carries one, a Mut<T> over the run's change-tick column;
                             Vectors: a cluster's lanes against each neighbor through Kernel's pair mode, under its
                             gate; Reduce<T> results after the pass, a parallel job's Merges / Merge of its chunks; a
                             method writing both sides, IsSymmetric: each unordered pair once, the half of a table
                             against itself, IPairJob.Symmetric)
PairForm.cs                  what a pair method is, read once for the generator and the analyzer: Problem (EECS105's
                             reason; IsSymmetric / BothSides: a component by 'ref' of the row and of the neighbor, the
                             method writing both sides of each unordered pair, and what that form refuses), ReadCutoff (the distance test: dx * dx sums, DistanceSquared, LengthSquared, and the
                             cutoff: values, singletons, the row's in components, as C# the pass computes once; EECS106's reason, a failure included), HandWritten
                             (EECS107: a loop over other entities, read through a Lookup<T> of the row's component or walked as a query of the World,
                             testing their squared distance)
PairRewrite.cs               EECS107's fix, read once for the analyzer and its code fix: a search over candidate ids
                             read through Lookup<T> or a walk of world.Read / Query rewritten as the pair method ([Neighbor] parameters, the cutoff
                             test returning; a walk of the candidates after the row writing both sides as the method
                             writing both sides), only when mechanical (the search is the whole body, the pair reads
                             nothing of it, the generator reads its test)
Fusion.cs                    the fusion rule read once for the generator and the analyzer (FusionSide: what a method
                             reads, writes, filters and blocks it; Fusion.Problem: why two passes cannot share a walk)
SystemGenerator.Lambdas.cs   lambda passes: world.ForEach / ForEachParallel(values..., static (ref P p, ...) => ...)
                             -> an interceptor per call ([InterceptsLocation], one file per source file in the namespace
                             EasyEcs.Generated) running the job a [ForEach] method with the lambda's parameters and body
                             gets (body copied with names qualified and constants inlined; the static delegate per
                             row where it cannot be: EECS096); its calls and the tile loads are bound after their syntax
                             provider (SystemGenerator.BoundLater), so an IDE that skips their output never binds them
SystemGenerator.TileLoads.cs tile loads checked once per loop: c.Load128 / Load256 / LoadVector(x + k, dy, dz) in a
                             proven column loop -> an interceptor per loop and offset (one file per source file in
                             EasyEcs.Generated, implementation output): Covers, the loop's first and last column against
                             the padded row in constants the JIT folds, then a load at one zero-extended index off the
                             row's first halo cell (the cursor's indexer checks dy, dz); else the cursor's checked load
TileLoads.cs                 what a tile load the generator checks once is (TileLoad.Read: the loop declaring the
                             column's variable, an int from a constant, its bound (a constant or the cursor's shape),
                             a positive step, the variable written nowhere else, no function between loop and call)
Views.cs                     what a [View] is, read once for the generators and the analyzer: Fields ('ref', 'ref
                             readonly' and Mut<T> fields of components), Problem (EECS098's reason), Overlap (EECS099:
                             a component reached twice through views and parameters)
ViewGenerator.cs             [View] -> entity.AsX() / TryGetX(out x) (a view parameter is SystemGenerator's: the job
                             builds the view over the row, a Mut<T> field over the run's change-tick column)
Prefabs.cs                   what a [Prefab] declaration is (Fields, Problem: EECS102's reason), read once for the
                             generator and the analyzer
PrefabGenerator.cs           [Prefab] -> a part of the struct: static readonly Prefab Prefab built once from the field
                             initializers (one Set per nine components) and the constructor they need
LambdaPass.cs                what a lambda pass call is, read once for the generator and the analyzer: Read (the call,
                             its values, lambda, filters; a change filter's tick first), Problem (EECS095's reason and
                             the captured values), Unreachable (EECS096's reason), IsStable, Discards (an expression
                             lambda's dropped value)
ComponentGenerator.cs        C# 14 extension members per component / singleton: entity.X (ref T; ref readonly for
                             tracked or indexed), HasX, TryGetX, GetOrAddX, SetX(ctor args), SetXIfChanged, RemoveX,
                             tags IsX,
                             world.XIndex, singletons world.X / HasX / GetOrAddX / SetX / RemoveX, query items
                             item.X per Query<T1..Tn> shape the code calls, TypeRegistry registration (OrderBy<T, K>
                             for [OrderBy]); classes carry [GeneratedCode("EasyEcs.Generator")], so Symbols.IsGenerated
                             lets the analyzer and SystemGenerator treat SetX / GetOrAddX / RemoveX as structural
EasyEcsAnalyzer.cs           EECS002-006, EECS009-024, EECS035-036, EECS054, EECS056, EECS070, EECS072-073 and EECS118 (the
                             declaration and [ForEach] rules of the range EECS002-103 that are not performance rules;
                             generators never report diagnostics): DeclarationAnalyzer. Every EasyEcsAnalyzer*.cs
                             file ends with its area's analyzer (DeclarationAnalyzer, SystemAnalyzer, ...: a
                             DiagnosticAnalyzer deriving from EasyEcsAnalyzer.Area with the rules it reports), since
                             Roslyn skips an analyzer only when all its rules are suppressed; Parameters.cs and
                             Layout.cs feed PerformanceAnalyzer. EasyEcsAnalyzer (static, partial over the files)
                             holds the descriptors, the checks and All, every area's analyzer (tests, DocCheck)
EasyEcsAnalyzer.Systems.cs   EECS007-008, EECS074-077 and EECS090: systems as declarations (state in a static field, a
                             reach into another system; an [EcsSystem] that is no ref struct, an instance member, a hook
                             no signature fits, a system API given a type that is no [EcsSystem], a method named almost
                             like a hook)
UnusedSuppressor.cs          suppresses IDE0051 / IDE0052 on [ForEach], [EntityMethod] and hook methods (called through
                             UnsafeAccessor; Rider reads their [MeansImplicitUse])
EasyEcsAnalyzer.Performance.cs  EECS025-028, EECS030-032, EECS034, EECS039, EECS046, EECS052, EECS053 and EECS055 (with the
                             check of EECS056):
                             code that works but costs (write access that
                             only reads, unused or per-row lookups in [ForEach] methods and jobs, allocations per
                             update, per-entity structural changes and counting over queries, creation loops,
                             Update in a system, a Mut<T> of an untracked component, a Query loop
                             that only reads, a Set right after a creation, a tracked ref written on some rows only
                             (through a view's 'ref' field and in view members too), a
                             method left without a vector loop; EECS025 and EECS053 for the parameters of lambda passes
                             too); each computes its fix as
                             diagnostic properties for EasyEcs.CodeFixes
EasyEcsAnalyzer.Members.cs   EECS033 (the generated member a call or a guarded Set says shorter) in an analyzer of its
                             own (MemberAnalyzer): the rule a project most often turns off is the costliest to run, and
                             Roslyn skips an analyzer only when every rule it reports is suppressed
PartGenerator.cs             [Sum]..[Any], [LookupKey]/[UniqueKey], [Group], [OrderBy] -> a part of the partial struct
                             (IReduction<T>, IIndexed<K>.Key, IGroup.Group, IOrderKey<K>.OrderKey / BoxPoint); Parts.cs: the models and Problems read once for the
                             generator and the analyzer (other generators read keys and groups through Symbols)
Kernel.cs                    a per-entity [ForEach] method as a Vector<T> loop (enums as their integer's lanes, a bool
                             component of one field as 1-byte lanes read as a mask; Kernel.Vectorize: the loop, or the
                             reason EECS055 / EECS056 report, as a ScalarReason: fixable or inherent; a lambda pass's
                             body through the same Translate, with its Lane);
                             Kernel.Narrow.cs: byte / sbyte / short / ushort lanes (int values followed as low bits and an interval, saturating idioms); Kernel.Struct.cs:
                             struct values (Vector2/3/4 fields, new T(...), with) as one vector per lane;
                             Kernel.Wide.cs: components of up to 16 words, two forms each emitted where it pays (its
                             own gate): AVX-512 (fields gathered by a pruned permute tree) and ARM64's 128 bits (4 rows
                             per vector, one NEON TBL per field over up to 4 registers, a TBX per written vector:
                             Interleaved.Pick / PickInto); lines marked per width, kept or dropped by SystemGenerator;
                             Kernel.Pairs.cs: lanes of two sizes (int rows summed into a long, float into a double) as
                             pairs of vectors (WidenLower / WidenUpper), every lane bit-exact; four times as wide stays scalar;
                             Kernel.Views.cs: a method taking views desugared before translation (view fields as
                             parameters, view members inlined by Kernel.Inlining's Inliner) as a static local function
                             bound in a speculative model of the body, or the reason it stays scalar;
                             Kernel.Inlining.cs: the user's own calls inlined before translation (static and extension
                             methods, C# 14 extension members, struct methods and properties, local functions, whose
                             source is in the compilation or whose assembly recorded the body (Recorded.cs, bound in a
                             speculative model of the calling method, its placeholders declared at the method's end):
                             Callees reads each callee once per compilation, syntax
                             refusals first; Inliner / Rewriter: parameters bound to the caller's variables or locals,
                             early returns as a flag, a value as one expression or a local assigned before the
                             statement), bound in a speculative model of the method body; a call left gets its reason
                             (ReasonForCall: virtual, an interface's, a delegate, another assembly, recursive, what its body
                             holds) for EECS055 / EECS056;
                             Kernel.Lookups.cs: reads of other entities through Lookup<T> (lookup[id], Read, Has, at an
                             EntityId of the lanes) gathered per vector from the one table they live in, the vector's
                             rows sent to the method when they live in several, with a backoff; only for SimdMode.Require (D131);
                             the chases of the rows ahead prefetched behind the job's gate (Code.Prefetched);
                             Kernel.Neighbors.cs: a pair method's pair mode (a [Neighbor] component as uniform values
                             broadcast per neighbor, the body masked by the cluster's passing lanes; a write of the
                             neighbor, a method writing both sides included, keeps the pairs one at a time) and the
                             cutoff's text (ScalarOf);
                             Kernel.Structural.cs: a structural change of the row's entity under a condition (marked
                             rows, applied in bulk after the pass: Code.StructuralChanges) and Emit<T> records of a
                             lane or the entity's id (Code.VectorTail: every row takes the vector form);
                             Kernel.Streams.cs: the columns a loop writes without reading (WriteOnly, read off the
                             loop's text: Code.Streamed, FlatCode.Streamed) and the streaming loop's text (Streaming:
                             their loads dropped, their stores StreamingStores.Store);
                             Kernel.Switches.cs: patterns and switches as lane masks (relational, constant, and / or /
                             not, property, positional, tuple, type and var patterns; a switch expression first match
                             wins from its arms' masks, the lanes no arm matches sent to the method; a switch statement's
                             sections under the lanes their first matching label takes, the default section's wherever
                             it stands), their reasons (a subject no lane holds, a list pattern, a switch over a call on
                             a struct: the split), and the inliner's lowering of a switch statement that returns
EasyEcsAnalyzer.Usage.cs     EECS037-038, EECS040-045 and EECS047-051: calls and declarations that compile but do not
                             do what they say (a job's WriteMask, a dropped copy, SetIfChanged that cannot compare, calls
                             a pass refuses, an index added to while walked, a record key that prints itself, a sliced
                             filter where every row is needed, the world's own tags written by hand, a ref used after
                             a structural change, Disable of a component that cannot be disabled, filters no entity
                             passes, a second group in one filter, an update during a pass)
EasyEcsAnalyzer.Parallel.cs  EECS060-061: code on several threads (a parallel [ForEach] method, the Execute of a job
                             run by ForEachParallel or ParallelFor) that writes memory every thread shares, a parallel
                             job's field read after the call; EECS071: System.Random in a [ForEach] method (fix:
                             EntityRandom); EECS088: two EntityRandom draws of one stream for one entity in pass code
                             (fix: the next free stream), or a draw in a loop that does not vary its stream
EasyEcsAnalyzer.Parts.cs     EECS062-069, EECS102 (a [Prefab] the generator cannot write) and EECS104 (an [OrderBy] it
                             cannot keep: a key no radix sort orders, box members, interval, margin): generated parts (not partial, hand-written members, fields no attribute
                             combines), hand-written reductions, keys and groups the attributes could declare, a wrong
                             Identity, a setup attribute on a type it does not fit, a managed type without a Nino
                             formatter (Warning when the code saves what holds it: entity saves write no singletons)
EasyEcsAnalyzer.Forms.cs     EECS080-085, EECS103, EECS108-109 and EECS119 (rows collected and looped over by hand in an OnUpdate:
                             [ForEach(MaxRows = N)], its fix computed as properties): what the generator declares for code written by hand (a one-entity
                             job [EntityMethod] writes, a [ForEach] method that destroys every entity it visits: [Consume],
                             a batch method's vector loop written by hand that the method for one row gets generated,
                             the walk and Clear of Emitted<T> a [Drain] method gets generated)
                             and those attributes (and [UpdateInterval]) where they cannot apply (EECS082 is retired)
HandSimd.cs                  EECS103's detection, read once for the analyzer and its code fix: a batch [ForEach]
                             method whose body is a hand-written vector loop (prelude of streams and broadcasts, one
                             loop by 1 or by Count, lane-wise operations, a scalar tail) rewritten as the method for one
                             row (PerRow), which must bind in a copy of the compilation and get Kernel.Vectorize's loop
EasyEcsAnalyzer.Parameters.cs  EECS086, EECS087, EECS089: a [ForEach] method doing by hand what a parameter does per
                             chunk (a singleton reset by system code and accumulated row by row: Reduce<T>, reported
                             at the end of the compilation; other entities read through the World: Lookup<T>; records
                             appended to a singleton's list: Emit<T>); a total kept across passes and added to row by row: Reduce<T> of an
                             [Accumulate] singleton (Info until the singleton reduces, then a Warning with its fix);
                             no fix for a reset total (the reset, the readers move too)
EasyEcsAnalyzer.Layout.cs    EECS091 (compilation end): a component of 32 bytes or more whose fields no frequent
                             [ForEach] pass touches are half of it (ColdFieldPasses: the fields each pass names,
                             collected from AnalyzeBody; components EECS055 already advises to split are left to it)
EasyEcsAnalyzer.Lambdas.cs   EECS095-097: a lambda pass the generator cannot compile (Error, it would throw; the fix
                             passes captured values before a static lambda), one run through its delegate (the fix
                             makes the private member internal), one left without a vector loop (Info, Kernel's reason); EECS049 for a
                             lambda pass whose own filters no entity passes
EasyEcsAnalyzer.Views.cs     EECS098-101: an invalid [View], a component reached twice through views, a view member
                             that reaches beyond its entity, a view built by hand (fixes: the view as the parameter,
                             entity.AsX()); EECS053's check in view members; EECS025 (compilation end) for a view's
                             'ref' field no code writes through (fix: 'ref readonly')
EasyEcsAnalyzer.Records.cs   EECS092 (compilation end): an Emit<T> whose Emitted<T> no code of the compilation clears
                             (a public record type of a library is left to the assemblies using it)
EasyEcsAnalyzer.Pairs.cs     EECS105-107: a pair method that cannot run per pair (Error), one whose cutoff test the
                             generator cannot read (every pair runs), a neighbor search written by hand (a Warning
                             with PairRewrite's fix when mechanical, else Info: the grid's upkeep goes with it)
EasyEcsAnalyzer.Tiles.cs     EECS115-117: a tile step the generator cannot write (Symbols.StepProblem, Write on a tile
                             taken by value, Sweep read on one, Restart outside a [GaussSeidel] step's ref tile: Error), a second buffer and its plain commit written by hand where the
                             tile could step itself (compilation end, Warning), a Min / Max relaxation stepped by Jacobi
                             that [GaussSeidel] would settle (Info, fix: [GaussSeidel] when the key colors the tiles)
EasyEcsAnalyzer.Fusion.cs    EECS093-094: two passes over the same rows that almost share one walk (the reason
                             Fusion.Problem gives), a FusionMode.Require method left apart (Error)
Uses.cs                      whether a use of a value may write it (EECS025's conservative read/write test)
Threads.cs                   EECS014's check: does a structural call in a delegate handed to another thread race;
                             the writes of code on several threads that reach shared memory (EECS060, EECS061)
BodyFacts.cs                 the analyzer's per-compilation cache of what a [ForEach] method's body allows (IsStable,
                             OnlyIdentifies, ParallelProblem), read by several rules per method
Chases.cs                    a [ForEach] method's or lambda pass's chases, read once for the row form's prefetches
                             (SystemGenerator.EmitChases): reads of other entities at an id of an in component
                             (lookup[x.F], Read, Has), key probes (TryFind / Find / Count) and reads at the ids of an in
                             buffer's elements (foreach, the indexer), with the stages each one prefetches
Recorder.cs                  what this assembly records for the generators of assemblies referencing it (source output,
                             one file per source file): [assembly: ConstructorFields] per public constructor
                             of a public struct (the member each parameter sets, or the reason), [assembly: InlineBody]
                             per small public helper taking and returning values (static method, struct member,
                             accessor with a body: the body with names qualified, this as @this, hidden fields as
                             placeholders __EasyEcsAccessor{i}, hidden static helpers as local functions
                             __EasyEcsHelper{j}; or the reason)
Recorded.cs                  those facts read back per referenced assembly (cached on its symbol): Constructor, BodyOf,
                             Parse (placeholders annotated with their field), AccessOf / FieldOf (a placeholder's field,
                             which Kernel reads as a lane or, uniform, through an [UnsafeAccessor] extern of the job);
                             Assignments: what a constructor of this compilation sets, read once for Kernel.Struct and
                             the recorder
Symbols.cs                   symbol checks shared by generator and analyzer
Code.cs                      tiny string helpers for the generators' templates (List, Lines, TypeParams)
EquatableArray.cs            array with value equality (generator models cache between edits)
Polyfills.cs                 IsExternalInit for netstandard2.0 (records); do not delete
```
