using System.Buffers;
using EasyEcs;

namespace Actors;

// Plumbing, not ECS: a database stand-in that keeps one record per actor key, and the host's side of saving actors.
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
        storage.Save(earlier.CreateEntity(new Shop(1), new Stock(2)));
        storage.Save(earlier.CreateEntity(new Bank(7), new Balance(1_000)));
        return storage;
    }

    /// <summary>How many records this storage wrote, the earlier run's included.</summary>
    public int Writes { get; private set; }

    public void Save(Entity actor)
    {
        object key = actor.HasShop ? actor.Shop : actor.Bank;
        var output = new ArrayBufferWriter<byte>();
        actor.World.SaveEntitiesWith(output, [actor.Id], Activation.Persisted);
        _records[key] = output.WrittenSpan.ToArray();
        Writes++;
    }
}
