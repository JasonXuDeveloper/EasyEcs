using EasyEcs;

namespace BlockWorld;

// What the server sends each tick. A [Changed<T>] method visits only the rows written since its system last ran, so
// a chunk nobody dug and a player standing still cost nothing. Here the "packets" are counted; a real server would
// encode them for the players in view.
public class SendChunksSystem : EcsSystem
{
    [ForEach, With<Chunk>, Changed<DynamicBuffer<Block>>]
    static void Send(ref Stats stats) => stats.ChunkUpdates++;
}

public class SendPositionsSystem : EcsSystem
{
    [ForEach, Changed<Position>]
    static void Send(ref Stats stats) => stats.PositionUpdates++;
}

// Advances the clock after everything else ran this tick.
public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
