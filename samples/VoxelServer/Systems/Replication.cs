using EasyEcs;

namespace VoxelServer;

// #region sight
// A player's side of the area of interest: the entities its client shows. Twice a second a player looks through the
// grid (the InChunk index) at the chunks its Vision reaches and keeps the players, zombies and items within range,
// up to Rules.MaxSeen. What is new came into view (a spawn packet with the whole entity), what is gone left it (a
// despawn packet); between two looks, the entity updates keep the client current. The set is a DynamicBuffer<Seen> on
// the player, written through Mut<T> only when it changed, so a player whose view stayed the same is not stamped. A
// tenth of the players look each tick (Slices).
[UpdateInGroup<Replication>]
public class PlayerSightSystem : EcsSystem
{
    [ForEach(Slices = Rules.LoadEvery), With<Player>]
    void Look(Entity player, in Position position, in Vision vision, Mut<DynamicBuffer<Seen>> seen, ref Stats stats)
    {
        Span<Seen> now = stackalloc Seen[Rules.MaxSeen];
        int count = 0;
        var p = position.Value;
        var center = Terrain.ChunkOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z));
        int reach = Rules.ChunksIn(vision.Range);
        float range = vision.Range * vision.Range;
        for (int dz = -reach; dz <= reach && count < now.Length; dz++)
        for (int dx = -reach; dx <= reach && count < now.Length; dx++)
        {
            foreach (var other in World.InChunkIndex.Find(new ChunkPos(center.X + dx, center.Z + dz)))
            {
                if (other == player || (other.Position.Value - p).LengthSquared() > range) continue;
                now[count++] = new Seen(other);
                if (count == now.Length) break;
            }
        }
        now = now[..count];

        var before = seen.Value.AsReadOnlySpan();
        int came = 0, kept = 0;
        foreach (var entity in now)
        {
            if (before.Contains(entity)) kept++;
            else came++;
        }
        int left = before.Length - kept;
        if (came == 0 && left == 0) return;

        ref var buffer = ref seen.Write();
        buffer.Clear();
        buffer.AddRange(now);
        stats.CameIntoView += came;
        stats.LeftView += left;
    }
}
// #endregion sight

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
