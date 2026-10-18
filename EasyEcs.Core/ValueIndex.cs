using System;
using System.Collections.Generic;
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

/// <summary>The list of one key of an index: its first entity (or link), its length, and for a <see cref="TargetSlots"/> slot the target incarnation it belongs to.</summary>
internal struct IndexHead
{
    public int First, Count, Version;
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
/// (and the first after <see cref="World.Restore"/>) indexes every row once. Lookups allocate nothing once every key has
/// been seen; the index belongs to the world's owner thread, like component values (systems that look up from
/// <c>OnUpdate</c> or instance methods run alone).
/// </summary>
public sealed class ValueIndex<T, TKey> : World.IValueIndex, World.IIndexOf<T>
    where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
{
    private readonly World _world;

    // Key -> its list (first entity id and length). Lists are doubly linked through _nodes (indexed by entity id), so
    // moving an entity between keys and dropping it are O(1) without allocation.
    private readonly Dictionary<TKey, IndexHead> _heads = new();

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
        public int Next, Prev;
        public int Version; // the entity incarnation the node belongs to
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
        return new Matches(this, Unsafe.IsNullRef(ref head) ? -1 : head.First);
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

    /// <summary>Forget everything; after <see cref="World.Restore"/> the next lookup re-indexes every row, after <see cref="World.Dispose"/> it finds nothing.</summary>
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

    /// <summary>Put unlinked entity <paramref name="id"/> first in <paramref name="head"/>, the list of <paramref name="key"/>.</summary>
    private void Link(int id, int version, TKey key, ref IndexHead head)
    {
        ref var node = ref _nodes[id];
        node.Key = key;
        node.Version = version;
        node.Linked = true;
        node.Prev = -1;
        node.Next = head.Count != 0 ? head.First : -1;
        if (head.Count != 0) _nodes[head.First].Prev = id;
        head.First = id;
        head.Count++;
    }

    private void Unlink(int id)
    {
        ref var node = ref _nodes[id];
        // Decided by key, before the count changes: the same storage HeadOf picks.
        bool inSlot = typeof(TKey) == typeof(EntityId) && !Unsafe.IsNullRef(ref SlotOf(node.Key));
        ref var head = ref HeadOf(node.Key);
        if (--head.Count == 0)
        {
            // An emptied _byTarget slot just stays (Count 0 marks it free); a dictionary entry goes.
            if (!inSlot)
            {
                _heads.Remove(node.Key);
                if (_sorted != null) RemoveSorted(node.Key);
            }
        }
        else if (node.Prev < 0) head.First = node.Next;
        if (node.Prev >= 0) _nodes[node.Prev].Next = node.Next;
        if (node.Next >= 0) _nodes[node.Next].Prev = node.Prev;
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
        private int _pos, _version, _next;
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
            _next = -1;
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
                while (_next >= 0)
                {
                    ref var node = ref index._nodes[_next];
                    int id = _next;
                    _next = node.Next;
                    if (!node.Linked) continue;
                    Current = new Entity(index._world, id, node.Version);
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
                _next = index.HeadOf(_key).First; // a sorted key has entities
            }
        }
    }

    /// <summary>Enumerates the entities of one key (<c>foreach</c>).</summary>
    public ref struct Matches
    {
        private readonly ValueIndex<T, TKey> _index;
        private int _next;

        internal Matches(ValueIndex<T, TKey> index, int head)
        {
            _index = index;
            _next = head;
            Current = default;
        }

        /// <summary>The current matching entity.</summary>
        public Entity Current { get; private set; }

        /// <summary>The enumerator itself (<c>foreach</c> support, no allocation).</summary>
        public readonly Matches GetEnumerator() => this;

        /// <summary>Move to the next entity with the key. O(1) per entity.</summary>
        public bool MoveNext()
        {
            // An entity destroyed during the loop (a cascade) leaves the list; the loop moves on past it.
            while (_next >= 0)
            {
                ref var node = ref _index._nodes[_next];
                int id = _next;
                _next = node.Next;
                if (!node.Linked) continue;
                Current = new Entity(_index._world, id, node.Version);
                return true;
            }

            return false;
        }
    }
}
