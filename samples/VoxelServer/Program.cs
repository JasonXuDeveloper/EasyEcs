using System.Buffers;
using EasyEcs;
using VoxelServer;

// A Minecraft-style server: 20 ticks a second, chunks loaded around the players, blocks dug into items, springs whose
// water runs and carries whoever stands in it, zombies at night. Options: --players N (50), --ticks N (1200: one
// minute, a day and a night twice), --realtime, --schedule (print the order the systems run in and stop).
int players = Support.Option(args, "--players", 50);
int ticks = Support.Option(args, "--ticks", 1200);

// #region options
// A chunk's blocks are 8 KB, more than a buffer's pool keeps by default: let each loaded chunk reuse the list of one
// that was unloaded.
DynamicBuffer<Block>.PoolCapacity = Terrain.BlocksPerChunk;

// The destroy log keeps the last 64k destroyed ids with their ticks, for SendDespawnsSystem.
using var world = new World(new WorldOptions { MeasureSystems = true, DestroyLogCapacity = 1 << 16 });
// #endregion options

world.SetClock(Tick: 0, DayLength: Rules.DayLength);
world.AddSingleton<Stats>();
SetPrefabs(world);

// #region systems
// One tick, in this order: what the clients asked for, the world around them, movement, the rules of the game, what
// is sent back.
world.AddSystem<FakeClientsSystem>();
world.AddSystem<ChunkLoadSystem>();
world.AddSystem<WaterFlowSystem>();
world.AddSystem<PlayerMoveSystem>();
world.AddSystem<ZombieSightSystem>();
world.AddSystem<ZombieChaseSystem>();
world.AddSystem<FloatSystem>();
world.AddSystem<PhysicsSystem>();
world.AddSystem<GridSystem>();
world.AddSystem<DigSystem>();
world.AddSystem<PickupSystem>();
world.AddSystem<ItemExpirySystem>();
world.AddSystem<NightSpawnSystem>();
world.AddSystem<SunburnSystem>();
world.AddSystem<ZombieDeathSystem>();
world.AddSystem<PlayerRespawnSystem>();
world.AddSystem<ChunkUnloadSystem>();
world.AddSystem<SendChunksSystem>();
world.AddSystem<SendEntitiesSystem>();
world.AddSystem<SendDespawnsSystem>();
world.AddSystem<ClockSystem>();
// #endregion systems

if (args.Contains("--schedule"))
{
    Console.WriteLine(world.DescribeSchedule());
    return;
}

// #region players
var player = world.Prefabs.Player;
for (int i = 0; i < players; i++)
{
    var at = Rules.SpawnPoint(i);
    world.Instantiate(player, new Player(i), new Position(at), new InChunk(Terrain.ChunkOf((int)at.X, (int)at.Z)));
}
// #endregion players

Server.Run(world, ticks, args.Contains("--realtime"));

// #region leaderboard
// The best three diggers: the ordered index walks the scores from the top, no sort and no list of all the players.
Console.Write("top diggers:");
int shown = 0;
foreach (var digger in world.ScoreIndex.Descending())
{
    Console.Write($" player {digger.Player.Client} ({digger.Score.BlocksDug})");
    if (++shown == 3) break;
}
Console.WriteLine();
// #endregion leaderboard

// #region save
// Stop the way a vanilla server does: save the world. The prefabs are not in the save: every start builds them, and a
// Prefab has no serialized form. A restart builds them, then loads the save beside them.
var startup = ComponentSet.Create().WithSingleton<Prefabs>();
var save = new ArrayBufferWriter<byte>();
world.SaveWithout(save, in startup);

using var restarted = new World(new WorldOptions { DestroyLogCapacity = 1 << 16 });
SetPrefabs(restarted);
restarted.LoadKeeping(save.WrittenSpan, in startup, markWritten: false);   // a client that reconnects gets it all anyway
Console.WriteLine($"restarted from the save: {restarted.Count<Player>()} players, {restarted.Count<Chunk>()} chunks, " +
                  $"{restarted.Count<Water>()} water, {restarted.Count<Zombie>()} zombies");
Console.Error.WriteLine($"save: {save.WrittenCount / 1024} KiB");
// #endregion save

// #region prefabs
// The recipes players, zombies and items are created from: every start builds them. Players and zombies differ in
// what they see: each prefab carries its own Vision.
static void SetPrefabs(World world) =>
    world.SetPrefabs(
        Player: new Prefab()
            .SetPlayer(0).Set(new Humanoid()).SetVision(Rules.PlayerVision).SetInChunk(default).SetScore(0)
            .SetPosition(default).SetVelocity(default).SetBody(false, false)
            .SetHealth(Rules.PlayerHealth).SetInput(default, false).SetBuffer<ItemStack>(),
        Zombie: new Prefab()
            .Set(new Zombie()).Set(new Humanoid()).SetVision(Rules.ZombieVision).SetTarget(default)
            .SetPosition(default).SetVelocity(default).SetBody(false, false)
            .SetHealth(Rules.ZombieHealth).SetCooldown(0),
        Item: new Prefab()
            .SetItem(default, default).SetPosition(default).SetVelocity(new System.Numerics.Vector3(0, 0.2f, 0))
            .SetBody(false, false).SetLifetime(Rules.ItemLifetime));
// #endregion prefabs
