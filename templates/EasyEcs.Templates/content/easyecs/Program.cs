using System.Diagnostics;
using EasyEcs;
using EasyEcsApp;

// A fixed-tick server: the world updates TickRate times a second until Ctrl+C (or --ticks N), then shuts down.
const int TickRate = 20;
long ticks = args.Length == 2 && args[0] == "--ticks" ? long.Parse(args[1]) : long.MaxValue;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

using var world = new World();
world.SetClock(0, 1f / TickRate);
world.AddSystem<MovementSystem>();
world.AddSystem<LifetimeSystem>();
world.AddSystem<ClockSystem>();

// A kind of entity spawned again and again is a prefab: a recipe of component values.
var bullet = new Prefab().SetPosition(0, 0).SetVelocity(10, 0).SetLifetime(3 * TickRate);   // lives 3 s
var spawned = new Entity[100];

var tickLength = TimeSpan.FromSeconds(1.0 / TickRate);
var clock = Stopwatch.StartNew();
var next = TimeSpan.Zero;
while (world.Clock.Tick < ticks && !stop.IsCancellationRequested)
{
    if (world.Clock.Tick % TickRate == 0) world.Instantiate(bullet, spawned);   // a wave every second
    world.Update();
    if (world.Clock.Tick % TickRate == 0) Console.WriteLine($"tick {world.Clock.Tick}: {world.EntityCount} entities");

    next += tickLength;
    var wait = next - clock.Elapsed;
    if (wait > TimeSpan.Zero) stop.Token.WaitHandle.WaitOne(wait);              // sleep until the next tick
}

Console.WriteLine($"stopped after {world.Clock.Tick} ticks");
