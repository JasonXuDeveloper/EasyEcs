using EasyEcs;

namespace VoxelServer;

// What the server sends each tick, and to whom. A [Changed<T>] method visits only the rows written since its system
// last ran, so a chunk nobody dug and a player standing still cost nothing; each change goes to the players that have
// its chunk in view (the chunk's Viewers). Here the packets are counted; a real server would encode them.
public class SendChunksSystem : EcsSystem
{
    [ForEach, With<Chunk>, Changed<DynamicBuffer<Block>>]
    static void Send(in Viewers viewers, in Clock clock, ref Stats stats) =>
        stats.ChunkUpdates += Rules.Watching(viewers, clock);
}

public class SendPositionsSystem : EcsSystem
{
    [ForEach, Changed<Position>]
    void Send(in Position position, in Clock clock, ref Stats stats)
    {
        var p = position.Value;
        if (World.ChunkIndex.TryGet(Terrain.ChunkOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z)), out var chunk))
            stats.PositionUpdates += Rules.Watching(chunk.Viewers, clock);
    }
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
