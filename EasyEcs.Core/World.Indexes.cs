using System;
using System.Runtime.CompilerServices;
namespace EasyEcs;

// Value indexes: one per indexed component type (IIndexed<TKey>), created on first use.
public sealed partial class World
{
    // Indexed by TypeIndex<ValueIndexFamily, T>.Value; each slot is a ValueIndex<T, TKey>.
    private IValueIndex[] _indexes = [];

    // Indexed by component bit: the index of that component, told when an entity loses it. Null until the first index.
    private IValueIndex[] _indexByBit;

    private readonly struct ValueIndexFamily;

    internal interface IValueIndex
    {
        /// <summary>Forget every entry; <paramref name="rebuild"/>: index the rows again on the next lookup (after a restore).</summary>
        void Reset(bool rebuild);

        /// <summary>Entity <paramref name="id"/> is destroyed or loses the indexed component. Inside the structural gate.</summary>
        void Removed(int id);

        /// <summary>
        /// New entities hold the component in rows <paramref name="first"/> .. <paramref name="first"/> +
        /// <paramref name="count"/> - 1 of <paramref name="archetype"/> (its column <paramref name="column"/>; rows
        /// without the component, by their presence bit, are skipped). Inside the structural gate.
        /// </summary>
        void AddedRows(Archetype archetype, int column, int first, int count);
    }

    /// <summary>
    /// The value index of <typeparamref name="T"/>: find entities by <typeparamref name="T"/>'s key, or by the entity a
    /// relation component points at. Created on the first call (cold), O(1) after; keep it in a local or field. Throws
    /// <see cref="InvalidOperationException"/> when <typeparamref name="T"/> is already indexed by another key type.
    /// </summary>
    public ValueIndex<T, TKey> Index<T, TKey>() where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        int slot = TypeIndex<ValueIndexFamily, T>.Value;
        if (slot < _indexes.Length && _indexes[slot] is { } existing)
            return existing as ValueIndex<T, TKey> ?? throw OtherKey(typeof(T), existing);
        return Register(slot, ComponentType<T>.Bit, new ValueIndex<T, TKey>(this));
    }

    /// <summary>
    /// The relation index of <typeparamref name="T"/>: the entities whose <see cref="DynamicBuffer{T}"/> links to a
    /// target. Created on the first call (cold), O(1) after; keep it in a local or field.
    /// </summary>
    public RelationIndex<T> Relations<T>() where T : struct, IRelation
    {
        int slot = TypeIndex<ValueIndexFamily, DynamicBuffer<T>>.Value;
        if (slot < _indexes.Length && _indexes[slot] is { } existing) return (RelationIndex<T>)existing;
        return Register(slot, ComponentType<DynamicBuffer<T>>.Bit, new RelationIndex<T>(this));
    }

    /// <summary>The relation index of <typeparamref name="T"/> if it is built (writes then update it), else null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal RelationIndex<T> LiveRelations<T>() where T : struct, IRelation
    {
        int bit = ComponentType<DynamicBuffer<T>>.Bit;
        var byBit = _indexByBit;
        return byBit != null && bit < byBit.Length && byBit[bit] is RelationIndex<T> { Live: true } index ? index : null;
    }

    private TIndex Register<TIndex>(int slot, int bit, TIndex index) where TIndex : IValueIndex
    {
        if (slot >= _indexes.Length) Array.Resize(ref _indexes, Math.Max(slot + 1, _indexes.Length * 2));
        _indexes[slot] = index;
        _indexByBit ??= [];
        if (bit >= _indexByBit.Length) Array.Resize(ref _indexByBit, bit + 1);
        _indexByBit[bit] = index;
        return index;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException OtherKey(Type component, IValueIndex existing) =>
        new($"{component.Name} is already indexed by another key type ({existing.GetType().GenericTypeArguments[1].Name}): one index per component.");

    /// <summary>The index of a component type, told of every value written with Set or CreateEntity.</summary>
    internal interface IIndexOf<T> where T : struct, IComponent
    {
        void Put(int id, int version, in T value);
    }

    /// <summary>
    /// Entity <paramref name="id"/> (<paramref name="version"/>) now holds <paramref name="value"/> (Set, CreateEntity,
    /// Enable): re-index it if <typeparamref name="T"/> is indexed and this world has its index, move it to its group's
    /// table if <typeparamref name="T"/> is grouped here. Folded away for other components. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Written<T>(int id, int version, in T value) where T : struct, IComponent
    {
        if (!ComponentType<T>.Watched) return;
        int bit = ComponentType<T>.Bit;
        if (ComponentType<T>.Indexed)
        {
            var byBit = _indexByBit;
            if (byBit != null && bit < byBit.Length && byBit[bit] is { } index)
                Unsafe.As<IIndexOf<T>>(index).Put(id, version, in value);
        }

        if (ComponentType<T>.Grouped && _groupByBit != null) Regroup(id, bit, in value);
    }

    /// <summary>Tell the index of <paramref name="bit"/>, if any, that entity <paramref name="id"/> lost it (cold).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Unindex(ushort bit, int id)
    {
        if (bit < _indexByBit.Length) _indexByBit[bit]?.Removed(id);
    }

    /// <summary>Tell the indexes about the entities of copied rows [<paramref name="first"/>, +<paramref name="count"/>) of <paramref name="table"/>.</summary>
    private void IndexRows(Archetype table, int first, int count)
    {
        if (_indexByBit == null) return;
        var components = table.Components;
        for (int c = 0; c < components.Length; c++) // per indexed column, every row at once
            if (components[c] < _indexByBit.Length && _indexByBit[components[c]] is { } index)
                index.AddedRows(table, c, first, count);
    }

    /// <summary>Tell the indexes of <paramref name="archetype"/>'s components that entity <paramref name="id"/> is destroyed (cold).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Unindex(Archetype archetype, int id)
    {
        var components = archetype.Components;
        for (int i = 0; i < components.Length; i++) Unindex(components[i], id);
    }

    /// <summary>Empty every index: <see cref="Restore"/> re-indexes on the next lookup, <see cref="Dispose"/> leaves them empty.</summary>
    private void ResetIndexes(bool rebuild)
    {
        foreach (var index in _indexes) index?.Reset(rebuild);
    }
}
