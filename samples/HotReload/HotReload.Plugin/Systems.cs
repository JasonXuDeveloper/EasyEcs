using EasyEcs;

namespace HotReload.Plugin;

// Every unit the host created takes part: a unit without a score gets one.
public class JoinScoringSystem : EcsSystem
{
    [ForEach, With<Position>, Without<Score>]
    static void Join(Entity unit)
    {
        unit.SetScore(0);
    }
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
