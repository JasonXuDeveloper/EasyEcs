using EasyEcs;

namespace Actors;

// An actor exists in the world only while it is active: its entity is made when it is first needed, and its record
// (what an earlier activation saved) is loaded onto that entity.
public static class Activation
{
    /// <summary>The components an actor's record holds: its persistent state, nothing that lives only in memory.</summary>
    public static readonly ComponentSet Persisted = ComponentSet.Create().With<Stock>().With<Balance>();

    /// <summary>
    /// The entity of <paramref name="key"/>. When it is not active: made from <paramref name="kind"/> (what an actor of
    /// that kind starts with), then given its record when storage has one.
    /// </summary>
    public static Entity Activate<TActor>(World world, TActor key, Prefab kind, Storage storage)
        where TActor : struct, IActor<TActor>, IEquatable<TActor>
    {
        if (world.Index<TActor, TActor>().TryFind(key, out var active))
        {
            return world.GetEntity(active);
        }

        // The key and the inbox tag land with the prefab's components in one row copy.
        var actor = world.Instantiate(kind, key, new In<TActor>());
        if (storage.TryGet(key, out var record))
        {
            // The record replaces the prefab's defaults, with markWritten: false: what came from storage is not a
            // change, so the next save skips this actor until a message writes it. A new actor has no record: its
            // defaults are new data, and the next save writes them.
            world.LoadEntitiesInto(record, [actor], markWritten: false);
        }

        return actor;
    }
}
