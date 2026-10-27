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

// The field after the next step: the shader writes it, the settle copies it into the Scent buffer once the half of the
// chunks that stepped are done (Systems/Scent/Settle.cs), as the water ping-pongs NextFluid.
public record struct NextScent(byte Strength);

// The side a zombie on a column walks toward to follow the scent up, one byte per column beside the field
// (DynamicBuffer<Trail>): the shader writes it once the field settled, so ZombieChaseSystem reads one byte, not the
// scent and the ground of five columns. None where no scent leads anywhere; Player on a player's own column.
public record struct Trail(byte Toward)
{
    public const byte None = 0, West = 1, East = 2, North = 3, South = 4, Player = 5;
}

// Whether any zombie hunts: none by day once the night's have burned. The field sleeps then (ScentStepSystem), and
// HuntSystem wakes every chunk's field when the first zombie of a night rises.
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
public partial record struct Ground([UniqueKey] ChunkPos Pos) : IComponent, IDirectIndexed<ChunkPos>, ITile<ChunkPos>
{
    static int IDirectIndexed<ChunkPos>.Slots => 64 * 64;
    static int IDirectIndexed<ChunkPos>.SlotOf(ChunkPos pos) => (pos.Z & 63) * 64 + (pos.X & 63);
    static int ITileShape.Width => Terrain.Size;
    static int ITileShape.Depth => Terrain.Size;

    static ChunkPos ITile<ChunkPos>.Beside(ChunkPos pos, TileFaces face) => face switch
    {
        TileFaces.North => new(pos.X, pos.Z - 1),
        TileFaces.South => new(pos.X, pos.Z + 1),
        TileFaces.West => new(pos.X - 1, pos.Z),
        _ => new(pos.X + 1, pos.Z),
    };
}

// What a chunk's last step of the field did: whether any column changed, and the edges whose columns changed, which the
// chunks across them read into their halo.
public record struct ScentStep(TileFaces Edges, bool Changed) : IComponent;

// A tag on a chunk whose field may still change: the field's systems visit only these. A chunk loses it when its step
// changed nothing, and gets it back when a player steps onto another column in it (FootprintSystem), its ground changed
// in it or beside it, or the edge of a neighbor's field changed (ScentWakeSystem, the settle).
public struct Spreading : IComponent;

// The two colors of a checkerboard of chunks, by the parity of the chunk's position: no chunk touches a chunk of its
// own color across a face. The red chunks step, then the black ones from the red ones' new edges (red-black
// Gauss-Seidel), so the scent crosses two chunks per update instead of one. Every chunk has both in the prefab and loses
// one when it is generated: a presence bit, all chunks in one table.
public struct Red : IComponent;

public struct Black : IComponent;

// The column a player stands on, by chunk: an index of the players by it (Lookup<Footprint>.Find(chunk)) gives the
// shader the players of its chunk, the sources of its scent, without walking the zombies and items of InChunk.
// FootprintSystem writes it when the player steps onto another column, and wakes the chunks that lost or gained it.
public partial record struct Footprint([LookupKey] ChunkPos Chunk, byte Column) : IComponent
{
    // The footprint of a body at `p`: its chunk, and its column there (x + 16 z, the layout of the field).
    public static Footprint At(Vector3 p)
    {
        var cell = Terrain.Cell(p);
        return new(Terrain.ChunkOf(cell.X, cell.Z), (byte)((cell.Z & 15) * Terrain.Size + (cell.X & 15)));
    }
}
