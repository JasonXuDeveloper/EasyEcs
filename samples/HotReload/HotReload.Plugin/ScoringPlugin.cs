using EasyEcs;

namespace HotReload.Plugin;

// The entry point the host finds through IGamePlugin.
public sealed class ScoringPlugin : IGamePlugin
{
    public void Install(World world)
    {
        // After a reload the saved bonus is already back; only a first install sets it.
        if (!world.HasBonus)
        {
            world.SetBonus(Points: 1);
        }

        world.AddSystem<JoinScoringSystem>();
        world.AddSystem<ScoringSystem>();
    }

    public string Status(World world)
    {
        int points = 0;
        foreach (var item in world.Read<Score>())
        {
            points += item.Score.Points;
        }

        return $"{points} points";
    }
}
