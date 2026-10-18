using EasyEcs;

namespace HotReload.Plugin;

// The plugin's own data. Both types leave the world with World.RemoveTypes when the plugin
// unloads. Saving before and loading after a reload carries their values over: a save
// names types by their name, not by the assembly that loaded them.

// Points a unit has scored. Rides on the host's entities.
public record struct Score(int Points) : IComponent, ITrackChanges;

// Points every moving unit scores per update.
public record struct Bonus(int Points) : ISingletonComponent;
