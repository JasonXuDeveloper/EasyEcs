using EasyEcs;

namespace HotReload;

// The host's components: they stay when a plugin unloads.
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;

/// <summary>Game logic that can be loaded, unloaded and loaded again while the server runs.</summary>
public interface IGamePlugin
{
    /// <summary>Add the plugin's systems and singletons to <paramref name="world"/>.</summary>
    void Install(World world);

    /// <summary>The plugin's state in <paramref name="world"/>, for the host's log.</summary>
    string Status(World world);
}
