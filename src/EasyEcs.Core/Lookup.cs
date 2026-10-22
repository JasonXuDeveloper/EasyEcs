using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
/// Each lookup is one entity lookup (<see cref="World.TryGetEntity"/>, then the row's column). Valid for the call it is
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
        get => new(in new Entity(_world, id).Find<T>(false));
    }

    /// <summary>The <typeparamref name="T"/> of entity <paramref name="id"/>; throws when the entity lacks it or is not alive. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read(EntityId id) => ref new Entity(_world, id).Find<T>(true);

    /// <summary>Whether entity <paramref name="id"/> is alive and has <typeparamref name="T"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(EntityId id) => new Entity(_world, id).Has<T>();

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
        public bool TryFind(TKey key, out EntityId id)
        {
            bool found = IndexOf<T, TKey>(lookup).TryFind(key, out var entity);
            id = entity.Id;
            return found;
        }

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
            "method gets one when it calls TryFind or Count on the parameter itself (the index is then built before the pass)");

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
