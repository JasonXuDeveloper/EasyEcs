namespace EasyEcs;

/// <summary>
/// A copy of a world's entities, components and singletons, taken by <see cref="World.Capture()"/> and put back by
/// <see cref="World.Restore"/> (systems and options are not part of it). Holds the copies only; it can be restored any
/// number of times, into the world it was taken from. <see cref="World.Capture(WorldSnapshot)"/> overwrites it,
/// reusing its arrays (no allocation once they are large enough).
/// </summary>
public sealed class WorldSnapshot
{
    internal WorldSnapshot(World world) => World = world;

    internal readonly World World;
    internal EntityMeta[] Meta = [];
    internal int NextId;           // the entity records in use: Meta[..NextId]
    internal int[] FreeIds = [];
    internal int FreeCount;        // FreeIds[..FreeCount]
    internal Archetype.Rows[] Tables = [];
    internal int TableCount;       // Tables[..TableCount], by the world's table order
    internal SingletonBox[] Singletons = [];

    /// <summary>Alive entities in the snapshot.</summary>
    public int EntityCount { get; internal set; }
}
