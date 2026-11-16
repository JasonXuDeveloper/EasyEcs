using EasyEcs;

namespace HotReload.Plugin;

// Every unit the host created takes part: the units without a score get one, all in one bulk call
// (a whole table at a time) instead of a structural change per entity.
[EcsSystem]
public ref struct JoinScoringSystem
{
    static readonly QueryFilter Unscored = QueryFilter.Create().With<Position>().Without<Score>();

    static void OnUpdate(World world) => world.SetAll(new Score(0), Unscored);
}

// Every moving unit scores the bonus once per update.
[EcsSystem]
public ref struct ScoringSystem
{
    [ForEach, With<Velocity>]
    static void Score(ref Score score, in Bonus bonus)
    {
        score.Points += bonus.Points;
    }
}

// The total of every unit's points, for the host's log: a runner, a [ForEach] pass the host runs itself
// between updates (the generated Reports.SumPoints(world, ref total)).
public static class Reports
{
    [ForEach]
    static void SumPoints(in Score score, ref int total) => total += score.Points;
}
