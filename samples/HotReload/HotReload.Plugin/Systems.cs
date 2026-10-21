using EasyEcs;

namespace HotReload.Plugin;

// Every unit the host created takes part: the units without a score get one, all in one bulk call
// (a whole table at a time) instead of a structural change per entity.
public class JoinScoringSystem : EcsSystem
{
    static readonly QueryFilter Unscored = QueryFilter.Create().With<Position>().Without<Score>();

    protected override void OnUpdate() => World.SetAll(new Score(0), Unscored);
}

// Every moving unit scores the bonus once per update.
public class ScoringSystem : EcsSystem
{
    [ForEach, With<Velocity>]
    static void Score(ref Score score, in Bonus bonus)
    {
        score.Points += bonus.Points;
    }
}
