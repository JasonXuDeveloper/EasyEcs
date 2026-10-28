using EasyEcs;

namespace MultiWorld;

// Every unit of the zone counts the units within sight: the zone's own and the ghosts of its neighbors' border rows,
// so a unit near the border sees across it. [Neighbor] makes it a pair method: the generator writes the neighbor
// search from the distance test (rows kept in space order, only the clusters within sight compared).
[EcsSystem]
public ref struct SightSystem
{
    // Ghosts are seen, they do not look: their own zone counts what they see.
    [ForEach, Without<Ghost>]
    static void See(in Position position, [Neighbor] in Position other, ref Reduce<Tally> tally)
    {
        float dx = position.X - other.X, dy = position.Y - other.Y, distanceSquared = dx * dx + dy * dy;
        if (distanceSquared >= Map.Sight * Map.Sight || distanceSquared == 0) return; // 0: the unit itself
        tally.Value.Sightings++;
    }
}

// Moves every unit by its velocity: a vector loop over the position and velocity columns. Ghosts have no velocity,
// so they never move here.
[EcsSystem]
public ref struct MoveSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity)
    {
        position = new Position(position.X + velocity.X, position.Y + velocity.Y);
    }
}

// Units that walked off the map turn back. Only the map's edges: the borders between zones are open.
[EcsSystem]
public ref struct BounceSystem
{
    [ForEach]
    static void Bounce(in Position position, ref Velocity velocity)
    {
        if (position.X < 0 || position.X >= Map.Width) velocity.X = -velocity.X;
        if (position.Y < 0 || position.Y >= Map.Height) velocity.Y = -velocity.Y;
    }
}
