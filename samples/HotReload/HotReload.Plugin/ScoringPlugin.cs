using EasyEcs;

namespace HotReload.Plugin;

// The entry point the host finds through IGamePlugin.
public sealed class ScoringPlugin : IGamePlugin
{
    public void Install(World world)
    {
        // After a reload the saved bonus is already back and stays; only a first install adds it.
        world.GetOrAddBonus(Points: 1);

        world.AddSystem<JoinScoringSystem>();
        world.AddSystem<ScoringSystem>();
    }

    public string Status(World world)
    {
        int points = 0;
        Reports.SumPoints(world, ref points);
        return $"{points} points";
    }
}
