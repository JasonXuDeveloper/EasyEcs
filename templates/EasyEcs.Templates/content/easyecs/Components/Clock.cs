using EasyEcs;

namespace EasyEcsApp;

// A singleton: one value per world instead of one per entity.
// Tick counts the updates; DeltaTime is the length of one tick in seconds.
public record struct Clock(long Tick, float DeltaTime) : ISingletonComponent;
