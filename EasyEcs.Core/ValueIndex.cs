using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
/// first re-indexes the rows of <typeparamref name="T"/> written since the previous lookup (found through their change
/// ticks, 64 rows per word), then walks the entities of one key, dropping on the way those that were destroyed or lost
/// the component. Lookups allocate nothing once every key has been seen; the index belongs to the world's owner
/// thread, like component values (systems that look up from <c>OnUpdate</c> or instance methods run alone).
/// </summary>
public sealed class ValueIndex<T, TKey> : World.IResettable
    where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
{
    private static readonly QueryFilter Written = QueryFilter.Create().Changed<T>();

    private readonly World _world;

    // Key -> first entity id of its list. Lists are doubly linked through _nodes (indexed by entity id), so moving an
    // entity between keys and dropping it are O(1) without allocation.
    private readonly Dictionary<TKey, int> _heads = new();
    private Node[] _nodes = [];
    private ulong _synced; // rows written at or after this tick are not indexed yet

    private struct Node
    {
        public TKey Key;
        public int Next, Prev;
        public int Version; // the entity incarnation the node belongs to
        public bool Linked;
    }

    internal ValueIndex(World world) => _world = world;

    /// <summary>
    /// The entities whose <typeparamref name="T"/> has key <paramref name="key"/>. O(rows written since the previous
    /// lookup / 64 + their count) to catch up, then O(entities of the key). Not while a query or structural change runs.
    /// </summary>
    public Matches Find(TKey key)
    {
        Sync();
        return new Matches(this, _heads.TryGetValue(key, out int head) ? head : -1);
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

    /// <summary>Number of entities with key <paramref name="key"/>.</summary>
    public int Count(TKey key)
    {
        int count = 0;
        foreach (var _ in Find(key)) count++;
        return count;
    }

    /// <summary>Forget everything: the next lookup re-indexes every row (after <see cref="World.Restore"/>).</summary>
    void World.IResettable.Reset()
    {
        _heads.Clear();
        Array.Clear(_nodes);
        _synced = 0;
    }

    /// <summary>Re-index the rows of <typeparamref name="T"/> written since the previous sync.</summary>
    private void Sync()
    {
        ulong since = _synced;
        // Advance the world's tick: rows written from now on are newer than every row this sync indexes, so the next
        // sync visits only them (without it, writes between updates would share one tick and be re-indexed every time).
        _synced = _world.AdvanceChangeTick();
        var job = new SyncJob { Index = this };
        _world.ForEach<T, SyncJob>(ref job, Written.ChangedSince(since));
    }

    private struct SyncJob : IForEach<T>, INoStructuralChanges
    {
        public ValueIndex<T, TKey> Index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(int entityId, ref T component) => Index.Put(entityId, component.Key);

        static int IForEach<T>.Writes => 0; // reads only: syncing is not a change
    }

    /// <summary>Index entity <paramref name="id"/> (its current incarnation) under <paramref name="key"/>.</summary>
    private void Put(int id, TKey key)
    {
        if (id >= _nodes.Length) Array.Resize(ref _nodes, Math.Max(id + 1, _nodes.Length * 2));
        int version = _world.MetaRef(id).Version;
        ref var node = ref _nodes[id];
        if (node.Linked)
        {
            if (node.Version == version && node.Key.Equals(key)) return;
            Unlink(id);
        }

        node.Key = key;
        node.Version = version;
        node.Linked = true;
        node.Prev = -1;
        if (_heads.TryGetValue(key, out int head))
        {
            node.Next = head;
            _nodes[head].Prev = id;
        }
        else node.Next = -1;

        _heads[key] = id;
    }

    private void Unlink(int id)
    {
        ref var node = ref _nodes[id];
        if (node.Prev >= 0) _nodes[node.Prev].Next = node.Next;
        else if (node.Next >= 0) _heads[node.Key] = node.Next;
        else _heads.Remove(node.Key);
        if (node.Next >= 0) _nodes[node.Next].Prev = node.Prev;
        node.Linked = false;
        node.Key = default!; // let the GC collect a managed key
    }

    /// <summary>
    /// The entity of node <paramref name="id"/> if it is still indexed correctly: the same incarnation, alive and
    /// having <typeparamref name="T"/> (a destroyed entity or a removed component leaves no change tick to sync).
    /// </summary>
    private bool Valid(int id, out Entity entity)
    {
        entity = new Entity(_world, id, _nodes[id].Version);
        ref var meta = ref _world.MetaRef(id);
        return meta.Version == _nodes[id].Version && meta.Archetype != null && entity.Has<T>();
    }

    /// <summary>Enumerates the entities of one key (<c>foreach</c>); drops stale entries as it passes them.</summary>
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

        public Entity Current { get; private set; }

        public readonly Matches GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_next >= 0)
            {
                int id = _next;
                _next = _index._nodes[id].Next;
                if (_index.Valid(id, out var entity))
                {
                    Current = entity;
                    return true;
                }

                _index.Unlink(id);
            }

            return false;
        }
    }
}
