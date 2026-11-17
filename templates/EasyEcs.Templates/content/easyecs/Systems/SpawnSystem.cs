using System.Buffers;
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

        // The whole wave in one call: one pass through the structural gate and at most one table growth. The
        // handles land in a pooled array (an Entity holds its world, so it cannot live on the stack).
        var handles = ArrayPool<Entity>.Shared.Rent(spawner.Wave);
        world.Instantiate(spawner.Bullet, handles.AsSpan(0, spawner.Wave));
        ArrayPool<Entity>.Shared.Return(handles);
    }
}
