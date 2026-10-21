using EasyEcs;

namespace VoxelServer;

// #region chunks
// What the server sends each tick, and to whom. A [Changed<T>] method visits only the rows written since its system
// last ran, so a chunk nobody dug and a player standing still cost nothing. A chunk whose blocks or water changed
// goes out as a block-change packet to the players that have it in view (the chunk's Viewers); [ChangedAny<..>]
// takes either buffer. A chunk that comes into a view is sent whole by ChunkViewSystem, and so is a Fresh one, just
// loaded or generated: its buffers count as written, but no client has it yet. Here the packets are counted; a real
// server would encode them.
[UpdateInGroup<Replication>]
public class SendChunksSystem : EcsSystem
{
    [ForEach, With<Chunk>, Without<Fresh>, ChangedAny<DynamicBuffer<Block>, DynamicBuffer<Fluid>>]
    static void Send(in Viewers viewers, ref Stats stats) => stats.BlockUpdates += viewers.Count;

    // From the next tick on, its changes are block changes.
    [ForEach, With<Fresh>]
    static void Sent(Entity chunk) => chunk.Remove<Fresh>();
}
// #endregion chunks

// #region entities
// An entity that moved goes out as an update of its position, with its health bar when it has one: players and
// zombies do, a dropped item does not. Maybe<Health> reads the health where the entity has it, so one method serves
// all three kinds instead of one per kind. It goes to the players that have the entity's chunk in view, as the
// entity's spawn did (SightSystem): the chunk's Viewers, one lookup instead of a walk of every viewer.
[UpdateInGroup<Replication>]
public class SendEntitiesSystem : EcsSystem
{
    [ForEach, Changed<Position>]
    void Send(in Position position, Maybe<Health> health, ref Stats stats)
    {
        if (!World.ChunkIndex.TryGet(Terrain.ChunkOf(position.Value), out var chunk)) return;
        int watching = chunk.Viewers.Count;
        stats.EntityUpdates += watching;
        stats.BytesSent += watching * (Rules.PositionBytes + (health.HasValue ? Rules.HealthBytes : 0));
    }
}
// #endregion entities

// #region clock
// Starts a tick of the simulation: the clock goes first, so every system of the tick, and the replication after it,
// sees the same tick. In the Simulation group, not Replication, so a server that replicates less often than it
// simulates keeps game time.
[UpdateInGroup<Simulation>]
public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
// #endregion clock
