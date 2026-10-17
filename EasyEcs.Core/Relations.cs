using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// An element of a <see cref="DynamicBuffer{T}"/> that links its entity to <see cref="Target"/>: an entity holds any
/// number of links of one kind (the members of a party, the items a merchant offers, the units a buff applies to), and
/// <see cref="World.Relations{T}"/> finds the entities linking to a target. Links are written with
/// <see cref="Entity.AddRelation{T}"/> / <see cref="Entity.RemoveRelation{T}"/> (or <c>Set</c> of a whole buffer), so
/// the index follows; read them with <c>Read&lt;DynamicBuffer&lt;T&gt;&gt;()</c> or an <c>in</c> parameter. The
/// analyzer reports writes through a <c>ref</c> (EECS015), as for any indexed component.
/// </summary>
public interface IRelation
{
    /// <summary>The entity the link points at.</summary>
    EntityId Target { get; }
}

/// <summary>
/// The links of relation <typeparamref name="T"/> in one world, by target (<see cref="World.Relations{T}"/>): which
/// entities link to an entity, O(1) to start and O(1) per link. Kept current as links are written and as their entities
/// are destroyed or lose the buffer; the first lookup (and the first after <see cref="World.Restore"/>) indexes every
/// buffer once. Links to a destroyed target stay findable by its id, which no later entity has. Owner thread only.
/// Memory: one 32-byte link per element, recycled.
/// </summary>
public sealed class RelationIndex<T> : World.IValueIndex, World.IIndexOf<DynamicBuffer<T>> where T : struct, IRelation
{
    private readonly World _world;

    // Links live in one array, recycled through a free list. Each is in two lists: its target's (doubly linked, so a
    // link leaves in O(1)) and its source's (singly linked, walked when the source's links are rewritten or dropped).
    private Link[] _links = new Link[16];
    private int _linkCount, _free = -1;

    // First link of each source entity, + 1 (0: none), by entity index.
    private int[] _bySource = [];

    // Target lists by target index; a target whose index holds another incarnation's list, and only it, goes to _older.
    private TargetSlots _byTarget;
    private readonly Dictionary<EntityId, IndexHead> _older = new();
    private bool _built;

    private struct Link
    {
        public EntityId Target;
        public int Source, SourceVersion; // Source < 0: a free link
        public int Next, Prev;            // the target's list (Next stays valid after unlinking: walks step past it)
        public int NextOfSource;          // the source's list, or the free list
    }

    internal RelationIndex(World world) => _world = world;

    /// <summary>The entities linking to <paramref name="target"/>, once per link. Destroying entities during the loop is allowed, changing links is not.</summary>
    public Sources Find(EntityId target)
    {
        if (!_built) Build();
        ref var head = ref HeadOf(target);
        return new Sources(this, Unsafe.IsNullRef(ref head) ? -1 : head.First);
    }

    /// <summary>Number of links to <paramref name="target"/>. O(1).</summary>
    public int Count(EntityId target)
    {
        if (!_built) Build();
        ref var head = ref HeadOf(target);
        return Unsafe.IsNullRef(ref head) ? 0 : head.Count;
    }

    /// <summary>
    /// Decide what destroying an entity does to the links to it: <see cref="TargetPolicy.Remove"/> drops them (a party
    /// forgets a member that left the world), <see cref="TargetPolicy.Destroy"/> destroys the linking entities (the
    /// buffs a caster applied end with the caster), <see cref="TargetPolicy.Keep"/> (the default) leaves them. Applied
    /// inside every destroy (the index is kept built); O(1) per destroyed entity, O(links of each source) per link.
    /// Owner thread only.
    /// </summary>
    public RelationIndex<T> WhenTargetDestroyed(TargetPolicy policy)
    {
        _world.SetRule(this, policy, static index => new World.RelationRule<T>(index));
        return this;
    }

    /// <summary>Index every buffer now if the index is not built (before entering the structural gate).</summary>
    internal void Prepare()
    {
        if (!_built) Build();
    }

    /// <summary>Whether the index is built, so writes must update it (else the first lookup reads every buffer).</summary>
    internal bool Live => _built;

    /// <summary>Entity <paramref name="source"/> gained a link to <paramref name="target"/>.</summary>
    internal void Add(int source, int version, EntityId target)
    {
        if (target.IsNull) return;
        int link = _free >= 0 ? _free : _linkCount++;
        if (link == _free) _free = _links[link].NextOfSource;
        else if (link == _links.Length) Array.Resize(ref _links, _links.Length * 2);
        if (source >= _bySource.Length) Array.Resize(ref _bySource, Math.Max(source + 1, _bySource.Length * 2));

        ref var head = ref AddHead(target);
        ref var node = ref _links[link];
        node.Target = target;
        node.Source = source;
        node.SourceVersion = version;
        node.Prev = -1;
        node.Next = head.Count != 0 ? head.First : -1;
        if (head.Count != 0) _links[head.First].Prev = link;
        head.First = link;
        head.Count++;
        node.NextOfSource = _bySource[source] - 1;
        _bySource[source] = link + 1;
    }

    /// <summary>Entity <paramref name="source"/> dropped its links to <paramref name="target"/> (every one when null).</summary>
    internal void Remove(int source, EntityId target, bool all)
    {
        if (source >= _bySource.Length) return;
        int previous = -1;
        for (int link = _bySource[source] - 1; link >= 0;)
        {
            ref var node = ref _links[link];
            int next = node.NextOfSource;
            if (all || node.Target == target)
            {
                if (previous < 0) _bySource[source] = next + 1;
                else _links[previous].NextOfSource = next;
                Release(link);
            }
            else previous = link;

            link = next;
        }
    }

    private void Release(int link)
    {
        ref var node = ref _links[link];
        // Decided before the count changes: the same storage HeadOf picks.
        bool older = Unsafe.IsNullRef(ref SlotOf(node.Target));
        ref var head = ref HeadOf(node.Target);
        if (--head.Count == 0)
        {
            if (older) _older.Remove(node.Target);
        }
        else if (node.Prev < 0) head.First = node.Next;

        if (node.Prev >= 0) _links[node.Prev].Next = node.Next;
        if (node.Next >= 0) _links[node.Next].Prev = node.Prev;
        node.Source = -1;
        node.NextOfSource = _free;
        _free = link;
    }

    /// <summary>Index the links of <paramref name="buffer"/>, entity <paramref name="source"/>'s, replacing what it had.</summary>
    private void Put(int source, int version, in DynamicBuffer<T> buffer)
    {
        Remove(source, default, all: true);
        foreach (ref readonly var relation in buffer.AsReadOnlySpan()) Add(source, version, relation.Target);
    }

    void World.IIndexOf<DynamicBuffer<T>>.Put(int id, int version, in DynamicBuffer<T> value)
    {
        if (_built) Put(id, version, in value);
    }

    void World.IValueIndex.Added(int id, int version, Archetype archetype, int slot)
    {
        if (_built) Put(id, version, in archetype.Ref<DynamicBuffer<T>>(ComponentType<DynamicBuffer<T>>.Bit, slot));
    }

    void World.IValueIndex.Removed(int id) => Remove(id, default, all: true);

    void World.IValueIndex.Reset(bool rebuild)
    {
        Array.Clear(_links, 0, _linkCount);
        _linkCount = 0;
        _free = -1;
        Array.Clear(_bySource);
        _byTarget.Clear();
        _older.Clear();
        _built = !rebuild;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Build()
    {
        _built = true;
        var job = new BuildJob { Index = this };
        _world.ForEachEntity<DynamicBuffer<T>, BuildJob>(ref job);
    }

    // Reads every buffer without stamping it (Writes = 0): building the index is not a change.
    private struct BuildJob : IForEachEntity<DynamicBuffer<T>>, INoStructuralChanges
    {
        public RelationIndex<T> Index;

        public void Execute(Entity entity, ref DynamicBuffer<T> links)
        {
            foreach (ref readonly var relation in links.AsReadOnlySpan()) Index.Add(entity.Index, entity.Version, relation.Target);
        }

        public static int Writes => 0;
    }

    /// <summary>The list of <paramref name="target"/>, or a null ref when nothing links to it.</summary>
    private ref IndexHead HeadOf(EntityId target)
    {
        ref var head = ref SlotOf(target);
        if (!Unsafe.IsNullRef(ref head) || _older.Count == 0) return ref head;
        return ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_older, target);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref IndexHead SlotOf(EntityId target) => ref _byTarget.Find(target);

    private ref IndexHead AddHead(EntityId target)
    {
        ref var head = ref _byTarget.Find(target);
        if (!Unsafe.IsNullRef(ref head)) return ref head;
        head = ref _byTarget.Claim(target, _older.Count != 0 && _older.ContainsKey(target));
        if (!Unsafe.IsNullRef(ref head)) return ref head;
        return ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_older, target, out _);
    }

    /// <summary>Enumerates the entities linking to one target (<c>foreach</c>), once per link.</summary>
    public ref struct Sources
    {
        private readonly RelationIndex<T> _index;
        private int _next;

        internal Sources(RelationIndex<T> index, int first)
        {
            _index = index;
            _next = first;
            Current = default;
        }

        /// <summary>The current linking entity.</summary>
        public Entity Current { get; private set; }

        /// <summary>The enumerator itself (<c>foreach</c> support, no allocation).</summary>
        public readonly Sources GetEnumerator() => this;

        /// <summary>Move to the next link. O(1) per link.</summary>
        public bool MoveNext()
        {
            while (_next >= 0)
            {
                ref var node = ref _index._links[_next];
                _next = node.Next;
                if (node.Source < 0) continue; // dropped during the loop (its source was destroyed)
                Current = new Entity(_index._world, node.Source, node.SourceVersion);
                return true;
            }

            return false;
        }
    }
}
