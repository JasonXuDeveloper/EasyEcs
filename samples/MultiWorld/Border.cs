using System.Runtime.InteropServices;
using EasyEcs;

namespace MultiWorld;

// The halo exchange between the zones, run between two updates while every zone waits, so the one thread that runs it
// may touch every world. Host code, not a system: a system belongs to one world, the border to two.
public sealed class Border(World[] zones)
{
    // Ghosts are copied from the units a zone owns, never from the ghosts it holds.
    static readonly QueryFilter Owned = QueryFilter.Create().Without<Ghost>();

    readonly List<EntityId> _toLeft = [], _toRight = []; // reused: no allocation once they are large enough

    // How many units arrived in each zone from its neighbors.
    public readonly int[] Arrivals = new int[zones.Length];

    public void Exchange()
    {
        // The ghosts of the last exchange are stale.
        foreach (var zone in zones) zone.DestroyAll<Ghost>();

        // A unit that walked into the neighbor zone moves there, with every component.
        for (int z = 0; z < zones.Length; z++) MoveCrossers(z);

        // A unit within sight of a border is copied into the neighbor zone as a ghost row.
        for (int z = 0; z < zones.Length; z++) CopyBorderRows(z);
    }

    void MoveCrossers(int z)
    {
        _toLeft.Clear();
        _toRight.Clear();
        foreach (var unit in zones[z].Query<Position>())
        {
            int now = Map.ZoneOf(unit.Position.X);
            if (now < z) _toLeft.Add(unit.Entity.Id);
            else if (now > z) _toRight.Add(unit.Entity.Id);
        }

        // A unit moves one zone at most per update: it is slower than a zone is wide.
        if (_toLeft.Count != 0) Arrivals[z - 1] += zones[z].MoveEntities(zones[z - 1], CollectionsMarshal.AsSpan(_toLeft));
        if (_toRight.Count != 0) Arrivals[z + 1] += zones[z].MoveEntities(zones[z + 1], CollectionsMarshal.AsSpan(_toRight));
    }

    void CopyBorderRows(int z)
    {
        float left = z * Map.ZoneWidth, right = left + Map.ZoneWidth;
        foreach (var unit in zones[z].Query<Position>(Owned))
        {
            var position = unit.Position;
            if (z > 0 && position.X < left + Map.Sight) zones[z - 1].CreateEntity(position, new Ghost());
            if (z < zones.Length - 1 && position.X >= right - Map.Sight) zones[z + 1].CreateEntity(position, new Ghost());
        }
    }
}
