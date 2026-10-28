using EasyEcs;

namespace MultiWorld;

// The map: a strip of zones side by side along x, each zone a world of its own.
public static class Map
{
    public const int Zones = 4;
    public const float ZoneWidth = 250;
    public const float Width = Zones * ZoneWidth;
    public const float Height = 250;
    public const int Units = 8_000;

    // How far a unit sees, and so how deep the border rows copied to a neighbor go (the halo's width).
    public const float Sight = 10;

    // The zone that owns the units standing at x; the map's edges belong to the zones beside them.
    public static int ZoneOf(float x) => Math.Clamp((int)MathF.Floor(x / ZoneWidth), 0, Zones - 1);

    // Unit i of the map: spread evenly over it, its direction from its number (never standing still).
    public static (Position, Velocity) Unit(int i)
    {
        float x = (float)(i * 0.6180339887 % 1 * Width);
        float y = (float)(i * 0.7548776662 % 1 * Height);
        float vx = i % 9 * 0.25f - 0.875f;
        float vy = i % 7 * 0.25f - 0.625f;
        return (new Position(x, y), new Velocity(vx, vy));
    }
}

// One zone: a world updated by one thread. The same systems serve every zone, and the whole map as one world.
public static class Zone
{
    public static World Create()
    {
        var world = new World();
        world.AddSystem<SightSystem>(); // first: what the units see where the last exchange left them and their ghosts
        world.AddSystem<MoveSystem>();
        world.AddSystem<BounceSystem>();
        return world;
    }

    // Updates the world and waits at the exchange after each update, while the units that crossed move and the ghosts
    // are copied again. Returns how many times its units saw another unit.
    public static long Run(World world, int updates, Barrier? exchange = null)
    {
        long sightings = 0;
        for (int i = 0; i < updates; i++)
        {
            world.Update();
            sightings += world.Tally.Sightings; // each update's count replaces the last one
            exchange?.SignalAndWait();
        }

        return sightings;
    }
}
