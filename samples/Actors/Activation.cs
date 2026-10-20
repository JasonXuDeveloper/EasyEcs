using EasyEcs;

namespace Actors;

// An actor exists in the world only while it is active: its entity is made when it is first needed, and its record
// (what an earlier activation saved) is loaded onto that entity.
public static class Activation
{
    /// <summary>The components an actor's record holds: its persistent state, nothing that lives only in memory.</summary>
    public static readonly ComponentSet Persisted = ComponentSet.Create().With<Stock>().With<Balance>();

    /// <summary>
    /// The entity of <paramref name="key"/>. When it is not active: made from <paramref name="kind"/> (the state that
    /// lives in memory) plus its record, or from <paramref name="fresh"/> (the same, plus the persistent state of an
    /// actor that has never been saved).
    /// </summary>
    public static Entity Activate<TActor>(World world, TActor key, Prefab kind, Prefab fresh, Storage storage)
        where TActor : struct, IActor<TActor>, IEquatable<TActor>
    {
        if (world.Index<TActor, TActor>().TryFind(key, out var active))
        {
            return active;
        }

        if (!storage.TryGet(key, out var record))
        {
            // A new actor: its first state is new data, so the next save writes it.
            var created = world.Instantiate(fresh);
            created.Set(key, new In<TActor>());
            return created;
        }

        // The persistent components come only from the record, with markWritten: false. What came from storage is
        // not a change: the next save skips this actor until a message writes it. (A value made here first and then
        // replaced would keep its own change tick, and count as written.)
        var actor = world.Instantiate(kind);
        actor.Set(key, new In<TActor>());
        world.LoadEntitiesInto(record, [actor], markWritten: false);
        return actor;
    }
}
