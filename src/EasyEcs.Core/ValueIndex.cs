using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace EasyEcs;

/// <summary>
/// What every <see cref="IIndexed{TKey}"/> is, so a component type is known to be indexed without reflection (a type
/// test the JIT folds). Implement <see cref="IIndexed{TKey}"/>, not this.
/// </summary>
public interface IIndexed;

/// <summary>
/// A component whose <see cref="Key"/> the world indexes: <c>world.Index&lt;T, TKey&gt;().Find(key)</c> enumerates the
/// entities whose <typeparamref name="TKey"/> equals a value (an item's owner, a player's name, a cell of a grid). An
/// <see cref="EntityId"/> key makes the component a relation: <c>ChildOf(EntityId Parent)</c> indexed by its parent
/// finds the children of an entity. The index follows the values written with <c>Set</c> and <c>CreateEntity</c> as they are
/// written, so an indexed component is never written through a <c>ref</c> (<c>Get</c>, <c>ref</c> parameters, query
/// items): the analyzer reports such writes (EECS015).
/// </summary>
public interface IIndexed<TKey> : IIndexed where TKey : IEquatable<TKey>
{
    /// <summary>The value the entity is found by.</summary>
    TKey Key { get; }
}

/// <summary>
/// The list of one key of an index (a value index's key, a relation's target): its entities in one array (the first
/// <see cref="Used"/> slots, entities and holes), its length, and for a <see cref="TargetSlots"/> slot the target
/// incarnation it belongs to. A leaving entity leaves a hole (a negative index: the next hole of <see cref="Free"/>'s
/// chain, + 1) that the next entity fills. Entities never move, so a loop reads them in order and finds each once,
/// whatever is destroyed during it.
/// </summary>
internal struct IndexHead
{
    public EntityId[] Ids;
    public int Used, Free, Count, Version;

    /// <summary>Add <paramref name="id"/> in the first hole, else at the end; its place in <see cref="Ids"/>.</summary>
    public int Add(EntityId id, SpareIds spares)
    {
        int place;
        if (Free != 0)
        {
            place = Free - 1;
            Free = -1 - Ids[place].Index;
        }
        else
        {
            if (Ids == null || Used == Ids.Length) Ids = spares.Grow(Ids, Used, Used + 1);
            place = Used++;
        }

        Ids[place] = id;
        Count++;
        return place;
    }

    /// <summary>Make room for <paramref name="more"/> entities at once (a run of rows with this key), instead of growing step by step.</summary>
    public void Reserve(int more, SpareIds spares)
    {
        if (Free == 0 && (Ids == null || Used + more > Ids.Length)) Ids = spares.Grow(Ids, Used, Used + more);
    }

    /// <summary>
    /// Remove the entity at <paramref name="place"/>; whether the list is empty now. An emptied list gives its array
    /// to the lists that grow next (keeping it would strand it in a slot whose id no longer names a target).
    /// </summary>
    public bool Remove(int place, SpareIds spares)
    {
        // A hole even in an emptied list: a loop over it may still be walking the array.
        Ids[place] = new EntityId(-1 - Free, 0);
        Free = place + 1;
        if (--Count != 0) return false;
        spares.Return(Ids);
        Ids = null;
        Used = Free = 0;
        return true;
    }

    /// <summary>A walk over the entities the list holds now.</summary>
    public readonly ListWalk Walk() => new(Ids, Used);
}

/// <summary>The arrays of emptied lists of an index, by length, for the next lists that grow: keys that come and go allocate nothing.</summary>
internal sealed class SpareIds
{
    private readonly List<EntityId[]>[] _byLength = new List<EntityId[]>[32]; // by log2 of the length

    /// <summary>
    /// An array holding the first <paramref name="used"/> ids of <paramref name="ids"/> with room for
    /// <paramref name="needed"/>: twice as long, or the power of two that fits (4 at least).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EntityId[] Grow(EntityId[] ids, int used, int needed)
    {
        int length = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(Math.Max(needed, 4), ids == null ? 0 : ids.Length * 2));
        var spares = _byLength[BitOperations.Log2((uint)length)];
        EntityId[] grown;
        if (spares is { Count: > 0 })
        {
            grown = spares[^1];
            spares.RemoveAt(spares.Count - 1);
        }
        else grown = new EntityId[length];

        if (ids != null)
        {
            ids.AsSpan(0, used).CopyTo(grown);
            Return(ids);
        }

        return grown;
    }

    public void Return(EntityId[] ids) => (_byLength[BitOperations.Log2((uint)ids.Length)] ??= []).Add(ids);
}

/// <summary>
/// A walk over the entities of one <see cref="IndexHead"/> list, past its holes, the latest first: a loop that
/// destroys what it finds (a cascade) removes the newest rows of their table first, which moves no other row.
/// </summary>
internal struct ListWalk
{
    private readonly EntityId[] _ids;
    private int _place;

    public ListWalk(EntityId[] ids, int used)
    {
        _ids = ids;
        _place = used;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Next(out EntityId id)
    {
        // _place starts at the length the list had when the walk began, within the array the walk holds: no bounds
        // check.
        while (--_place >= 0)
        {
            id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ids), _place);
            if (id.Index >= 0) return true;
        }

        id = default;
        return false;
    }
}

/// <summary>
/// The lists of a relation index by the target's entity index, no hashing: slot i holds the list of one incarnation of
/// entity i (Count 0: free). A target whose slot holds another incarnation's list lives in the index's dictionary, and
/// stays there until its list empties.
/// </summary>
internal struct TargetSlots
{
    private IndexHead[] _slots;

    /// <summary>The slot list of <paramref name="target"/>, or a null ref when its slot holds none of it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref IndexHead Find(EntityId target)
    {
        var slots = _slots;
        if (slots != null && (uint)target.Index < (uint)slots.Length)
        {
            ref var head = ref slots[target.Index];
            if (head.Count != 0 && head.Version == target.Version) return ref head;
        }

        return ref Unsafe.NullRef<IndexHead>();
    }

    /// <summary>
    /// Claim the free slot of <paramref name="target"/>, which <see cref="Find"/> did not find, for its new list; a null
    /// ref when the slot holds another incarnation's list or the target's list lives in the dictionary already
    /// (<paramref name="elsewhere"/>).
    /// </summary>
    public ref IndexHead Claim(EntityId target, bool elsewhere)
    {
        var slots = _slots ??= [];
        if (target.Index >= slots.Length) Array.Resize(ref _slots, Math.Max(target.Index + 1, slots.Length * 2));
        ref var head = ref _slots[target.Index];
        if (head.Count != 0 || elsewhere) return ref Unsafe.NullRef<IndexHead>();
        head.Version = target.Version;
        return ref head;
    }

    public readonly void Clear()
    {
        if (_slots != null) Array.Clear(_slots);
    }
}

/// <summary>
/// The value index of component <typeparamref name="T"/> in one world (<see cref="World.Index{T,TKey}"/>). It is kept
/// current as values are written: <c>Set</c> and <c>CreateEntity</c> re-index the entity at once (O(1)), and entities
/// leave it as soon as they are destroyed or lose the component, so a lookup does no catching up. The first lookup
/// (and the first after <see cref="World.Restore(WorldSnapshot)"/>) indexes every row once. Lookups allocate nothing once every key has
/// been seen; the index belongs to the world's owner thread, like component values (systems that look up from
/// <c>OnUpdate</c> or instance methods run alone).
/// </summary>
public sealed class ValueIndex<T, TKey> : World.IValueIndex, World.IIndexOf<T>
    where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
{
    private readonly World _world;

    // Key -> its list (IndexHead: the entities in one array, read in order by a lookup). _nodes (indexed by entity id)
    // knows each entity's key and place in its list, so moving it between keys and dropping it are O(1).
    private readonly Dictionary<TKey, IndexHead> _heads = new();
    private readonly SpareIds _spares = new();

    // Relations (EntityId keys): the lists by the target's index, no hashing. A target whose index is still held by
    // links to an older incarnation, and EntityId.Null, use _heads.
    private TargetSlots _byTarget;
    private Node[] _nodes = [];
    private bool _built; // every row is indexed (false until the first lookup, and again after a restore)

    // Ordered walks (Range / Ascending / Descending, TKey : IComparable): the keys that have entities, sorted. Built by
    // the first ordered walk, then kept by AddHead / Unlink (a binary search and a block move per new or emptied key).
    // _keysVersion changes when a key enters or leaves, so a walk that outlives the change finds its place again.
    private TKey[] _sorted;
    private int _sortedCount;
    private int _keysVersion;

    private struct Node
    {
        public TKey Key;
        public int Place; // in its key's Ids
        public bool Linked;
    }

    internal ValueIndex(World world) => _world = world;

    /// <summary>The world the index belongs to.</summary>
    internal World World => _world;

    /// <summary>
    /// The entities whose <typeparamref name="T"/> has key <paramref name="key"/> (none for a null key): O(1), then O(1)
    /// per entity (the first lookup indexes every row once). Owner thread only. Destroying entities during the loop is
    /// allowed, changing <typeparamref name="T"/> is not.
    /// </summary>
    public Matches Find(TKey key)
    {
        if (!_built) Build();
        ref var head = ref HeadOf(key);
        return new Matches(_world, Unsafe.IsNullRef(ref head) ? default : head.Walk());
    }

    /// <summary>Index every row now if the index is not built (before entering the structural gate).</summary>
    internal void Prepare()
    {
        if (!_built) Build();
    }

    /// <summary>The first entity found with key <paramref name="key"/> (any, when several have it).</summary>
    public bool TryFind(TKey key, out Entity entity)
    {
        foreach (var found in Find(key))
        {
            entity = found;
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>
    /// Number of entities with key <paramref name="key"/>: O(1) (a hash probe, or an array access for a relation to an
    /// entity of this world; the first lookup indexes every row once).
    /// </summary>
    public int Count(TKey key)
    {
        if (!_built) Build();
        ref var head = ref HeadOf(key);
        return Unsafe.IsNullRef(ref head) ? 0 : head.Count;
    }

    /// <summary>Forget everything; after <see cref="World.Restore(WorldSnapshot)"/> the next lookup re-indexes every row, after <see cref="World.Dispose"/> it finds nothing.</summary>
    void World.IValueIndex.Reset(bool rebuild)
    {
        _heads.Clear();
        _byTarget.Clear();
        Array.Clear(_nodes);
        _sorted = null; // sorted again by the next ordered walk (one sort instead of one insertion per key)
        _sortedCount = 0;
        _keysVersion++;
        _built = !rebuild;
    }

    /// <summary>Entity <paramref name="id"/> was given <paramref name="value"/> (Set, CreateEntity). Inside the structural gate.</summary>
    void World.IIndexOf<T>.Put(int id, int version, in T value)
    {
        if (_built) Put(id, version, value.Key); // else the first lookup indexes the row
    }

    /// <summary>New entities all given <paramref name="value"/> (CreateEntities): one key lookup, the list sized once.</summary>
    void World.IIndexOf<T>.PutAll(ReadOnlySpan<Entity> entities, in T value)
    {
        if (!_built) return;
        var key = value.Key;
        if (IsNull(key)) return;
        ref var head = ref AddHead(key);
        head.Reserve(entities.Length, _spares);
        foreach (var entity in entities)
        {
            int id = entity.Index;
            if (id >= _nodes.Length) Array.Resize(ref _nodes, Math.Max(id + 1, _nodes.Length * 2));
            Link(id, entity.Version, key, ref head); // a new entity is in no list: its id left its list when destroyed
        }
    }

    /// <summary>
    /// New entities were given rows of <paramref name="archetype"/> holding <typeparamref name="T"/> (Instantiate, moves,
    /// loads). Rows of one key in a row, as the children of one parent, share one head lookup.
    /// </summary>
    void World.IValueIndex.AddedRows(Archetype archetype, int column, int first, int count)
    {
        if (!_built) return;
        ref var values = ref archetype.Data<T>(column);
        var slots = archetype.Slots;
        ref var head = ref Unsafe.NullRef<IndexHead>();
        TKey previous = default!;
        for (int r = first; r < first + count; r++)
        {
            if (!archetype.Has(column, r)) continue;
            int id = slots[r].Index;
            var key = Unsafe.Add(ref values, r).Key;
            if (id >= _nodes.Length) Array.Resize(ref _nodes, Math.Max(id + 1, _nodes.Length * 2));
            if (_nodes[id].Linked || IsNull(key))
            {
                Put(id, slots[r].Version, key); // may unlink: heads change, so look the next one up
                head = ref Unsafe.NullRef<IndexHead>();
                continue;
            }

            // Valid until the next AddHead or Unlink: linking only writes nodes and this head.
            if (Unsafe.IsNullRef(ref head) || !previous.Equals(key))
            {
                head = ref AddHead(key);
                previous = key;
                int run = 1; // the rows of this key in a row (the children of one parent) get their room at once
                while (r + run < first + count && Unsafe.Add(ref values, r + run).Key.Equals(key)) run++;
                head.Reserve(run, _spares);
            }

            Link(id, slots[r].Version, key, ref head);
        }
    }

    /// <summary>Entity <paramref name="id"/> is destroyed or loses <typeparamref name="T"/>. Inside the structural gate.</summary>
    void World.IValueIndex.Removed(int id)
    {
        if (id < _nodes.Length && _nodes[id].Linked) Unlink(id);
    }

    /// <summary>The list of <paramref name="key"/>, or a null ref when no entity has it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref IndexHead HeadOf(TKey key)
    {
        if (typeof(TKey) == typeof(EntityId)) // a JIT-time constant
        {
            ref var head = ref SlotOf(key);
            if (!Unsafe.IsNullRef(ref head)) return ref head;
            if (_heads.Count == 0) return ref Unsafe.NullRef<IndexHead>(); // no relation outside _byTarget
        }

        if (IsNull(key)) return ref Unsafe.NullRef<IndexHead>();
        return ref CollectionsMarshal.GetValueRefOrNullRef(_heads, key);
    }

    /// <summary>The <see cref="_byTarget"/> list of relation key <paramref name="key"/>, or a null ref when it has none there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref IndexHead SlotOf(TKey key) => ref _byTarget.Find(Unsafe.As<TKey, EntityId>(ref key));

    // A value-type key is never null; testing it would box it in unoptimized (tier-0) code.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNull(TKey key) => !typeof(TKey).IsValueType && key is null;

    /// <summary>The list of <paramref name="key"/> (not null), created empty when no entity has it.</summary>
    private ref IndexHead AddHead(TKey key)
    {
        if (typeof(TKey) == typeof(EntityId))
        {
            var target = Unsafe.As<TKey, EntityId>(ref key);
            if (!target.IsNull)
            {
                ref var head = ref _byTarget.Find(target);
                if (!Unsafe.IsNullRef(ref head)) return ref head;
                head = ref _byTarget.Claim(target, _heads.Count != 0 && _heads.ContainsKey(key));
                if (!Unsafe.IsNullRef(ref head)) return ref head;
            }
        }

        ref var added = ref CollectionsMarshal.GetValueRefOrAddDefault(_heads, key, out bool exists);
        if (!exists && _sorted != null) InsertSorted(key);
        return ref added;
    }

    /// <summary>Index every row of <typeparamref name="T"/>: the first lookup, and the first after a restore.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Build()
    {
        _built = true;
        var job = new BuildJob { Index = this };
        _world.ForEachEntity<T, BuildJob>(ref job);
    }

    // Reads every row without stamping it (Writes = 0): building the index is not a change of a tracked T.
    private struct BuildJob : IForEachEntity<T>, INoStructuralChanges
    {
        public ValueIndex<T, TKey> Index;

        public void Execute(Entity entity, ref T value) => Index.Put(entity.Index, entity.Version, value.Key);

        public static int Writes => 0;
    }

    /// <summary>
    /// Index entity <paramref name="id"/> (its current incarnation, <paramref name="version"/>: its row's, next to its id)
    /// under <paramref name="key"/>; a null key (the default of a reference-type key) indexes it nowhere.
    /// </summary>
    private void Put(int id, int version, TKey key)
    {
        if (id >= _nodes.Length) Array.Resize(ref _nodes, Math.Max(id + 1, _nodes.Length * 2));
        ref var node = ref _nodes[id];
        if (node.Linked)
        {
            if (node.Key.Equals(key)) return;
            Unlink(id);
        }

        if (IsNull(key)) return;
        Link(id, version, key, ref AddHead(key));
    }

    /// <summary>Put unlinked entity <paramref name="id"/> in <paramref name="head"/>, the list of <paramref name="key"/>.</summary>
    private void Link(int id, int version, TKey key, ref IndexHead head)
    {
        ref var node = ref _nodes[id];
        node.Place = head.Add(new EntityId(id, version), _spares);
        node.Key = key;
        node.Linked = true;
    }

    private void Unlink(int id)
    {
        ref var node = ref _nodes[id];
        // The same storage HeadOf picks, one lookup: the target's slot, else the dictionary.
        ref var head = ref Unsafe.NullRef<IndexHead>();
        bool inSlot = false;
        if (typeof(TKey) == typeof(EntityId))
        {
            head = ref SlotOf(node.Key);
            inSlot = !Unsafe.IsNullRef(ref head);
        }

        if (!inSlot) head = ref CollectionsMarshal.GetValueRefOrNullRef(_heads, node.Key);
        // An emptied _byTarget slot just stays (Count 0 marks it free); a dictionary entry goes.
        if (head.Remove(node.Place, _spares) && !inSlot)
        {
            _heads.Remove(node.Key);
            if (_sorted != null) RemoveSorted(node.Key);
        }

        node.Linked = false;
        node.Key = default!; // let the GC collect a managed key
    }

    /// <summary>
    /// Entities by key order: keys from <paramref name="min"/> to <paramref name="max"/> (inclusive, when
    /// <paramref name="bounded"/>), ascending or <paramref name="descending"/>. The first ordered walk sorts the keys
    /// once. Comparable keys only (the public members are extensions constrained on <see cref="IComparable{T}"/>).
    /// </summary>
    internal Ordered Walk(TKey min, TKey max, bool bounded, bool descending)
    {
        if (!_built) Build();
        if (_sorted == null) SortKeys();
        return new Ordered(this, min, max, bounded, descending);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SortKeys()
    {
        var sorted = new TKey[Math.Max(16, _heads.Count)];
        _heads.Keys.CopyTo(sorted, 0);
        _sortedCount = _heads.Count;
        Array.Sort(sorted, 0, _sortedCount, Comparer<TKey>.Default);
        _sorted = sorted;
        _keysVersion++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Compare(TKey a, TKey b) => Comparer<TKey>.Default.Compare(a, b); // devirtualized for value types

    /// <summary>Position of the first sorted key not below <paramref name="key"/> (<paramref name="above"/>: above it).</summary>
    private int Bound(TKey key, bool above)
    {
        int lo = 0, hi = _sortedCount;
        while (lo < hi)
        {
            int mid = (int)((uint)(lo + hi) >> 1);
            int order = Compare(_sorted[mid], key);
            if (order < 0 || (above && order == 0)) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private void InsertSorted(TKey key)
    {
        int at = Bound(key, above: false);
        if (_sortedCount == _sorted.Length) Array.Resize(ref _sorted, _sorted.Length * 2);
        Array.Copy(_sorted, at, _sorted, at + 1, _sortedCount - at);
        _sorted[at] = key;
        _sortedCount++;
        _keysVersion++;
    }

    private void RemoveSorted(TKey key)
    {
        int at = Bound(key, above: false);
        _sortedCount--;
        Array.Copy(_sorted, at + 1, _sorted, at, _sortedCount - at);
        _sorted[_sortedCount] = default!; // let the GC collect a managed key
        _keysVersion++;
    }

    /// <summary>
    /// Enumerates entities in key order (<c>foreach</c>): every entity of a key, then the next key. O(log keys) to
    /// start, O(1) per entity and per key. Destroying entities during the loop is allowed, changing
    /// <typeparamref name="T"/> is not.
    /// </summary>
    public ref struct Ordered
    {
        private readonly ValueIndex<T, TKey> _index;
        private readonly TKey _min, _max;
        private readonly bool _bounded, _descending;
        private TKey _key;
        private ListWalk _walk;
        private int _pos, _version;
        private bool _started;

        internal Ordered(ValueIndex<T, TKey> index, TKey min, TKey max, bool bounded, bool descending)
        {
            _index = index;
            _min = min;
            _max = max;
            _bounded = bounded;
            _descending = descending;
            _key = default!;
            _pos = 0;
            _version = 0;
            _walk = default;
            _started = false;
            Current = default;
        }

        /// <summary>The current entity.</summary>
        public Entity Current { get; private set; }

        /// <summary>The key of <see cref="Current"/>.</summary>
        public readonly TKey Key => _key;

        /// <summary>The enumerator itself (<c>foreach</c> support, no allocation).</summary>
        public readonly Ordered GetEnumerator() => this;

        /// <summary>Move to the next entity, in key order.</summary>
        public bool MoveNext()
        {
            var index = _index;
            while (true)
            {
                if (_walk.Next(out var id))
                {
                    Current = new Entity(index._world, id);
                    return true;
                }

                if (!_started)
                {
                    _pos = _descending
                        ? (_bounded ? index.Bound(_max, above: true) : index._sortedCount) - 1
                        : _bounded ? index.Bound(_min, above: false) : 0;
                    _started = true;
                }
                else if (_version != index._keysVersion) // keys came or went: find the key after the last one again
                    _pos = _descending ? index.Bound(_key, above: false) - 1 : index.Bound(_key, above: true);
                else _pos += _descending ? -1 : 1;

                _version = index._keysVersion;
                if ((uint)_pos >= (uint)index._sortedCount) return false;
                _key = index._sorted[_pos];
                if (_bounded && (_descending ? Compare(_key, _min) < 0 : Compare(_key, _max) > 0)) return false;
                _walk = index.HeadOf(_key).Walk(); // a sorted key has entities
            }
        }
    }

    /// <summary>Enumerates the entities of one key (<c>foreach</c>).</summary>
    public ref struct Matches
    {
        private readonly World _world;
        private ListWalk _walk;

        internal Matches(World world, ListWalk walk)
        {
            _world = world;
            _walk = walk;
            Current = default;
        }

        /// <summary>The current matching entity.</summary>
        public Entity Current { get; private set; }

        /// <summary>The enumerator itself (<c>foreach</c> support, no allocation).</summary>
        public readonly Matches GetEnumerator() => this;

        /// <summary>Move to the next entity with the key. O(1) per entity.</summary>
        public bool MoveNext()
        {
            // An entity destroyed during the loop (a cascade) leaves a hole; the walk moves on past it.
            if (!_walk.Next(out var id)) return false;
            Current = new Entity(_world, id);
            return true;
        }
    }
}
