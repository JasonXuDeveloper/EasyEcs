using EasyEcs;

namespace SaveLoad;

// Creates the players the sample saves, loads and moves.
public static class Players
{
    public const int PotionKind = 1;
    public const int MapKind = 2;

    // A player with a name, a position, full health, a bag with three potions and a map,
    // and `swords` sword entities that are its children.
    public static Entity Spawn(World world, string name, float x, float y, int swords)
    {
        Entity player = world.CreateEntity(
            new Name(name),
            new Position(x, y),
            new Health(100),
            new DynamicBuffer<Item>());

        ref var items = ref player.Get<DynamicBuffer<Item>>();
        items.Add(new Item(PotionKind, 3));
        items.Add(new Item(MapKind, 1));

        for (int i = 0; i < swords; i++)
        {
            world.CreateEntity(new Sword(10 + i), new ChildOf(player));
        }

        return player;
    }
}
