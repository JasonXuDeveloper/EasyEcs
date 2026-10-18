using EasyEcs;

namespace HelloWorld;

// Components are plain data stored on entities. They have fields and no logic.
public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;

// A component with no fields is a tag: it only says "this entity is a player",
// so systems can filter on it.
public struct Player : IComponent;

// A singleton has one value per world instead of one per entity.
public record struct Clock(long Tick) : ISingletonComponent;
