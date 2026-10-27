using System;

namespace EasyEcs.Benchmark.Locality;

/// <summary>Where the players stand.</summary>
public enum Layout
{
    /// <summary>The sample's spawn: a grid 16 wide, 3 blocks apart (Rules.SpawnPoint), a crowd.</summary>
    Packed,

    /// <summary>Uniform over a square of 64 * sqrt(players) blocks: players apart.</summary>
    Spread,
}

/// <summary>
/// The players a nearest search runs over, sorted into clusters (<see cref="SortedClusters"/>): their ids and the tie key
/// (the entity index) in sorted order. Prototype side arrays, as <see cref="ClusterRows"/>.
/// </summary>
public sealed class PlayerRows
{
    public readonly SortedClusters Clusters = new();
    public EntityId[] Ids;
    public int[] TieKeys;
    public float[] SourceX, SourceY;
    public EntityId[] SourceIds;

    public void Reserve(int capacity)
    {
        Clusters.Reserve(capacity);
        Ids = new EntityId[capacity];
        TieKeys = new int[capacity];
        SourceX = new float[capacity];
        SourceY = new float[capacity];
        SourceIds = new EntityId[capacity];
    }

    public void Sort(int count, float extent)
    {
        Clusters.Sort(SourceX.AsSpan(0, count), SourceY.AsSpan(0, count), extent);
        for (int s = 0; s < count; s++)
        {
            Ids[s] = SourceIds[Clusters.Order[s]];
            TieKeys[s] = Ids[s].Index;
        }
    }
}

/// <summary>
/// The shape of samples/VoxelServer's <c>ZombieTargetSystem.Pick</c> (locality.md 4.2): each zombie finds the nearest
/// player within its vision. The row loop as the sample writes it (the players' chunks through a value index, walked
/// ring by ring from the zombie's chunk, stopping at the first ring that cannot hold anyone nearer; each candidate's
/// position through a lookup) against a search of the players' sorted clusters. 2D (x, z): the sample's distance adds y.
/// The keep-the-target shortcut of the sample is left out: every zombie searches.
/// <para>
/// Ties: the ring walk keeps the first player found at the smallest distance; the cluster search the one with the
/// smallest entity index (its order does not follow rings). Both find the same distance; the ids differ only on ties.
/// </para>
/// </summary>
public static partial class ZombieHunt
{
    public const int ChunkSize = 16;
    public const float Vision = 35;      // Rules.ZombieVision

    public record struct Position(float X, float Z) : IComponent;
    public record struct Player : IComponent;
    public record struct Zombie : IComponent;
    public record struct Sight(float Range) : IComponent;
    public record struct Target(EntityId Player) : IComponent;
    public record struct ChunkKey(int X, int Z);

    /// <summary>The player's chunk, indexed: the sample's ViewCenter (here current, so a ring r player is r - 1 chunks away at least).</summary>
    public partial record struct PlayerChunk([LookupKey] ChunkKey At) : IComponent;

    public record struct Map(float Extent) : ISingletonComponent;

    [NotSaved]
    public struct Players : ISingletonComponent
    {
        public PlayerRows Rows;
    }

    private static readonly QueryFilter PlayersOnly = QueryFilter.Create().With<Player>();

    public static World Create(int players, Layout layout, int seed = 42)
    {
        int zombies = 3 * players;
        var world = new World(new WorldOptions { InitialEntityCapacity = players + zombies + 16 });
        var random = new Random(seed);
        float extent = layout == Layout.Packed ? Math.Max(48, (players + 15) / 16 * 3) : 64 * MathF.Sqrt(players);
        world.AddSingleton<Map>() = new Map(extent);
        var rows = new PlayerRows();
        rows.Reserve(players);
        world.AddSingleton<Players>().Rows = rows;

        var spots = new Position[players];
        var all = new Entity[players];
        world.CreateEntities(all, new Position(), new Player(), new PlayerChunk());
        for (int i = 0; i < players; i++)
        {
            spots[i] = layout == Layout.Packed
                ? new Position(i % 16 * 3 + 0.5f, i / 16 * 3 + 0.5f)
                : new Position(random.NextSingle() * extent, random.NextSingle() * extent);
            all[i].Get<Position>() = spots[i];
            all[i].Set(new PlayerChunk(ChunkOf(spots[i])));
        }

        // Zombies rise 24 to 32 blocks from a player on the larger axis, as NightSpawnSystem places them.
        var hunters = new Entity[zombies];
        world.CreateEntities(hunters, new Position(), new Zombie(), new Sight(Vision), new Target());
        for (int i = 0; i < zombies; i++)
        {
            var near = spots[i % players];
            int dx = random.Next(-32, 33), dz = random.Next(-32, 33);
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) < 24)
            {
                if (Math.Abs(dx) >= Math.Abs(dz)) dx = dx < 0 ? -24 : 24;
                else dz = dz < 0 ? -24 : 24;
            }

            hunters[i].Get<Position>() = new Position(MathF.Floor(near.X) + dx + 0.5f, MathF.Floor(near.Z) + dz + 0.5f);
        }

        return world;
    }

    public static ChunkKey ChunkOf(Position p) => new((int)MathF.Floor(p.X) >> 4, (int)MathF.Floor(p.Z) >> 4);

    // #region ring-walk
    [ForEach(SimdMode = SimdMode.Off), With<Zombie>]
    public static void Pick(in Position position, in Sight sight, ref Target target, Lookup<PlayerChunk> chunks,
        Lookup<Position> positions)
    {
        float nearest = sight.Range * sight.Range;
        EntityId hunted = default;
        var at = ChunkOf(position);
        int reach = (int)MathF.Ceiling(sight.Range / ChunkSize);
        for (int ring = 0; ring <= reach; ring++)
        {
            float gap = Math.Max(ring - 1, 0) * ChunkSize;      // a player of ring r stands r - 1 chunks away at least
            if (gap * gap >= nearest) break;
            for (int i = 0, chunks8 = Math.Max(8 * ring, 1); i < chunks8; i++)    // the ring's perimeter, each chunk once
            foreach (var other in chunks.Find(OnRing(at, ring, i)))
            {
                ref readonly var q = ref positions.Read(other.Id);
                float dx = q.X - position.X, dz = q.Z - position.Z, distance = dx * dx + dz * dz;
                if (distance >= nearest) continue;
                nearest = distance;
                hunted = other.Id;
            }
        }

        target = new Target(hunted);
    }

    // Chunk i of ring r around a center: the four sides of 2r chunks each, corners once.
    private static ChunkKey OnRing(ChunkKey at, int ring, int i)
    {
        if (ring == 0) return at;
        int side = 2 * ring, offset = i % side;
        return (i / side) switch
        {
            0 => new ChunkKey(at.X - ring + offset, at.Z - ring),
            1 => new ChunkKey(at.X + ring, at.Z - ring + offset),
            2 => new ChunkKey(at.X + ring - offset, at.Z + ring),
            _ => new ChunkKey(at.X - ring, at.Z + ring - offset),
        };
    }
    // #endregion ring-walk

    // #region clusters
    /// <summary>The players copied out of the table and sorted into clusters.</summary>
    public static void SortPlayers(World world)
    {
        var rows = world.GetSingleton<Players>().Rows;
        int k = 0;
        foreach (var item in world.Query<Position>(PlayersOnly))
        {
            rows.SourceX[k] = item.Component1.X;
            rows.SourceY[k] = item.Component1.Z;
            rows.SourceIds[k++] = item.Entity.Id;
        }

        rows.Sort(k, world.GetSingleton<Map>().Extent);
    }

    [ForEach(SimdMode = SimdMode.Off), With<Zombie>]
    public static void PickInClusters(in Position position, in Sight sight, ref Target target, in Players players)
    {
        var rows = players.Rows;
        int slot = rows.Clusters.Nearest(position.X, position.Z, sight.Range * sight.Range, rows.TieKeys);
        target = new Target(slot < 0 ? default : rows.Ids[slot]);
    }
    // #endregion clusters

    /// <summary>Each zombie's target and the squared distance to it (-1: none), in table order.</summary>
    public static (EntityId Target, float Distance)[] Targets(World world)
    {
        var all = new (EntityId, float)[world.Count<Zombie>()];
        int k = 0;
        foreach (var item in world.Query<Position, Target>())
        {
            var target = item.Component2.Player;
            float distance = -1;
            if (world.TryGetEntity(target, out var player))
            {
                var q = player.Read<Position>();
                float dx = q.X - item.Component1.X, dz = q.Z - item.Component1.Z;
                distance = dx * dx + dz * dz;
            }

            all[k++] = (target, distance);
        }

        return all;
    }
}

/// <summary>
/// The shape of BattleServer's <c>NearestPlayer</c> (locality.md 4.2): the nearest player to a point, no range. The
/// sample's linear scan of the players' positions against a search of their sorted clusters (with and without the sort).
/// Ties as <see cref="ZombieHunt"/>: the scan keeps the first in table order, the clusters the smallest entity index.
/// </summary>
public static class PlayerSearch
{
    public record struct Position(float X, float Y) : IComponent;
    public record struct Player : IComponent;

    [NotSaved]
    public struct Players : ISingletonComponent
    {
        public PlayerRows Rows;
    }

    public const float Extent = 4096;

    private static readonly QueryFilter PlayersOnly = QueryFilter.Create().With<Player>();

    /// <summary>BattleServer's 100,000 units: a third players, all at integer spots of a 4096 x 4096 map.</summary>
    public static World Create(int units, int seed = 42)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = units + 16 });
        var random = new Random(seed);
        int players = units / 3;
        var rows = new PlayerRows();
        rows.Reserve(players);
        world.AddSingleton<Players>().Rows = rows;
        var all = new Entity[players];
        world.CreateEntities(all, new Position(), new Player());
        foreach (var e in all) e.Get<Position>() = new Position(random.Next(4096), random.Next(4096));
        var npcs = new Entity[units - players];
        world.CreateEntities(npcs, new Position());
        foreach (var e in npcs) e.Get<Position>() = new Position(random.Next(4096), random.Next(4096));
        return world;
    }

    /// <summary>The sample's scan.</summary>
    public static Entity Scan(World world, Position at)
    {
        Entity nearest = default;
        float best = float.MaxValue;
        foreach (var item in world.Query<Position>(PlayersOnly))
        {
            float dx = item.Component1.X - at.X, dy = item.Component1.Y - at.Y, distance = dx * dx + dy * dy;
            if (distance < best) (best, nearest) = (distance, item.Entity);
        }

        return nearest;
    }

    public static void Sort(World world)
    {
        var rows = world.GetSingleton<Players>().Rows;
        int k = 0;
        foreach (var item in world.Query<Position>(PlayersOnly))
        {
            rows.SourceX[k] = item.Component1.X;
            rows.SourceY[k] = item.Component1.Y;
            rows.SourceIds[k++] = item.Entity.Id;
        }

        rows.Sort(k, Extent);
    }

    /// <summary>The search of the sorted clusters; the id of the nearest player (default when there is none).</summary>
    public static EntityId Search(World world, Position at)
    {
        var rows = world.GetSingleton<Players>().Rows;
        int slot = rows.Clusters.Nearest(at.X, at.Y, float.MaxValue, rows.TieKeys);
        return slot < 0 ? default : rows.Ids[slot];
    }

    public static float DistanceTo(World world, EntityId player, Position at)
    {
        var q = world.GetEntity(player).Read<Position>();
        float dx = q.X - at.X, dy = q.Y - at.Y;
        return dx * dx + dy * dy;
    }
}
