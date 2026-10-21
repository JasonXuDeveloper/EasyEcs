using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// #region spawn
// At night, every five seconds, a zombie rises near each player: 24 to 32 blocks away on the larger axis, in a
// direction the hash picks, on top of the highest block of that column (a dug pit included) and out of the water, as
// in Minecraft, which spawns no hostile mob closer than 24 blocks. A column whose chunk is not loaded gets none. What
// a zombie hunts is up to its eyes (ZombieTargetSystem). UpdatePhase = 1: the first spawn of a night is
// at its first tick.
[UpdateInGroup<Simulation>]
public class NightSpawnSystem : EcsSystem
{
    public override int UpdateInterval => Rules.SpawnEvery;
    public override int UpdatePhase => 1;

    [ForEach]
    void Spawn(in Player client, in Position position, in Clock clock, in Prefabs prefabs, ref Stats stats)
    {
        if (!Rules.IsNight(clock)) return;
        int seed = (int)(clock.Tick / Rules.SpawnEvery);
        int span = 2 * Rules.SpawnMax + 1;
        int dx = Rules.Hash(client.Client, seed) % span - Rules.SpawnMax;
        int dz = Rules.Hash(seed, client.Client) % span - Rules.SpawnMax;
        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) < Rules.SpawnMin)              // too close: out to the ring
        {
            if (Math.Abs(dx) >= Math.Abs(dz)) dx = dx < 0 ? -Rules.SpawnMin : Rules.SpawnMin;
            else dz = dz < 0 ? -Rules.SpawnMin : Rules.SpawnMin;
        }
        var feet = Terrain.Cell(position.Value);
        var near = new Nearby(World);
        int x = feet.X + dx, z = feet.Z + dz, top = near.Top(x, z);
        if (top < 0 || near.Water(new BlockPos(x, top + 1, z)) > 0) return;
        World.Instantiate(prefabs.Zombie, new Position(new Vector3(x + 0.5f, top + 1, z + 0.5f)),
            new InChunk(Terrain.ChunkOf(x, z)));
        stats.ZombiesSpawned++;
    }
}
// #endregion spawn

// #region target
// A zombie hunts the nearest player within its Vision, found through the grid, and keeps hunting it while it stays
// within reach, as a vanilla mob keeps its target. It looks twice a second, a tenth of the zombies each tick (Slices),
// so a horde costs the same every tick; none in reach, it has no target and stands.
[UpdateInGroup<Simulation>]
public class ZombieTargetSystem : EcsSystem
{
    [ForEach(Slices = Rules.LookEvery), With<Zombie>]
    void Pick(in Position position, in InChunk at, in Vision vision, ref Target target)
    {
        float nearest = vision.Range * vision.Range;
        if (World.TryGetEntity(target.Player, out var hunting)
            && (hunting.Position.Value - position.Value).LengthSquared() <= nearest) return;   // still in reach
        EntityId hunted = default;
        foreach (var chunk in Terrain.ChunksAround(at.Pos, vision.Range))
        foreach (var other in World.InChunkIndex.Find(chunk))
        {
            if (!other.HasPlayer) continue;
            float distance = (other.Position.Value - position.Value).LengthSquared();
            if (distance >= nearest) continue;
            nearest = distance;
            hunted = other;
        }
        target = new Target(hunted);
    }
}
// #endregion target

// #region chase
// Walks a zombie toward the player it hunts while the player is within its Vision (it may have walked off since the
// zombie looked); with none in sight, the player gone or too far, it stands.
[UpdateInGroup<Simulation>]
public class ZombieChaseSystem : EcsSystem
{
    [ForEach, With<Zombie>]
    void Chase(in Position position, in Target target, in Vision vision, ref Velocity velocity)
    {
        var v = velocity.Value;
        v.X = v.Z = 0;
        if (World.TryGetEntity(target.Player, out var player))
        {
            var to = player.Position.Value - position.Value;
            var flat = new Vector2(to.X, to.Z);
            float distance = flat.Length();
            if (distance > 0.1f && distance <= vision.Range)
            {
                flat /= distance;
                v.X = flat.X * Rules.ZombieSpeed;
                v.Z = flat.Y * Rules.ZombieSpeed;
            }
        }
        velocity = new Velocity(v);
    }
}
// #endregion chase

// #region attack
// A zombie close to the player it hunts hits it, then waits a second. The cooldown counts down in its own system
// (CooldownSystem), whatever the zombie does meanwhile.
[UpdateInGroup<Simulation>]
public class ZombieAttackSystem : EcsSystem
{
    [ForEach, With<Zombie>]
    void Attack(in Position position, in Target target, ref Cooldown cooldown, ref Stats stats)
    {
        if (cooldown.Ticks > 0 || !World.TryGetEntity(target.Player, out var player) || player.Health.Value <= 0) return;
        var to = player.Position.Value - position.Value;
        if (new Vector2(to.X, to.Z).LengthSquared() >= 1.2f * 1.2f || MathF.Abs(to.Y) >= 1.5f) return;
        player.SetHealth(player.Health.Value - Rules.ZombieDamage);
        cooldown = new Cooldown(Rules.TicksPerSecond);
        stats.Hits++;
    }
}

// Counts every cooldown down to zero: the same arithmetic over a column, 8 per instruction (4 on Arm).
[UpdateInGroup<Simulation>]
public class CooldownSystem : EcsSystem
{
    [ForEach]
    static void Tick(Span<Cooldown> cooldowns)
    {
        var values = MemoryMarshal.Cast<Cooldown, int>(cooldowns);
        var lanes = MemoryMarshal.Cast<int, Vector<int>>(values);
        for (int i = 0; i < lanes.Length; i++) lanes[i] = Vector.Max(lanes[i] - Vector<int>.One, Vector<int>.Zero);
        for (int i = lanes.Length * Vector<int>.Count; i < values.Length; i++) values[i] = Math.Max(values[i] - 1, 0);
    }
}
// #endregion attack

// #region sunlight
// A zombie under the open sky by day catches fire, as in Minecraft: no block above it and no water around its feet.
// The fire goes on for Rules.BurnFor ticks after the zombie found shade; water puts it out.
[UpdateInGroup<Simulation>]
public class SunlightSystem : EcsSystem
{
    [ForEach, With<Zombie>]
    void Expose(Entity zombie, in Position position, in Clock clock)
    {
        var feet = Terrain.Cell(position.Value);
        var near = new Nearby(World);
        if (near.Water(feet) > 0)
        {
            if (zombie.HasBurning) zombie.RemoveBurning();
            return;
        }
        if (!Rules.IsNight(clock) && near.UnderSky(feet)) zombie.SetBurning(Rules.BurnFor);
    }
}
// #endregion sunlight

// #region burn
// Fire burns down by one tick, and every Rules.BurnEvery ticks it hurts: two systems, each a batch method over one
// column, 8 rows per instruction with Vector<int> (4 on Arm; the JIT does not vectorize the loops by itself, and
// integer lanes give the same result on every machine). The hurt has its own system so that the cadence is the
// system's (UpdateInterval), not a branch per tick: Health is tracked, and a batch method writing it stamps its whole
// run, so it is written only on the ticks that hurt and the death systems ([Changed<Health>]) find nothing to do on
// the others. Putting the fire out is a structural change, which a batch method may not make (its spans point into
// the table), so it is the next system's job.
[UpdateInGroup<Simulation>]
public class BurnSystem : EcsSystem
{
    [ForEach]
    static void Burn(Span<Burning> fire)
    {
        var ticks = MemoryMarshal.Cast<Burning, int>(fire);
        var lanes = MemoryMarshal.Cast<int, Vector<int>>(ticks);
        for (int i = 0; i < lanes.Length; i++) lanes[i] -= Vector<int>.One;
        for (int i = lanes.Length * Vector<int>.Count; i < ticks.Length; i++) ticks[i]--;   // the rows left over
    }
}

[UpdateInGroup<Simulation>]
public class FireDamageSystem : EcsSystem
{
    public override int UpdateInterval => Rules.BurnEvery;

    [ForEach, With<Burning>]
    static void Hurt(Span<Health> health)
    {
        var points = MemoryMarshal.Cast<Health, int>(health);
        var lanes = MemoryMarshal.Cast<int, Vector<int>>(points);
        for (int i = 0; i < lanes.Length; i++) lanes[i] -= Vector<int>.One;
        for (int i = lanes.Length * Vector<int>.Count; i < points.Length; i++) points[i]--;
    }
}

// Puts out the fires that burned down.
[UpdateInGroup<Simulation>]
public class ExtinguishSystem : EcsSystem
{
    [ForEach]
    static void Out(Entity entity, in Burning fire)
    {
        if (fire.Ticks <= 0) entity.RemoveBurning();
    }
}
// #endregion burn
