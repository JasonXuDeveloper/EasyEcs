using EasyEcs;

namespace EasyEcs.Demo;

// Ages every spark and destroys the ones whose time ran out.
public class LifetimeSystem : EcsSystem
{
    [ForEach]
    static void Age(Entity spark, ref Lifetime life, in Clock clock)
    {
        life.Left -= clock.Dt;
        if (life.Left <= 0)
        {
            spark.Destroy();
        }
    }
}
