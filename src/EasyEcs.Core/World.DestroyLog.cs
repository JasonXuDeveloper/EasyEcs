using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

public sealed partial class World
{
    // The destroy log (WorldOptions.DestroyLog): a ring of the last destroyed entities with the change tick of their
    // destruction, oldest overwritten first. Null when off, so a destroy pays one field check and nothing else. Owner
    // thread, like the entity records it follows: written only inside the structural gate, which no thread of a parallel
    // stage takes for a destroy (frozen), so readers on those threads see it still.
    private DestroyRecord[] _destroyLog;
    private int _destroyNext;  // where the next record goes
    private int _destroyCount; // records held (the capacity once the ring is full)

    // The oldest `since` for which the log is complete: one past the tick of the newest record overwritten. Ticks of
    // records only grow, so every record with a tick of at least this one is still in the ring.
    private ulong _destroyLogFrom;

    /// <summary>One destroyed entity of the log: 16 bytes, four per cache line.</summary>
    private readonly struct DestroyRecord(EntityId id, ulong tick)
    {
        public readonly EntityId Id = id;
        public readonly ulong Tick = tick;
    }

    /// <summary>
    /// The oldest tick <see cref="DestroyedSince"/> can answer for: it reports every entity destroyed at a change tick of
    /// <c>since</c> or later for any <c>since</c> of at least this one, and returns false for an older one (the records
    /// were overwritten). 0 until the ring of <see cref="WorldOptions.DestroyLog"/> first overwrites a record;
    /// <see cref="ulong.MaxValue"/> when the log is off. O(1).
    /// </summary>
    public ulong DestroyLogOldestTick => _destroyLog == null ? ulong.MaxValue : _destroyLogFrom;

    /// <summary>
    /// The entities destroyed at a change tick of <paramref name="since"/> or later (the rule of
    /// <c>QueryFilter.Since</c>: take <see cref="ChangeTick"/> as a mark, pass it later), from the log of
    /// <see cref="WorldOptions.DestroyLog"/>. Each reader keeps its own mark, so readers that acknowledged different
    /// ticks share one log. <paramref name="count"/> is how many there are; the first
    /// <c>min(count, into.Length)</c> are written to <paramref name="into"/>, oldest first, each with the version it had
    /// (a recycled index names the new entity with another version). A span of <see cref="WorldOptions.DestroyLog"/>
    /// ids always holds them all.
    /// <br/>
    /// An entity counts as destroyed when its id stops being alive (<see cref="IsAlive"/> turns false): at
    /// <c>Destroy</c>, <c>DestroyEntities</c>, a hierarchy or relation cascade, a <c>MoveEntities</c> out of this world;
    /// an entity holding <see cref="ICleanupComponent"/>s when its last one is removed (until then it is alive, tagged
    /// <see cref="Destroyed"/>); a <see cref="Restore(WorldSnapshot)"/> or <c>Load</c> for every entity alive before it and not
    /// after it (with the same version). <see cref="Dispose"/> records nothing.
    /// <br/>
    /// Returns false, writing nothing and <paramref name="count"/> 0, when <paramref name="since"/> is older than
    /// <see cref="DestroyLogOldestTick"/>: records the reader needs were overwritten, and it must resynchronize (send the
    /// whole state again). O(count). Allocates nothing. Owner thread, or a thread of a parallel stage.
    /// </summary>
    /// <exception cref="InvalidOperationException">The log is off (<see cref="WorldOptions.DestroyLog"/> is 0).</exception>
    /// <exception cref="ObjectDisposedException">The world was disposed.</exception>
    public bool DestroyedSince(ulong since, Span<EntityId> into, out int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        var log = _destroyLog;
        if (log == null) ThrowNoDestroyLog();
        count = 0;
        if (since < _destroyLogFrom) return false;

        // From the newest record back to the first one older than since (records are in tick order).
        int at = _destroyNext, held = _destroyCount, found = 0;
        while (found < held)
        {
            at = at == 0 ? log!.Length - 1 : at - 1;
            if (log![at].Tick < since) break;
            found++;
        }

        count = found;
        int start = _destroyNext - found;
        if (start < 0) start += log!.Length;
        int written = Math.Min(found, into.Length);
        for (int i = 0, slot = start; i < written; i++)
        {
            into[i] = log[slot].Id;
            if (++slot == log.Length) slot = 0;
        }

        return true;
    }

    /// <summary>Record <paramref name="entity"/> (the incarnation that dies) at the current change tick. Inside the structural gate, log on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LogDestroy(DestroyRecord[] log, EntityId entity)
    {
        int at = _destroyNext;
        ref var slot = ref log[at];
        if (_destroyCount == log.Length) _destroyLogFrom = slot.Tick + 1; // the oldest record goes
        else _destroyCount++;
        slot = new DestroyRecord(entity, _changeTick);
        _destroyNext = at + 1 == log.Length ? 0 : at + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNoDestroyLog() =>
        throw new InvalidOperationException("The world keeps no destroy log: set WorldOptions.DestroyLog to its capacity");
}
