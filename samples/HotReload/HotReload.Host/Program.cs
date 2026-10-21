using System.Buffers;
using System.Runtime.CompilerServices;
using EasyEcs;

namespace HotReload.Host;

// Game logic as a plugin, replaced while the world keeps running.
// A reload saves the world, removes the plugin's types from it (World.RemoveTypes), unloads the
// plugin, loads it again (on a real server: the rebuilt one) and loads the save, so the plugin's
// components keep their values.
public static class Program
{
    public static int Main()
    {
        string pluginFolder = Path.Combine(AppContext.BaseDirectory, "plugins");
        string pluginPath = Path.Combine(pluginFolder, "HotReload.Plugin.dll");
        var checks = new Checks();

        using var world = new World();
        world.AddSystem<MoveSystem>();
        for (int i = 0; i < 100; i++)
        {
            world.CreateEntity(new Position(i, 0), new Velocity(1, 0));
        }

        // ---- Version 1: load, run three updates, save, unload ----
        WeakReference firstContext = RunAndUnload(world, pluginPath, updates: 3, out byte[] saved);

        checks.That("the plugin's context was collected: nothing in the world roots it",
            Plugins.WaitUntilCollected(firstContext));
        checks.That("its systems left, the host's stayed", world.Systems.Count == 1);
        int units = world.Count<Position, Velocity>();
        checks.That("the host's entities and components stayed", units == 100);

        // ---- Version 2: a new context, the saved values, two more updates ----
        var context = new PluginContext();
        var plugin = Plugins.Load(context, pluginPath);
        world.Load(saved);
        IGamePlugin scoring = Plugins.Install(world, plugin);
        world.Update();
        world.Update();
        int systems = world.Systems.Count;
        string status = scoring.Status(world);
        Console.WriteLine($"plugin reloaded, 2 more updates: {status}, {systems} systems");
        checks.That("the scores carried over the reload (3 + 2 updates of 100 units)",
            status == "500 points");

        // And out again, for good.
        world.RemoveTypes(context);
        context.Unload();

        Console.WriteLine(checks.AllPassed ? "all checks passed" : "a check failed");
        return checks.AllPassed ? 0 : 1;
    }

    // Not inlined: when it returns, nothing of the plugin is left on Main's stack, so the
    // context can be collected.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunAndUnload(
        World world,
        string path,
        int updates,
        out byte[] saved)
    {
        var context = new PluginContext();
        var plugin = Plugins.Load(context, path);
        IGamePlugin scoring = Plugins.Install(world, plugin);
        for (int i = 0; i < updates; i++)
        {
            world.Update();
        }

        int systems = world.Systems.Count;
        string status = scoring.Status(world);
        Console.WriteLine($"plugin loaded, {updates} updates: {status}, {systems} systems");

        // The save holds the plugin's components too, each by its type id: a hash of its full name.
        var output = new ArrayBufferWriter<byte>();
        world.Save(output);
        saved = output.WrittenSpan.ToArray();

        // The plugin's systems, singletons and components leave the world;
        // then the context can go.
        world.RemoveTypes(plugin);
        context.Unload();
        return new WeakReference(context);
    }
}
