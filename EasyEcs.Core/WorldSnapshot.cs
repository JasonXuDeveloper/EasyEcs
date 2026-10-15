namespace EasyEcs;

/// <summary>
/// A copy of a world's entities, components and singletons, taken by <see cref="World.Capture"/> and put back by
/// <see cref="World.Restore"/> (systems and options are not part of it). Holds the copies only; it can be restored any
/// number of times, into the world it was taken from.
/// </summary>
public sealed class WorldSnapshot
{
    internal WorldSnapshot(World world, EntityMeta[] meta, int nextId, int[] freeIds, int entityCount,
        Archetype.Rows[] tables, SingletonBox[] singletons)
    {
        World = world;
        Meta = meta;
        NextId = nextId;
        FreeIds = freeIds;
        EntityCount = entityCount;
        Tables = tables;
        Singletons = singletons;
    }

    internal readonly World World;
    internal readonly EntityMeta[] Meta;
    internal readonly int NextId;
    internal readonly int[] FreeIds;
    internal readonly Archetype.Rows[] Tables;
    internal readonly SingletonBox[] Singletons;

    /// <summary>Alive entities in the snapshot.</summary>
    public int EntityCount { get; }
}
