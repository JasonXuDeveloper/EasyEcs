#:package EasyEcs@4.0.1
// Parallel: systems with no conflict share a stage (ParallelSystems), and one heavy method splits its rows into chunks.
using EasyEcs;

using var world = new World(new WorldOptions { ParallelSystems = true });
world.SetTarget(500, 500);
world.AddSystem<SteeringSystem>();                                 // reads Position, Target; writes Velocity
world.AddSystem<RegenerateSystem>();                               // writes Health: no conflict, same stage
world.AddSystem<MoveSystem>();                                     // writes Position: after SteeringSystem reads it
var units = new Entity[100_000];
world.CreateEntities(units, new Position(0, 0), new Velocity(0, 0), new Health(50));

for (int i = 0; i < 10; i++) world.Update();
Console.WriteLine($"unit 0 at {units[0].Position}, {units[0].Health.Value} hp");

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Target(float X, float Y) : ISingletonComponent;

public class SteeringSystem : EcsSystem
{
    [ForEach(Parallel = true)]                                      // chunks of rows on several threads
    static void Steer(in Position p, ref Velocity v, in Target target)
    {
        float dx = target.X - p.X, dy = target.Y - p.Y, length = MathF.Sqrt(dx * dx + dy * dy) + 1;
        v = new Velocity(dx / length, dy / length);
    }
}

public class RegenerateSystem : EcsSystem
{
    [ForEach]
    static void Regenerate(ref Health hp) => hp.Value = Math.Min(hp.Value + 1, 100);
}

public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}
