namespace EasyEcs;

/// <summary>
/// Marks a struct as component data. Components are plain data stored in one dense column per type and indexed by
/// entity id; keep them small (ideally a few fields, well under a 64-byte cache line) and split rarely-used data
/// into separate components so iteration only loads what it touches.
/// </summary>
public interface IComponent;

/// <summary>Marks a struct as a world singleton: exactly one value per world (clock, configuration, shared state).</summary>
public interface ISingletonComponent;

/// <summary>Marks a struct as an event published with <see cref="World.Publish{T}"/>.</summary>
public interface IEvent;

/// <summary>
/// Receives events of type <typeparamref name="T"/> published in a world. A <c>partial</c> <see cref="EcsSystem"/>
/// implementing this interface is subscribed automatically when added to a world and unsubscribed when removed;
/// other objects call <see cref="World.Subscribe{T}"/> themselves.
/// </summary>
public interface IEventHandler<T> where T : struct, IEvent
{
    void Handle(in T e);
}
