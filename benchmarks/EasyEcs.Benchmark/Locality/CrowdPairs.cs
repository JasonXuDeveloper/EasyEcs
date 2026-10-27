namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// <see cref="Crowd"/>'s sum as a pair method (L2): the neighbor's position and velocity as [Neighbor] parameters, the
/// cutoff test first, one term per pair. The generator writes the rest: the order of the Position tables (Morton codes
/// quantized by the cutoff), the pair lists over windows of rows, and the vector loop of a window against each
/// neighbor (<see cref="Separate"/>); <see cref="SeparateRows"/> takes the neighbor's entity, so it runs one pair at a
/// time. Each pass sums into Push, so a tick resets it first.
/// </summary>
public static class CrowdPairs
{
    /// <summary>One tick of the vector loop: the pushes reset, then summed over the pairs.</summary>
    public static void Tick(World world)
    {
        CrowdPairs.Reset(world);
        CrowdPairs.Separate(world);
    }

    /// <summary>One tick one pair at a time.</summary>
    public static void TickRows(World world)
    {
        CrowdPairs.Reset(world);
        CrowdPairs.SeparateRows(world);
    }

    [ForEach]
    public static void Reset(ref Crowd.Push push) => push = default;

    // #region pairs
    [ForEach]
    public static void Separate(in Crowd.Position p, in Crowd.Velocity v, ref Crowd.Push push,
        [Neighbor] in Crowd.Position q, [Neighbor] in Crowd.Velocity u)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Crowd.Range2 || d2 == 0) return;
        float w = Crowd.Range2 - d2;
        push.X += w * (dx + Crowd.Drag * (u.X - v.X));
        push.Y += w * (dy + Crowd.Drag * (u.Y - v.Y));
    }
    // #endregion pairs

    [ForEach]
    public static void SeparateRows(in Crowd.Position p, in Crowd.Velocity v, ref Crowd.Push push,
        [Neighbor] in Crowd.Position q, [Neighbor] in Crowd.Velocity u, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Crowd.Range2 || d2 == 0 || other.Id.Index < 0) return;
        float w = Crowd.Range2 - d2;
        push.X += w * (dx + Crowd.Drag * (u.X - v.X));
        push.Y += w * (dy + Crowd.Drag * (u.Y - v.Y));
    }
}
