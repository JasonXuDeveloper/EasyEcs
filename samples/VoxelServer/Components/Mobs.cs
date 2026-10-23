using EasyEcs;

namespace VoxelServer;

// Zombies and dropped items. Times are ticks, not counts: a value that would count down every tick for every entity
// is written once, as the tick it ends, and compared when it matters.

public struct Zombie : IComponent;

// How far a zombie sees a player from, in blocks: it hunts the players within it. Each prefab sets its own, so a boss
// with a longer view is one more prefab. (A player's view is not a component: every player sees Rules.PlayerVision.)
public record struct Vision(float Range) : IComponent;

// The player a zombie chases, the nearest it saw (none: default). An EntityId, so a player who left is noticed
// (TryGetEntity fails).
public record struct Target(EntityId Player) : IComponent;

// The tick from which a zombie may hit again.
public record struct Cooldown(long Until) : IComponent;

// A zombie in daylight is on fire until this tick, as in Minecraft: the fire hurts every Rules.BurnEvery ticks and
// goes on burning for a while after the zombie found shade. In the zombie prefab, absent: catching fire and putting it
// out flip a presence bit, and the zombie stays in its table.
public record struct Burning(long Until) : IComponent;

// A stack of dug blocks lying on the ground, for any player who walks over it.
public record struct Item(Block Kind, int Count) : IComponent;

// The tick an item was dropped: it can be picked up Rules.PickupDelay ticks later and is gone Rules.ItemLifetime ticks
// later. An ordered index of the items by it (world.DroppedIndex.Range) finds the items that expire without visiting
// the others: the oldest come first.
public partial record struct Dropped([IndexKey] long Tick) : IComponent;
