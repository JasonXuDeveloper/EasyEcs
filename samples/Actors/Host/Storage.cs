using System.Buffers;
using EasyEcs;

namespace Actors;

// Plumbing, not ECS: a database stand-in that keeps one record per actor key, and the host's save of changed actors.
public sealed class Storage
{
    // Keyed by the actor's key (Shop(1), Bank(7)): records compare by value.
    private readonly Dictionary<object, byte[]> _records = new();

    public bool TryGet(object key, out byte[] record) => _records.TryGetValue(key, out record!);

    /// <summary>Records written by an earlier run, made the way a run saves them.</summary>
    public static Storage FromEarlierRun()
    {
        var storage = new Storage();
        using var earlier = new World();
        storage.Save(earlier.CreateEntity(new Shop(1), new Stock(2)), new Shop(1));
        storage.Save(earlier.CreateEntity(new Bank(7), new Balance(1_000)), new Bank(7));
        return storage;
    }

    /// <summary>
    /// Save the actors the save group listed (world.Update&lt;Saving&gt;()), then empty the list: the tracked components
    /// told which actors changed, so an actor nobody wrote costs nothing.
    /// </summary>
    public int Save(World world, List<EntityId> actors)
    {
        foreach (var id in actors)
        {
            var actor = world.GetEntity(id);
            Save(actor, actor.HasShop ? actor.Shop : actor.Bank);
        }

        int saved = actors.Count;
        actors.Clear();
        return saved;
    }

    private void Save(Entity actor, object key)
    {
        var output = new ArrayBufferWriter<byte>();
        actor.World.SaveEntitiesWith(output, [actor.Id], Activation.Persisted);
        _records[key] = output.WrittenSpan.ToArray();
    }
}
