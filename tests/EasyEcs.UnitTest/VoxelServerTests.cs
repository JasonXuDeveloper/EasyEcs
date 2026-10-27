using System;
using System.Numerics;
using NUnit.Framework;

namespace EasyEcs.UnitTest.Voxel;

using VoxelServer;   // inside the namespace: wins over the shared test fixtures (Position, Velocity, Health)

/// <summary>
/// The VoxelServer sample's zombies (samples/VoxelServer, Systems/Scent and ZombieChaseSystem): a zombie behind a wall,
/// or across a pit it could not climb out of, reaches a player standing still by following the scent around it; one
/// that walks the straight line to its target, as the sample's zombies did before the field, sticks at the wall or in
/// the pit. The world is the sample's own (Startup), with the bots, the night's spawns and the sun turned off, so the
/// one zombie and the one player are all that moves.
/// </summary>
public class VoxelServerTests
{
    public enum Obstacle { Wall, Pit }

    // The zombie starts this far east of the player, the obstacle between them, Reach blocks north and south of the line.
    private const int Gap = 10, Reach = 5;

    [TestCase(Obstacle.Wall)]
    [TestCase(Obstacle.Pit)]
    public void AZombieFollowsTheScentAroundIt(Obstacle obstacle)
    {
        float closest = Hunt(obstacle, straight: false);
        Assert.That(closest, Is.LessThan(1.2f), "the zombie reaches the player");
    }

    [TestCase(Obstacle.Wall)]
    [TestCase(Obstacle.Pit)]
    public void AZombieWalkingTheStraightLineSticks(Obstacle obstacle)
    {
        float closest = Hunt(obstacle, straight: true);
        Assert.That(closest, Is.GreaterThan(3f), "the zombie sticks at the obstacle");
    }

    [Test]
    public void TheScentIsTheWalkingDistanceToTheNearestPlayer()
    {
        using var regions = new RegionFiles(256);
        using var world = Setup(regions, out var feet);
        Build(world, feet, Obstacle.Wall);
        Spawn(world, new Vector3(feet.X + Gap + 0.5f, Top(feet.X + Gap, feet.Z) + 1, feet.Z + 0.5f));
        for (int i = 0; i < 40; i++) Server.Tick(world);

        // Beside the player one step less, on the other side of the wall the way around it.
        Assert.That(ScentAt(world, feet.X, feet.Z), Is.EqualTo(Rules.ScentReach));
        Assert.That(ScentAt(world, feet.X + 1, feet.Z), Is.EqualTo(Rules.ScentReach - 1));
        int around = (Reach + 1) + (Gap / 2 + 1) + (Reach + 1);     // north past the wall's end, east, back south
        Assert.That(ScentAt(world, feet.X + Gap / 2 + 1, feet.Z), Is.EqualTo(Rules.ScentReach - around));
    }

    // The closest the zombie came to the player, flat, in 400 ticks (20 seconds: the way around is about 25 blocks, at
    // 2.4 blocks a second).
    private static float Hunt(Obstacle obstacle, bool straight)
    {
        using var regions = new RegionFiles(256);
        using var world = Setup(regions, out var feet);
        if (straight)
        {
            world.System<ZombieChaseSystem>().Enabled = false;
            world.AddSystem<StraightChaseSystem>();
        }
        Build(world, feet, obstacle);
        var zombie = Spawn(world, new Vector3(feet.X + Gap + 0.5f, Top(feet.X + Gap, feet.Z) + 1, feet.Z + 0.5f));
        var player = new Vector2(feet.X + 0.5f, feet.Z + 0.5f);
        float closest = float.MaxValue;
        for (int i = 0; i < 400; i++)
        {
            Server.Tick(world);
            var at = zombie.Position.Value;
            closest = MathF.Min(closest, Vector2.Distance(new Vector2(at.X, at.Z), player));
        }
        return closest;
    }

    // The sample's world with one player standing near the west edge of a chunk with no spring in it or beside it (the
    // water would fill the pit), so the scene, wall and way around included, lies in that chunk; its chunks loaded.
    private static World Setup(RegionFiles regions, out BlockPos feet)
    {
        var world = Startup.Create(regions, Rules.CapacityFor(1), measure: false);
        world.System<FakeClientsSystem>().Enabled = false;    // the player stands still
        world.System<NightSpawnSystem>().Enabled = false;     // the test's zombie is the only one
        world.System<SunlightSystem>().Enabled = false;       // and it does not burn
        world.System<ZombieAttackSystem>().Enabled = false;   // nor kill the player, who would respawn elsewhere

        var chunk = new ChunkPos(0, 0);
        while (!Dry(chunk)) chunk = new ChunkPos(chunk.X + 1, chunk.Z);
        int x = chunk.X * Terrain.Size + 2, z = chunk.Z * Terrain.Size + 8;
        feet = new BlockPos(x, Top(x, z) + 1, z);
        var at = new Vector3(x + 0.5f, feet.Y, z + 0.5f);
        world.Instantiate(world.Prefabs.Player, new Player(0), new Position(at), new InChunk(Terrain.ChunkOf(at)), Footprint.At(at));
        for (int i = 0; i < 5; i++) Server.Tick(world);        // the chunks around the player load
        return world;
    }

    private static bool Dry(ChunkPos center)
    {
        foreach (var chunk in new ChunkSquare(center, 1))
            if (Rules.HasSpring(chunk)) return false;
        return true;
    }

    private static int Top(int x, int z) => Terrain.SurfaceHeight(x, z);

    // Between the player and the zombie, north-south across the line, from Reach blocks north to Reach south: a wall
    // three blocks high, or a pit four deep and three wide (deeper than a mob's path drops, too wide to step across).
    private static void Build(World world, BlockPos feet, Obstacle obstacle)
    {
        for (int z = feet.Z - Reach; z <= feet.Z + Reach; z++)
        for (int x = feet.X + Gap / 2 - (obstacle == Obstacle.Pit ? 1 : 0); x <= feet.X + Gap / 2 + (obstacle == Obstacle.Pit ? 1 : 0); x++)
        {
            var blocks = world.ChunkIndex.Get(Terrain.ChunkOf(x, z)).WriteBlock().AsSpan();
            int top = Top(x, z);
            if (obstacle == Obstacle.Wall)
                for (int y = top + 1; y <= top + 3; y++) blocks[Terrain.IndexOf(x, y, z)] = Block.Stone;
            else
                for (int y = top - 3; y <= top; y++) blocks[Terrain.IndexOf(x, y, z)] = Block.Air;
        }
    }

    private static Entity Spawn(World world, Vector3 at) =>
        world.Instantiate(world.Prefabs.Zombie, new Position(at), new InChunk(Terrain.ChunkOf(at)));

    private static int ScentAt(World world, int x, int z) =>
        world.GroundIndex.Get(Terrain.ChunkOf(x, z)).Scent[(z & 15) * Terrain.Size + (x & 15)].Strength;
}

/// <summary>
/// The zombies' chase before the scent: straight at the player they hunt, while within their Vision. Kept here as
/// what the field replaced, to show where it sticks. One row at a time: the generator's vector loop fails on components
/// declared in another assembly (an IndexOutOfRangeException in Kernel, which drops every generated file of this
/// project), and a test needs no vector loop.
/// </summary>
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct StraightChaseSystem
{
    [ForEach(SimdMode = SimdMode.Off), With<Zombie>]
    private static void Chase(in Position position, in Target target, in Vision vision, ref Velocity velocity, Lookup<Position> positions)
    {
        var v = velocity.Value;
        v.X = v.Z = 0;
        var player = positions[target.Player];
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
