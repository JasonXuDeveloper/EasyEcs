using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using EasyEcs;
using HotReload;

// Game logic as a plugin: load it into a collectible context, run it, then reload it while the world keeps running.
// A reload saves the world, removes the plugin's types from it (World.RemoveTypes), unloads the context, loads the
// plugin again (in a real server: the rebuilt one) and loads the save, so the plugin's components keep their values.
string pluginPath = Path.Combine(AppContext.BaseDirectory, "plugins", "HotReload.Plugin.dll");
bool ok = true;

using var world = new World();
world.AddSystem<MoveSystem>();                                      // the host's own logic stays through reloads
for (int i = 0; i < 100; i++) world.CreateEntity(new Position(i, 0), new Velocity(1, 0));

// Version 1: load, run three updates, save and unload. Not inlined: nothing of the plugin stays on this stack.
var (unloaded, saved) = RunAndUnload(world, pluginPath, updates: 3);
for (int i = 0; i < 10 && unloaded.IsAlive; i++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}

ok &= Check("the plugin's context was collected: nothing in the world roots it", !unloaded.IsAlive);
ok &= Check("its systems left, the host's stayed", world.Systems.Count == 1);
ok &= Check("the host's entities and components stayed", world.Count<Position, Velocity>() == 100);

// Version 2: a new context, the saved values, two more updates.
var context = new PluginContext();
var plugin = context.LoadFromAssemblyPath(pluginPath);
RuntimeHelpers.RunModuleConstructor(plugin.ManifestModule.ModuleHandle); // registers its types, so the save can name them
world.Load(saved);
var scoring = Install(world, plugin);
world.Update();
world.Update();
Console.WriteLine($"plugin reloaded, 2 more updates: {scoring.Status(world)}, {world.Systems.Count} systems");
ok &= Check("the scores carried over the reload (3 + 2 updates of 100 units)", scoring.Status(world) == "500 points");

world.RemoveTypes(context);                                         // and out again, for good
context.Unload();
Console.WriteLine(ok ? "all checks passed" : "a check failed");
return ok ? 0 : 1;

[MethodImpl(MethodImplOptions.NoInlining)]
static (WeakReference Context, byte[] Saved) RunAndUnload(World world, string path, int updates)
{
    var context = new PluginContext();
    var plugin = context.LoadFromAssemblyPath(path);
    var scoring = Install(world, plugin);
    for (int i = 0; i < updates; i++) world.Update();
    Console.WriteLine($"plugin loaded, {updates} updates: {scoring.Status(world)}, {world.Systems.Count} systems");

    var output = new ArrayBufferWriter<byte>();
    world.Save(output);                                             // the plugin's components too, by type name
    world.RemoveTypes(plugin);                                      // its systems, singletons and components leave
    context.Unload();
    return (new WeakReference(context), output.WrittenSpan.ToArray());
}

// The host knows the plugin only through IGamePlugin (from the shared contracts assembly).
static IGamePlugin Install(World world, System.Reflection.Assembly plugin)
{
    foreach (var type in plugin.GetExportedTypes())
        if (typeof(IGamePlugin).IsAssignableFrom(type))
        {
            var instance = (IGamePlugin)Activator.CreateInstance(type)!;
            instance.Install(world);
            return instance;
        }

    throw new InvalidOperationException($"{plugin.GetName().Name} has no IGamePlugin");
}

static bool Check(string what, bool passed)
{
    Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}");
    return passed;
}

/// <summary>A collectible context: the plugin loads here; EasyEcs and the contracts resolve from the default context.</summary>
sealed class PluginContext() : AssemblyLoadContext("plugin", isCollectible: true);

public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}
