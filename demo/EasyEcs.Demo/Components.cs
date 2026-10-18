using EasyEcs;

namespace EasyEcs.Demo;

// Components: plain data on each entity, in pixels and seconds.
public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;

// Seconds left of a total: the entity fades in, fades out and is destroyed when it runs out.
public record struct Lifetime(float Left, float Total) : IComponent;

// Present while an entity glows after bouncing off a wall. It comes and goes: a presence bit, the row stays.
public record struct Flash(float Left) : IComponent;

// Singletons: one value per world.
// The walls, resized by the page with the canvas.
public record struct Arena(float Width, float Height) : ISingletonComponent;

// The step the page asked for and the number of steps so far, written by the host before each update.
public record struct Clock(float Dt, long Tick) : ISingletonComponent;

// How many entities the world keeps alive: SpawnSystem replaces the ones LifetimeSystem destroys.
public record struct Population(int Target) : ISingletonComponent;
