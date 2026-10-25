using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EasyEcs;

/// <summary>
/// Read-only access to component <typeparamref name="T"/> of any entity, from a <c>[ForEach]</c> method (MPI's one-sided
/// <c>Get</c>, Unity's <c>ComponentLookup</c>): <c>static void Chase(ref Velocity v, in Target target,
/// Lookup&lt;Position&gt; positions)</c> reads the target's position with <c>positions[target.Id]</c>. The method stays
/// free of the World, so it may run in parallel (<see cref="ForEachAttribute.Parallel"/>) and share a parallel stage
/// with systems that do not write <typeparamref name="T"/>.
/// <para>
/// The contract: no one writes <typeparamref name="T"/> while the pass runs, so every read sees the value from before
/// the pass. The system is scheduled as a reader of <typeparamref name="T"/>, so no system writing it shares its stage;
/// a method that writes <typeparamref name="T"/> itself (a <c>ref</c>, <c>Mut</c>, writable <c>Maybe</c> or span
/// parameter) may not look it up (EECS059: rows written during the pass would be read before or after their write, or
/// torn on another thread); and the pass makes no structural change, so ids keep their rows.
/// </para>
/// <para>
/// An indexed <typeparamref name="T"/> (<see cref="IIndexed{TKey}"/>) is also found by key
/// (<see cref="LookupExtensions"/>: <c>TryFind</c>, <c>Find</c>, <c>Count</c>, called on the parameter itself): its index
/// is created and built before the pass, and before the parallel stage of its system, so a lookup during a pass never
/// changes it. (A value index otherwise builds on its first lookup; reached any other way during a parallel stage or
/// pass, an index not built yet throws instead of building.)
/// </para>
/// Each lookup is one entity lookup (bounds and version checked like <see cref="World.TryGetEntity"/>, then the row's
/// column). Valid for the call it is
/// passed to (a ref struct).
/// </summary>
/// <typeparam name="T">The component read.</typeparam>
public readonly ref struct Lookup<T> where T : struct, IComponent
{
    private readonly World _world;
    private readonly object _index; // T's value index, built, when T is indexed and the method finds it by key

    /// <summary>
    /// Lookups of <paramref name="world"/>'s <typeparamref name="T"/>; <paramref name="index"/> is its value index
    /// (<see cref="LookupExtensions.Prepared{T, TKey}"/>) or null. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Lookup(World world, object index)
    {
        _world = world;
        _index = index;
    }

    /// <summary>The <typeparamref name="T"/> of entity <paramref name="id"/>, read-only; no value when the entity lacks it or is not alive. O(1).</summary>
    public ReadOnlyMaybe<T> this[EntityId id]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(in Of(id).Find<T>(false));
    }

    /// <summary>The <typeparamref name="T"/> of entity <paramref name="id"/>; throws when the entity lacks it or is not alive. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read(EntityId id) => ref Of(id).Find<T>(true);

    /// <summary>Whether entity <paramref name="id"/> is alive and has <typeparamref name="T"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(EntityId id) => Of(id).Has<T>();

    /// <summary>
    /// The handle of <paramref name="id"/>, or a default handle (which finds nothing) when its index is past this world's
    /// records: the id comes from data (another world, a message, a save), not from one of this world's handles, so
    /// <see cref="Entity.LocateArchetype"/>'s unchecked record read needs the bound first.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity Of(EntityId id) => (uint)id.Index < (uint)_world.Meta.Length ? new Entity(_world, id) : default;

    /// <summary>
    /// The lanes of a vector loop reading <typeparamref name="T"/> of the entities in lanes (<paramref name="version"/>,
    /// <paramref name="index"/>): <paramref name="has"/>, the lanes whose entity is alive and has it (all bits set);
    /// <paramref name="rows"/>, their rows' byte offsets in <paramref name="column"/> (<see cref="LookupLanes.Gather{TLane}(nint, Vector256{int}, Vector256{int}, int)"/>).
    /// False when those entities live in several tables, or their table holds <typeparamref name="T"/> for some rows only:
    /// the vector's rows then run the method, and so do those of the next <paramref name="backoff"/> vectors (a count the
    /// loop keeps per lookup, 0 at its start, which grows to 63 with misses in a row). Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Resolve(Vector256<int> version, Vector256<int> index, ref int backoff, out Vector256<int> has, out Vector256<int> rows, out nint column)
    {
        Archetype table = null;
        bool none = false;
        if (LookupLanes.Records(_world, version, index, ref table, out has, out var slots) && LookupLanes.Column(table, ComponentType<T>.Bit, out column, ref none))
        {
            if (none) has = default;
            rows = slots * Unsafe.SizeOf<T>();
            backoff = 0;
            return true;
        }

        LookupLanes.Missed(ref backoff);
        has = rows = default;
        column = 0;
        return false;
    }

    /// <inheritdoc cref="Resolve(Vector256{int}, Vector256{int}, ref int, out Vector256{int}, out Vector256{int}, out nint)"/>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Resolve(Vector512<int> version, Vector512<int> index, ref int backoff, out Vector512<int> has, out Vector512<int> rows, out nint column)
    {
        Archetype table = null; // both halves' entities in one table
        bool none = false;
        if (LookupLanes.Records(_world, version.GetLower(), index.GetLower(), ref table, out var lower, out var low)
            && LookupLanes.Records(_world, version.GetUpper(), index.GetUpper(), ref table, out var upper, out var high)
            && LookupLanes.Column(table, ComponentType<T>.Bit, out column, ref none))
        {
            has = none ? default : Vector512.Create(lower, upper);
            rows = Vector512.Create(low, high) * Unsafe.SizeOf<T>();
            backoff = 0;
            return true;
        }

        LookupLanes.Missed(ref backoff);
        has = rows = default;
        column = 0;
        return false;
    }

    /// <inheritdoc cref="Resolve(Vector256{int}, Vector256{int}, ref int, out Vector256{int}, out Vector256{int}, out nint)"/>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Resolve(Vector<int> version, Vector<int> index, ref int backoff, out Vector<int> has, out Vector<int> rows, out nint column)
    {
        bool resolved;
        // Vector<T> is 256 bits on AVX2 hardware, 512 with DOTNET_PreferredVectorBitWidth=512 (IsAccelerated gates the loop).
        if (Vector<int>.Count == 16)
        {
            resolved = Resolve(version.AsVector512(), index.AsVector512(), ref backoff, out var wide, out var at, out column);
            (has, rows) = (wide.AsVector(), at.AsVector());
        }
        else
        {
            resolved = Resolve(version.AsVector256(), index.AsVector256(), ref backoff, out var narrow, out var at, out column);
            (has, rows) = (narrow.AsVector(), at.AsVector());
        }

        return resolved;
    }

    /// <summary>T's index, which the generated code resolved and built before the pass, or null.</summary>
    internal object Index => _index;
}

/// <summary>Key lookups of an indexed component (<see cref="IIndexed{TKey}"/>) through a <see cref="Lookup{T}"/>.</summary>
public static class LookupExtensions
{
    extension<T, TKey>(Lookup<T> lookup) where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        /// <summary>
        /// An entity whose <typeparamref name="T"/> has key <paramref name="key"/>: the one of a unique index
        /// (<see cref="IUniqueIndexed{TKey}"/>), any of them otherwise. O(1): a hash probe.
        /// </summary>
        public bool TryFind(TKey key, out Entity entity) => IndexOf<T, TKey>(lookup).TryFind(key, out entity);

        /// <summary>
        /// The entities whose <typeparamref name="T"/> has key <paramref name="key"/> (<c>foreach</c>). O(1), then O(1)
        /// per entity. Nothing writes <typeparamref name="T"/> during the pass, so the list holds still while it is walked.
        /// </summary>
        public ValueIndex<T, TKey>.Matches Find(TKey key) => IndexOf<T, TKey>(lookup).Find(key);

        /// <summary>The number of entities whose <typeparamref name="T"/> has key <paramref name="key"/>. O(1): a hash probe.</summary>
        public int Count(TKey key) => IndexOf<T, TKey>(lookup).Count(key);
    }

    private static ValueIndex<T, TKey> IndexOf<T, TKey>(Lookup<T> lookup) where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey> =>
        lookup.Index as ValueIndex<T, TKey> ?? Unprepared<T, TKey>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueIndex<T, TKey> Unprepared<T, TKey>() where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey> =>
        throw new InvalidOperationException($"This Lookup<{typeof(T).Name}> holds no index by {typeof(TKey).Name}: a [ForEach] " +
            "method gets one when it calls TryFind, Find or Count on the parameter itself (the index is then built before the pass)");

    /// <summary>
    /// <paramref name="world"/>'s index of <typeparamref name="T"/>, created and built: what a <see cref="Lookup{T}"/>
    /// of a method finding <typeparamref name="T"/> by key holds. Owner thread (or before a parallel stage). Generated
    /// code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static object Prepared<T, TKey>(World world) where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        var index = world.Index<T, TKey>();
        index.Prepare();
        return index;
    }
}
