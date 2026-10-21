using EasyEcs;

namespace VoxelServer;

// #region chunks
// What the server sends each tick, and to whom. A [Changed<T>] method visits only the rows written since its system
// last ran, so a chunk nobody dug and a player standing still cost nothing; each change goes to the players that have
// its chunk in view (the chunk's Viewers). Here the packets are counted; a real server would encode them.
[UpdateInGroup<Replication>]
public class SendChunksSystem : EcsSystem
{
    [ForEach, With<Chunk>, Changed<DynamicBuffer<Block>>]
    static void Send(in Viewers viewers, in Clock clock, ref Stats stats) =>
        stats.ChunkUpdates += Rules.Watching(viewers, clock);
}
// #endregion chunks

// #region entities
// An entity that moved goes out as an update of its position, with its health bar when it has one: players and
// zombies do, a dropped item does not. Maybe<Health> reads the health where the entity has it, so one method serves
// all three kinds instead of one per kind.
[UpdateInGroup<Replication>]
public class SendEntitiesSystem : EcsSystem
{
    [ForEach, Changed<Position>]
    void Send(in Position position, Maybe<Health> health, in Clock clock, ref Stats stats)
    {
        var p = position.Value;
        if (!World.ChunkIndex.TryGet(Terrain.ChunkOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z)), out var chunk))
            return;
        int watching = Rules.Watching(chunk.Viewers, clock);
        stats.EntityUpdates += watching;
        stats.BytesSent += watching * (Rules.PositionBytes + (health.HasValue ? Rules.HealthBytes : 0));
    }
}
// #endregion entities

// #region despawns
// A destroyed entity has no row left to find: the world's destroy log (WorldOptions.DestroyLogCapacity) lists the
// entities destroyed since a tick, here since this system last ran: the zombies that burned, the items picked up,
// the chunks unloaded with their water. A real server would write the ids into the despawn packets.
[UpdateInGroup<Replication>]
public class SendDespawnsSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        Span<EntityId> gone = stackalloc EntityId[256];
        if (World.DestroyedSince(LastRunTick + 1, gone, out int count)) World.Stats.Despawns += count;
    }
}
// #endregion despawns

// #region clock
// Advances the clock after everything else ran this tick.
[UpdateInGroup<Replication>]
public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
// #endregion clock
