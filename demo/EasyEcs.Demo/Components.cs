using EasyEcs;

namespace EasyEcs.Demo;

// Components: plain data on each entity, in pixels and seconds.
public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;

// Seconds left of a total: a spark fades in, fades out and is destroyed when it runs out.
public record struct Lifetime(float Left, float Total) : IComponent;

// Present while a spark glows after hitting a wall. It comes and goes: a presence bit, the row
// stays.
public record struct Flash(float Left) : IComponent;

// A point that travels along a curve and fires bursts of sparks: Phase is where it is on the curve,
// Cooldown the seconds until its next burst.
public record struct Emitter(float Phase, float Cooldown) : IComponent;

// Singletons: one value per world.
// The walls, resized by the page with the canvas.
public record struct Arena(float Width, float Height) : ISingletonComponent;

// The step the page asked for and the number of steps so far, written by the host before each
// update.
public record struct Clock(float Dt, long Tick) : ISingletonComponent;

// How many sparks the world keeps alive: the emitters replace the ones LifetimeSystem destroys.
public record struct Population(int Target) : ISingletonComponent;

// The visitor's pointer over the canvas, written by the page: input is data the systems read.
public record struct Pointer(float X, float Y, bool Active) : ISingletonComponent;
