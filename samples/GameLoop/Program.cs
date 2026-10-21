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
world.SetPrefabs(
    Player: new Prefab().SetPosition(0, 0).SetVelocity(0, 0),
    Bullet: new Prefab().SetPosition(0, 0).SetVelocity(0, 0).SetLifetime(TicksPerSecond));
world.SetOutbox(Bytes: new ArrayBufferWriter<byte>(64 * 1024), Updates: 0);
world.AddSingleton<Stats>();

// Input group: runs whenever the host wakes up. The system keeps the mailbox it drains.
world.AddSystem(new ReceiveInputSystem(mailbox));

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

// Every run is checked; the process exits with 1 if a check fails.
var checks = new Checks();
checks.That("players have the prefab's components and their client id",
    world.Count<Client, Position, Velocity>() == world.Count<Client>());
checks.That("bullets have the prefab's components and their lifetime",
    world.Count<Lifetime, Position, Velocity>() == world.Count<Lifetime>());
bool onePlayerPerClient = true;
foreach (var item in world.Query<Client>())
{
    onePlayerPerClient &= world.ClientIndex.Count(item.Client.Id) == 1;
}

checks.That("no client has two players", onePlayerPerClient);
checks.That("the mailbox refused no message", mailbox.Refused == 0);

world.Dispose();
Console.WriteLine("stopped");
return checks.AllPassed ? 0 : 1;
