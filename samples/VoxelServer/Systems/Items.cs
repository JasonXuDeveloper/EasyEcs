using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// #region dig
// Digs the block under a player who asked to: the chunk's buffer loses the block (a tracked write: the chunk is sent
// again, and BlockUpdateSystem wakes the water around it) and the block drops as an item, grass as dirt, as in
// Minecraft. The digger's score goes up by one.
[UpdateInGroup<Simulation>]
public class DigSystem : EcsSystem
{
    [ForEach]
    void Dig(Entity player, in Input input, in Position position, in Prefabs prefabs, ref Stats stats)
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
            new InChunk(Terrain.ChunkOf(at.X, at.Z)), new Item(drop, 1));
        player.SetScore(player.Score.BlocksDug + 1);
        stats.BlocksDug++;
    }
}
// #endregion dig

// #region age
// Every item gets one tick older. The same arithmetic over a column: a batch method takes the ages of a run of items
// as one span and adds one to 8 of them per instruction.
[UpdateInGroup<Simulation>]
public class ItemAgeSystem : EcsSystem
{
    [ForEach]
    static void Grow(Span<Age> ages)
    {
        var values = MemoryMarshal.Cast<Age, int>(ages);
        var lanes = MemoryMarshal.Cast<int, Vector<int>>(values);
        for (int i = 0; i < lanes.Length; i++) lanes[i] += Vector<int>.One;
        for (int i = lanes.Length * Vector<int>.Count; i < values.Length; i++) values[i]++;   // the items left over
    }
}
// #endregion age

// #region pickup
// An item lying on the ground for Rules.PickupDelay ticks goes to a player standing on it, any player, as in
// Minecraft: the players near are the ones of its chunk in the grid. The stacks of its kind grow up to Rules.StackSize,
// then free slots take the rest; what does not fit stays on the ground.
[UpdateInGroup<Simulation>]
public class PickupSystem : EcsSystem
{
    [ForEach]
    void Pickup(Entity entity, ref Item item, in Age age, in Position position, in InChunk chunk, ref Stats stats)
    {
        if (age.Ticks < Rules.PickupDelay) return;
        foreach (var player in World.InChunkIndex.Find(chunk.Pos))
        {
            if (!player.HasPlayer || (player.Position.Value - position.Value).LengthSquared() > Rules.PickupRange * Rules.PickupRange)
                continue;
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
// Removes the items nobody picked up in time.
[UpdateInGroup<Simulation>]
public class ItemExpirySystem : EcsSystem
{
    [ForEach, With<Item>]
    static void Expire(Entity entity, in Age age, ref Stats stats)
    {
        if (age.Ticks < Rules.ItemLifetime) return;
        entity.Destroy();
        stats.ItemsExpired++;
    }
}
// #endregion expiry
