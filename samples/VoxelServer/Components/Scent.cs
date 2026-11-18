using System.Numerics;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// What the zombies follow: the scent of the players over the ground, a distance field. Each chunk holds a 16 x 16 field
// over its columns, one byte per column in a DynamicBuffer<Scent>: Rules.ScentReach where a player stands, one less per
// step a zombie can walk away from there (Footing.CanStep), 0 past the reach, the zombies' Vision. A zombie walks to the
// column beside it where the scent is stronger (ZombieChaseSystem), so it goes around a wall and a pit it could not climb
// out of, toward the nearest player along the ground, and the field gives every zombie its nearest player at once. The
// field is stored as a strength rather than a distance so that where no player is near it is 0, and a chunk nobody
// hunts in costs nothing to load, compare or skip.
public record struct Scent(byte Strength);

// Where the players stand, one byte per column beside the field (DynamicBuffer<Seed>): Rules.ScentReach under a player
// while zombies hunt, 0 elsewhere and by day. SeedSystem writes it where it changed, which wakes the field of the chunk.
public record struct Seed(byte Strength);

// The side a zombie on a column walks toward to follow the scent up, one byte per column beside the field
// (DynamicBuffer<Trail>): the shader writes it once the field settled, so ZombieChaseSystem reads one byte, not the
// scent and the ground of five columns. None where no scent leads anywhere; Player on a player's own column.
public record struct Trail(byte Toward)
{
    public const byte None = 0, West = 1, East = 2, North = 3, South = 4, Player = 5;
}

// Whether any zombie hunts: none by day once the night's have burned. The seeds go then (SeedSystem), the field fades
// to nothing and sleeps, and the first zombie of a night brings the seeds back.
public record struct Hunt(bool On) : ISingletonComponent;

// The ground of a column as a path sees it, one byte per column: the height a body stands at (the surface's top + 1, the
// low bits) and a bit for water standing there, which no path enters. FootingSystem writes it from the height map and
// the water; the shader reads it beside the scent, padded like it, so a step across the edge of a chunk is checked too.
public record struct Footing(byte Value)
{
    public const byte Wet = 0x80, Floor = 0x3F;

    // What the halo holds where no chunk is loaded: water, so no path leaves the loaded world.
    public static readonly Footing Unloaded = new(Wet);

    // Whether a zombie standing on `from` can walk onto `to`, by Minecraft's rules for a walking mob, for 16 columns at
    // once (all bits set where it can): up one block at most (a step, as PhysicsSystem lets a body climb), down
    // Rules.MaxDrop at most (a mob's path does not fall further), and not into water. Unsigned byte compares: a wet
    // column (bit 7) is higher than any stand + 1, so the first test refuses it without a test of its own.
    public static Vector128<byte> CanStep(Vector128<byte> from, Vector128<byte> to)
    {
        var stand = from & Vector128.Create(Floor);                  // standing in water, a zombie can still climb out
        return Vector128.LessThanOrEqual(to, stand + Vector128<byte>.One)
               & Vector128.LessThanOrEqual(stand, Vector128.AddSaturate(to, Vector128.Create((byte)Rules.MaxDrop)));
    }
}

// The chunk's ground as a tile of the world's grid: 16 x 16 columns, one layer, beside the Chunk tile of 16 x 16 x 32
// blocks on the same entity. A Tile<Scent, Ground> is the chunk's field with the edges of its neighbors' around it, as
// the water's Tile<Fluid, Chunk> is its water: the 2D field cannot be a tile of the 3D Chunk, whose buffers hold a cell
// per block. Direct-indexed by its position like Chunk, so the four neighbors are four array reads.
[Tile(Width = Terrain.Size, Depth = Terrain.Size)]
public partial record struct Ground([UniqueKey(SlotsPerAxis = 64)] ChunkPos Pos) : IComponent;

// The column a player stands on, by chunk: an index of the players by it (Lookup<Footprint>.Find(chunk)) gives
// SeedSystem the players of a chunk, the sources of its scent, without walking the zombies and items of InChunk.
// FootprintSystem writes it when the player steps onto another column.
public partial record struct Footprint([LookupKey] ChunkPos Chunk, byte Column) : IComponent
{
    // The footprint of a body at `p`: its chunk, and its column there (x + 16 z, the layout of the field).
    public static Footprint At(Vector3 p)
    {
        var cell = Terrain.Cell(p);
        return new(Terrain.ChunkOf(cell.X, cell.Z), (byte)((cell.Z & 15) * Terrain.Size + (cell.X & 15)));
    }
}
