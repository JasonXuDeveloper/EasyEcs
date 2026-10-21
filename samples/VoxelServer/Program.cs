using System.Buffers;
using EasyEcs;
using VoxelServer;

// A Minecraft-style server: 20 ticks a second, chunks loaded around the players, blocks dug into items, springs whose
// water runs and carries whoever stands in it, zombies at night. Options: --players N (50), --ticks N (1200: one
// minute, a day and a night twice), --realtime, --schedule (print the order the systems run in and stop).
int players = Support.Option(args, "--players", 50);
int ticks = Support.Option(args, "--ticks", 1200);

// The destroy log keeps the last 64k destroyed ids with their ticks, for SendDespawnsSystem.
using var world = new World(new WorldOptions { MeasureSystems = true, DestroyLogCapacity = 1 << 16 });

world.SetClock(Tick: 0, DayLength: Rules.DayLength);
world.AddSingleton<Stats>();
SetPrefabs(world);

// One tick, in this order: what the clients asked for, the world around them, movement, the rules of the game, what
// is sent back.
world.AddSystem<FakeClientsSystem>();
world.AddSystem<ChunkLoadSystem>();
world.AddSystem<WaterFlowSystem>();
world.AddSystem<PlayerMoveSystem>();
world.AddSystem<ZombieChaseSystem>();
world.AddSystem<FloatSystem>();
world.AddSystem<PhysicsSystem>();
world.AddSystem<DigSystem>();
world.AddSystem<PickupSystem>();
world.AddSystem<ItemExpirySystem>();
world.AddSystem<NightSpawnSystem>();
world.AddSystem<SunburnSystem>();
world.AddSystem<ZombieDeathSystem>();
world.AddSystem<PlayerRespawnSystem>();
world.AddSystem<ChunkUnloadSystem>();
world.AddSystem<SendChunksSystem>();
world.AddSystem<SendPositionsSystem>();
world.AddSystem<SendDespawnsSystem>();
world.AddSystem<ClockSystem>();

if (args.Contains("--schedule"))
{
    Console.WriteLine(world.DescribeSchedule());
    return;
}

var player = world.Prefabs.Player;
for (int i = 0; i < players; i++)
    world.Instantiate(player, new Player(i), new Position(Rules.SpawnPoint(i)));

Server.Run(world, ticks, args.Contains("--realtime"));

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

// The recipes players, zombies and items are created from: every start builds them.
static void SetPrefabs(World world) =>
    world.SetPrefabs(
        Player: new Prefab()
            .SetPlayer(0).Set(new Humanoid()).SetPosition(default).SetVelocity(default).SetBody(false, false)
            .SetHealth(Rules.PlayerHealth).SetInput(default, false).SetBuffer<ItemStack>(),
        Zombie: new Prefab()
            .Set(new Zombie()).Set(new Humanoid()).SetTarget(default).SetPosition(default).SetVelocity(default)
            .SetBody(false, false).SetHealth(Rules.ZombieHealth).SetCooldown(0),
        Item: new Prefab()
            .SetItem(default, default).SetPosition(default).SetVelocity(new System.Numerics.Vector3(0, 0.2f, 0))
            .SetBody(false, false).SetLifetime(Rules.ItemLifetime));
