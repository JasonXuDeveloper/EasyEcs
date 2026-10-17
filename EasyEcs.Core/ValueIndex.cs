using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace EasyEcs;

/// <summary>
/// A component whose <see cref="Key"/> the world indexes: <c>world.Index&lt;T, TKey&gt;().Find(key)</c> enumerates the
/// entities whose <typeparamref name="TKey"/> equals a value (an item's owner, a player's name, a cell of a grid). An
/// <see cref="EntityId"/> key makes the component a relation: <c>ChildOf(EntityId Parent)</c> indexed by its parent
/// finds the children of an entity. The index follows the values written with <c>Set</c> and <c>CreateEntity</c> as they are
/// written, so an indexed component is never written through a <c>ref</c> (<c>Get</c>, <c>ref</c> parameters, query
/// items): the analyzer reports such writes (EECS015).
/// </summary>
public interface IIndexed<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>The value the entity is found by.</summary>
    TKey Key { get; }
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
    private readonly Dictionary<TKey, Head> _heads = new();

    // Relations (EntityId keys): the lists by the target's index, no hashing. A target whose index is still held by
    // links to an older incarnation, and EntityId.Null, use _heads.
    private Head[] _byTarget = [];
    private Node[] _nodes = [];
    private bool _built; // every row is indexed (false until the first lookup, and again after a restore)

    private struct Head
    {
        public int First, Count;
        public int Version; // _byTarget: the target incarnation the list belongs to
    }

    private struct Node
    {
        public TKey Key;
        public int Next, Prev;
        public int Version; // the entity incarnation the node belongs to
        public bool Linked;
    }

    internal ValueIndex(World world) => _world = world;

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
        Array.Clear(_byTarget);
        Array.Clear(_nodes);
        _built = !rebuild;
    }

    /// <summary>Entity <paramref name="id"/> was given <paramref name="value"/> (Set, CreateEntity). Inside the structural gate.</summary>
    void World.IIndexOf<T>.Put(int id, int version, in T value)
    {
        if (_built) Put(id, version, value.Key); // else the first lookup indexes the row
    }

    /// <summary>Entity <paramref name="id"/> was created with row <paramref name="slot"/> of <paramref name="archetype"/>, which has <typeparamref name="T"/> (Instantiate).</summary>
    void World.IValueIndex.Added(int id, int version, Archetype archetype, int slot)
    {
        if (_built) Put(id, version, archetype.Ref<T>(ComponentType<T>.Bit, slot).Key);
    }

    /// <summary>Entity <paramref name="id"/> is destroyed or loses <typeparamref name="T"/>. Inside the structural gate.</summary>
    void World.IValueIndex.Removed(int id)
    {
        if (id < _nodes.Length && _nodes[id].Linked) Unlink(id);
    }

    /// <summary>The list of <paramref name="key"/>, or a null ref when no entity has it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Head HeadOf(TKey key)
    {
        if (typeof(TKey) == typeof(EntityId)) // a JIT-time constant
        {
            ref var head = ref SlotOf(key);
            if (!Unsafe.IsNullRef(ref head)) return ref head;
            if (_heads.Count == 0) return ref Unsafe.NullRef<Head>(); // no relation outside _byTarget
        }

        if (IsNull(key)) return ref Unsafe.NullRef<Head>();
        return ref CollectionsMarshal.GetValueRefOrNullRef(_heads, key);
    }

    /// <summary>The <see cref="_byTarget"/> list of relation key <paramref name="key"/>, or a null ref when it has none there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Head SlotOf(TKey key)
    {
        var target = Unsafe.As<TKey, EntityId>(ref key);
        if ((uint)target.Index < (uint)_byTarget.Length)
        {
            ref var head = ref _byTarget[target.Index];
            if (head.Count != 0 && head.Version == target.Version) return ref head;
        }

        return ref Unsafe.NullRef<Head>();
    }

    // A value-type key is never null; testing it would box it in unoptimized (tier-0) code.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNull(TKey key) => !typeof(TKey).IsValueType && key is null;

    /// <summary>The list of <paramref name="key"/> (not null), created empty when no entity has it.</summary>
    private ref Head AddHead(TKey key)
    {
        if (typeof(TKey) == typeof(EntityId))
        {
            var target = Unsafe.As<TKey, EntityId>(ref key);
            if (!target.IsNull)
            {
                if (target.Index >= _byTarget.Length) Array.Resize(ref _byTarget, Math.Max(target.Index + 1, _byTarget.Length * 2));
                ref var head = ref _byTarget[target.Index];
                if (head.Count != 0 && head.Version == target.Version) return ref head;
                if (head.Count == 0 && (_heads.Count == 0 || !_heads.ContainsKey(key)))
                {
                    head.Version = target.Version;
                    return ref head;
                }
                // The id's slot holds another incarnation's list (or this key already lives in _heads).
            }
        }

        return ref CollectionsMarshal.GetValueRefOrAddDefault(_heads, key, out _);
    }

    /// <summary>Index every row of <typeparamref name="T"/>: the first lookup, and the first after a restore.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Build()
    {
        _built = true;
        foreach (var item in _world.Query<T>()) Put(item.Entity.Index, item.Entity.Version, item.Component1.Key);
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
        ref var head = ref AddHead(key);
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
            if (!inSlot) _heads.Remove(node.Key);
        }
        else if (node.Prev < 0) head.First = node.Next;
        if (node.Prev >= 0) _nodes[node.Prev].Next = node.Next;
        if (node.Next >= 0) _nodes[node.Next].Prev = node.Prev;
        node.Linked = false;
        node.Key = default!; // let the GC collect a managed key
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
