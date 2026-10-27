using System;

namespace EasyEcs;

/// <summary>
/// Runs a static method of an <see cref="EcsSystemAttribute">[EcsSystem]</see> (or of any other type, through a generated entry point) for every entity matching its component parameters (and
/// the <see cref="WithAttribute{T}"/> / <see cref="WithoutAttribute{T}"/> / <c>[WithAny&lt;T1, T2&gt;]</c> filters), each update, after
/// its <c>OnUpdate</c> method, in declaration order.
/// <para>Parameters: 1..9 components (<c>ref</c> to write, <c>in</c> to read, <see cref="Mut{T}"/> to write some rows; or <c>Span&lt;T&gt;</c> /
/// <c>ReadOnlySpan&lt;T&gt;</c> for a batch method that receives runs of rows), singletons (<c>ref</c> / <c>in</c>), and
/// optionally <see cref="Entity"/> and <see cref="World"/>.</para>
/// <code>
/// [EcsSystem]
/// ref struct Movement
/// {
///     [ForEach, Without&lt;Frozen&gt;]
///     static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
/// }
/// </code>
/// EasyEcs.Generator compiles it to a struct job (<see cref="IForEach{T1}"/>, <see cref="IForEachEntity{T1}"/> or
/// <see cref="IForEachBatch{T1}"/>): no delegate, no allocation, inlined.
/// </summary>
[AttributeUsage(AttributeTargets.Method), JetBrains.Annotations.MeansImplicitUse]
public sealed class ForEachAttribute : Attribute
{
    /// <summary>
    /// Run chunks of the matching rows on several threads (<c>World.ForEachParallel</c>). The method must make no
    /// structural change, run per entity and write no singleton (EECS010); it pays off for heavy per-row work over tens
    /// of thousands of rows. Default false.
    /// </summary>
    public bool Parallel { get; init; }

    /// <summary>
    /// Spread the method's rows over N runs of its system: each run visits one slice, about 1 / N of the rows of every
    /// matching table (slice <see cref="SystemHandle.RunCount"/> mod N, <see cref="QueryFilter.Slice"/>), so every row is
    /// visited once per N runs and a system over many entities costs about 1 / N per update instead of a spike on one.
    /// With <see cref="UpdateIntervalAttribute">[UpdateInterval(M)]</see>, a row is visited once per N * M updates. Rows that move between
    /// runs (swap-removes, structural changes, tables that grow or shrink) may be skipped or visited twice in one cycle.
    /// Works per entity, in batches and with <see cref="Parallel"/> (the slice is cut into chunks). Not with
    /// <c>[Changed&lt;T&gt;]</c> / <c>[Removed&lt;T&gt;]</c> (EECS035): the rows of the other slices would lose the
    /// changes made since the system last ran. Default 1: every row on every run.
    /// </summary>
    public int Slices { get; init; } = 1;

    /// <summary>
    /// With <see cref="Parallel"/>: the rows a thread takes at a time, rounded up to a multiple of 64 (OpenMP's
    /// <c>schedule(dynamic, n)</c>). By default the runtime chooses from the rows' cost, which each pass measures: about
    /// four chunks per thread, chunks of about 25 µs when rows are heavy (a few hundred rows of a chunk of terrain or a
    /// path search each spread over every thread), fewer chunks for a short pass; before the first pass, and for a
    /// method that merges per chunk (<see cref="Reduce{T}"/>, <see cref="Emit{T}"/>: 16 chunks, so its result does not
    /// depend on timing), the row count decides. Set it only to override that. Smaller chunks cost a claim each, and
    /// chunks that share a 4,096-row block of change ticks share its summary (atomically). Default 0: chosen by the
    /// runtime. Not without <see cref="Parallel"/> and not below 0 (EECS054).
    /// </summary>
    public int ChunkSize { get; init; }

    /// <summary>
    /// Whether the generator turns the method into a <c>System.Numerics.Vector&lt;T&gt;</c> loop over its runs of rows
    /// (several rows per instruction, the results of the method bit for bit). <see cref="SimdMode.Auto"/> vectorizes a
    /// method that qualifies and reports why one that looks eligible does not (EECS055, a message);
    /// <see cref="SimdMode.Require"/> makes a method that does not qualify an error (EECS056);
    /// <see cref="SimdMode.Off"/> keeps the method one row at a time. Default <see cref="SimdMode.Auto"/>.
    /// </summary>
    public SimdMode SimdMode { get; init; }

    /// <summary>
    /// What a vector loop may change in floating-point results. <see cref="FloatMode.Exact"/> keeps every result the
    /// method's bit for bit, so a float sum into a <see cref="Reduce{T}"/> stays one row at a time (EECS055 says so):
    /// the lanes of a vector loop each add their own rows, in another order. <see cref="FloatMode.Fast"/> accepts that
    /// order: the sum of the same rows, rounded differently (not bit for bit the method's, and not the same across
    /// vector widths), and the vector approximations of <c>Sin</c>, <c>Cos</c>, <c>Exp</c>, <c>Log</c>, <c>Log2</c> and
    /// <c>Hypot</c> (within 1 or 2 ulp of the scalar functions). Integer sums and the Min / Max of any lane type are exact
    /// either way. Default <see cref="FloatMode.Exact"/>: the same results on every machine (each lane an IEEE operation).
    /// </summary>
    public FloatMode FloatMode { get; init; }

    /// <summary>
    /// Whether the method shares one walk with the [ForEach] methods declared next to it in its system (pass fusion: each
    /// row loaded into cache once, the methods run on it in declaration order). <see cref="FusionMode.Auto"/> fuses
    /// consecutive methods when one walk gives the results of separate passes (the same filter and settings, no structural
    /// change, no read of other entities or of a singleton another method writes) and reports a near miss (EECS093, a
    /// message); <see cref="FusionMode.Require"/> makes a method that does not share the walk of the method before it an
    /// error (EECS094); <see cref="FusionMode.Off"/> gives the method a walk of its own. Default <see cref="FusionMode.Auto"/>.
    /// </summary>
    public FusionMode FusionMode { get; init; }
}

/// <summary>
/// The <see cref="ForEachAttribute"/> method consumes the rows it visits: when its pass ends, the entities it visited
/// are destroyed, for event and command entities that are handled once. Exactly the pass's rows go, never an entity
/// that matches later: a method that makes no structural change has every row it visited destroyed at once after the
/// pass (one <c>DestroyAll</c> with the method's filter, a table whose rows all go emptied in one step); a method that may
/// change structure has each entity destroyed right after its row. Destroying follows the rules of
/// <see cref="Entity.Destroy"/>: an entity holding cleanup components is retired, the destroy log records each id.
/// A pass that throws destroys nothing it has not finished: a method without structural changes destroys none of its
/// rows, which it visits again on the next run. Not with <see cref="ForEachAttribute.Slices"/> (EECS085).
/// <code>
/// [ForEach, Consume]
/// static void Count(in Damage hit, ref DamageDealt dealt) => dealt.Total += hit.Amount;   // each Damage event, once
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConsumeAttribute : Attribute;

/// <summary>
/// A static method that runs on one entity: the generator writes an entry point, a static member of the method's type
/// named like the method, which takes the entity (then the tick with <c>[Changed&lt;T&gt;]</c> / <c>[Removed&lt;T&gt;]</c>,
/// then the method's other values) and returns whether the method ran. The method takes what a
/// <see cref="ForEachAttribute"/> method outside a system takes for one entity: components (<c>ref</c> / <c>in</c>,
/// <see cref="Mut{T}"/>, <see cref="Maybe{T}"/>), singletons, <see cref="Lookup{T}"/>, the <see cref="Entity"/>, the
/// <see cref="World"/> and values of any other type, and filters (<see cref="WithAttribute{T}"/>,
/// <see cref="WithoutAttribute{T}"/>, <c>[WithAny&lt;T1, T2&gt;]</c>, <c>[Changed&lt;T&gt;]</c>, <c>[Removed&lt;T&gt;]</c>).
/// It compiles to the job <c>entity.Run</c> runs (an <see cref="IForEachEntity{T1}"/> struct with its <c>WriteMask</c>,
/// <see cref="INoStructuralChanges"/> when the method makes no structural change): one table lookup for all its
/// components, no allocation.
/// <code>
/// public static class Orders
/// {
///     [ForEntity, Without&lt;Closed&gt;]
///     static void Buy(ref Stock stock, ref Sales sales, int quantity) { stock.Items -= quantity; sales.Count += quantity; }
/// }
///
/// bool sold = Orders.Buy(shop, 3);   // false: closed, no Stock or Sales, or not alive
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method), JetBrains.Annotations.MeansImplicitUse]
public sealed class ForEntityAttribute : Attribute;

/// <summary>
/// A static method that handles each record the passes emitted (<see cref="Emit{T}"/>) once: its first parameter is the
/// record, <c>in T</c>, and the generator writes the pass that runs it on every record of <see cref="Emitted{T}"/> in
/// the order they were emitted (row order of each pass, passes in the order they ran), then clears them, so none is
/// handled twice. In an <see cref="EcsSystemAttribute"/> system it runs in declaration order with the system's
/// <see cref="ForEachAttribute"/> methods; in any other type it gets an entry point, a static member named like the
/// method that takes the world and the method's other values. The other parameters are what a [ForEach] method takes
/// beside its components: singletons (<c>ref</c> / <c>in</c>), <see cref="Lookup{T}"/>, <see cref="Emit{T}"/> of
/// another record type, the <see cref="World"/>, and (outside a system) values of any other type. One thread, no
/// allocation; the system writes <see cref="Emitted{T}"/>. A pass that throws clears nothing: the records stay, the
/// ones handled included. Records added to <see cref="Emitted{T}"/> while the pass runs are cleared unhandled
/// (an <see cref="Emit{T}"/> of the same record is refused: EECS108).
/// <code>
/// [ForEachRecord]
/// static void Send(in EntityUpdate update, ref Stats stats) => stats.Sent += update.Viewers;   // each update, once
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method), JetBrains.Annotations.MeansImplicitUse]
public sealed class ForEachRecordAttribute : Attribute;

/// <summary>
/// Whether a <see cref="ForEachAttribute"/> method runs as a vector loop (<see cref="ForEachAttribute.SimdMode"/>). A method
/// qualifies when it is static, makes no structural change (on one thread or in parallel), takes components (by <c>ref</c> or
/// <c>in</c>) and <see cref="Maybe{T}"/> of them, singletons and values, and computes with operations that have an exact
/// vector counterpart. <see cref="World.DescribeSchedule"/> names the loop each method of a system got
/// (<see cref="SystemInfo.Loops"/>).
/// </summary>
public enum SimdMode
{
    /// <summary>A vector loop when the method qualifies; EECS055 names the reason when one that looks eligible does not.</summary>
    Auto,

    /// <summary>A vector loop, or the build fails with the reason (EECS056).</summary>
    Require,

    /// <summary>One row at a time, as written.</summary>
    Off,
}

/// <summary>
/// Whether a <see cref="ForEachAttribute"/> method shares one walk with its neighbors in its system
/// (<see cref="ForEachAttribute.FusionMode"/>). <see cref="SystemInfo.Loops"/> names the methods each method shares its walk with.
/// </summary>
public enum FusionMode
{
    /// <summary>One walk with the methods next to it when that gives the same results; EECS093 names a near miss.</summary>
    Auto,

    /// <summary>One walk with the method declared before it, or the build fails with the reason (EECS094).</summary>
    Require,

    /// <summary>A walk of its own.</summary>
    Off,
}

/// <summary>What a vector loop of a <see cref="ForEachAttribute"/> method may change in floating-point results.</summary>
public enum FloatMode
{
    /// <summary>Every result bit for bit the method's: float sums of a reduction stay one row at a time.</summary>
    Exact,

    /// <summary>
    /// Float sums of a reduction may add their rows in another order (per vector lane), so they round differently, and
    /// Sin, Cos, Exp, Log, Log2 and Hypot take .NET's vector approximations: results may differ from the method's and
    /// between machines.
    /// </summary>
    Fast,
}

/// <summary>
/// The <see cref="ForEachAttribute"/> method also requires component <typeparamref name="T"/> (not passed in). On a
/// system group (an <see cref="ISystemGroup"/> struct), every [ForEach] method of the group's systems does.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>
/// The <see cref="ForEachAttribute"/> method skips entities having component <typeparamref name="T"/>. On a system
/// group (an <see cref="ISystemGroup"/> struct), every [ForEach] method of the group's systems does:
/// <code>[Without&lt;Inactive&gt;] public struct Gameplay : ISystemGroup;</code>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithoutAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>
/// The <see cref="ForEachAttribute"/> method visits only entities whose <typeparamref name="T"/> was written since its
/// system last ran (<see cref="SystemHandle.LastRunTick"/>; on the first run, every entity having it). Requires
/// <typeparamref name="T"/> like <see cref="WithAttribute{T}"/>; <typeparamref name="T"/> tracks changes
/// (<see cref="ITracked"/>). Several attributes require all of the components to have changed; for any of them,
/// use <c>[ChangedAny&lt;T1, T2&gt;]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ChangedAttribute<T> : Attribute where T : struct, IComponent, ITracked;

/// <summary>
/// The <see cref="ForEachAttribute"/> method visits only entities that lost <typeparamref name="T"/> since its system
/// last ran (<see cref="SystemHandle.LastRunTick"/>) and have not got it back: replication sends the removal, a cleanup
/// releases what the component stood for. Removing a component keeps the entity's row, so the method can read the
/// entity's other components; destroyed entities are not visited. <typeparamref name="T"/> tracks changes
/// (<see cref="ITracked"/>) and is not a parameter (the entity no longer has it, EECS016).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RemovedAttribute<T> : Attribute where T : struct, IComponent, ITracked;
