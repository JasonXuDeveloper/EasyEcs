using EasyEcs;

namespace VoxelServer;

// #region chunks
// What the server sends each tick, and to whom. A [Changed<T>] method visits only the rows written since its system
// last ran, so a chunk nobody dug and a player standing still cost nothing. A chunk whose blocks or water changed
// goes out as a block-change packet to the players that have it in view (the chunk's Viewers); [ChangedAny<..>]
// takes either buffer. A chunk that comes into a view is sent whole by AoiViewSystem, and so is a Fresh one, just
// loaded or generated: its buffers count as written, but no client has it yet; ChunkSentSystem clears the tag. Here the packets are counted; a real
// server would encode them.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct SendChunksSystem
{
    [ForEach, With<Chunk>, Without<Fresh>, ChangedAny<DynamicBuffer<Block>, DynamicBuffer<Fluid>>]
    static void Send(in Viewers viewers, ref Stats stats) => stats.BlockUpdates += viewers.Count;

}

// From the next tick on, a chunk loaded this tick is like any other: its changes are block changes. One bulk removal
// clears the tag of every chunk at once, whole words of presence bits, after the systems that read it.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct ChunkSentSystem
{
    [OnUpdate]
    static void OnUpdate(World world) => world.RemoveAll<Fresh>();
}
// #endregion chunks

// #region entities
// An entity that moved goes out as an update of its position, with its health bar when it has one: players and
// zombies do, a dropped item does not. ReadOnlyMaybe<Health> reads the health where the entity has it, so one method
// serves all three kinds instead of one per kind. It goes to the players that have the entity's chunk in view, as the
// entity's spawn did (AoiSpawnSystem): the chunk's Viewers, read through lookups (the chunk by its key, its Viewers by
// id). Each row only reads, so the rows are split across threads, and each appends its packet to an Emit<EntityUpdate>:
// every chunk of rows to a list of its own, joined in row order into Emitted<EntityUpdate> when the pass ends, the
// outbox SendPacketsSystem sends.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct SendEntitiesSystem
{
    [ForEach(Parallel = true), Changed<Position>]
    static void Send(Entity entity, in InChunk at, in Position position, ReadOnlyMaybe<Health> health, Lookup<Chunk> chunks,
        Lookup<Viewers> viewers, Emit<EntityUpdate> updates)
    {
        if (!chunks.TryFind(at.Pos, out var chunk)) return;
        int watching = viewers.Read(chunk.Id).Count;
        if (watching == 0) return;
        updates.Add(new EntityUpdate(entity.Id, position.Value, health.HasValue ? health.Value.Value : -1, watching));
    }
}

// Sends the outbox: each packet to the players watching, with its health bar when it has one. Here the packets are
// counted; a real server would encode each into its viewers' connections. Cleared once sent, so none goes twice.
[EcsSystem, UpdateInGroup<Replication>]
public ref struct SendPacketsSystem
{
    [OnUpdate]
    static void OnUpdate(World world)
    {
        ref var outbox = ref world.GetOrAddSingleton<Emitted<EntityUpdate>>().Items;
        ref var stats = ref world.Stats;
        foreach (ref readonly var update in outbox.AsReadOnlySpan())
        {
            stats.EntityUpdates += update.Viewers;
            stats.BytesSent += update.Viewers * (Rules.PositionBytes + (update.Health >= 0 ? Rules.HealthBytes : 0));
        }
        outbox.Clear();
    }
}
// #endregion entities

// #region tally
// Ends a tick of the simulation: what its passes on several threads counted (each pass a reduction of its own, which
// it replaces) is added to the run's Stats.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct TallySystem
{
    [OnUpdate]
    static void OnUpdate(World world)
    {
        ref var stats = ref world.Stats;
        stats.WaterCells += world.Filled.Blocks;
        stats.Drifts += world.Carried.Bodies;
    }
}
// #endregion tally

// #region clock
// Starts a tick of the simulation: the clock goes first, so every system of the tick, and the replication after it,
// sees the same tick. In the Simulation group, not Replication, so a server that replicates less often than it
// simulates keeps game time.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ClockSystem
{
    [OnUpdate]
    static void OnUpdate(World world) => world.Clock.Tick++;
}
// #endregion clock
