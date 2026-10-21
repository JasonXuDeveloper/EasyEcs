using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// #region spawn
// At night, every five seconds, a zombie rises near each player: 24 to 32 blocks away on the larger axis, in a
// direction the hash picks, on top of the highest block of that column (a dug pit included) and out of the water, as
// in Minecraft, which spawns no hostile mob closer than 24 blocks. A column whose chunk is not loaded gets none. What
// a zombie hunts is up to its eyes (SightSystem, ZombieTargetSystem). UpdatePhase = 1: the first spawn of a night is
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
        var feet = Terrain.Cell(position.Value.X, position.Value.Y, position.Value.Z);
        int x = feet.X + dx, z = feet.Z + dz, top = Terrain.TopAt(World, x, z);
        if (top < 0 || Terrain.WaterAt(World, new BlockPos(x, top + 1, z)) > 0) return;
        World.Instantiate(prefabs.Zombie, new Position(new Vector3(x + 0.5f, top + 1, z + 0.5f)),
            new InChunk(Terrain.ChunkOf(x, z)));
        stats.ZombiesSpawned++;
    }
}
// #endregion spawn

// #region target
// A zombie hunts the nearest player it sees. It looks again only when what it sees changed: [Changed<T>] on its Seen
// buffer, which SightSystem writes only then. None in sight: it has no target and stands.
[UpdateInGroup<Simulation>]
public class ZombieTargetSystem : EcsSystem
{
    [ForEach, With<Zombie>, Changed<DynamicBuffer<Seen>>]
    void Pick(in Position position, in DynamicBuffer<Seen> seen, ref Target target)
    {
        float nearest = float.MaxValue;
        EntityId hunted = default;
        foreach (var entity in seen)
        {
            if (!World.TryGetEntity(entity.Entity, out var other) || !other.HasPlayer) continue;
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
// Walks a zombie toward the player it hunts; with none in sight, or the player gone, it stands.
[UpdateInGroup<Simulation>]
public class ZombieChaseSystem : EcsSystem
{
    [ForEach, With<Zombie>]
    void Chase(in Position position, in Target target, ref Velocity velocity)
    {
        var v = velocity.Value;
        v.X = v.Z = 0;
        if (World.TryGetEntity(target.Player, out var player))
        {
            var to = player.Position.Value - position.Value;
            var flat = new Vector2(to.X, to.Z);
            float distance = flat.Length();
            if (distance > 0.1f)
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
        if (cooldown.Ticks > 0 || !World.TryGetEntity(target.Player, out var player)) return;
        var to = player.Position.Value - position.Value;
        if (new Vector2(to.X, to.Z).LengthSquared() >= 1.2f * 1.2f || MathF.Abs(to.Y) >= 1.5f) return;
        player.Health = new Health(player.Health.Value - Rules.ZombieDamage);
        cooldown = new Cooldown(Rules.TicksPerSecond);
        stats.Hits++;
    }
}

// Counts every cooldown down to zero: the same arithmetic over a column, 8 per instruction.
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
        var feet = Terrain.Cell(position.Value.X, position.Value.Y, position.Value.Z);
        if (Terrain.WaterAt(World, feet) > 0)
        {
            if (zombie.HasBurning) zombie.RemoveBurning();
            return;
        }
        if (!Rules.IsNight(clock) && Terrain.UnderSky(World, feet)) zombie.SetBurning(Rules.BurnFor);
    }
}
// #endregion sunlight

// #region burn
// Fire hurts every Rules.BurnEvery ticks and burns down by one tick. A batch method: the fire and the health of a run
// of burning entities are two spans, and Vector<int> works on 8 of them per instruction (4 on Arm); the JIT does not
// vectorize the loop by itself. Integer lanes give the same result on every machine. A batch method runs on one
// thread (EECS010): the vector loop is the speed-up. Putting the fire out is a structural change, which a batch
// method may not make (its spans point into the table), so it is the next system's job.
[UpdateInGroup<Simulation>]
public class BurnSystem : EcsSystem
{
    [ForEach]
    static void Burn(Span<Burning> fire, Span<Health> health, in Clock clock)
    {
        var ticks = MemoryMarshal.Cast<Burning, int>(fire);
        var points = MemoryMarshal.Cast<Health, int>(health);
        int damage = clock.Tick % Rules.BurnEvery == 0 ? 1 : 0;
        var fireLanes = MemoryMarshal.Cast<int, Vector<int>>(ticks);
        var healthLanes = MemoryMarshal.Cast<int, Vector<int>>(points);
        var hurt = new Vector<int>(damage);
        for (int i = 0; i < fireLanes.Length; i++)
        {
            fireLanes[i] -= Vector<int>.One;
            healthLanes[i] -= hurt;
        }
        for (int i = fireLanes.Length * Vector<int>.Count; i < ticks.Length; i++)     // the rows left over
        {
            ticks[i]--;
            points[i] -= damage;
        }
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
