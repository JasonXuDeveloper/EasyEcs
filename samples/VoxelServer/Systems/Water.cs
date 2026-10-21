using EasyEcs;

namespace VoxelServer;

// #region flow
// Spreads water the way Minecraft does: a cell with air below pours down at its own level; otherwise it runs into the
// air beside it one level further from its spring, up to Rules.WaterReach. A cell spreads once, FlowDelay ticks
// after it appeared, and then settles (its Flowing is removed), so a lake that stopped growing is not visited again.
[UpdateInGroup<Simulation>]
public class WaterFlowSystem : EcsSystem
{
    [ForEach]
    void Flow(Entity cell, in Water water, in Flowing flowing, in Clock clock, ref Stats stats)
    {
        if (clock.Tick < flowing.At) return;
        cell.RemoveFlowing();

        var p = water.Pos;
        var below = p with { Y = p.Y - 1 };
        if (Terrain.BlockAt(World, below) == Block.Air)
        {
            Terrain.PourWater(World, below, water.Source, water.Level, clock.Tick, ref stats);
            return;
        }

        if (water.Level >= Rules.WaterReach) return;
        int level = water.Level + 1;
        Terrain.PourWater(World, p with { X = p.X + 1 }, water.Source, level, clock.Tick, ref stats);
        Terrain.PourWater(World, p with { X = p.X - 1 }, water.Source, level, clock.Tick, ref stats);
        Terrain.PourWater(World, p with { Z = p.Z + 1 }, water.Source, level, clock.Tick, ref stats);
        Terrain.PourWater(World, p with { Z = p.Z - 1 }, water.Source, level, clock.Tick, ref stats);
    }
}
// #endregion flow

// #region float
// Lifts a humanoid standing in water and carries it with the current: away from the spring, hardest next to it and
// weaker the farther the water ran (a cell's Level is its distance from the spring). Runs after the walk set the
// velocity and before physics moves the body.
[UpdateInGroup<Simulation>]
public class FloatSystem : EcsSystem
{
    [ForEach, With<Humanoid>]
    void Float(in Position position, ref Velocity velocity, ref Stats stats)
    {
        var p = position.Value;
        var at = Terrain.Cell(p.X, p.Y, p.Z);
        if (!World.WaterIndex.TryGet(at, out var cell)) return;

        ref readonly var water = ref cell.Water;
        var away = new System.Numerics.Vector2(at.X - water.Source.X, at.Z - water.Source.Z);
        if (away != System.Numerics.Vector2.Zero)
            away = System.Numerics.Vector2.Normalize(away)
                   * (Rules.Current * (Rules.WaterReach + 1 - water.Level) / (Rules.WaterReach + 1));

        var v = velocity.Value;
        velocity = new Velocity(new System.Numerics.Vector3(v.X + away.X, Rules.FloatSpeed, v.Z + away.Y));
        stats.Drifts++;
    }
}
// #endregion float
