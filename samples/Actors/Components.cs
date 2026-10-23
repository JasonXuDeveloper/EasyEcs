using EasyEcs;

namespace Actors;

// An actor is an entity that answers messages addressed to its key. Each kind of actor (a shop, a bank) is one type
// that does three jobs at once, through one interface:
//   - its key component: the entity's address, indexed, so a message finds its actor in O(1);
//   - the system group of its kind: world.Update<Shop>() runs the shop systems;
//   - the filter of that group: those systems see only the entities tagged In<Shop>.
// Nothing here is an EasyEcs feature of its own: an index, a group and a tag, declared once for every kind.

/// <summary>The tag of a kind of actor: In&lt;Shop&gt; marks the shop actors.</summary>
public struct In<TActor> : IComponent;

/// <summary>What every kind of actor is: key component, index key, system group, and the group's filter.</summary>
public interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, ISystemGroupWith<In<TSelf>>
    where TSelf : struct, IActor<TSelf>, IEquatable<TSelf>;

// The key is the actor itself: [IndexKey] on the type, and the generator writes the explicit Key.
[IndexKey]
public readonly partial record struct Shop(long Id) : IActor<Shop>;

[IndexKey]
public readonly partial record struct Bank(long Id) : IActor<Bank>;

// State that persists: tracked, so the host saves only the actors whose state changed.
public record struct Stock(int Items) : IComponent, ITracked;

public record struct Balance(long Coins) : IComponent, ITracked;

// State that lives only while the actor is active: not in the saved record.
public record struct Sales(int Count) : IComponent;

/// <summary>A shop that sold out takes no orders until it restocks.</summary>
public struct Closed : IComponent;

/// <summary>The save group: world.Update&lt;Saving&gt;() lists the actors written since it last ran.</summary>
public struct Saving : ISystemGroup;

/// <summary>
/// An actor the save group found written, for the host to save after the update: a pass refuses saves (EECS041), so
/// the system emits one record per actor, which the host finds in singleton Emitted&lt;Unsaved&gt;.
/// </summary>
public record struct Unsaved(EntityId Actor);
