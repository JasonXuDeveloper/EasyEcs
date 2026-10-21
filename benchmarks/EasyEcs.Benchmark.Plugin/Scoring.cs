namespace EasyEcs.Benchmark.Plugin;

// A plugin's game logic: components that ride on the host's entities (one tracked, one indexed), a singleton and a
// system. World.RemoveTypes takes all of it out of a world again.

public record struct Score(int Value) : IComponent, ITracked;

public record struct Badge(int Level) : IComponent, IIndexed<int>
{
    public int Key => Level;
}

public record struct Bonus(int Points) : ISingletonComponent;

public class Scoring : EcsSystem
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
