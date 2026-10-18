using EasyEcs;

namespace GameLoop.Host;

/// <summary>
/// Plumbing, not ECS: the lines the host prints while it runs and when it stops.
/// </summary>
public static class Report
{
    public static void Status(World world, int second)
    {
        int players = world.Count<Client>();
        int bullets = world.Count<Lifetime>();
        int entities = world.EntityCount;
        Console.WriteLine($"t={second}s players={players} bullets={bullets} entities={entities}");
    }

    public static void Summary(World world, int ticks, TimeSpan elapsed, long refused)
    {
        ref readonly var stats = ref world.Stats;
        double kilobytes = stats.SentBytes / 1024.0;
        Console.WriteLine($"{ticks} ticks in {elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"  {stats.Received} messages ({refused} refused)");
        Console.WriteLine($"  {stats.Joined} joins, {stats.Left} leaves");
        Console.WriteLine($"  {stats.Fired} bullets fired");
        Console.WriteLine($"  {stats.Replicated} position updates replicated ({kilobytes:F1} KB)");

        // WorldOptions.MeasureSystems records how often each system ran
        // and how long its last run took.
        foreach (var system in world.Systems)
        {
            string name = system.GetType().Name;
            double microseconds = system.Stats.Last.TotalMicroseconds;
            long runs = system.Stats.Runs;
            Console.WriteLine($"  {name,-20} {runs,5} runs, {microseconds,8:F1} us last run");
        }
    }
}
