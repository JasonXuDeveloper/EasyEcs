using EasyEcs;

namespace EasyEcsApp;

// Components: plain data on entities. Keep them small; a field nobody reads every tick is a component of its own.
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Lifetime(int Ticks) : IComponent;

// Singletons: one value per world.
public record struct Clock(long Tick, float DeltaTime) : ISingletonComponent;
