using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// #region spawn
// At night, every five seconds, a zombie rises near each player: 24 to 32 blocks away on the larger axis, in a
// direction the hash picks, on top of the highest block of that column (a dug pit included) and out of the water, as
// in Minecraft, which spawns no hostile mob closer than 24 blocks. A column whose chunk is not loaded gets none. What
// a zombie hunts is up to its eyes (ZombieTargetSystem). Phase = 1: the first spawn of a night is at its first
// tick.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.SpawnEvery, Phase = 1)]
public ref struct NightSpawnSystem
{

    [ForEach]
    static void Spawn(in Player client, in Position position, in Clock clock, in Prefabs prefabs, ref Stats stats, World world)
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
        var near = new Nearby(world);
        int x = feet.X + dx, z = feet.Z + dz, top = near.Top(x, z);
        if (top < 0 || near.Water(new BlockPos(x, top + 1, z)) > 0) return;
        world.Instantiate(prefabs.Zombie, new Position(new Vector3(x + 0.5f, top + 1, z + 0.5f)),
            new InChunk(Terrain.ChunkOf(x, z)));
        stats.ZombiesSpawned++;
    }
}
// #endregion spawn

// #region target
// A zombie hunts the nearest player within its Vision, found through the index of the players' view centers (players
// only: the zombies and items of a chunk are not walked), and keeps hunting it while it stays within reach, as a
// vanilla mob keeps its target. The chunks are walked ring by ring from the zombie's own, and the walk stops at the
// first ring that cannot hold anyone nearer than the nearest found: in a crowd, one or two rings of the seven by
// seven. It looks twice a second, a tenth of the zombies each tick (Slices), so a horde costs the same every tick;
// none in reach, it has no target and stands.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ZombieTargetSystem
{
    [ForEach(Slices = Rules.LookEvery), With<Zombie>]
    static void Pick(in Position position, in InChunk at, in Vision vision, ref Target target, World world)
    {
        float nearest = vision.Range * vision.Range;
        if (world.TryGetEntity(target.Player, out var hunting)
            && (hunting.Position.Value - position.Value).LengthSquared() <= nearest) return;   // still in reach
        EntityId hunted = default;
        int reach = Rules.ChunksIn(vision.Range);
        for (int ring = 0; ring <= reach; ring++)
        {
            // A player of ring r stands r - 1 chunks away at least, one chunk less again for a view center a tick
            // behind its player.
            float gap = Math.Max(ring - 2, 0) * Terrain.Size;
            if (gap * gap >= nearest) break;
            foreach (var chunk in new ChunkRing(at.Pos, ring))
            foreach (var other in world.ViewCenterIndex.Find(chunk))
            {
                float distance = (other.Position.Value - position.Value).LengthSquared();
                if (distance >= nearest) continue;
                nearest = distance;
                hunted = other;
            }
        }
        target = new Target(hunted);
    }
}
// #endregion target

// #region chase
// Walks a zombie toward the player it hunts while the player is within its Vision (it may have walked off since the
// zombie looked); with none in sight, the player gone or too far, it stands.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ZombieChaseSystem
{
    [ForEach, With<Zombie>]
    static void Chase(in Position position, in Target target, in Vision vision, ref Velocity velocity, Lookup<Position> positions)
    {
        var v = velocity.Value;
        v.X = v.Z = 0;
        var player = positions[target.Player];                           // no value: the player is gone
        if (player.HasValue)
        {
            var to = player.Value.Value - position.Value;
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
// A zombie close to the player it hunts hits it, then waits a second: its Cooldown is the tick it may hit again, so a
// zombie that waits costs one comparison and no system counts the cooldowns down.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct ZombieAttackSystem
{
    [ForEach, With<Zombie>]
    static void Attack(in Position position, in Target target, ref Cooldown cooldown, in Clock clock, ref Stats stats, World world)
    {
        if (clock.Tick < cooldown.Until || !world.TryGetEntity(target.Player, out var player)) return;
        int health = player.Health.Value;                                   // one lookup for the read and the write
        if (health <= 0) return;
        var to = player.Position.Value - position.Value;
        if (new Vector2(to.X, to.Z).LengthSquared() >= 1.2f * 1.2f || MathF.Abs(to.Y) >= 1.5f) return;
        player.SetHealth(health - Rules.ZombieDamage);
        cooldown = new Cooldown(clock.Tick + Rules.TicksPerSecond);
        stats.Hits++;
    }
}

// #endregion attack

// #region sunlight
// A zombie under the open sky by day catches fire, as in Minecraft: no block above it and no water at its feet. It
// looks every Rules.BurnEvery ticks, the cadence of the fire's damage, so a zombie catches fire before it is first
// hurt, and not at all at night. Burning is in the zombie prefab, so catching fire flips a presence bit.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.BurnEvery)]
public ref struct SunlightSystem
{
    [ForEach, With<Zombie>]
    static void Expose(Entity zombie, in Position position, in Clock clock, World world)
    {
        if (Rules.IsNight(clock)) return;
        var feet = Terrain.Cell(position.Value);
        var near = new Nearby(world);
        if (near.Water(feet) == 0 && near.UnderSky(feet)) zombie.SetBurning(clock.Tick + Rules.BurnFor);
    }
}
// #endregion sunlight

// #region burn
// Every Rules.BurnEvery ticks the fire hurts: a batch method over the Health column of the burning zombies, 8 rows per
// instruction with Vector<int> (4 on Arm; the JIT does not vectorize the loop by itself, and integer lanes give the
// same result on every machine). The cadence is the system's ([UpdateInterval]), not a branch per tick: Health is
// tracked, and a batch method writing it stamps its whole run, so it is written only on the ticks that hurt and the
// death systems ([Changed<Health>]) find nothing to do on the others. Putting the fire out is a structural change,
// which a batch method may not make (its spans point into the table), so it is the next system's job.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.BurnEvery)]
public ref struct FireDamageSystem
{
    [ForEach, With<Burning>]
    static void Hurt(Span<Health> health)
    {
        var points = MemoryMarshal.Cast<Health, int>(health);
        var lanes = MemoryMarshal.Cast<int, Vector<int>>(points);
        for (int i = 0; i < lanes.Length; i++) lanes[i] -= Vector<int>.One;
        for (int i = lanes.Length * Vector<int>.Count; i < points.Length; i++) points[i]--;   // the rows left over
    }
}

// Puts out the fires that burned down, and those of the zombies that walked into water: it visits only the burning,
// at the fire's own cadence.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.BurnEvery)]
public ref struct ExtinguishSystem
{
    [ForEach]
    static void Out(Entity zombie, in Burning fire, in Position position, in Clock clock, World world)
    {
        if (clock.Tick >= fire.Until || new Nearby(world).Water(Terrain.Cell(position.Value)) > 0) zombie.RemoveBurning();
    }
}
// #endregion burn
