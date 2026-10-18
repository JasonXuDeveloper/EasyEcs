using EasyEcs;

namespace EasyEcs.Demo;

// Keeps the population at its target: instantiates the prefab for every spark missing.
public class SpawnSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        ref readonly var arena = ref World.Arena;
        long tick = World.Clock.Tick;
        int missing = World.Population.Target - World.EntityCount;

        // The first update fills an empty world: those sparks start at a random point of their life, so they do
        // not all expire together.
        bool filling = World.EntityCount == 0;

        for (int i = 0; i < missing; i++)
        {
            int n = i * 4;
            float angle = Units.Unit(tick, n + 2) * MathF.Tau;
            float speed = 30 + Units.Unit(tick, n + 3) * 120;
            float total = 4 + Units.Unit(tick, n) * 6;

            var spark = World.Instantiate(Units.Spark);
            spark.Position = new Position(Units.Unit(tick, n + 1) * arena.Width, Units.Unit(tick, n + 4) * arena.Height);
            spark.Velocity = new Velocity(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
            spark.Lifetime = new Lifetime(filling ? total * Units.Unit(tick, n + 5) : total, total);
        }
    }
}
