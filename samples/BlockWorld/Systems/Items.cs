using EasyEcs;

namespace BlockWorld;

// Digs the block under a player who asked to: the chunk's buffer loses the block (a tracked write, so the chunk is
// sent again) and an item pops out of the hole.
public class DigSystem : EcsSystem
{
    [ForEach]
    void Dig(Entity player, in Input input, in Position position, in Prefabs prefabs, ref Stats stats)
    {
        if (!input.Dig) return;
        int x = (int)MathF.Floor(position.Value.X), y = (int)MathF.Floor(position.Value.Y) - 1;
        int z = (int)MathF.Floor(position.Value.Z);
        if (y < 0 || !World.ChunkIndex.TryGet(Terrain.ChunkOf(x, z), out var chunk)) return;

        int slot = Terrain.IndexOf(x, y, z);
        var kind = chunk.Read<DynamicBuffer<Block>>()[slot];   // a read: not a change
        if (kind is Block.Air or Block.Bedrock) return;
        chunk.Get<DynamicBuffer<Block>>().AsSpan()[slot] = Block.Air;

        var center = new System.Numerics.Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        World.Instantiate(prefabs.Item, new Position(center), new Item(kind, player));
        stats.BlocksDug++;
    }
}

// Puts an item into the inventory of the player who dug it once that player is close: the stack of its kind grows,
// or a new stack starts.
public class PickupSystem : EcsSystem
{
    [ForEach]
    void Pickup(Entity entity, in Item item, in Position position, ref Stats stats)
    {
        if (!World.TryGetEntity(item.Owner, out var owner)) return;
        var to = owner.Position.Value - position.Value;
        if (to.LengthSquared() > Rules.PickupRange * Rules.PickupRange) return;

        ref var inventory = ref owner.WriteItemStack();
        var stacks = inventory.AsSpan();
        int i = 0;
        while (i < stacks.Length && stacks[i].Kind != item.Kind) i++;
        if (i < stacks.Length) stacks[i] = stacks[i] with { Count = stacks[i].Count + 1 };
        else inventory.Add(new ItemStack(item.Kind, 1));

        entity.Destroy();
        stats.ItemsPickedUp++;
    }
}

// Removes the items nobody picked up in time.
public class ItemExpirySystem : EcsSystem
{
    [ForEach, With<Item>]
    static void Expire(Entity entity, ref Lifetime lifetime, ref Stats stats)
    {
        lifetime = new Lifetime(lifetime.Ticks - 1);
        if (lifetime.Ticks > 0) return;
        entity.Destroy();
        stats.ItemsExpired++;
    }
}
