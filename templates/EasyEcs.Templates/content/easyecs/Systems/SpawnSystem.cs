using EasyEcs;

namespace EasyEcsApp;

// Spawns a wave of bullets from the spawner's prefab every Interval ticks.
// The game's rules live in systems like this one, never in Program.cs.
public class SpawnSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        ref readonly var spawner = ref World.Spawner;
        bool waveDue = World.Clock.Tick % spawner.Interval == 0;
        if (!waveDue)
        {
            return;
        }

        for (int i = 0; i < spawner.Wave; i++)
        {
            World.Instantiate(spawner.Bullet);
        }
    }
}
