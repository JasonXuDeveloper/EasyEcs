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
                             Query/Count/Any, QueryItem/QueryEnumerator, Filters.g.cs: QueryFilter.WithAny /
                             ChangedAny / RemovedAny and their attributes), one template each; World.Lambdas.g.cs:
                             World.ForEach / ForEachParallel(values..., Delegate), the calls lambda passes intercept
SystemGenerator.cs           [ForEach] -> struct jobs (per entity or per run of rows; INoStructuralChanges when
                             the method cannot change structure; Parallel -> chunked jobs; [Changed<T>] /
                             [Removed<T>] -> Since(LastRunTick + 1) and a read of T; Mut<T> -> a write stamped per
                             write; Slices -> the slice of the run; ChunkRows -> the chunkRows argument; [Consume] -> DestroyAll of
                             the filter after a pass without structural changes, a destroy per row otherwise;
                             [ForEntity] -> the IForEachEntity job and an entry point Owner.Method(entity, values)
                             over entity.Run; [EcsSystem] -> the code struct __X_SystemCode (hooks, then the
                             methods in declaration order; [UpdateInterval] -> its Interval / Phase), and per
                             assembly the set __EasyEcsSystems: one Add per system and per closed generic system any
                             API names (AddSystem<G<P>>, [UpdateAfter<G<P>>], ...), a switch per hook; no system
                             needs partial, its methods are reached through UnsafeAccessor);
                             SystemGenerator.Fusion.cs: pass fusion, consecutive [ForEach] methods of a system as one
                             walk (one fused job per group: per row, per block of FusedBlock rows, per word of a
                             masked walk) and pairs of systems declared or ordered one after the other as one job
                             (__A__B_Fusion, run by the set's RunFused switch when both are adjacent, enabled and due)
Fusion.cs                    the fusion rule read once for the generator and the analyzer (FusionSide: what a method
                             reads, writes, filters and blocks it; Fusion.Problem: why two passes cannot share a walk)
SystemGenerator.Lambdas.cs   lambda passes: world.ForEach / ForEachParallel(values..., static (ref P p, ...) => ...)
                             -> an interceptor per call ([InterceptsLocation], one file per source file in the namespace
                             EasyEcs.Generated) running the job a [ForEach] method with the lambda's parameters and body
                             gets (body copied with names qualified and constants inlined; the static delegate per
                             row where it cannot be: EECS096)
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
EasyEcsAnalyzer.cs           EECS002-006, EECS009-024, EECS035-036, EECS054, EECS056, EECS070 and EECS072-073 (the
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
UnusedSuppressor.cs          suppresses IDE0051 / IDE0052 on [ForEach], [ForEntity] and hook methods (called through
                             UnsafeAccessor; Rider reads their [MeansImplicitUse])
EasyEcsAnalyzer.Performance.cs  EECS025-028, EECS030-034, EECS039, EECS046, EECS052, EECS053 and EECS055 (with the
                             check of EECS056):
                             code that works but costs (write access that
                             only reads, unused or per-row lookups in [ForEach] methods and jobs, allocations per
                             update, per-entity structural changes and counting over queries, creation loops, the
                             generated member, Update in a system, a Mut<T> of an untracked component, a Query loop
                             that only reads, a Set right after a creation, a tracked ref written on some rows only
                             (through a view's 'ref' field and in view members too), a
                             method left without a vector loop; EECS025 and EECS053 for the parameters of lambda passes
                             too); each computes its fix as
                             diagnostic properties for EasyEcs.CodeFixes
PartGenerator.cs             [Sum]..[Any], [LookupKey]/[UniqueKey], [Group], [OrderBy] -> a part of the partial struct
                             (IReduction<T>, IIndexed<K>.Key, IGroup.Group, IOrderKey<K>.OrderKey / BoxPoint); Parts.cs: the models and Problems read once for the
                             generator and the analyzer (other generators read keys and groups through Symbols)
Kernel.cs                    a per-entity [ForEach] method as a Vector<T> loop (Kernel.Vectorize: the loop, or the
                             reason EECS055 / EECS056 report, as a ScalarReason: fixable or inherent; a lambda pass's
                             body through the same Translate, with its Lane);
                             Kernel.Narrow.cs: byte / sbyte / short / ushort lanes (int values followed as low bits and an interval, saturating idioms); Kernel.Struct.cs:
                             struct values (Vector2/3/4 fields, new T(...), with) as one vector per lane;
                             Kernel.Wide.cs: components of up to 16 words on AVX-512 (fields gathered by a pruned
                             permute tree, where it pays);
                             Kernel.Pairs.cs: lanes of two sizes (int rows summed into a long, float into a double) as
                             pairs of vectors (WidenLower / WidenUpper), every lane bit-exact; four times as wide stays scalar;
                             Kernel.Views.cs: a method taking views desugared before translation (view fields as
                             parameters, void members inlined with plain arguments in place, expression members as
                             expressions; ViewInliner and its Rewriter), or the reason it stays scalar;
                             Kernel.Lookups.cs: reads of other entities through Lookup<T> (lookup[id], Read, Has, at an
                             EntityId of the lanes) gathered per vector from the one table they live in, the vector's
                             rows sent to the method when they live in several, with a backoff; only for SimdMode.Require (D131);
                             Kernel.Structural.cs: a structural change of the row's entity under a condition (marked
                             rows, applied in bulk after the pass: Code.StructuralChanges) and Emit<T> records of a
                             lane or the entity's id (Code.VectorTail: every row takes the vector form)
EasyEcsAnalyzer.Usage.cs     EECS037-038, EECS040-045 and EECS047-051: calls and declarations that compile but do not
                             do what they say (a job's WriteMask, a dropped copy, SetIfChanged that cannot compare, calls
                             a pass refuses, an index added to while walked, a record key that prints itself, a sliced
                             filter where every row is needed, the world's own tags written by hand, a ref used after
                             a structural change, Disable of a component that cannot be disabled, filters no entity
                             passes, a second group in one filter, an update during a pass)
EasyEcsAnalyzer.Parallel.cs  EECS060-061: code on several threads (a parallel [ForEach] method, the Execute of a job
                             run by ForEachParallel or ForParallel) that writes memory every thread shares, a parallel
                             job's field read after the call; EECS071: System.Random in a [ForEach] method (fix:
                             EntityRandom); EECS088: two EntityRandom draws of one stream for one entity in pass code
                             (fix: the next free stream), or a draw in a loop that does not vary its stream
EasyEcsAnalyzer.Parts.cs     EECS062-069, EECS102 (a [Prefab] the generator cannot write) and EECS104 (an [OrderBy] it
                             cannot keep: a key no radix sort orders, box members, interval, margin): generated parts (not partial, hand-written members, fields no attribute
                             combines), hand-written reductions, keys and groups the attributes could declare, a wrong
                             Identity, a setup attribute on a type it does not fit, a managed type without a Nino
                             formatter (Warning when the code saves what holds it: entity saves write no singletons)
EasyEcsAnalyzer.Forms.cs     EECS080-085 and EECS103: what the generator declares for code written by hand (a one-entity
                             job [ForEntity] writes, a [ForEach] method that destroys every entity it visits: [Consume],
                             a batch method's vector loop written by hand that the method for one row gets generated)
                             and those attributes (and [UpdateInterval]) where they cannot apply (EECS082 is retired)
HandSimd.cs                  EECS103's detection, read once for the analyzer and its code fix: a batch [ForEach]
                             method whose body is a hand-written vector loop (prelude of streams and broadcasts, one
                             loop by 1 or by Count, lane-wise operations, a scalar tail) rewritten as the method for one
                             row (PerRow), which must bind in a copy of the compilation and get Kernel.Vectorize's loop
EasyEcsAnalyzer.Parameters.cs  EECS086, EECS087, EECS089: a [ForEach] method doing by hand what a parameter does per
                             chunk (a singleton reset by system code and accumulated row by row: Reduce<T>, reported
                             at the end of the compilation; other entities read through the World: Lookup<T>; records
                             appended to a singleton's list: Emit<T>); no code fix (the reset, the readers move too)
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
EasyEcsAnalyzer.Fusion.cs    EECS093-094: two passes over the same rows that almost share one walk (the reason
                             Fusion.Problem gives), a FusionMode.Require method left apart (Error)
Uses.cs                      whether a use of a value may write it (EECS025's conservative read/write test)
Threads.cs                   EECS014's check: does a structural call in a delegate handed to another thread race;
                             the writes of code on several threads that reach shared memory (EECS060, EECS061)
BodyFacts.cs                 the analyzer's per-compilation cache of what a [ForEach] method's body allows (IsStable,
                             OnlyIdentifies, ParallelProblem), read by several rules per method
Symbols.cs                   symbol checks shared by generator and analyzer
Code.cs                      tiny string helpers for the generators' templates (List, Lines, TypeParams)
EquatableArray.cs            array with value equality (generator models cache between edits)
Polyfills.cs                 IsExternalInit for netstandard2.0 (records); do not delete
```
