using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// Everything a world stores per entity id, in one 16-byte record (4 per 64-byte cache line): a version check, a
/// component lookup or a structural change touches a single line. The entity's component set is its archetype's
/// mask, so it is not duplicated per entity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EntityMeta
{
    /// <summary>Archetype the entity lives in; null means the id is free.</summary>
    public Archetype Archetype;

    /// <summary>Incremented on destroy; handles carry the version they were created with.</summary>
    public int Version;

    /// <summary>Slot index inside <see cref="Archetype"/>.EntityIds.</summary>
    public int Slot;
}
