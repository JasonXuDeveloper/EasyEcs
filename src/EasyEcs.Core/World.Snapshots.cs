using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

// Snapshots: copies of the entity records, every table's rows and the singletons, to roll a world back (prediction,
// replays, tests, a turn that must be undone). Both directions are O(entities × components); nothing is shared with
// the world, so the world keeps changing freely after a capture.
public sealed partial class World
{
    /// <summary>
    /// Copy the entities, their components and the singletons. O(entities × components + archetypes + singletons).
    /// Components and singletons are copied by value: a managed reference inside one is shared, not cloned. Not
    /// while a pass iterates.
    /// </summary>
    public WorldSnapshot Capture() => Capture(null);

    /// <summary>
    /// <see cref="Capture()"/> into <paramref name="into"/> (a snapshot of this world, or null for a new one),
    /// overwriting it and reusing its arrays: capturing every tick (rollback, replays) allocates nothing once they are
    /// large enough. Returns the snapshot.
    /// </summary>
    public WorldSnapshot Capture(WorldSnapshot into)
    {
        if (into != null && !ReferenceEquals(into.World, this))
            throw new ArgumentException("The snapshot was taken from another world", nameof(into));
        using (Structural())
        {
            ThrowIfIterating(nameof(Capture));
            FillDirtyTables(); // left by a pass an exception interrupted: tables are dense from here
            var snapshot = into ?? NewSnapshot();
            Fit(ref snapshot.Meta, _nextId);
            Meta.AsSpan(0, _nextId).CopyTo(snapshot.Meta);
            snapshot.NextId = _nextId;
            snapshot.TypesRemoved = TypesRemoved;
            Fit(ref snapshot.FreeIds, _freeIdCount);
            _freeIds.AsSpan(0, _freeIdCount).CopyTo(snapshot.FreeIds);
            snapshot.FreeCount = _freeIdCount;
            snapshot.EntityCount = _entityCount;

            var tables = _allArchetypes;
            if (snapshot.Tables.Length < tables.Length) Array.Resize(ref snapshot.Tables, tables.Length);
            for (int i = 0; i < tables.Length; i++) snapshot.Tables[i] = tables[i].Capture(snapshot.Tables[i]);
            snapshot.TableCount = tables.Length;

            if (snapshot.Singletons.Length < _singletons.Length) Array.Resize(ref snapshot.Singletons, _singletons.Length);
            for (int i = 0; i < snapshot.Singletons.Length; i++)
            {
                var box = i < _singletons.Length ? _singletons[i] as SingletonBox : null;
                ref var copy = ref snapshot.Singletons[i];
                if (box == null) copy = null;
                else if (copy != null && copy.GetType() == box.GetType()) copy.CopyFrom(box);
                else copy = box.Clone();
            }

            return snapshot;
        }
    }

    /// <summary>Make <paramref name="array"/> hold at least <paramref name="length"/> elements (grown, never shrunk).</summary>
    private static void Fit<T>(ref T[] array, int length)
    {
        if (array.Length < length) array = new T[Math.Max(length, array.Length * 2)];
    }

    /// <summary>
    /// Put the world back to <paramref name="snapshot"/> (taken from this world). Handles of entities alive in the
    /// snapshot are valid again; every other handle is stale, also handles of entities created after the capture.
    /// Archetypes created after the capture stay, empty. Singletons keep their boxes (a <see cref="SingletonRef{T}"/>
    /// still works) and get the snapshot's values; singletons added after the capture are removed.
    /// O(entities × components + archetypes + singletons). Not while a pass iterates.
    /// </summary>
    public void Restore(WorldSnapshot snapshot) => Restore(snapshot, false, true, default, nameof(Restore));

    // swap: the snapshot is Load's own, its buffer lists move into the world and the world's old ones into it.
    // markWritten: every restored row counts as written now (Changed<T> sees it), else as never written.
    // keep: the singletons left as the world has them (Load's keep set).
    // call: the public call, named when a pass refuses it.
    private void Restore(WorldSnapshot snapshot, bool swap, bool markWritten, in ComponentSet keep, string call)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.World, this))
            throw new ArgumentException("The snapshot was taken from another world", nameof(snapshot));
        if (!swap && snapshot.TypesRemoved != TypesRemoved) // Load's own snapshot is built from the tables of now
            throw new InvalidOperationException("The snapshot was taken before RemoveTypes dropped some of its tables");

        using (Structural())
        {
            ThrowIfIterating(call);
            // Entity records. Versions only grow: a recorded id keeps the snapshot's version if its entity was alive
            // then (its old handles work again), else it moves past every version it ever had, so no handle made after
            // the capture can match a later incarnation.
            var saved = snapshot.Meta.AsSpan(0, snapshot.NextId);
            if (Meta.Length < saved.Length) Grow(swap ? Math.Max(saved.Length, Meta.Length * 2) : saved.Length, true);
            int used = Math.Max(_nextId, snapshot.NextId);
            var log = _destroyLog;
            for (int id = 0; id < used; id++)
            {
                ref var meta = ref Meta[id];
                // An entity alive now that the snapshot does not hold (that incarnation) is destroyed by the restore.
                if (log != null && meta.Archetype != null && (id >= saved.Length || saved[id].Archetype == null || saved[id].Version != meta.Version))
                    LogDestroy(log, new EntityId(id, meta.Version));
                if (id < saved.Length && saved[id].Archetype != null) meta = saved[id];
                else meta = new EntityMeta { Version = Math.Max(meta.Version, id < saved.Length ? saved[id].Version : 0) + 1 };
            }

            _nextId = used;
            // Ids recorded free, then ids first used after the capture; LIFO takes the recorded ones first.
            int free = snapshot.FreeCount + (used - snapshot.NextId);
            if (_freeIds.Length < free) _freeIds = new int[swap ? Math.Max(free, _freeIds.Length * 2) : free];
            for (int id = used - 1, i = 0; id >= snapshot.NextId; id--, i++) _freeIds[i] = id;
            snapshot.FreeIds.AsSpan(0, snapshot.FreeCount).CopyTo(_freeIds.AsSpan(used - snapshot.NextId));
            _freeIdCount = free;
            _entityCount = snapshot.EntityCount;

            var tables = _allArchetypes;
            for (int i = 0; i < tables.Length; i++)
            {
                tables[i].Restore(i < snapshot.TableCount ? snapshot.Tables[i] : null, swap);
                if (markWritten) tables[i].StampTracked(_changeTick);
                else tables[i].ClearTracked();
            }

            ResetIndexes(true);
            Array.Clear(_dirty, 0, _dirtyCount);
            _dirtyCount = 0;

            for (int i = 0; i < _singletons.Length; i++)
            {
                var recorded = i < snapshot.Singletons.Length ? snapshot.Singletons[i] : null;
                if (keep.ContainsSingleton(i)) continue;
                if (recorded == null) Volatile.Write(ref _singletons[i], null);
                else if (_singletons[i] is SingletonBox box) box.CopyFrom(recorded);
                else Volatile.Write(ref _singletons[i], recorded.Clone());
            }

            for (int i = _singletons.Length; i < snapshot.Singletons.Length; i++)
                if (snapshot.Singletons[i] is { } recorded && !keep.ContainsSingleton(i))
                {
                    Array.Resize(ref _singletons, snapshot.Singletons.Length);
                    Volatile.Write(ref _singletons[i], recorded.Clone());
                }

            StorageVersion++;
        }
    }

    /// <summary>
    /// Throws when a pass iterates this world: <paramref name="call"/> (named in the message) rewrites or drops whole
    /// tables, or reads them dense (Capture, Restore, the saves and loads, MoveEntities, GroupBy, RemoveTypes, TrimExcess).
    /// </summary>
    private void ThrowIfIterating(string call)
    {
        if (Volatile.Read(ref PassDepth) != 0) ThrowIterating(call);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIterating(string call) =>
        throw new InvalidOperationException($"{call} cannot run while a query or system iterates the world: call it between updates");
}
