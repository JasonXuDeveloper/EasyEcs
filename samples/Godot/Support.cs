using EasyEcs;
using Godot;

namespace GodotSample;

// Not part of the lesson: how CI runs the sample for a fixed number of ticks and checks what it prints.
static class Support
{
    // `godot --headless --path samples/Godot -- --ticks 120` stops after 120 physics ticks; without it the scene runs
    // until the window is closed.
    public static int TicksFromCommandLine()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--ticks") return int.Parse(args[i + 1]);
        return -1;
    }

    public static void Report(World world, int ticks, long allocated)
    {
        var arena = world.Arena.Size;
        int inside = 0;
        foreach (var item in world.Query<Position>())
        {
            var p = item.Position.Value;
            if (p.X >= 0 && p.X <= arena.X && p.Y >= 0 && p.Y <= arena.Y) inside++;
        }
        GD.Print($"{ticks} ticks, {world.EntityCount} entities, {inside} inside the arena");
        GD.Print($"updates after the first allocated {allocated} B");
    }
}
