using EasyEcs;

namespace HotReload.Host;

// The host's own logic. It is not in the plugin, so it keeps running through reloads.
public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity)
    {
        position = new Position(position.X + velocity.X, position.Y + velocity.Y);
    }
}
