#:package EasyEcs@4.0.1
// Queries and filters: [ForEach] filters in systems, and Query / Count / Any / struct jobs in ad-hoc code.
using EasyEcs;

using var world = new World();
world.CreateEntity(new Position(0, 0), new Velocity(1, 0));
world.CreateEntity(new Position(0, 0), new Velocity(0, 1), new Frozen());
world.CreateEntity(new Position(0, 0), new Velocity(1, 1), new Burning());
world.CreateEntity(new Position(9, 9));

world.AddSystem<SlowSystem>();
world.AddSystem<MoveSystem>();
world.Update();

// Ad-hoc: iterate with Query (items are refs into storage), count, test. Build filters once, keep them in fields.
foreach (var item in world.Query<Position, Velocity>(Filters.Moving))
    Console.WriteLine($"{item.Entity}: {item.Position}");
Console.WriteLine($"moving: {world.Count<Position, Velocity>(Filters.Moving)}, any frozen: {world.Any<Frozen>()}");

// A struct job: the fastest ad-hoc pass, no delegate.
var job = new SumX();
world.ForEach<Position, SumX>(ref job);
Console.WriteLine($"sum of X: {job.Sum}");

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public struct Frozen : IComponent;
public struct Burning : IComponent;

public static class Filters
{
    public static readonly QueryFilter Moving = QueryFilter.Create().Without<Frozen>();
}

public class MoveSystem : EcsSystem
{
    [ForEach, Without<Frozen>]                                      // every mover that is not frozen
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}

public class SlowSystem : EcsSystem
{
    [ForEach, WithAny<Frozen>, WithAny<Burning>]                    // frozen or burning (or both)
    static void Slow(ref Velocity v) => v = new Velocity(v.X / 2, v.Y / 2);
}

public struct SumX : IForEach<Position>
{
    public float Sum;
    public void Execute(ref Position p) => Sum += p.X;
}
