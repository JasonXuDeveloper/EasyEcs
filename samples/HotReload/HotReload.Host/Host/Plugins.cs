using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using EasyEcs;

namespace HotReload.Host;

/// <summary>
/// A collectible context: the plugin loads here and can be unloaded. EasyEcs and the contracts
/// assembly are not in the plugin's folder, so they resolve from the default context and the
/// plugin sees the host's types.
/// </summary>
public sealed class PluginContext() : AssemblyLoadContext("plugin", isCollectible: true);

/// <summary>
/// Plumbing, not ECS: loading a plugin assembly and finding its <see cref="IGamePlugin"/>.
/// </summary>
public static class Plugins
{
    /// <summary>
    /// Loads the plugin into <paramref name="context"/> and registers its EasyEcs types.
    /// </summary>
    public static Assembly Load(PluginContext context, string path)
    {
        Assembly plugin = context.LoadFromAssemblyPath(path);

        // The module initializer registers the plugin's components with EasyEcs, so a save can
        // name them before any of its code has run.
        RuntimeHelpers.RunModuleConstructor(plugin.ManifestModule.ModuleHandle);
        return plugin;
    }

    /// <summary>
    /// Creates the plugin's <see cref="IGamePlugin"/> and installs it into
    /// <paramref name="world"/>.
    /// </summary>
    public static IGamePlugin Install(World world, Assembly plugin)
    {
        foreach (var type in plugin.GetExportedTypes())
        {
            if (typeof(IGamePlugin).IsAssignableFrom(type))
            {
                var instance = (IGamePlugin)Activator.CreateInstance(type)!;
                instance.Install(world);
                return instance;
            }
        }

        throw new InvalidOperationException($"{plugin.GetName().Name} has no IGamePlugin");
    }

    /// <summary>
    /// Runs the garbage collector until the unloaded context is gone, a few times at most.
    /// Returns whether it was collected: if not, something still holds one of its types.
    /// </summary>
    public static bool WaitUntilCollected(WeakReference context)
    {
        for (int i = 0; i < 10 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !context.IsAlive;
    }
}
