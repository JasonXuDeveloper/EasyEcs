using EasyEcs;

namespace EasyEcsApp;

// How many ticks an entity has left. LifetimeSystem destroys it when they run out.
public record struct Lifetime(int Ticks) : IComponent;
