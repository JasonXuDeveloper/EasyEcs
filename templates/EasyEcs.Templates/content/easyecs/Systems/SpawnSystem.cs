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

        for (int i = 0; i < spawner.Wave; i++)
        {
            world.Instantiate(spawner.Bullet);
        }
    }
}
