using EasyEcs;

namespace HotReload.Plugin;

// The plugin's data: a component that rides on the host's entities and a singleton. Both leave the world with
// World.RemoveTypes; a Save before and a Load after a reload carry their values over (types are saved by name).
public record struct Score(int Points) : IComponent, ITrackChanges;
public record struct Bonus(int Points) : ISingletonComponent;

// Every unit the host moves takes part: units without a score get one.
public class JoinScoring : EcsSystem
{
    [ForEach, With<Position>, Without<Score>]
    static void Join(Entity unit) => unit.SetScore(0);
}

public class ScoringSystem : EcsSystem
{
    [ForEach, With<Velocity>]
    static void Add(ref Score score, in Bonus bonus) => score.Points += bonus.Points;
}

public sealed class ScoringPlugin : IGamePlugin
{
    public void Install(World world)
    {
        if (!world.HasBonus) world.SetBonus(1);                     // a reload keeps the saved one
        world.AddSystem<JoinScoring>();
        world.AddSystem<ScoringSystem>();
    }

    public string Status(World world)
    {
        int points = 0;
        foreach (var item in world.Query<Score>()) points += item.Score.Points;
        return $"{points} points";
    }
}
