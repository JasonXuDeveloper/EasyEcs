using System.Text;
using EasyEcs;

namespace SaveLoad;

// Plumbing, not ECS: prints a world as text and checks the results, so a run proves
// every step worked.

/// <summary>Collects the result of each check and prints a line per check.</summary>
public sealed class Checks
{
    public bool AllPassed { get; private set; } = true;

    public void That(string what, bool passed)
    {
        string mark = passed ? "ok  " : "FAIL";
        Console.WriteLine($"  {mark} {what}");
        AllPassed &= passed;
    }
}

/// <summary>A world as one line of text, so two worlds can be compared.</summary>
public static class WorldText
{
    // One entry per player, sorted by name: name, position, health, bag, swords. Then the clock.
    public static string Describe(World world)
    {
        var players = new List<string>();
        foreach (var item in world.Query<Name, Position, Health>())
        {
            players.Add(DescribePlayer(world, item.Entity));
        }

        players.Sort(StringComparer.Ordinal);
        long tick = world.HasClock ? world.Clock.Tick : 0;
        return $"[{string.Join("; ", players)}] clock {tick}";
    }

    private static string DescribePlayer(World world, Entity player)
    {
        var text = new StringBuilder();
        Position position = player.Position;
        string name = player.Name.Value;
        int health = player.Health.Value;
        text.Append($"{name}@({position.X},{position.Y}) hp {health}");

        if (player.TryGet<DynamicBuffer<Item>>(out var bag))
        {
            foreach (var stack in bag)
            {
                text.Append($" item{stack.Kind}x{stack.Count}");
            }
        }

        foreach (var sword in world.Children(player.Id))
        {
            text.Append($" sword+{sword.Sword.Damage}");
        }

        return text.ToString();
    }
}
