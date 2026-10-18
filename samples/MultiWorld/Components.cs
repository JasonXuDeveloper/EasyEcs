using EasyEcs;

namespace MultiWorld;

public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;

public record struct Health(int Value) : IComponent;

// How many units died in this zone. Each world has its own.
public record struct Tally(int Deaths) : ISingletonComponent;
