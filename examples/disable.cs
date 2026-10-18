#:package EasyEcs@4.0.1
// Disable: hide a component from queries, Has and Get while keeping its value; Enable gives it back.
using EasyEcs;

using var world = new World();
world.AddSystem<CooldownSystem>();
var unit = world.CreateEntity(new Cooldown(3), new Health(10));

unit.Disable<Cooldown>();                                           // paused: kept, absent
world.Update();
Console.WriteLine($"paused: has {unit.HasCooldown}, disabled {unit.IsDisabled<Cooldown>()}, " +
                  $"paused units {world.Count<Health>(QueryFilter.Create().With<Disabled<Cooldown>>())}");

unit.Enable<Cooldown>();
world.Update();
Console.WriteLine($"resumed: {unit.Cooldown.Ticks} ticks left");    // 2: the paused update did not count

public record struct Cooldown(int Ticks) : IComponent;
public record struct Health(int Value) : IComponent;

public class CooldownSystem : EcsSystem
{
    [ForEach]
    static void Tick(ref Cooldown cooldown) => cooldown.Ticks--;
}
