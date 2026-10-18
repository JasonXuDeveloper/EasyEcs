using EasyEcs;

namespace EasyEcsApp;

// Components are plain data on entities: fields, no logic. Keep them small. A field that
// a system does not read every tick belongs in a component of its own.
public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;
