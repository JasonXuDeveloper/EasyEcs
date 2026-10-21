using BlockWorld;
using EasyEcs;

// A Minecraft-style server: 20 ticks a second, chunks loaded around the players, blocks dug into items, zombies at
// night. Options: --players N (50), --ticks N (1200: one minute, a day and a night twice), --realtime.
int players = Support.Option(args, "--players", 50);
int ticks = Support.Option(args, "--ticks", 1200);

using var world = new World(new WorldOptions { MeasureSystems = true });

world.SetClock(Tick: 0, DayLength: Rules.DayLength);
world.AddSingleton<Stats>();
world.SetPrefabs(
    Player: new Prefab()
        .SetPlayer(0).SetPosition(default).SetVelocity(default).SetBody(false, false)
        .SetHealth(Rules.PlayerHealth).SetInput(default, false).SetBuffer<ItemStack>(),
    Zombie: new Prefab()
        .Set(new Zombie()).SetTarget(default).SetPosition(default).SetVelocity(default).SetBody(false, false)
        .SetHealth(Rules.ZombieHealth).SetCooldown(0),
    Item: new Prefab()
        .SetItem(default, default).SetPosition(default).SetVelocity(new System.Numerics.Vector3(0, 0.2f, 0))
        .SetBody(false, false).SetLifetime(Rules.ItemLifetime));

// One tick, in this order: what the clients asked for, the world around them, movement, the rules of the game, what
// is sent back.
world.AddSystem<FakeClientsSystem>();
world.AddSystem<ChunkLoadSystem>();
world.AddSystem<PlayerMoveSystem>();
world.AddSystem<ZombieChaseSystem>();
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
world.AddSystem<ClockSystem>();

var player = world.Prefabs.Player;
for (int i = 0; i < players; i++)
    world.Instantiate(player, new Player(i), new Position(Rules.SpawnPoint(i)));

Server.Run(world, ticks, args.Contains("--realtime"));
