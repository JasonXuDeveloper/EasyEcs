using EasyEcs;

namespace VoxelServer;

// #region sight
// The area of interest, one system for every entity with a Vision: twice a second it looks through the grid (the
// InChunk index) at the chunks its view reaches and keeps what is in range, up to Rules.MaxSeen, in its Seen buffer.
// A player's client shows that set: what is new came into view (a spawn packet), what is out of range left it and
// what no longer exists was destroyed (a despawn packet either way, counted apart); between two looks the entity
// updates keep the client current. A zombie hunts from it
// (ZombieTargetSystem). Seen is written through Mut<T> only when the set changed, so a viewer whose view stayed the
// same is not stamped, and [Changed<DynamicBuffer<Seen>>] readers visit only the viewers whose view changed. A tenth
// of the viewers look each tick (Slices), so a crowd costs the same every tick.
[UpdateInGroup<Simulation>]
public class SightSystem : EcsSystem
{
    [ForEach(Slices = Rules.LoadEvery)]
    void Look(Entity viewer, in Position position, in Vision vision, Mut<DynamicBuffer<Seen>> seen,
        Maybe<Player> client, ref Stats stats)
    {
        Span<Seen> now = stackalloc Seen[Rules.MaxSeen];
        int count = 0;
        var p = position.Value;
        float range = vision.Range * vision.Range;
        foreach (var chunk in Terrain.ChunksAround(Terrain.ChunkOf(p), vision.Range))
        {
            foreach (var other in World.InChunkIndex.Find(chunk))
            {
                if (other == viewer || (other.Position.Value - p).LengthSquared() > range) continue;
                now[count++] = new Seen(other);
                if (count == now.Length) goto full;
            }
        }
        full:
        now = now[..count];
        now.Sort();

        // Both sets are sorted: one walk finds what came, what left and what was destroyed (not just out of range).
        var before = seen.Value.AsReadOnlySpan();
        int came = 0, left = 0, gone = 0, i = 0, j = 0;
        while (i < now.Length || j < before.Length)
        {
            int order = i == now.Length ? 1 : j == before.Length ? -1 : now[i].CompareTo(before[j]);
            if (order == 0) { i++; j++; continue; }
            if (order < 0) { came++; i++; continue; }
            if (World.IsAlive(before[j++].Entity)) left++;
            else gone++;
        }
        if (came == 0 && left == 0 && gone == 0) return;

        ref var buffer = ref seen.Write();
        buffer.Clear();
        buffer.AddRange(now);
        if (!client.HasValue) return;                  // only a player has a client to send spawns and despawns to
        stats.CameIntoView += came;
        stats.LeftView += left;
        stats.Despawns += gone;
    }
}
// #endregion sight
