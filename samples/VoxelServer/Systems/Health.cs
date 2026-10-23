using EasyEcs;

namespace VoxelServer;

// A zombie with no health left is gone. [Changed<Health>]: only the zombies hurt since the last tick are looked at.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ZombieDeathSystem
{
    [ForEach, With<Zombie>, Changed<Health>]
    static void Die(Entity zombie, in Health health, ref Stats stats)
    {
        if (health.Value > 0) return;
        zombie.Destroy();
        stats.ZombiesBurned++;
    }
}

// A player with no health left drops its inventory where it died, as items anyone can pick up, and starts again at
// the spawn point. Position and the inventory are tracked, and a ref parameter of a tracked component stamps every
// row it visits, written or not: every living player would be sent again each tick. Mut<T> stamps only the rows of
// the players who respawn, and [Changed<Health>] visits only the players hurt since the last tick.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct PlayerRespawnSystem
{
    [ForEach, Changed<Health>]
    static void Respawn(in Player player, Mut<Health> health, Mut<Position> position, ref Velocity velocity,
        Mut<DynamicBuffer<ItemStack>> inventory, in Prefabs prefabs, in Clock clock, ref Stats stats, World world)
    {
        if (health.Value.Value > 0) return;
        var died = position.Value.Value;
        Span<ItemStack> dropped = stackalloc ItemStack[Rules.InventorySlots];
        var stacks = inventory.Value.AsReadOnlySpan();
        stacks.CopyTo(dropped);
        dropped = dropped[..stacks.Length];
        inventory.Write().Clear();
        position.Write() = new Position(Rules.SpawnPoint(player.Client));
        velocity = default;
        health.Write() = new Health(Rules.PlayerHealth);
        stats.PlayerDeaths++;

        // Last: creating the items is a structural change, after which the refs into this row are not used again.
        var chunk = new InChunk(Terrain.ChunkOf(died));
        var now = new Dropped(clock.Tick);
        foreach (var stack in dropped)
            world.Instantiate(prefabs.Item, new Position(died), chunk, new Item(stack.Kind, stack.Count), now);
    }
}
