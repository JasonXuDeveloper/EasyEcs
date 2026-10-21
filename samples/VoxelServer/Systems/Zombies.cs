using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// #region spawn
// At night, every few seconds, a zombie rises 8 to 15 blocks from each player. What it hunts is up to its eyes
// (ZombieSightSystem).
[UpdateInGroup<Simulation>]
public class NightSpawnSystem : EcsSystem
{
    public override int UpdateInterval => Rules.SpawnEvery;

    [ForEach]
    void Spawn(in Player client, in Position position, in Clock clock, in Prefabs prefabs, ref Stats stats)
    {
        if (!Rules.IsNight(clock)) return;
        int seed = (int)(clock.Tick / Rules.SpawnEvery);
        int dx = 8 + Rules.Hash(client.Client, seed) % 8, dz = 8 + Rules.Hash(seed, client.Client) % 8;
        int x = (int)MathF.Floor(position.Value.X) + dx, z = (int)MathF.Floor(position.Value.Z) - dz;
        var at = new System.Numerics.Vector3(x + 0.5f, Terrain.SurfaceHeight(x, z) + 1, z + 0.5f);
        World.Instantiate(prefabs.Zombie, new Position(at), new InChunk(Terrain.ChunkOf(x, z)));
        stats.ZombiesSpawned++;
    }
}
// #endregion spawn

// #region sight
// Twice a second, a zombie looks for the nearest player within its Vision: it walks the players of the chunks its view
// reaches through the InChunk index, not every player of the world. None in sight: it has no target and stands. A
// tenth of the zombies look each tick (Slices), so a horde costs the same every tick instead of a spike twice a second.
[UpdateInGroup<Simulation>]
public class ZombieSightSystem : EcsSystem
{
    [ForEach(Slices = Rules.LoadEvery), With<Zombie>]
    void Look(in Position position, in Vision vision, ref Target target)
    {
        var p = position.Value;
        var center = Terrain.ChunkOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z));
        int reach = Rules.ChunksIn(vision.Range);
        float nearest = vision.Range * vision.Range;
        EntityId seen = default;
        for (int dz = -reach; dz <= reach; dz++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            foreach (var player in World.InChunkIndex.Find(new ChunkPos(center.X + dx, center.Z + dz)))
            {
                if (!player.HasPlayer) continue;                  // the grid holds zombies and items too
                float distance = (player.Position.Value - p).LengthSquared();
                if (distance >= nearest) continue;
                nearest = distance;
                seen = player;
            }
        }
        target = new Target(seen);
    }
}
// #endregion sight

// #region chase
// Walks a zombie toward its player and hits when close enough.
[UpdateInGroup<Simulation>]
public class ZombieChaseSystem : EcsSystem
{
    [ForEach, With<Zombie>]
    void Chase(in Position position, in Target target, ref Velocity velocity, ref Cooldown cooldown,
        ref Stats stats)
    {
        if (!World.TryGetEntity(target.Player, out var player))           // none in sight, or the player left
        {
            velocity = new Velocity(velocity.Value with { X = 0, Z = 0 });
            return;
        }
        var to = player.Position.Value - position.Value;
        var flat = new System.Numerics.Vector2(to.X, to.Z);
        float distance = flat.Length();

        var v = velocity.Value;
        if (distance > 0.1f)
        {
            flat /= distance;
            v.X = flat.X * Rules.ZombieSpeed;
            v.Z = flat.Y * Rules.ZombieSpeed;
        }
        velocity = new Velocity(v);

        if (cooldown.Ticks > 0)
        {
            cooldown = new Cooldown(cooldown.Ticks - 1);
            return;
        }
        if (distance < 1.2f && MathF.Abs(to.Y) < 1.5f)
        {
            player.Health = new Health(player.Health.Value - Rules.ZombieDamage);
            cooldown = new Cooldown(Rules.TicksPerSecond);
            stats.Hits++;
        }
    }
}
// #endregion chase

// #region sunburn
// Zombies burn in daylight: one point a tick, so a zombie lasts a second after sunrise. A batch method: the health of
// a run of zombies is one span, and Vector<int> takes away a point from 8 of them per instruction (4 on Arm); the
// JIT does not vectorize the loop by itself. Integer lanes give the same result on every machine. A batch method
// runs on one thread (EECS010): the vector loop is the speed-up.
[UpdateInGroup<Simulation>]
public class SunburnSystem : EcsSystem
{
    [ForEach, With<Zombie>]
    static void Burn(Span<Health> health, in Clock clock)
    {
        if (Rules.IsNight(clock)) return;
        var values = MemoryMarshal.Cast<Health, int>(health);
        var lanes = MemoryMarshal.Cast<int, Vector<int>>(values);
        for (int i = 0; i < lanes.Length; i++) lanes[i] -= Vector<int>.One;
        for (int i = lanes.Length * Vector<int>.Count; i < values.Length; i++) values[i]--;   // the zombies left over
    }
}
// #endregion sunburn
