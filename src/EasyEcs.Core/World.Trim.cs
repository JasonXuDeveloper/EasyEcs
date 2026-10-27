using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace EasyEcs;

// Giving memory back after a peak: tables double when full and never shrink on their own (a world that held 100,000
// entities once keeps their rows), and an empty table stays in the match list of every query. TrimExcess is the one
// call that undoes both, when the owner asks.
public sealed partial class World
{
    // The snapshots Capture made (held weakly: a snapshot nobody holds is not kept alive by its world). TrimExcess keeps
    // the tables they hold rows of and rewrites their table order to the world's new one, so they restore as before.
    private List<WeakReference<WorldSnapshot>> _snapshots;
    private int _snapshotsPrunedAt = 8;

    /// <summary>A new snapshot of this world (Capture), remembered for <see cref="TrimExcess"/>. Cold.</summary>
    private WorldSnapshot NewSnapshot()
    {
        var snapshot = new WorldSnapshot(this);
        _snapshots ??= [];
        if (_snapshots.Count >= _snapshotsPrunedAt) // amortized: the collected ones go once the list doubled
        {
            _snapshots.RemoveAll(static reference => !reference.TryGetTarget(out _));
            _snapshotsPrunedAt = Math.Max(8, _snapshots.Count * 2);
        }

        _snapshots.Add(new WeakReference<WorldSnapshot>(snapshot));
        return snapshot;
    }

    /// <summary>
    /// Give back the memory a peak left: drop every empty table (it leaves the registry and the match list of every
    /// cached query, and a later change that needs it creates it again), shrink the rows of the others to their
    /// entities rounded up to a power of two (64 at least) with their columns, presence bits and change-tick summaries,
    /// and release the reusable buffers of saves, loads, migrations and prefab lookups (they are rebuilt on demand).
    /// The empty table and the tables a <see cref="WorldSnapshot"/> of this world holds rows of stay, and snapshots keep
    /// restoring. The entity records keep their length (handles index them unchecked), as do indexes, relations and
    /// singletons.
    /// <br/>
    /// On request only, at a quiet moment (after a wave despawned, between matches, from a maintenance task): what
    /// it drops, the next structural changes allocate again (cold), so calling it every tick trades the steady state's
    /// zero allocations for churn. Handles, queries, systems, indexes, groupings and change tracking are unaffected.
    /// Owner thread only, not during a pass or inside a parallel stage. O(tables × (cached queries + snapshots) +
    /// rows of the shrunk tables × columns); allocates (cold).
    /// </summary>
    public void TrimExcess()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        ThrowIfIterating(nameof(TrimExcess));

        using (Structural())
        {
            FillDirtyTables(); // left by a pass an exception interrupted: tables are dense from here
            var held = HeldBySnapshots();
            var dropped = new HashSet<Archetype>();
            foreach (var table in _allArchetypes)
                if (table.Count == 0 && !ReferenceEquals(table, _emptyArchetype) && !held.Contains(table))
                    dropped.Add(table);

            if (dropped.Count != 0)
            {
                RenumberSnapshots(dropped); // before the registry forgets the old order
                Unregister(dropped);
                foreach (var table in _allArchetypes) table.ForgetEdges(dropped);
                foreach (var pair in _queryCache) // holders of an entry see the new match (no entry is removed)
                    Volatile.Write(ref pair.Value.Match, pair.Value.Match.Without(dropped));
            }

            foreach (var table in _allArchetypes) table.Trim();
            _archetypes.TrimExcess();
            _groupedTables.TrimExcess();
            TrimBuffers();
            StorageVersion++; // columns moved: refs into them are refreshed by the next pass
        }
    }

    /// <summary>The tables the live snapshots of this world hold rows of (their entity records name them). Inside the gate.</summary>
    private HashSet<Archetype> HeldBySnapshots()
    {
        var held = new HashSet<Archetype>();
        if (_snapshots == null) return held;
        var all = _allArchetypes;
        foreach (var reference in _snapshots)
        {
            if (!reference.TryGetTarget(out var snapshot)) continue;
            int count = Math.Min(snapshot.TableCount, all.Length);
            for (int i = 0; i < count; i++)
                if (snapshot.Tables[i] is { Count: > 0 })
                    held.Add(all[i]);
        }

        return held;
    }

    /// <summary>
    /// Rewrite the table order of every live snapshot for the world without <paramref name="dropped"/> (none of which
    /// a snapshot holds rows of): a snapshot's tables are by the world's order, which the registry is about to compact.
    /// Inside the gate.
    /// </summary>
    private void RenumberSnapshots(HashSet<Archetype> dropped)
    {
        if (_snapshots == null) return;
        var all = _allArchetypes;
        foreach (var reference in _snapshots)
        {
            if (!reference.TryGetTarget(out var snapshot)) continue;
            var tables = snapshot.Tables;
            int length = Math.Min(tables.Length, all.Length), kept = 0, keptCaptured = 0;
            for (int i = 0; i < length; i++)
            {
                if (dropped.Contains(all[i])) continue;
                tables[kept++] = tables[i];
                if (i < snapshot.TableCount) keptCaptured++;
            }

            Array.Clear(tables, kept, tables.Length - kept); // the copies of dropped tables go with them
            snapshot.TableCount = keptCaptured; // the captured tables come first in both orders
        }
    }

    /// <summary>
    /// Delete <paramref name="dropped"/> (emptied tables) from the registry: the dictionaries and the table list. Edges
    /// and cached queries are the caller's. Inside the gate.
    /// </summary>
    private void Unregister(HashSet<Archetype> dropped)
    {
        foreach (var pair in new List<KeyValuePair<BitMask, Archetype>>(_archetypes))
            if (dropped.Contains(pair.Value))
                _archetypes.Remove(pair.Key);
        foreach (var pair in new List<KeyValuePair<GroupedTable, Archetype>>(_groupedTables))
            if (dropped.Contains(pair.Value))
                _groupedTables.Remove(pair.Key);

        var kept = new List<Archetype>(_allArchetypes.Length - dropped.Count);
        foreach (var table in _allArchetypes)
            if (!dropped.Contains(table))
                kept.Add(table);
        Volatile.Write(ref _allArchetypes, kept.ToArray());
    }

    /// <summary>
    /// The reusable buffers that grew with a peak: those of passes, saves, loads and migrations go (each is created again
    /// on its next use), the free ids shrink to the ones held. Inside the gate, outside passes.
    /// </summary>
    private void TrimBuffers()
    {
        if (_dirty.Length > 8) _dirty = new Archetype[8]; // dense tables: none is dirty
        _passCounts = [];
        int free = Math.Max(64, (int)Math.Min(BitOperations.RoundUpToPowerOf2((uint)_freeIdCount), int.MaxValue));
        if (free < _freeIds.Length) Array.Resize(ref _freeIds, free);

        _saveBuffer = null;
        _saveTypes = [];
        _saveTables = [];
        _saveCounts = [];
        _saveStarts = [];
        _saveSelected = [];
        _saveOrder = [];
        _saveRows = [];
        _saveBits = [];
        _loadSnapshot = null;
        _loadBits = [];

        _arrivals?.Dispose();
        _arrivals = null;
        _arrived = [];
        _arrivedStamp = [];
        _arrivedRows.TrimExcess();
        _roots.TrimExcess();
        _prefabTables = null; // prefab table -> table of this world: found again by the next instance
        _columnSpares.Clear(); // the next sort of each shape makes its spare again
    }
}
