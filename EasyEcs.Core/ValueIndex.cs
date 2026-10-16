using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
namespace EasyEcs;

/// <summary>
/// A component whose <see cref="Key"/> the world indexes: <c>world.Index&lt;T, TKey&gt;().Find(key)</c> enumerates the
/// entities whose <typeparamref name="TKey"/> equals a value (an item's owner, a player's name, a cell of a grid). An
/// <see cref="Entity"/> key makes the component a relation: <c>ChildOf(Entity Parent)</c> indexed by its parent finds
/// the children of an entity. Indexed components track changes (<see cref="ITrackChanges"/>): the index catches up
/// with the rows written since its previous lookup.
/// </summary>
public interface IIndexed<TKey> : ITrackChanges where TKey : IEquatable<TKey>
{
    /// <summary>The value the entity is found by.</summary>
    TKey Key { get; }
}

/// <summary>
/// The value index of component <typeparamref name="T"/> in one world (<see cref="World.Index{T,TKey}"/>). A lookup
/// first re-indexes the rows of <typeparamref name="T"/> written since the previous lookup (found through the change-tick
/// summaries per table, per 4,096-row block and per 64-row word); entities leave the index as soon as they are destroyed or lose the component. Lookups
/// allocate nothing once every key has been seen; the index belongs to the world's owner thread, like component values
/// (systems that look up from <c>OnUpdate</c> or instance methods run alone).
/// </summary>
public sealed class ValueIndex<T, TKey> : World.IValueIndex
    where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
{
    private static readonly QueryFilter Written = QueryFilter.Create().Changed<T>();

    private readonly World _world;

    // Key -> its list (first entity id and length). Lists are doubly linked through _nodes (indexed by entity id), so
    // moving an entity between keys and dropping it are O(1) without allocation.
    private readonly Dictionary<TKey, Head> _heads = new();

    // Relations (Entity keys of this world): the lists by the target's id, no hashing. A target whose id is still held
    // by links to an older incarnation, and other keys (default, another world's entities), use _heads.
    private Head[] _byTarget = [];
    private Node[] _nodes = [];
    private ulong _synced; // rows written at or after this tick are not indexed yet
    private World.QueryCacheEntry _tables; // the tables having T (kept current by the world's query cache)

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
    /// The entities whose <typeparamref name="T"/> has key <paramref name="key"/> (none for a null key). Catching up with
    /// the w rows written since the previous lookup costs O(tables) when w = 0, else O(rows of <typeparamref name="T"/> /
    /// 4,096 + written 64-row words + w); then O(1) per entity. Owner thread only; inside a pass a lookup sees that
    /// pass's writes so far. Destroying entities during the loop is allowed, changing <typeparamref name="T"/> is not.
    /// </summary>
    public Matches Find(TKey key)
    {
        Sync();
        ref var head = ref HeadOf(key);
        return new Matches(this, Unsafe.IsNullRef(ref head) ? -1 : head.First);
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
    /// Number of entities with key <paramref name="key"/>: O(1) once caught up (a hash probe, or an array access for a
    /// relation to an entity of this world; see <see cref="Find"/>).
    /// </summary>
    public int Count(TKey key)
    {
        Sync();
        ref var head = ref HeadOf(key);
        return Unsafe.IsNullRef(ref head) ? 0 : head.Count;
    }

    /// <summary>Forget everything: the next lookup re-indexes every row (after <see cref="World.Restore"/> or <see cref="World.Dispose"/>).</summary>
    void World.IValueIndex.Reset()
    {
        _heads.Clear();
        Array.Clear(_byTarget);
        Array.Clear(_nodes);
        _synced = 0;
        _tables = null; // restored tables may differ
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
        if (typeof(TKey) == typeof(Entity)) // a JIT-time constant
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
        var target = Unsafe.As<TKey, Entity>(ref key);
        if (ReferenceEquals(target.World, _world) && (uint)target.Id < (uint)_byTarget.Length)
        {
            ref var head = ref _byTarget[target.Id];
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
        if (typeof(TKey) == typeof(Entity))
        {
            var target = Unsafe.As<TKey, Entity>(ref key);
            if (ReferenceEquals(target.World, _world))
            {
                if (target.Id >= _byTarget.Length) Array.Resize(ref _byTarget, Math.Max(target.Id + 1, _byTarget.Length * 2));
                ref var head = ref _byTarget[target.Id];
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

    /// <summary>Catch up with the rows of <typeparamref name="T"/> written since the previous sync.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Sync()
    {
        // Lookups in a row find nothing written: one change-tick summary per table, inlined into the lookup.
        if (_tables is { } entry)
        {
            var tables = Volatile.Read(ref entry.Match);
            var archetypes = tables.Archetypes;
            var plans = tables.Filters;
            int i = 0;
            while (i < archetypes.Length && archetypes[i].LastWrite[plans[i].Changed[0]] < _synced) i++;
            if (i == archetypes.Length) return;
        }

        CatchUp();
    }

    /// <summary>Re-index the rows of <typeparamref name="T"/> written since the previous sync.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CatchUp()
    {
        _tables ??= _world.GetQueryEntry([ComponentType<T>.Bit], in Written);
        var tables = Volatile.Read(ref _tables.Match);
        ulong since = _synced;
        for (int i = 0; i < tables.Archetypes.Length; i++)
        {
            var archetype = tables.Archetypes[i];
            var rows = new RowFilter(tables.Filters[i], since);
            // A table not written since the previous sync costs one summary check (lookups in a row stop here).
            if (rows.Mode(archetype) == RowFilter.None) continue;
            // Advance the world's tick: rows written from now on are newer than every row this sync indexes, so the
            // next sync visits only them (else writes between updates would share one tick and be re-indexed each time).
            // Not inside a pass: it stamps its rows before running them, so a row stamped before this lookup may be
            // written after it; the next sync then re-reads the rows stamped by that pass (only those).
            if (_synced == since)
                _synced = Volatile.Read(ref _world.Passes) == 0 ? _world.AdvanceChangeTick() : _world.ChangeTick;
            // CatchUp opens no pass of its own: it only reads keys, so no row moves while the written rows are walked
            // (inside a caller's pass, rows that lost their entity are tombstones, skipped below).
            int count = archetype.Count, words = (count + 63) >> 6;
            var slots = archetype.Slots;
            ref T first = ref archetype.Base<T>(ComponentType<T>.Bit);
            for (int w = rows.NextWord(archetype, 0, words); w < words; w = rows.NextWord(archetype, w + 1, words))
                for (ulong bits = rows.Word(archetype, w) & RowFilter.RowMask(w, count); bits != 0; bits &= bits - 1)
                {
                    int slot = (w << 6) + BitOperations.TrailingZeroCount(bits);
                    var entity = slots[slot];
                    if (entity.Id >= 0) Put(entity.Id, entity.Version, Unsafe.Add(ref first, slot).Key); // tombstones are skipped
                }
        }
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
        bool inSlot = typeof(TKey) == typeof(Entity) && !Unsafe.IsNullRef(ref SlotOf(node.Key));
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
