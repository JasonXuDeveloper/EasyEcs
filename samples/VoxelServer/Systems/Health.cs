using EasyEcs;

namespace VoxelServer;

// A zombie with no health left is gone.
[UpdateInGroup<Simulation>]
public class ZombieDeathSystem : EcsSystem
{
    [ForEach, With<Zombie>]
    static void Die(Entity zombie, in Health health, ref Stats stats)
    {
        if (health.Value > 0) return;
        zombie.Destroy();
        stats.ZombiesBurned++;
    }
}

// A player with no health left drops everything and starts again at the spawn point. Position and the inventory are
// tracked, and a ref parameter of a tracked component stamps every row it visits, written or not: every living player
// would be sent again each tick. Mut<T> stamps only the rows of the players who respawn.
[UpdateInGroup<Simulation>]
public class PlayerRespawnSystem : EcsSystem
{
    [ForEach]
    static void Respawn(in Player player, ref Health health, Mut<Position> position, ref Velocity velocity,
        Mut<DynamicBuffer<ItemStack>> inventory, ref Stats stats)
    {
        if (health.Value > 0) return;
        position.Write() = new Position(Rules.SpawnPoint(player.Client));
        velocity = default;
        health = new Health(Rules.PlayerHealth);
        inventory.Write().Clear();
        stats.PlayerDeaths++;
    }
}
