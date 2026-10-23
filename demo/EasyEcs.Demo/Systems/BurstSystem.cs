using EasyEcs;

namespace EasyEcs.Demo;

// Keeps the population at its target: each emitter whose cooldown is over fires the sparks missing
// as one ring from where it stands.
[EcsSystem]
public ref struct BurstSystem
{
    // Seconds between two bursts of an emitter, at least and at most.
    private const float MinInterval = 0.25f;
    private const float MaxInterval = 0.6f;

    // Seconds a spark lives, at least and at most: mixed lifetimes keep the bursts from falling in
    // step.
    private const float MinLife = 1.6f;
    private const float MaxLife = 4f;

    // EntityRandom: numbers drawn for this emitter in this step, the same on every run. Each draw of a step has
    // its own stream: 0 for the cooldown, then three per spark.
    [ForEach]
    static void Burst(Entity self, ref Emitter emitter, in Position at, in Arena arena, in Clock clock,
        in Population population, in EntityRandom random, World world)
    {
        emitter.Cooldown -= clock.Dt;
        if (emitter.Cooldown > 0)
        {
            return;
        }

        emitter.Cooldown = random.Range(self.Id, 0, MinInterval, MaxInterval);

        // A burst takes at most a sixteenth of the target, so an empty world fills over a second
        // or so and the lifetimes of the first sparks are spread out
        int missing = Math.Min(population.Target - world.Count<Lifetime>(), population.Target / 16);
        float speed = Units.BurstSpeed * MathF.Min(arena.Width, arena.Height);

        for (int i = 0; i < missing; i++)
        {
            uint stream = 1 + (uint)i * 3;
            float angle = random.Range(self.Id, stream, 0, MathF.Tau);

            // Most sparks leave at nearly the same speed: the burst reads as a ring
            float v = speed * random.Range(self.Id, stream + 1, 0.75f, 1f);
            float life = random.Range(self.Id, stream + 2, MinLife, MaxLife);

            world.Instantiate(
                Units.Spark,
                at,
                new Velocity(MathF.Cos(angle) * v, MathF.Sin(angle) * v),
                new Lifetime(life, life));
        }
    }
}
