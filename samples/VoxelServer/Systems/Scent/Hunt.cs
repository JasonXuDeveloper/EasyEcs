using EasyEcs;

namespace VoxelServer;

// Whether the field is worth its cost: only while some zombie hunts. By day, once the night's zombies have burned, Hunt
// is off and the seeds go (SeedSystem): the field fades to nothing in a step and sleeps, costing nothing until the night.
// When the first zombie of a night rises, the seeds come back under the players, who have walked since the field last
// settled. At the field's cadence, before its steps.
[EcsSystem, UpdateInterval(Rules.ScentEvery)]
public ref struct HuntSystem
{
    static void OnUpdate(World world) => world.Hunt = new Hunt(world.Any<Zombie>());
}
