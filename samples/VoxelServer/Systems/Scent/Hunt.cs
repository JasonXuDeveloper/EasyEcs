using EasyEcs;

namespace VoxelServer;

// Whether the field is worth its cost: only while some zombie hunts. By day, once the night's zombies have burned,
// Hunt is off and every chunk's step stops at its first line, so the chunks sleep and the field costs nothing until
// the night. When the first zombie of a night rises, every chunk's field wakes at once (a bulk write of a presence bit,
// all chunks in one table): the players have walked since the field last settled. At the field's cadence, before its
// steps.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.ScentEvery)]
public ref struct HuntSystem
{
    static void OnUpdate(World world)
    {
        bool on = world.Any<Zombie>();
        ref var hunt = ref world.Hunt;
        if (on && !hunt.On) world.SetAll(new Spreading(), Chunks);
        hunt = new Hunt(on);
    }

    static readonly QueryFilter Chunks = QueryFilter.Create().With<Ground>();
}
