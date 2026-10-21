using EasyEcs;

namespace BlockWorld;

// A zombie with no health left is gone.
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

// A player with no health left drops everything and starts again at the spawn point.
public class PlayerRespawnSystem : EcsSystem
{
    [ForEach]
    static void Respawn(in Player player, ref Health health, ref Position position, ref Velocity velocity,
        ref DynamicBuffer<ItemStack> inventory, ref Stats stats)
    {
        if (health.Value > 0) return;
        position = new Position(Rules.SpawnPoint(player.Client));
        velocity = default;
        health = new Health(Rules.PlayerHealth);
        inventory.Clear();
        stats.PlayerDeaths++;
    }
}
