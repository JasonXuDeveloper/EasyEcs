#:package EasyEcs@4.0.1
// Cleanup components: an entity destroyed while holding one stays (tagged Destroyed, its other components gone) until
// a system releases what it holds and removes the last cleanup component.
using EasyEcs;

using var world = new World();
world.AddSingleton<Seats>();
world.AddSystem<ReleaseSeats>();
var player = world.CreateEntity(new Seat(7), new Health(10));

player.Destroy();
Console.WriteLine($"destroyed: alive {player.IsAlive}, tagged {player.Has<Destroyed>()}, health {player.HasHealth}");
world.Update();
Console.WriteLine($"after the update: alive {player.IsAlive}, seats released {world.Seats.Released}");

public record struct Seat(int Number) : ICleanupComponent;          // a resource outside the world
public record struct Health(int Value) : IComponent;
public record struct Seats(int Released) : ISingletonComponent;

public class ReleaseSeats : EcsSystem
{
    [ForEach, With<Destroyed>]                                       // only entities already destroyed
    static void Release(Entity entity, in Seat seat, ref Seats seats)
    {
        seats.Released++;                                            // give the seat back here
        entity.RemoveSeat();                                         // the last cleanup component: the entity is freed
    }
}
