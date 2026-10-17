using System;
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
    public WorldSnapshot Capture()
    {
        using (Structural())
        {
            ThrowIfIterating();
            FillDirtyTables(); // left by a pass an exception interrupted: tables are dense from here
            var tables = _allArchetypes;
            var rows = new Archetype.Rows[tables.Length];
            for (int i = 0; i < tables.Length; i++) rows[i] = tables[i].Capture();

            var singletons = new SingletonBox[_singletons.Length];
            for (int i = 0; i < singletons.Length; i++)
                if (_singletons[i] is SingletonBox box)
                    singletons[i] = box.Clone();

            return new WorldSnapshot(this, Meta.AsSpan(0, _nextId).ToArray(), _nextId,
                _freeIds.AsSpan(0, _freeIdCount).ToArray(), _entityCount, rows, singletons);
        }
    }

    /// <summary>
    /// Put the world back to <paramref name="snapshot"/> (taken from this world). Handles of entities alive in the
    /// snapshot are valid again; every other handle is stale, also handles of entities created after the capture.
    /// Archetypes created after the capture stay, empty. Singletons keep their boxes (a <see cref="SingletonRef{T}"/>
    /// still works) and get the snapshot's values; singletons added after the capture are removed.
    /// O(entities × components + archetypes + singletons). Not while a pass iterates.
    /// </summary>
    public void Restore(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.World, this))
            throw new ArgumentException("The snapshot was taken from another world", nameof(snapshot));

        using (Structural())
        {
            ThrowIfIterating();
            // Entity records. Versions only grow: a recorded id keeps the snapshot's version if its entity was alive
            // then (its old handles work again), else it moves past every version it ever had, so no handle made after
            // the capture can match a later incarnation.
            var saved = snapshot.Meta;
            if (Meta.Length < saved.Length) Grow(saved.Length);
            int used = Math.Max(_nextId, snapshot.NextId);
            for (int id = 0; id < used; id++)
            {
                ref var meta = ref Meta[id];
                int version = Math.Max(meta.Version, id < saved.Length ? saved[id].Version : 0) + 1;
                if (id < saved.Length && saved[id].Archetype != null) meta = saved[id];
                else meta = new EntityMeta { Version = version };
            }

            _nextId = used;
            // Ids recorded free, then ids first used after the capture; LIFO takes the recorded ones first.
            int free = snapshot.FreeIds.Length + (used - snapshot.NextId);
            if (_freeIds.Length < free) _freeIds = new int[free];
            for (int id = used - 1, i = 0; id >= snapshot.NextId; id--, i++) _freeIds[i] = id;
            snapshot.FreeIds.CopyTo(_freeIds, used - snapshot.NextId);
            _freeIdCount = free;
            _entityCount = snapshot.EntityCount;

            var tables = _allArchetypes;
            for (int i = 0; i < tables.Length; i++)
            {
                tables[i].Restore(i < snapshot.Tables.Length ? snapshot.Tables[i] : null);
                tables[i].StampTracked(_changeTick); // every restored row is written: Changed<T> sees it
            }

            ResetIndexes(true);
            Array.Clear(_dirty, 0, _dirtyCount);
            _dirtyCount = 0;

            for (int i = 0; i < _singletons.Length; i++)
            {
                var recorded = i < snapshot.Singletons.Length ? snapshot.Singletons[i] : null;
                if (recorded == null) Volatile.Write(ref _singletons[i], null);
                else if (_singletons[i] is SingletonBox box) box.CopyFrom(recorded);
                else Volatile.Write(ref _singletons[i], recorded.Clone());
            }

            for (int i = _singletons.Length; i < snapshot.Singletons.Length; i++)
                if (snapshot.Singletons[i] is { } recorded)
                {
                    Array.Resize(ref _singletons, snapshot.Singletons.Length);
                    Volatile.Write(ref _singletons[i], recorded.Clone());
                }

            StorageVersion++;
        }
    }

    private void ThrowIfIterating()
    {
        if (Volatile.Read(ref Passes) != 0)
            throw new InvalidOperationException("Snapshots cannot be captured or restored while a query iterates");
    }
}
