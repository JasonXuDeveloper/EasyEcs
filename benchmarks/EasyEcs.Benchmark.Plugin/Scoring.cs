namespace EasyEcs.Benchmark.Plugin;

// A plugin's game logic: components that ride on the host's entities (one tracked, one indexed), a singleton and a
// system. World.RemoveTypes takes all of it out of a world again.

public record struct Score(int Value) : IComponent, ITracked;

public partial record struct Badge([LookupKey] int Level) : IComponent;

public record struct Bonus(int Points) : ISingletonComponent;

[EcsSystem]
public ref struct Scoring
{
    [ForEach]
    private static void Add(ref Score score, in Bonus bonus) => score.Value += bonus.Points;
}

public static class Plugin
{
    /// <summary>Install the plugin into <paramref name="world"/>: every one of <paramref name="players"/> gets its components.</summary>
    public static void Install(World world, Entity[] players)
    {
        world.SetBonus(1);
        world.AddSystem<Scoring>();
        for (int i = 0; i < players.Length; i++) players[i].SetScore(0).SetBadge(i % 10);
        world.BadgeIndex.Count(1); // an index the world keeps
    }
}
