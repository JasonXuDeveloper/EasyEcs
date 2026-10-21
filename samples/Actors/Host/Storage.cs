using System.Buffers;
using EasyEcs;

namespace Actors;

// Plumbing, not ECS: a database stand-in that keeps one record per actor key, and the host's "save what changed".
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
    /// Save the actors whose persistent state was written since <paramref name="since"/>, then move the mark: the
    /// tracked components tell which, so an actor nobody wrote costs nothing.
    /// </summary>
    public int SaveChanged(World world, ref ulong since)
    {
        int saved = SaveChanged<Shop, Stock>(world, since) + SaveChanged<Bank, Balance>(world, since);
        since = world.ChangeTick;
        return saved;
    }

    private int SaveChanged<TActor, TState>(World world, ulong since)
        where TActor : struct, IActor<TActor>, IEquatable<TActor>
        where TState : struct, IComponent, ITrackChanges
    {
        var changed = QueryFilter.Create().With<In<TActor>>().Changed<TState>(since);
        var found = new Collect<TState> { Actors = new List<Entity>() };
        world.ForEachEntity<TState, Collect<TState>>(ref found, changed);
        foreach (var actor in found.Actors)
        {
            Save(actor, actor.Read<TActor>());
        }

        return found.Actors.Count;
    }

    private void Save(Entity actor, object key)
    {
        var output = new ArrayBufferWriter<byte>();
        actor.World.SaveEntitiesWith(output, [actor.Id], Activation.Persisted);
        _records[key] = output.WrittenSpan.ToArray();
    }

    // Lists the actors a filter selects, reading nothing (Writes => 0: listing them is not a change).
    private struct Collect<TState> : IForEachEntity<TState>
        where TState : struct, IComponent
    {
        public List<Entity> Actors;

        public static int Writes => 0;

        public void Execute(Entity actor, ref TState state) => Actors.Add(actor);
    }
}
