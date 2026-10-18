using System.Buffers;
using System.Diagnostics;
using EasyEcs;
using GameLoop;

// A fixed-tick server host: input is drained on every wake-up, the simulation runs at TickRate, replication at
// SendRate. Runs --ticks simulation ticks (default 3 seconds' worth), or until Ctrl+C, then shuts down cleanly.
const int TickRate = 30, SendEvery = 3;                              // 30 Hz simulation, 10 Hz replication
int ticks = args.Length == 2 && args[0] == "--ticks" ? int.Parse(args[1]) : 3 * TickRate;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

var mailbox = new Mailbox<ClientMessage>(capacity: 4096);
var world = new World(new WorldOptions { InitialEntityCapacity = 4096, MeasureSystems = true });

// Setup: singletons first, then systems in their order.
world.SetSimulation(1f / TickRate, 0);
world.SetPrefabs(
    new Prefab().SetPosition(0, 0).SetVelocity(0, 0),                // a player, before its client id and spawn point
    new Prefab().SetPosition(0, 0).SetVelocity(0, 0).SetLifetime(TickRate));   // a bullet lives one second
world.SetOutbox(new ArrayBufferWriter<byte>(64 * 1024), 0);
world.AddSingleton<Stats>();
world.AddSystem(new ReceiveInput(mailbox));
world.AddSystem<LeaveSystem>();                                     // leave before join: a client may rejoin in one tick
world.AddSystem<JoinSystem>();
world.AddSystem<MoveIntentSystem>();
world.AddSystem<FireSystem>();
world.AddSystem<MovementSystem>();
world.AddSystem<LifetimeSystem>();
world.AddSystem<ClockSystem>();
world.AddSystem<ReplicationSystem>();

var clients = new Clients(mailbox, count: 16);
var network = clients.Start(stop.Token);

long sentBytes = 0, sentUpdates = 0;
var tickLength = TimeSpan.FromSeconds(1.0 / TickRate);
var clock = Stopwatch.StartNew();
var nextTick = TimeSpan.Zero;
int tick = 0;
while (tick < ticks && !stop.IsCancellationRequested)
{
    world.Update<InputGroup>();                                      // every wake-up: input becomes data
    var now = clock.Elapsed;
    if (now < nextTick)
    {
        mailbox.Wait(nextTick - now);                                // sleep until input arrives or the tick is due
        continue;
    }

    nextTick += tickLength;
    world.Update();                                                  // the fixed tick
    if (++tick % SendEvery == 0)
    {
        world.Update<ReplicationGroup>();
        ref var outbox = ref world.Outbox;
        sentBytes += outbox.Bytes.WrittenCount;                      // a real server writes them to its sockets here
        sentUpdates += outbox.Updates;
        outbox.Bytes.ResetWrittenCount();
        outbox.Updates = 0;
    }

    if (tick % TickRate == 0)
        Console.WriteLine($"t={tick / TickRate}s players={world.Count<Client>()} bullets={world.Count<Lifetime>()} entities={world.EntityCount}");
}

// Shutdown: stop the producers first (messages still queued are dropped), then dispose the world (OnDestroy of every
// system, in reverse order).
stop.Cancel();
network.Join();
var elapsed = clock.Elapsed;
ref readonly var stats = ref world.Stats;
Console.WriteLine($"{tick} ticks in {elapsed.TotalSeconds:F1} s: {stats.Received} messages ({mailbox.Refused} refused), " +
                  $"{stats.Joined} joins, {stats.Left} leaves, {stats.Fired} bullets, {sentUpdates} position updates ({sentBytes / 1024.0:F1} KB) replicated");
foreach (var system in world.Systems)
    Console.WriteLine($"  {system.GetType().Name,-18} {system.Stats.Runs,5} runs, {system.Stats.Last.TotalMicroseconds,8:F1} us last run");
world.Dispose();
Console.WriteLine("stopped");
