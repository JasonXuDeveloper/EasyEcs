using EasyEcs;

namespace HotReload;

// The host's components. They belong to the host, so they stay when a plugin unloads.
public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;
