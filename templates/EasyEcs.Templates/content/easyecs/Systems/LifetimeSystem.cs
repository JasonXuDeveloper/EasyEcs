using EasyEcs;

namespace EasyEcsApp;

// Counts down every lifetime and destroys the entity when it runs out.
// Destroying an entity in the middle of the pass is safe.
[EcsSystem]
public ref struct LifetimeSystem
{
    [ForEach]
    static void Age(Entity entity, ref Lifetime lifetime)
    {
        lifetime.Ticks--;
        if (lifetime.Ticks <= 0)
        {
            entity.Destroy();
        }
    }
}
