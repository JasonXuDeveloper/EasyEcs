using EasyEcs;

namespace EasyEcsApp;

// What SpawnSystem creates, and how often. Bullet is a prefab: a recipe of component values
// that every spawned entity starts from. A wave of Wave bullets appears every Interval ticks.
public record struct Spawner(Prefab Bullet, int Wave, int Interval) : ISingletonComponent;
