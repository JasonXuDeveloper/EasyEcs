using EasyEcs;

namespace EasyEcs.Demo;

// Keeps the population at its target: each emitter whose cooldown is over fires the sparks missing
// as one ring from where it stands.
public class BurstSystem : EcsSystem
{
    // Seconds between two bursts of an emitter, at least and at most.
    private const float MinInterval = 0.25f;
    private const float MaxInterval = 0.6f;

    // Seconds a spark lives, at least and at most: mixed lifetimes keep the bursts from falling in
    // step.
    private const float MinLife = 1.6f;
    private const float MaxLife = 4f;

    [ForEach]
    static void Burst(ref Emitter emitter, in Position at, in Arena arena, in Clock clock,
        in Population population, World world)
    {
        emitter.Cooldown -= clock.Dt;
        if (emitter.Cooldown > 0)
        {
            return;
        }

        long tick = clock.Tick;
        int seed = (int)(emitter.Phase * 1000);
        emitter.Cooldown = MinInterval + Units.Unit(tick, seed) * (MaxInterval - MinInterval);

        // A burst takes at most a sixteenth of the target, so an empty world fills over a second
        // or so and the lifetimes of the first sparks are spread out
        int missing = Math.Min(population.Target - world.Count<Lifetime>(), population.Target / 16);
        float speed = Units.BurstSpeed * MathF.Min(arena.Width, arena.Height);

        for (int i = 0; i < missing; i++)
        {
            int n = seed + i * 3;
            float angle = Units.Unit(tick, n) * MathF.Tau;

            // Most sparks leave at nearly the same speed: the burst reads as a ring
            float v = speed * (0.75f + 0.25f * Units.Unit(tick, n + 1));
            float life = MinLife + Units.Unit(tick, n + 2) * (MaxLife - MinLife);

            var spark = world.Instantiate(Units.Spark);
            spark.Position = at;
            spark.Velocity = new Velocity(MathF.Cos(angle) * v, MathF.Sin(angle) * v);
            spark.Lifetime = new Lifetime(life, life);
        }
    }
}
