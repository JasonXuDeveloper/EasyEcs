# EasyEcs.Core: file map

> Read when: reading or changing anything in `src/EasyEcs.Core` (entities, tables, queries, systems, the parallel runner, indexes, saves).

net10.0 library, namespace `EasyEcs`: the hand-written shared core (arity families are generated, see
`src/EasyEcs.Generator/AGENTS.md`). The rules of the root AGENTS.md apply; `audit-core` audits this directory.

When you add, rename or delete a file here, update its line (`verify` checks every file is listed).

```
Contracts.cs                 IComponent, ITracked, ISingletonComponent, INoStructuralChanges and the other markers
Declarations.cs              attributes whose code the generators write: [Sum]/[Product]/[Min]/[Max]/[All]/[Any]
                             (a reduction; on the struct, for its fields without one), [Accumulate] (a total: each pass
                             combined into the world's value), [LookupKey]/[UniqueKey]/[Group] (an index key, a group), and world setup:
                             [GroupBy] (on the member: a group every world splits by), [OrderBy] (the order of the
                             tables, overriding an inferred one), [WhenTargetDestroyed], [NotSaved]
Entity.cs                    the entity handle (id, version, world): Has/Get/Read/TryGet/GetOrAdd/SetIfChanged/
                             Disable/Enable/Clone/Destroy
Entity.Relations.cs          entity.Link / Unlink / Links (IRelation buffers + their index)
EntityId.cs                  index + incarnation (8 bytes, unmanaged): how data refers to an entity
EntityMeta.cs                16-byte per-entity record (archetype, version, slot)
EntitySpan.cs                the entities of a batch job's run of rows (lazy handles over the run's EntitySlots; its
                             table and first row, through which a Mut<T> of the run stamps its row)
EntityDebugView.cs           what a debugger shows for an entity (Entity.Describe)
EcsSystem.cs                 [EcsSystem] (a system: a ref struct of static members, never instantiated), the hooks
                             (static methods named OnCreate / OnUpdate / OnDestroy, taking the World, the SystemHandle,
                             both or neither), [UpdateInterval(n, Phase = p)] (the cadence, read from the generated code);
                             SystemHandle.cs: what world.AddSystem<T>() / System<T>() / TryGetSystem<T>() / Systems
                             hand out (Enabled, Error, RunCount: completed runs, the slice of a sliced method;
                             LastRunTick, Stats, Type, World), a (world, slot, version) that throws once its system
                             is removed; SystemCode.cs: ISystemCode (a system's generated Update / Create / Destroy,
                             access and cadence) and ISystemSet (one generated __EasyEcsSystems per assembly: the
                             switch over its systems' codes, registered by a module initializer, weakly held for
                             collectible assemblies), the registry World.AddSystem<T> resolves a type through
ForEachAttributes.cs         [ForEach] (Parallel, Slices, ChunkRows, SimdMode), [With<T>], [Without<T>], [Changed<T>],
                             [Removed<T>] ([WithAny<..>] / [ChangedAny<..>] / [RemovedAny<..>] are generated: Filters.g.cs),
                             [Consume] (the visited rows destroyed after the pass), [ForEntity] (a static method run
                             on one entity through a generated entry point), [ForEachRecord] (a static method run on
                             each record of Emitted<T> in order, then the records cleared)
Maybe.cs                     Maybe<T>: an optional component parameter of a [ForEach] method
Mut.cs                       Mut<T>: a [ForEach] parameter stamped only on the rows it writes (Write, SetIfChanged);
                             Values.Same, the boxing-free equality of SetIfChanged
Reduce.cs                    IReduction<T> + Reduce<T> (taken by ref): a [ForEach] method's partial result per chunk,
                             combined in chunk order into a singleton when the pass ends (OpenMP reduction)
Scratch.cs                   Scratch<T>: per-thread working memory of a [ForEach] method, never shared, never reset
Lookup.cs                    Lookup<T> + LookupExtensions: read-only lookups of any entity (and index keys) from a
                             [ForEach] method, parallel ones included; the system reads T, so no writer shares its stage
LookupLanes.cs               the reads of other entities in generated vector loops (D131): Lookup<T>.Resolve gathers the
                             lanes' records (AVX2) into a presence mask and the one table their entities live in (false:
                             the vector's rows run the method), Gather the fields from that table's column;
                             PrefetchRecord / PrefetchRow (Lookup<T>.Prefetch / PrefetchRow: the two stages of a
                             prefetched chase; borrowed-ideas.md item 1), PrefetchAhead (a vector loop's
                             prefetches of the rows ahead), Prefetches (the gate a generated pass or vector loop reads once per pass:
                             the records and rows past its form's cache, CacheSizes), PrefetchesIds (the gate of the core's
                             loops over lists of ids: DestroyEntities)
Prefetching.cs               the one software prefetch (Line): x86 PREFETCHT0, ARM64 with SVE a PRFB of one element
                             (the experimental SVE API, SYSLIB5003 suppressed at that call), nothing elsewhere (IsSupported)
IndexPrefetch.cs             ValueIndex's probe prefetches (generated [ForEach] jobs finding by a key a row holds:
                             LookupExtensions.Probes / PrefetchKey): the stages by representation (a direct slot; a
                             relation's slot, then its ids; a hashed key's bucket, entry, then its ids), the gate (the
                             index's memory past the last-level cache, in parallel too); HashSlots: a
                             Dictionary's buckets and entries (UnsafeAccessor, reflection once per resize)
CacheSizes.cs                the last-level cache and one core's L2, read once per process from the OS as the GC does
                             (Windows GetLogicalProcessorInformationEx, Linux sysfs then sysconf, macOS sysctl), where a
                             prefetch exists (Prefetching.IsSupported); 0 is unknown, which never prefetches. A gate per
                             form: index probes and vector loops' chases past the last level; the row form's chases
                             (parallel, fused walks and buffers too) and DestroyEntities' id list past the L2
                             (L2OrLastLevel)
Tile.cs                      grid tiles: ITileShape / ITile<TKey> (a shape, halo, neighborhood, boundary and a key),
                             Tile<TCell, TTile> (a [ForEach] parameter: up to 26 neighbors found by generated code,
                             TileNeighbors), Pad into a [ThreadStatic] texture padded with the halo and a row mask,
                             PaddedTile.Rows (TZCNT walk), TileCursor.Load128, ChangedEdges / Facing (sleeping tiles)
Emit.cs                      Emit<T>: records a [ForEach] pass appends to a pooled list per chunk (EmitPool), joined in
                             chunk order into singleton Emitted<T> (a DynamicBuffer<T>; passes append, readers clear);
                             AddIds / AddLanes: a vector loop's passing lanes appended compacted, in row order;
                             Emitted<T>.Consume + IForEachRecord<T>: a [ForEachRecord] pass (the records walked, then cleared)
EntityRandom.cs              EntityRandom: deterministic random numbers, a pure function of (seed, entity, tick, stream)
                             (four SplitMix64 finalizer rounds); a singleton (Seed, and a Tick the world adds one to
                             at the start of every update: World.Run) and static members
Interleaved.cs               the deinterleaving loads / interleaving stores of generated vector loops over components
                             of 2 to 4 fields of one lane type, and the lane masks of the passing rows of a word;
                             LoadTail / StoreTail: the last rows of a run copied into a vector's worth (a loop that
                             marks rows runs them masked); Interleaved.Wide.cs: Pick / PickInto, the NEON table
                             lookups (TBL / TBX over 1 to 4 registers) of a wide component's 128-bit form, and
                             IsWideAccelerated (ARM64 with 128-bit Vector<T>)
LaneMath.cs                  lane functions of generated vector loops .NET has no vector form of: the quotient of
                             int / uint lanes through doubles (exact), FloatMode.Fast's Log10 / Sinh / Cosh / Tanh of floats
Lanes.cs                     Lanes.Compact: stream compaction of the elements a lane mask selects (AVX-512 compress,
                             AVX2 permutation table, 128-bit byte shuffle: NEON TBL / PSHUFB, picked at run time), for
                             batch methods, Emit and CollectIds; Lanes.RowNumbers: a mask to row numbers over it (the one
                             helper generated loops call); Lanes.Bits: a vector loop's lane mask as bits
MarkedRows.cs                the rows a vector loop marked for one structural change (words of 64 rows per table,
                             pooled per world), applied when the pass ends: SetAll / RemoveAll / DisableAll /
                             EnableAll / DestroyAll (World.Bulk.cs: SetMarked ... DestroyMarked, WalkMarks)
SystemGroups.cs              ISystemGroup, [UpdateInGroup<T>] (World.Update<T>() runs a group)
SystemOrder.cs               [UpdateBefore<T>] / [UpdateAfter<T>]
SystemAccess.cs              a system's read/write sets (generated from [ForEach] parameters) and the conflict rule
SystemStats.cs               per-system run counts, times, allocated bytes and visited entities
                             (WorldOptions.MeasureSystems)
ScheduleInfo.cs              what World.DescribeSchedule returns (groups, stages, systems, waits, registration-only
                             pairs) and its ToString / ToMermaid
World.cs                     entity lifecycle (create/destroy, id recycling, lookup, enumeration, Instantiate of a
                             Prefab, with given components too, and Entity.Clone: one row copy, CloneRow; Reserve and
                             ReserveBuffers of a prefab: its tables, and the pools of the buffer lists its instances take)
World.Defaults.cs            TypeDefaults: the setup a type declares ([GroupBy], [WhenTargetDestroyed]), applied by
                             every world from its first table holding the type (rules pending until a destroy or
                             the index needs them); what a world set up itself is never overridden
World.DestroyLog.cs          WorldOptions.DestroyLogCapacity: the ring of (EntityId, tick) records of freed ids,
                             DestroyedSince / DestroyLogOldestTick
World.Components.cs          AddBit/AddBits/RemoveBit/RemoveBits (shared core of every Set/GetOrAdd/Remove/Disable;
                             SetRow / EnableRow for one component), passes (BeginPass/EndPass, tombstones closed by
                             the last pass), CarryOver
World.Bulk.cs                SetAll / RemoveAll / DisableAll / EnableAll (IRowOp structs, whole words of rows at once);
                             the same ops over MarkedRows, in the order the pass marked them (a row that moves leaves a
                             tombstone inside a pass of their own, so tables keep the method's row order)
World.Archetypes.cs          archetype registry, transition edges, copy-on-write query cache, CompactArchetypes,
                             the structural gate (Structural())
StructuralGate.cs            owner flag (plain check-and-set, throws when reentered or while frozen: Freeze/Thaw around
                             parallel regions, batch jobs and INoStructuralChanges passes) +
                             shared CAS gate for the cold paths parallel stages reach; StructuralScope / SharedScope
SpinGate.cs                  SpinGate + SpinScope: the CAS lock (spin, then yield, never block) of the few instructions
                             several threads of parallel work enter at once (the helpers' queue, EmitPool, the buffer
                             pool, the shared structural gate); a Lock allocates its wait event when first waited on (D128)
World.Systems.cs             system groups, ordering, parallel stages (consecutive non-conflicting systems),
                             synchronous Update / Update<TGroup>, Dispose
World.Parallel.cs            allocation-free runner (the caller + the helper threads of Workers.cs, adaptive waking)
                             for system stages and the row chunks of parallel [ForEach]; World.For.cs: ForParallel
                             (IForRange over a range, IForSpan<T> over a buffer's elements) on the same runner
Workers.cs                   the fixed helper threads of every world's parallel work (one set per process, FIFO queue,
                             spin then park; ThreadSetup warms per-thread state once per thread: no allocation in steady state)
World.Singletons.cs          per-world singletons (ref returns)
World.Indexes.cs             world.Index<T, TKey>() (one ValueIndex per indexed component type) and its hooks
ValueIndex.cs                IIndexed<TKey> + ValueIndex: key -> one id array per key (IndexHead), built by the first
                             lookup, kept by Set / CreateEntities / GetOrAdd / Remove / Destroy
UniqueIndex.cs               IUniqueIndexed<TKey> (one entity per key, an eager index that every write claims keys in
                             before it changes anything) and index.Get / TryGet
OrderedIndex.cs              sorted walks and ranges of a value index with a comparable key
Relations.cs                 IRelation buffers + RelationIndex<T> (entities linking to a target)
Hierarchy.cs                 ChildOf (the hierarchy relation); World.Hierarchy.cs: Children, destroy cascades,
                             TargetPolicyExtensions (what happens to links when their target is destroyed)
World.Groups.cs              GroupBy: tables split by the value of an IGroup component
RowOrder.cs                  ordered tables: IRowOrder<T> (a key and a box point, static members; written by the
                             generator), RowKeys (Morton codes by a cell, key bits), IOrderKey<TKey> + DeclaredOrder
                             ([OrderBy]'s part), RowOrder (Archetype.Order: the LSD radix sort of (key, row), the boxes
                             per cluster (8 rows with 256-bit vectors, 4 on ARM64, or the size a request asks), widened
                             where a row moves in, reset by a restore)
RowClusters.cs               RowClusters: the read path of one ordered table (clusters, boxes per axis, columns, ids,
                             Pairs: the cluster pairs within a cutoff, a sweep) and ClusterPair
Pairs.cs                     [Neighbor] (a pair method's other entity) and the pass of a pair method: IPairJob (the
                             generated job: the cutoff, per row or one, and its run over a table against another),
                             PairRun (one table against one: clusters of Vector<float>.Count rows, their lane masks,
                             the CSR row ranges of the neighbors' clusters, a range of them per chunk), World.ForPairs
                             and ForPairsParallel (chunks of whole words of clusters on the chunk runner, the lists
                             drawn first on the calling thread; a job that Merges keeps each chunk's copy, combined in
                             chunk order; PairState.Next walks the pairs of tables) (the order requested once,
                             then PairState: cluster boxes drawn every pass and checked against the kept ones widened
                             by an adaptive margin, a sweep of the boxes widened by the clusters' reach when one left;
                             lists of each row's neighbors, Verlet's, where they cost less than the ranges for the
                             method's width: PairRun.Listed / Near, kept while the rows moved less than the margin)
World.Order.cs               OrderRows<T, TOrder> (the request generated setup makes), the cadence (SortDue from
                             World.Run), SortRows<T>, Clusters<T> (OrderedTables)
Cleanup.cs                   ICleanupComponent + Retired tag (entities kept alive until their cleanup components go)
Disabled.cs                  Disabled<T> tag (the value kept, the component hidden from queries); its bit is a
                             non-generic DisabledTag per component (ComponentInfo.DisablerOf)
DynamicBuffer.cs             DynamicBuffer<T> (a list per entity as a tracked component; writes are ref-this extensions)
                             and its BufferStore: shared by prefab instances, clones and snapshots until written
                             (copy-on-write, Shares; a prefab's uncounted)
Prefab.cs                    a recipe of component values outside any world (one row of a private world)
World.Snapshots.cs           Capture / Restore (WorldSnapshot.cs holds the copies)
World.Serialization.cs       Save / Load of a whole world (SaveWithout(output, exclude) / LoadKeeping(input, keep) for a restart);
                             TypeRegistry.cs: the types a save may name (generated)
ComponentSet.cs              a set of component bits and singleton slots: what SaveEntities writes, what Save leaves
                             out, which singletons Load keeps
World.Migration.cs           SaveEntities / LoadEntities / MoveEntities: a group of entities to another world or process
World.Types.cs               RemoveTypes(assembly): unload a plugin's types from a world; Collectible.cs: forgetting
                             the types of an unloaded AssemblyLoadContext process-wide
World.Introspection.cs       world.DescribeTables() (tables, columns, sizes), Entity.Describe() for tools and debuggers,
                             world.DescribeSchedule()
World.Trim.cs                TrimExcess: drop empty tables, shrink the others, release side buffers, renumber live
                             snapshots (tracked weakly)
WorldOptions.cs              configuration (ParallelSystems, MaxDegreeOfParallelism, MeasureSystems, DestroyLogCapacity,
                             capacities)
Query.cs                     QueryFilter (With/Without/WithAny/Changed/Removed, the ChangedAny / RemovedAny sets,
                             Since, the slice fields; partial: Filters.g.cs, generated by ArityGenerator, holds
                             ChangedAny<T1..Tn> / RemovedAny<T1..Tn>), QueryKey, QueryMatch, RowPlan
                             (cached per query and table), RowFilter (skip / all rows / test presence bits and change
                             ticks, skipping unwritten blocks and words), Tombstones / NoTombstones (the two compiled
                             forms of a structural loop), QueryWalk (the cold state of a walk, pooled per thread)
QuerySlice.cs                QueryFilter.Slice: the rows of a table one slice visits (64-row words, rotated per table)
FilterOverflow.cs            interned filter bits past 255, and FilterAnySets: the ChangedAny / RemovedAny sets
                             interned whole (a QueryFilter holds ids, no object reference)
TypeIndex.cs                 dense per-type index families (ComponentType bit, SingletonType slot, SystemGroup
                             index, ComponentSignature of multi-component calls) + SingletonBox; a tracked
                             component's TicksBit: its hidden RowTick column (Query.cs), non-generic so Native
                             AOT sees no generic recursion (IL3054)
Archetype.cs                 a table: mask, EntitySlot rows (version + id), one column per component (unmanaged
                             columns pinned and 64-byte aligned), presence bits + absent counts per column,
                             per-bit and per-signature add transitions (copy plans); swap-remove / tombstones,
                             FillHoles, Permute (a re-sort: every column, presence bit, change tick and slot);
                             change-tick summaries (LastWrite per column, WordTicks per 64 rows, BlockTicks
                             per 4,096 rows for writes and for removals), Stamp/StampRows/StampWord/SetTick,
                             MarkPresent/MarkAbsent, CloneRow; Resize (growth and Trim, which shrinks a table to a
                             power of two); TableCopy: the copy of a table's rows a snapshot or load holds, naming its
                             table (reused for no other)
ComponentInfo.cs             per-type column operations (size, managed, create/grow/copy/clone/clear) indexed by bit;
                             Pair links a tracked component with its change-tick column
Annotations.cs               MeansImplicitUse (internal): Rider / ReSharper see systems and [ForEach] methods as used
BitMask.cs                   256-bit SIMD bitset with overflow (component masks, query sets)
build/EasyEcs.props          packed as build/EasyEcs.props: adds EasyEcs.Generated to InterceptorsNamespaces (lambda
                             passes); the repository's Directory.Build.props imports it
```
