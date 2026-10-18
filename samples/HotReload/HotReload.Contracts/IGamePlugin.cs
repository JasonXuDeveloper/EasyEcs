using EasyEcs;

namespace HotReload;

/// <summary>
/// Game logic that can be loaded, unloaded and loaded again while the server runs.
/// The host knows a plugin only through this interface.
/// </summary>
public interface IGamePlugin
{
    /// <summary>Adds the plugin's systems and singletons to <paramref name="world"/>.</summary>
    void Install(World world);

    /// <summary>The plugin's state in <paramref name="world"/>, for the host's log.</summary>
    string Status(World world);
}
