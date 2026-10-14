using System.Runtime.InteropServices;

namespace EasyEcs.Core;

/// <summary>
/// Per-entity bookkeeping packed into one 16-byte record (4 per 64-byte cache line), so a structural change
/// or a version check touches a single cache line instead of one line per parallel array.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EntityMeta
{
    /// <summary>Archetype the entity lives in; null means the id is free (entity not alive).</summary>
    public Archetype Archetype;

    /// <summary>Incremented on destroy; refs carry the version they were created with.</summary>
    public int Version;

    /// <summary>Slot index inside <see cref="Archetype"/>.EntityIds.</summary>
    public int Slot;
}
