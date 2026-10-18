#:package EasyEcs@4.0.1
// Change tracking: an ITrackChanges component records each row's last write; [Changed<T>] visits only those rows.
using EasyEcs;

using var world = new World();
world.AddSystem<ReplicationSystem>();
var units = new Entity[1000];
world.CreateEntities(units, new Health(100), new Replica());

world.Update();                                                     // first run: every row is new
Console.WriteLine($"first send: {world.Sent.Count} rows");

units[7].SetHealth(90);                                             // two writes between updates
units[42].SetHealth(75);
world.Update();
Console.WriteLine($"second send: {world.Sent.Count} rows, unit 42 replica = {units[42].Replica.Value}");

world.Update();                                                     // nothing written: nothing visited
Console.WriteLine($"third send: {world.Sent.Count} rows");

// Outside systems: a filter by a tick read earlier.
ulong mark = world.ChangeTick;
units[1].SetHealth(1);
Console.WriteLine($"written since the mark: {world.Count<Health>(QueryFilter.Create().Changed<Health>(mark))}");

public record struct Health(int Value) : IComponent, ITrackChanges;   // tracked: entity.Health is read-only
public record struct Replica(int Value) : IComponent;
public record struct Sent(int Count) : ISingletonComponent;

public class ReplicationSystem : EcsSystem
{
    protected override void OnUpdate() => World.SetSent(0);           // runs before the [ForEach] methods

    [ForEach, Changed<Health>]                                      // rows written since this system last ran
    static void Send(in Health hp, ref Replica replica, ref Sent sent)
    {
        replica.Value = hp.Value;
        sent.Count++;
    }
}
