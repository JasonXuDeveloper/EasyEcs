using EasyEcs;

namespace EasyEcsApp;

// Spawns a wave of bullets from the spawner's prefab every Interval ticks.
// The game's rules live in systems like this one, never in Program.cs.
[EcsSystem]
public ref struct SpawnSystem
{
    static void OnUpdate(World world)
    {
        ref readonly var spawner = ref world.Spawner;
        bool waveDue = world.Clock.Tick % spawner.Interval == 0;
        if (!waveDue)
        {
            return;
        }

        // The whole wave in one call: one pass through the structural gate and at most one table growth, and
        // the handles land in a stack buffer. A wave is a few hundred entities; a bigger one passes a rented array.
        world.Instantiate(spawner.Bullet, stackalloc Entity[spawner.Wave]);
    }
}
