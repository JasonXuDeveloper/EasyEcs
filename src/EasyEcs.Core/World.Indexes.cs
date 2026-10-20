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
    /// relation component points at. Created on the first call (cold), O(1) after; keep it in a local or field. The
    /// index of a unique component (<see cref="IUniqueIndexed{TKey}"/>) exists from the world's first table holding it.
    /// Throws <see cref="InvalidOperationException"/> when <typeparamref name="T"/> is already indexed by another key
    /// type, or is unique by another key; <see cref="NotSupportedException"/> for a unique <see cref="EntityId"/> key (a
    /// load or a move rewrites such keys after it copies the rows, too late to check them first).
    /// </summary>
    public ValueIndex<T, TKey> Index<T, TKey>() where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        int slot = TypeIndex<ValueIndexFamily, T>.Value;
        if (slot < _indexes.Length && _indexes[slot] is { } existing)
            return existing as ValueIndex<T, TKey> ?? throw OtherKey(typeof(T), existing);
        if (ComponentType<T>.Unique) Unique<T, TKey>();
        return Register(slot, ComponentType<T>.Bit, new ValueIndex<T, TKey>(this));
    }

    /// <summary>Checks the key of unique <typeparamref name="T"/> and tells every world how to index it (cold).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unique<T, TKey>() where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        if (default(T) is not IUniqueIndexed<TKey>)
            throw new InvalidOperationException($"{typeof(T).Name} is unique by another key than {typeof(TKey).Name}: one index per component");
        if (typeof(TKey) == typeof(EntityId))
            throw new NotSupportedException($"{typeof(T).Name}: an EntityId key cannot be unique (index it with IIndexed<EntityId>)");
        UniqueIndexOf<T>.Create ??= static world => world.Index<T, TKey>();
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

        /// <summary>New <paramref name="entities"/> were all given <paramref name="value"/> (CreateEntities).</summary>
        void PutAll(ReadOnlySpan<Entity> entities, in T value);
    }

    /// <summary>
    /// The index of a unique component (<see cref="IUniqueIndexed{TKey}"/>), asked before every write whether its key is
    /// free. Each claim throws <see cref="InvalidOperationException"/> when the key is another entity's (or a batch's
    /// twice), and changes nothing.
    /// </summary>
    internal interface IUniqueOf<T> where T : struct, IComponent
    {
        /// <summary>Entity <paramref name="id"/> (-1: one about to be created) is about to hold <paramref name="value"/>.</summary>
        void Claim(int id, in T value);

        /// <summary><paramref name="count"/> new entities are about to hold <paramref name="value"/> (CreateEntities).</summary>
        void ClaimAll(int count, in T value);

        /// <summary>
        /// Entity <paramref name="id"/> is about to hold <paramref name="value"/> in a batch that began with
        /// <see cref="IUniqueIndex.BeginBatch"/>: its key must be free and not claimed by the batch already.
        /// </summary>
        void ClaimBatch(int id, in T value);
    }

    /// <summary>A unique index seen through a component bit: the claims of row copies (prefabs, clones, arrivals, loads).</summary>
    internal interface IUniqueIndex
    {
        /// <summary>
        /// Rows [<paramref name="first"/>, +<paramref name="count"/>) of column <paramref name="column"/> of
        /// <paramref name="table"/> (of any world) are about to be copied <paramref name="copies"/> times each, to new
        /// entities (<paramref name="into"/> -1) or onto entity <paramref name="into"/>: the keys of the rows having the
        /// component must be free (and, copied more than once, null). The rows' own keys are distinct (their world's
        /// index saw to it).
        /// </summary>
        void ClaimRows(Archetype table, int column, int first, int count, int copies, int into);

        /// <summary>Start a batch of claims (<see cref="IUniqueOf{T}.ClaimBatch"/>, <see cref="ClaimLoaded"/>).</summary>
        void BeginBatch();

        /// <summary>
        /// Loaded rows of column <paramref name="column"/> are about to replace the world's (<see cref="Load"/>): keys
        /// may repeat neither within them nor across the batch's other tables. Throws
        /// <see cref="System.IO.InvalidDataException"/>.
        /// </summary>
        void ClaimLoaded(Archetype.Rows rows, int column);
    }

    /// <summary>
    /// Before entity <paramref name="id"/> (-1: one about to be created) holds <paramref name="value"/>: a unique
    /// component's key must be free (<see cref="IUniqueOf{T}.Claim"/>). Folded away for other components. Throws before
    /// anything changes, so call it before the row moves. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Claim<T>(int id, in T value) where T : struct, IComponent
    {
        if (!ComponentType<T>.Unique) return;
        int bit = ComponentType<T>.Bit;
        var byBit = _indexByBit;
        // No index yet: no table holds T, so no entity has a key to collide with.
        if (byBit != null && bit < byBit.Length && byBit[bit] is { } index) Unsafe.As<IUniqueOf<T>>(index).Claim(id, in value);
    }

    /// <summary>
    /// Entity <paramref name="id"/> now holds <paramref name="value"/> of unique <typeparamref name="T"/>, added by
    /// <c>GetOrAdd</c>: its index learns the key (only the index: the caller holds a ref into the row, which a group must
    /// not move). Inside the structural gate.
    /// </summary>
    internal void IndexUnique<T>(int id, int version, in T value) where T : struct, IComponent =>
        Unsafe.As<IIndexOf<T>>(_indexByBit[ComponentType<T>.Bit]).Put(id, version, in value);

    /// <summary><see cref="Claim{T}"/> for <paramref name="count"/> new entities all holding <paramref name="value"/> (CreateEntities).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClaimAll<T>(int count, in T value) where T : struct, IComponent
    {
        if (ComponentType<T>.Unique) Unsafe.As<IUniqueOf<T>>(UniqueIndex(ComponentType<T>.Bit)).ClaimAll(count, in value);
    }

    /// <summary>The index of unique component <paramref name="bit"/>, created (empty) when this world has none yet. Cold.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal IUniqueIndex UniqueIndex(int bit)
    {
        var byBit = _indexByBit;
        if (byBit != null && bit < byBit.Length && byBit[bit] is { } index) return (IUniqueIndex)index;
        return (IUniqueIndex)ComponentInfo.Of(bit).CreateUniqueIndex(this);
    }

    /// <summary>
    /// The rows a <see cref="Load"/> read (<paramref name="snapshot"/>, by table of this world) are about to replace the
    /// world's: each unique component's keys must be distinct across them (<see cref="IUniqueIndex.ClaimLoaded"/>).
    /// Throws <see cref="System.IO.InvalidDataException"/> before anything changes. Cold.
    /// </summary>
    private void ClaimLoaded(WorldSnapshot snapshot)
    {
        using (Structural())
        {
            var tables = _allArchetypes; // the load created the tables its rows go to: their indexes exist
            if (_indexByBit == null) return;
            for (int bit = 0; bit < _indexByBit.Length; bit++)
                if (_indexByBit[bit] is IUniqueIndex index && ComponentInfo.Of(bit).Unique)
                    index.BeginBatch();
            for (int i = 0; i < snapshot.TableCount; i++)
            {
                if (snapshot.Tables[i] is not { Count: > 0 } rows || !tables[i].Unique) continue;
                var components = tables[i].Components;
                for (int c = 0; c < components.Length; c++)
                    if (ComponentInfo.Of(components[c]).Unique)
                        UniqueIndex(components[c]).ClaimLoaded(rows, c);
            }
        }
    }

    /// <summary>
    /// A new table: the unique components it holds get their index now, before any row, so every write of them is
    /// claimed (cold: once per table, and only once a unique type exists). Inside the structural gate.
    /// </summary>
    private void IndexUnique(Archetype table)
    {
        foreach (var bit in table.Components)
            if (ComponentInfo.Of(bit).Unique)
                UniqueIndex(bit);
    }

    /// <summary>
    /// Rows [<paramref name="first"/>, +<paramref name="count"/>) of <paramref name="table"/> (of any world) are about to
    /// be copied <paramref name="copies"/> times each into new entities of this world (<paramref name="into"/> -1) or onto
    /// entity <paramref name="into"/>: claim the keys of their unique components (<see cref="IUniqueIndex.ClaimRows"/>).
    /// Throws before anything changes. Inside the structural gate.
    /// </summary>
    private void ClaimRows(Archetype table, int first, int count, int copies, int into = -1)
    {
        if (!table.Unique || count == 0 || copies == 0) return;
        var components = table.Components;
        for (int c = 0; c < components.Length; c++)
            if (ComponentInfo.Of(components[c]).Unique)
                UniqueIndex(components[c]).ClaimRows(table, c, first, count, copies, into);
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

    /// <summary>
    /// New <paramref name="entities"/> all hold <paramref name="value"/> of indexed <typeparamref name="T"/>
    /// (CreateEntities, which already placed them in their group's table): one key lookup for all of them, if this
    /// world has the index. Inside the structural gate.
    /// </summary>
    internal void WrittenAll<T>(ReadOnlySpan<Entity> entities, in T value) where T : struct, IComponent
    {
        int bit = ComponentType<T>.Bit;
        var byBit = _indexByBit;
        if (byBit != null && bit < byBit.Length && byBit[bit] is { } index) Unsafe.As<IIndexOf<T>>(index).PutAll(entities, in value);
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

    /// <summary>Empty every index: <see cref="Restore(WorldSnapshot)"/> re-indexes on the next lookup, <see cref="Dispose"/> leaves them empty.</summary>
    private void ResetIndexes(bool rebuild)
    {
        foreach (var index in _indexes) index?.Reset(rebuild);
        if (!rebuild || _indexByBit == null || !ComponentInfo.AnyUnique) return;
        // Unique indexes stay eager: they index the restored rows now (no lookup may come before the next write).
        foreach (var table in _allArchetypes)
            if (table.Unique && table.Count != 0)
                for (int c = 0; c < table.Components.Length; c++)
                    if (ComponentInfo.Of(table.Components[c]).Unique && _indexByBit[table.Components[c]] is { } index)
                        index.AddedRows(table, c, 0, table.Count);
    }
}
