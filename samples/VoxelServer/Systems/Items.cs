using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// #region dig
// Digs the block under a player who asked to: the chunk's buffer loses the block (a tracked write: the chunk is sent
// again, and WaterWakeSystem wakes the water around it) and the block drops as an item, grass as dirt, as in
// Minecraft. The digger's score goes up by one.
[UpdateInGroup<Simulation>]
public class DigSystem : EcsSystem
{
    [ForEach]
    void Dig(Entity player, in Input input, in Position position, in Score score, in Prefabs prefabs, in Clock clock,
        ref Stats stats)
    {
        if (!input.Dig) return;
        var at = Terrain.Cell(position.Value.X, position.Value.Y - 1, position.Value.Z);
        if (at.Y <= 0 || at.Y >= Terrain.Height || !World.ChunkIndex.TryGet(Terrain.ChunkOf(at.X, at.Z), out var chunk))
            return;

        int slot = Terrain.IndexOf(at.X, at.Y, at.Z);
        var kind = chunk.Block[slot];                          // a read: not a change
        if (!Terrain.IsSolid(kind) || kind == Block.Bedrock) return;
        chunk.WriteBlock().AsSpan()[slot] = Block.Air;

        var drop = kind == Block.Grass ? Block.Dirt : kind;
        World.Instantiate(prefabs.Item, new Position(new Vector3(at.X + 0.5f, at.Y + 0.5f, at.Z + 0.5f)),
            new InChunk(Terrain.ChunkOf(at.X, at.Z)), new Item(drop, 1), new Dropped(clock.Tick));
        player.SetScore(score.BlocksDug + 1);                  // indexed: written with Set, read as a parameter
        stats.BlocksDug++;
    }
}
// #endregion dig

// #region pickup
// An item lying on the ground for Rules.PickupDelay ticks goes to a player standing on it, any player, as in
// Minecraft: the players near are the ones whose view centers on the chunks its pickup range touches, one to four
// (world.ViewCenterIndex: players only, not the zombies and items of those chunks). The stacks of its kind grow up to
// Rules.StackSize, then free slots take the rest; what does not fit stays on the ground. Each tick looks at half the
// items (Slices = 2): a pickup comes a tick later at most, and costs half.
[UpdateInGroup<Simulation>]
public class PickupSystem : EcsSystem
{
    [ForEach(Slices = 2)]
    void Pickup(Entity entity, ref Item item, in Dropped dropped, in Position position, in Clock clock, ref Stats stats)
    {
        if (clock.Tick - dropped.Tick < Rules.PickupDelay) return;
        var p = position.Value;
        var low = Terrain.ChunkOf(new System.Numerics.Vector3(p.X - Rules.PickupRange, 0, p.Z - Rules.PickupRange));
        var high = Terrain.ChunkOf(new System.Numerics.Vector3(p.X + Rules.PickupRange, 0, p.Z + Rules.PickupRange));
        for (int cz = low.Z; cz <= high.Z; cz++)
        for (int cx = low.X; cx <= high.X; cx++)
        foreach (var player in World.ViewCenterIndex.Find(new ChunkPos(cx, cz)))
        {
            if ((player.Position.Value - p).LengthSquared() > Rules.PickupRange * Rules.PickupRange) continue;
            if (!Inventory.HasRoom(player.ItemStack, item.Kind)) continue;   // a read: a full inventory is not stamped
            int taken = Inventory.Add(ref player.WriteItemStack(), item.Kind, item.Count);
            stats.ItemsPickedUp += taken;
            if (taken == item.Count)
            {
                entity.Destroy();
                return;
            }
            item = item with { Count = item.Count - taken };
        }
    }
}

// A player's inventory: a DynamicBuffer<ItemStack> of at most Rules.InventorySlots stacks of Rules.StackSize.
public static class Inventory
{
    // Whether a block of `kind` fits: a stack of it with room, or a free slot.
    public static bool HasRoom(in DynamicBuffer<ItemStack> inventory, Block kind)
    {
        if (inventory.Count < Rules.InventorySlots) return true;
        foreach (var stack in inventory)
            if (stack.Kind == kind && stack.Count < Rules.StackSize) return true;
        return false;
    }

    // Adds up to `count` blocks of `kind`; returns how many fit.
    public static int Add(ref DynamicBuffer<ItemStack> inventory, Block kind, int count)
    {
        int left = count;
        var stacks = inventory.AsSpan();
        for (int i = 0; i < stacks.Length && left > 0; i++)
        {
            if (stacks[i].Kind != kind || stacks[i].Count == Rules.StackSize) continue;
            int moved = Math.Min(left, Rules.StackSize - stacks[i].Count);
            stacks[i] = stacks[i] with { Count = stacks[i].Count + moved };
            left -= moved;
        }
        while (left > 0 && inventory.Count < Rules.InventorySlots)
        {
            int moved = Math.Min(left, Rules.StackSize);
            inventory.Add(new ItemStack(kind, moved));
            left -= moved;
        }
        return count - left;
    }
}
// #endregion pickup

// #region expiry
// Removes the items nobody picked up in time. The ordered index of drop ticks walks the items from the oldest and stops
// at the first that has time left: a tick visits the items that expire, not every item.
[UpdateInGroup<Simulation>]
public class ItemExpirySystem : EcsSystem
{
    protected override void OnUpdate()
    {
        ref var stats = ref World.Stats;
        foreach (var item in World.DroppedIndex.Range(long.MinValue, World.Clock.Tick - Rules.ItemLifetime))
        {
            item.Destroy();                                       // allowed while the walk goes on
            stats.ItemsExpired++;
        }
    }
}
// #endregion expiry
