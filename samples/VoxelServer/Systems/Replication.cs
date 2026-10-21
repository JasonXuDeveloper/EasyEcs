using EasyEcs;

namespace VoxelServer;

// #region chunks
// What the server sends each tick, and to whom. A [Changed<T>] method visits only the rows written since its system
// last ran, so a chunk nobody dug and a player standing still cost nothing. A chunk whose blocks or water changed
// goes out as a block-change packet to the players that have it in view (the chunk's Viewers); [ChangedAny<..>]
// takes either buffer. A chunk that comes into a view is sent whole by ChunkViewSystem. Here the packets are counted;
// a real server would encode them.
[UpdateInGroup<Replication>]
public class SendChunksSystem : EcsSystem
{
    [ForEach, With<Chunk>, ChangedAny<DynamicBuffer<Block>, DynamicBuffer<Fluid>>]
    static void Send(in Viewers viewers, in Clock clock, ref Stats stats) =>
        stats.BlockUpdates += Rules.Watching(viewers, clock);
}
// #endregion chunks

// #region entities
// An entity that moved goes out as an update of its position, with its health bar when it has one: players and
// zombies do, a dropped item does not. Maybe<Health> reads the health where the entity has it, so one method serves
// all three kinds instead of one per kind. It goes to the players that have the entity's chunk in view: a coarser set
// than the players whose Seen holds the entity, and one lookup instead of a walk of every viewer.
[UpdateInGroup<Replication>]
public class SendEntitiesSystem : EcsSystem
{
    [ForEach, Changed<Position>]
    void Send(in Position position, Maybe<Health> health, in Clock clock, ref Stats stats)
    {
        if (!World.ChunkIndex.TryGet(Terrain.ChunkOf(position.Value), out var chunk)) return;
        int watching = Rules.Watching(chunk.Viewers, clock);
        stats.EntityUpdates += watching;
        stats.BytesSent += watching * (Rules.PositionBytes + (health.HasValue ? Rules.HealthBytes : 0));
    }
}
// #endregion entities

// #region clock
// Advances the clock after everything else ran this tick.
[UpdateInGroup<Replication>]
public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
// #endregion clock
