using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace EasyEcs;

// Types of an assembly leaving the world: game logic loaded into a collectible AssemblyLoadContext and reloaded.
public sealed partial class World
{
    // Counts RemoveTypes calls: a snapshot taken before one names tables that no longer exist.
    internal int TypesRemoved;

    /// <summary>
    /// Remove from this world everything of the types of <paramref name="assembly"/>: its systems (OnDestroy runs),
    /// singletons, indexes and groupings, and its components from every entity (the entities stay, without them).
    /// Tables that had such a column are dropped, with every cache that names them, so the world holds no reference
    /// to the assembly any more: once every world removed it, its collectible context can unload, and when it does,
    /// EasyEcs reuses its component bits.
    /// <br/>
    /// To reload game logic and keep its data: <see cref="Save(System.Buffers.IBufferWriter{byte})"/>, remove the types, unload the old context, load the
    /// new version, then <see cref="Load(System.ReadOnlySpan{byte}, bool)"/> (types are saved by type id, so the new version's components take the
    /// values). Owner thread only, not during a pass or an update. O(entities with such components + tables + cached
    /// queries); allocates (cold).
    /// </summary>
    public void RemoveTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        RemoveTypes(type => Collectible.Uses(type, a => a == assembly));
    }

    /// <summary>
    /// <see cref="RemoveTypes(Assembly)"/> for every assembly loaded into <paramref name="context"/> (a plugin and the
    /// libraries it brought).
    /// </summary>
    public void RemoveTypes(AssemblyLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RemoveTypes(type => Collectible.Uses(type, a => AssemblyLoadContext.GetLoadContext(a) == context));
    }

    private void RemoveTypes(Func<Type, bool> doomed)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        if (Volatile.Read(ref Passes) != 0) throw new InvalidOperationException("Types cannot be removed during a pass");

        // Systems first: their OnDestroy may still use the types (remove a singleton, destroy entities).
        foreach (var group in _groups)
        {
            if (group == null) continue;
            foreach (var system in (EcsSystem[])group.Systems.Clone())
                if (doomed(system.GetType()) || BaseTypes(system.GetType(), doomed))
                    RemoveSystem(system);
        }

        var bits = new List<ushort>();
        var mask = default(BitMask);
        for (int bit = 0; bit < ComponentInfo.Count; bit++)
            if (ComponentInfo.TryOf(bit) is { } info && doomed(info.Type))
            {
                bits.Add((ushort)bit);
                mask.SetBit(bit);
            }

        using (Structural())
        {
            var dropped = new HashSet<Archetype>();
            foreach (var table in _allArchetypes)
                if (table.ComponentMask.Intersects(in mask))
                    dropped.Add(table);
            foreach (var table in dropped) Strip(table, bits);

            Drop(dropped, bits, in mask);
            Forget(doomed, bits);
            TypesRemoved++;
            StorageVersion++;
        }

        ComponentInfo.Reclaim(); // the bits of a context that unloaded already
    }

    private static bool BaseTypes(Type type, Func<Type, bool> doomed)
    {
        for (var t = type.BaseType; t != null && t != typeof(EcsSystem); t = t.BaseType)
            if (doomed(t))
                return true;
        return false;
    }

    /// <summary>Move every row of <paramref name="table"/> to the table without the columns of <paramref name="bits"/> (its groups kept). Inside the gate.</summary>
    private void Strip(Archetype table, List<ushort> bits)
    {
        if (table.Count == 0) return;
        var kept = table.ComponentMask.Clone();
        foreach (var bit in bits) kept.ClearBit(bit);
        var transition = new Transition(table, GetOrCreateArchetype(in kept, table));
        for (int slot = table.Count - 1; slot >= 0; slot--) // from the last row: a moved row is filled by none
        {
            int id = table.Slots[slot].Index;
            if (id >= 0) MoveTo(ref MetaRef(id), id, transition);
        }
    }

    /// <summary>
    /// Delete <paramref name="dropped"/> (emptied tables) and every cache that names them or <paramref name="bits"/>:
    /// the registry, transition edges, cached queries, the prefab tables. Inside the gate.
    /// </summary>
    private void Drop(HashSet<Archetype> dropped, List<ushort> bits, in BitMask mask)
    {
        Unregister(dropped);
        foreach (var table in _allArchetypes)
            table.ClearEdges(); // caches: rebuilt by the next change, never towards a dropped table

        foreach (var pair in _queryCache)
        {
            bool mentions = false;
            foreach (var bit in bits) mentions |= pair.Key.Mentions(bit);
            if (mentions) _queryCache.TryRemove(pair.Key, out _);
            else Volatile.Write(ref pair.Value.Match, pair.Value.Match.Without(dropped));
        }

        lock (_memoLock) _queryMemo = []; // refilled by the next queries

        if (_prefabTables != null)
            foreach (var pair in new List<KeyValuePair<PrefabKey, Archetype>>(_prefabTables))
                if (pair.Key.Source.ComponentMask.Intersects(in mask) || dropped.Contains(pair.Value))
                    _prefabTables.Remove(pair.Key);
    }

    /// <summary>The singletons, indexes, groupings, target rules and parallel work of the types. Inside the gate.</summary>
    private void Forget(Func<Type, bool> doomed, List<ushort> bits)
    {
        for (int i = 0; i < _singletons.Length; i++)
            if (_singletons[i] != null && doomed(_singletons[i].GetType()))
                Volatile.Write(ref _singletons[i], null);

        for (int i = 0; i < _indexes.Length; i++)
            if (_indexes[i] != null && doomed(_indexes[i].GetType()))
                _indexes[i] = null;

        foreach (var bit in bits)
        {
            if (_indexByBit != null && bit < _indexByBit.Length) _indexByBit[bit] = null;
            if (_groupByBit != null && bit < _groupByBit.Length) _groupByBit[bit] = null;
        }

        for (int i = _ruleCount - 1; i >= 0; i--)
            if (doomed(_rules[i].GetType()))
            {
                _rules.AsSpan(i + 1, _ruleCount - i - 1).CopyTo(_rules.AsSpan(i));
                _rules[--_ruleCount] = null;
            }

        lock (_parallelWorkLock)
        {
            var work = (ParallelWork[])_parallelWork.Clone();
            for (int i = 0; i < work.Length; i++)
                if (work[i] != null && doomed(work[i].GetType()))
                    work[i] = null;
            Volatile.Write(ref _parallelWork, work);
        }

        // Copies of rows a later Load or MoveEntities would reuse: rebuilt on demand.
        _loadSnapshot = null;
        _arrivals?.Dispose();
        _arrivals = null;
    }

    /// <summary>
    /// Whether this world mentions component <paramref name="bit"/>: a table, a cached query, a grouping or an index.
    /// Read from the thread of an unloading context; every structure read is a snapshot or a concurrent dictionary.
    /// </summary>
    internal bool Mentions(int bit)
    {
        foreach (var table in Volatile.Read(ref _allArchetypes))
            if (table.ComponentMask.HasBit(bit))
                return true;
        foreach (var key in _queryCache.Keys)
            if (key.Mentions(bit))
                return true;
        var groups = _groupByBit;
        var indexes = _indexByBit;
        return groups != null && bit < groups.Length && groups[bit] != null
               || indexes != null && bit < indexes.Length && indexes[bit] != null;
    }
}
