using EasyEcs;

namespace EasyEcsApp;

// Advances the clock once per update. OnUpdate runs once per update, not once per entity.
// Program.cs adds it last, so every other system sees the tick the update started with.
public class ClockSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        World.Clock.Tick++;
    }
}
