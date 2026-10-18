using System.Buffers;
using EasyEcs;
using GameLoop;
using GameLoop.Host;

// The simulation runs 30 ticks a second. Positions are replicated every third tick.
const int TicksPerSecond = 30;
const int ReplicateEvery = 3;

// Run 3 seconds by default; `--ticks N` runs N ticks instead.
int ticksToRun = 3 * TicksPerSecond;
if (args.Length == 2 && args[0] == "--ticks")
{
    ticksToRun = int.Parse(args[1]);
}

// Ctrl+C stops the loop cleanly instead of killing the process.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var mailbox = new Mailbox<ClientMessage>(capacity: 4096);
var world = new World(new WorldOptions { InitialEntityCapacity = 4096, MeasureSystems = true });

// Singletons first: the systems read them.
world.SetSimulation(DeltaTime: 1f / TicksPerSecond, Tick: 0);
world.SetInbox(Messages: mailbox);
world.SetPrefabs(
    Player: new Prefab().SetPosition(0, 0).SetVelocity(0, 0),
    Bullet: new Prefab().SetPosition(0, 0).SetVelocity(0, 0).SetLifetime(TicksPerSecond));
world.SetOutbox(Bytes: new ArrayBufferWriter<byte>(64 * 1024), Updates: 0);
world.AddSingleton<Stats>();

// Input group: runs whenever the host wakes up.
world.AddSystem<ReceiveInputSystem>();

// Default group: the fixed simulation tick, in this order.
world.AddSystem<LeaveSystem>();
world.AddSystem<JoinSystem>();
world.AddSystem<MoveSystem>();
world.AddSystem<FireSystem>();
world.AddSystem<MovementSystem>();
world.AddSystem<LifetimeSystem>();
world.AddSystem<ClockSystem>();

// Replication group: runs every third tick.
world.AddSystem<ReplicationSystem>();

var clients = new FakeClients(mailbox, count: 16);
Thread network = clients.Start(stop.Token);

var fixedTick = new FixedTick(TicksPerSecond);
int tick = 0;
while (tick < ticksToRun && !stop.IsCancellationRequested)
{
    // Turn the messages that arrived into data, even between ticks.
    world.Update<InputGroup>();

    if (!fixedTick.IsDue(out TimeSpan wait))
    {
        // Sleep until a message arrives or the next tick is due.
        mailbox.Wait(wait);
        continue;
    }

    world.Update();
    tick++;

    if (tick % ReplicateEvery == 0)
    {
        world.Update<ReplicationGroup>();
        Network.SendOutbox(world);
    }

    if (tick % TicksPerSecond == 0)
    {
        Report.Status(world, tick / TicksPerSecond);
    }
}

// Stop the clients first (messages still queued are dropped), then dispose the world:
// every system's OnDestroy runs, in reverse order.
stop.Cancel();
network.Join();
Report.Summary(world, tick, fixedTick.Elapsed, mailbox.Refused);
world.Dispose();
Console.WriteLine("stopped");
