using System;

namespace EasyEcs;

public readonly partial struct Entity
{
    /// <summary>
    /// Link the entity to <paramref name="relation"/>'s target: append it to the entity's
    /// <see cref="DynamicBuffer{T}"/> (added first if missing) and to the relation index. Returns the entity. O(1)
    /// amortized; structural only when the buffer is added. Throws if the entity is not alive.
    /// </summary>
    public Entity AddRelation<T>(in T relation) where T : struct, IRelation
    {
        if (_world == null) ThrowStale();
        GetOrAdd<DynamicBuffer<T>>().Add(relation);
        _world!.LiveRelations<T>()?.Add(Index, Version, relation.Target);
        return this;
    }

    /// <summary>
    /// Drop the entity's links of kind <typeparamref name="T"/> to <paramref name="target"/> (every element with that
    /// target). Returns how many went. O(links of the entity). Throws if the entity is not alive.
    /// </summary>
    public int RemoveRelation<T>(EntityId target) where T : struct, IRelation
    {
        if (_world == null) ThrowStale();
        if (!Has<DynamicBuffer<T>>()) return 0;
        ref var links = ref Get<DynamicBuffer<T>>();
        int removed = 0;
        for (int i = links.Count - 1; i >= 0; i--)
        {
            if (links[i].Target != target) continue;
            links.RemoveAtSwapBack(i);
            removed++;
        }

        if (removed != 0) _world!.LiveRelations<T>()?.Remove(Index, target, all: false);
        return removed;
    }

    /// <summary>Whether the entity links to <paramref name="target"/> with a <typeparamref name="T"/>. O(links of the entity).</summary>
    public bool HasRelation<T>(EntityId target) where T : struct, IRelation
    {
        if (!Has<DynamicBuffer<T>>()) return false;
        foreach (ref readonly var relation in Read<DynamicBuffer<T>>())
            if (relation.Target == target) return true;
        return false;
    }
}
