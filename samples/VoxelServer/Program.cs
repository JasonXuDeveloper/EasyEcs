using EasyEcs;
using Nino.Core;
using VoxelServer;

// A Minecraft-style server: 20 ticks a second, chunks loaded around the players, blocks dug into items, springs whose
// water runs and carries whoever stands in it, zombies at night. Options: --players N (50), --ticks N (1200: one
// minute, a day and a night twice), --realtime, --schedule (print the order the systems run in and stop).
int players = Support.Option(args, "--players", 50);
int ticks = Support.Option(args, "--ticks", 1200);

var capacity = Rules.CapacityFor(players);
using var regions = new RegionFiles(capacity.Chunks * 4);
using var world = Startup.Create(regions, capacity, measure: true);

if (args.Contains("--schedule"))
{
    Console.WriteLine(world.DescribeSchedule());
    return;
}

Startup.AddPlayers(world, players);
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
// Stop the way a vanilla server does: save the world; the unloaded chunks are in the region files already. The prefabs
// and the region files are not in the save: every start builds the prefabs (a Prefab has no serialized form) and
// opens the files. A restart does both, then loads the save beside them.
var startup = ComponentSet.Create().WithSingleton<Prefabs>().WithSingleton<Regions>();
var save = new NinoArrayBufferWriter();                                    // Save writes into it in place, no copy
world.SaveWithout(save, in startup);

using var restarted = new World();
restarted.SetRegions(regions);
Startup.SetPrefabs(restarted);
restarted.LoadKeeping(save.WrittenSpan, in startup, markWritten: false);   // a client that reconnects gets it all anyway
Console.WriteLine($"restarted from the save: {restarted.Count<Player>()} players, {restarted.Count<Chunk>()} chunks, " +
                  $"{Server.WaterIn(restarted)} water, {restarted.Count<Zombie>()} zombies; {regions.Count} chunks in the region files");
Console.Error.WriteLine($"save: {save.WrittenCount / 1024} KiB");
// #endregion save

// Not part of the lesson: the check that a tick in steady state allocates nothing (Host/Steady.cs).
Steady.Check(world, regions, capacity);

// The server's world, the same on every start (and in Benchmark/): its options, its singletons, its prefabs and what
// it reserves, its systems, its players.
public static class Startup
{
    public static World Create(RegionFiles regions, in Capacity capacity, bool measure)
    {
        // #region options
        // A chunk's blocks, its water and the water's next step are 8 KB each, more than a buffer's pool keeps by
        // default: let each chunk loaded reuse the lists of one that was unloaded.
        DynamicBuffer<Block>.MaxPooledLength = Terrain.BlocksPerChunk;
        DynamicBuffer<Fluid>.MaxPooledLength = Terrain.BlocksPerChunk;
        DynamicBuffer<NextFluid>.MaxPooledLength = Terrain.BlocksPerChunk;
        DynamicBuffer<Surface>.MaxPooledLength = Terrain.Size * Terrain.Size;
        DynamicBuffer<ItemStack>.MaxPooledLength = Rules.InventorySlots;

        // ParallelSystems: consecutive systems whose data do not conflict share a stage and run at once (the order
        // below puts such systems next to each other); the result is the one of running them one by one.
        var world = new World(new WorldOptions { MeasureSystems = measure, ParallelSystems = true });
        // #endregion options

        world.SetClock(Tick: 0, DayLength: Rules.DayLength);
        world.AddSingleton<EntityRandom>() = new EntityRandom(Rules.Seed);  // each Update() draws new numbers
        world.AddSingleton<Stats>();
        world.AddSingleton<Hunt>();
        world.SetRegions(regions);
        SetPrefabs(world);
        Reserve(world, capacity);
        AddSystems(world);
        return world;
    }

    public static void AddSystems(World world)
    {
        // #region systems
        // One tick, in this order: what the clients asked for, the world around them, movement, the rules of the game,
        // what is sent back. Systems that touch other data sit side by side, so they share a stage (--schedule shows
        // the stages): the walk and the zombies' targets beside the height map, the chase beside the water's step.
        // None of them reads what the water writes, so the order gives the same world as one system at a time.
        world.AddSystem<FakeClientsSystem>();
        world.AddSystem<ClockSystem>();
        world.AddSystem<ChunkReadSystem>();
        world.AddSystem<ChunkGenerateSystem>();
        world.AddSystem<HeightMapSystem>();                  // a stage of three
        world.AddSystem<PlayerMoveSystem>();
        world.AddSystem<ZombieTargetSystem>();
        world.AddSystem<WaterWakeSystem>();
        world.AddSystem<WaterFlowSystem>();                  // a stage of two
        world.AddSystem<ZombieChaseSystem>();
        world.AddSystem<WaterSleepSystem>();
        world.AddSystem<WaterCommitSystem>();
        world.AddSystem<ZombieAttackSystem>();
        world.AddSystem<PlayerRespawnSystem>();
        world.AddSystem<FloatSystem>();
        world.AddSystem<PhysicsSystem>();
        world.AddSystem<GridSystem>();
        world.AddSystem<FootprintSystem>();
        world.AddSystem<FootingSystem>();
        world.AddSystem<ScentWakeSystem>();
        world.AddSystem<HuntSystem>();
        world.AddSystem<ScentStepSystem<Red>>();             // red-black: the red half, then the black from its edges
        world.AddSystem<ScentSettleSystem<Red>>();
        world.AddSystem<ScentStepSystem<Black>>();
        world.AddSystem<ScentSettleSystem<Black>>();
        world.AddSystem<DigSystem>();
        world.AddSystem<PickupSystem>();
        world.AddSystem<ItemExpirySystem>();
        world.AddSystem<NightSpawnSystem>();
        world.AddSystem<ZombieExposureSystem>();
        world.AddSystem<SunlightSystem>();
        world.AddSystem<FireDamageSystem>();
        world.AddSystem<ExtinguishSystem>();
        world.AddSystem<ZombieDeathSystem>();
        world.AddSystem<ChunkUnloadSystem>();
        world.AddSystem<ChunkSaveSystem>();
        world.AddSystem<TallySystem>();
        world.AddSystem<AoiViewSystem>();
        world.AddSystem<AoiChunkSystem>();
        world.AddSystem<AoiSpawnSystem>();
        world.AddSystem<AoiDespawnSystem>();
        world.AddSystem<SendChunksSystem>();
        world.AddSystem<ChunkSentSystem>();
        world.AddSystem<SendEntitiesSystem>();
        world.AddSystem<SendPacketsSystem>();
        // #endregion systems
    }

    public static void AddPlayers(World world, int count)
    {
        // #region players
        var player = world.Prefabs.Player;
        for (int i = 0; i < count; i++)
        {
            var at = Rules.SpawnPoint(i);
            world.Instantiate(player, new Player(i), new Position(at), new InChunk(Terrain.ChunkOf(at)), Footprint.At(at));
        }
        // #endregion players
    }

    // #region capacity
    // Pooling reuses what the world gave back; the first time the world reaches a size still allocates it. A server
    // sized for its players reserves that peak before the first tick instead: the tables and the entity storage of
    // every kind (and the indexes, which follow the storage), and the buffer lists of the chunks. Then a tick
    // allocates only for a peak no reserve sizes (a chunk holding more entities than any before it, a longer list of
    // packets), once: a tick the world has been through allocates nothing (Host/Steady.cs checks it).
    static void Reserve(World world, in Capacity capacity)
    {
        // Each Reserve sizes the entity storage for its own count; the world holds them all at once.
        world.EnsureEntityCapacity(capacity.Chunks + capacity.Players + capacity.Zombies + capacity.Items + capacity.Requests);
        ref readonly var prefabs = ref world.Prefabs;
        world.Reserve(prefabs.Chunk, capacity.Chunks);
        world.Reserve(prefabs.Player, capacity.Players);
        world.Reserve(prefabs.Zombie, capacity.Zombies);
        world.Reserve(prefabs.Item, capacity.Items);
        world.Reserve<LoadRequest>(capacity.Requests);
        DynamicBuffer<Block>.FillPool(capacity.Chunks);
        DynamicBuffer<Fluid>.FillPool(capacity.Chunks);
        DynamicBuffer<Surface>.FillPool(capacity.Chunks);
        DynamicBuffer<NextFluid>.FillPool(capacity.Flowing);
        // The field's buffers twice over: parallel passes write them on the helper threads, and a helper keeps up to 64
        // lists of its own, which the owner thread's chunk loads cannot take back (a snapshot restored shares every
        // buffer until written, so Host/Steady.cs's replay writes them all again).
        DynamicBuffer<Footing>.FillPool(2 * capacity.Chunks);
        DynamicBuffer<Scent>.FillPool(2 * capacity.Chunks);
        DynamicBuffer<NextScent>.FillPool(2 * capacity.Chunks);
        DynamicBuffer<Trail>.FillPool(2 * capacity.Chunks);
        DynamicBuffer<ItemStack>.FillPool(capacity.Players);
    }
    // #endregion capacity

    // #region prefabs
    // The recipes chunks, players, zombies and items are created from: every start builds them. A chunk has every
    // component and tag it will ever have, the tags absent, so all chunks share one table (Systems/Chunks.cs); a zombie
    // has Burning, absent, for the same reason. A zombie carries the Vision it hunts within; a player's view is
    // Rules.PlayerVision, the same for every player (Aoi.Range).
    public static void SetPrefabs(World world) =>
        world.SetPrefabs(
            Chunk: new Prefab()
                .SetChunk(default).SetBuffer<Block>().SetBuffer<Fluid>().SetBuffer<NextFluid>().SetBuffer<Surface>()
                .SetViewers(0, 0).Set(FluidCount.Dry).SetFluidStep(StepResult.None, FluidCount.Dry, 0, 0)
                .Set(new Fresh(), new Flowing(), new Unloading()).RemoveUnloading()
                .SetGround(default).SetBuffer<Footing>().SetBuffer<Scent>().SetBuffer<NextScent>().SetBuffer<Trail>()
                .SetScentStep(TileFaces.None, false)
                .Set(new Spreading(), new Red(), new Black()),
            Player: new Prefab()
                .SetPlayer(0).SetInChunk(default).SetVisible(default, false).SetScore(0).SetFootprint(default, 0)
                .SetPosition(default).SetVelocity(default).SetBody(false, false)
                .SetHealth(Rules.PlayerHealth).SetInput(default, false).SetBuffer<ItemStack>(),
            Zombie: new Prefab()
                .Set(new Zombie()).SetVision(Rules.ZombieVision).SetInChunk(default).SetVisible(default, false).SetTarget(default)
                .SetPosition(default).SetVelocity(default).SetBody(false, false)
                .SetHealth(Rules.ZombieHealth).SetCooldown(0).SetExposure(false, false).SetBurning(0).RemoveBurning(),
            Item: new Prefab()
                .SetItem(default, 1).SetInChunk(default).SetVisible(default, false).SetPosition(default).SetVelocity(new System.Numerics.Vector3(0, 0.2f, 0))
                .SetBody(false, false).SetDropped(0));
    // #endregion prefabs
}
