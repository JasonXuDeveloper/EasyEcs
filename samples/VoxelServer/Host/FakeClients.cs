using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// Not part of the lesson: stands in for the players' clients. A real server fills Input from each connection's
// packets. A bot walks in one of eight directions (or stands), drawn for it every five seconds (EntityRandom's static
// form: the tick it draws for is the five seconds', not the clock's), and,
// while it walks freely, digs the block under its feet every two seconds (a bot that stands, or walks into a wall, would dig itself a pit).
[EcsSystem, UpdateInGroup<ClientInput>]
public ref struct FakeClientsSystem
{
    const float D = 0.70710677f;
    static readonly Vector2[] Directions =
        [Vector2.Zero, new(1, 0), new(D, D), new(0, 1), new(-D, D), new(-1, 0), new(-D, -D), new(0, -1), new(D, -D)];

    [ForEach]
    static void Play(Entity bot, in Player player, in Body body, in Clock clock, in EntityRandom random, ref Input input)
    {
        var walk = Directions[EntityRandom.Range(random.Seed, bot.Id, (ulong)(clock.Tick / 100), 0, 0, Directions.Length)];
        bool dig = walk != Vector2.Zero && body.OnGround && !body.Blocked && (clock.Tick + player.Client) % 40 == 0;
        input = new Input(walk, dig);
    }
}
