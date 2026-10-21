using EasyEcs;

namespace VoxelServer;

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

// A destroyed entity has no row left to find: the world's destroy log (WorldOptions.DestroyLogCapacity) lists the
// entities destroyed since a tick, here since this system last ran: the zombies that burned, the items picked up,
// the chunks unloaded with their water. A real server would write the ids into the despawn packets.
public class SendDespawnsSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        Span<EntityId> gone = stackalloc EntityId[256];
        if (World.DestroyedSince(LastRunTick + 1, gone, out int count)) World.Stats.Despawns += count;
    }
}

// Advances the clock after everything else ran this tick.
public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
