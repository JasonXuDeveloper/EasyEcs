# EasyEcs.Templates

`dotnet new` templates, published to NuGet as `EasyEcs.Templates` with each release (same version as `EasyEcs`).

## Commands

```bash
dotnet new install EasyEcs.Templates
dotnet new easyecs -n MyServer              # a console server: a world updated at 20 Hz until Ctrl+C
dotnet new easyecs -n MyServer --tests      # plus MyServer.Tests (NUnit), one test file per system
cd MyServer
dotnet new easyecs-system -n RegenSystem -o Systems   # one more system, in Systems/
dotnet run -- --ticks 100                   # stop after 100 ticks
```

`easyecs-system` takes the project's root namespace when it runs inside the project, as above; pass
`--namespace` to choose another.

## What you get

```text
MyServer/
├── MyServer.csproj
├── Program.cs                   the host: world setup, the fixed-tick loop, Ctrl+C, --ticks
├── Components/
│   ├── Movement.cs              Position, Velocity
│   ├── Lifetime.cs              Lifetime: ticks left before an entity goes
│   ├── Clock.cs                 Clock: a singleton, the tick and its length
│   └── Spawner.cs               Spawner: a singleton holding the bullet prefab, the wave size and interval
├── Systems/
│   ├── SpawnSystem.cs           a wave of bullets from the prefab every second
│   ├── MovementSystem.cs        moves by velocity over one tick
│   ├── LifetimeSystem.cs        destroys what ran out of time
│   ├── ClockSystem.cs           advances the clock
│   └── RegenSystem.cs           (from easyecs-system)
└── MyServer.Tests/              (with --tests)
    ├── MyServer.Tests.csproj
    ├── SpawnSystemTests.cs
    ├── MovementSystemTests.cs
    ├── LifetimeSystemTests.cs
    └── ClockSystemTests.cs
```

What each part teaches:

- **Components/**: components are plain data, grouped by what they describe; singletons (`Clock`, `Spawner`) hold
  one value per world. A prefab, the recipe every bullet starts from, lives in the `Spawner` singleton.
- **Systems/**: one system per file and per responsibility, no state of their own. The game's rules live here:
  even "spawn a wave every second" is `SpawnSystem`, reading the `Spawner` and the `Clock`.
- **Program.cs**: only the host. It sets the singletons (with named arguments), adds the systems in order, and
  calls `world.Update()` on a fixed tick, printing the entity count once a second and
  `stopped after N ticks` at the end.
- **Tests**: a system is tested through a world of its own: the singletons and entities it needs, one update,
  assertions on the data it left.
- **easyecs-system**: a system file with nothing but the system and a sketch of a `[ForEach]` method to fill in
  with your components.

## How the package is built

`EasyEcs.Templates/content/` holds the templates; `EASYECS_VERSION` in it becomes the version of
`Directory.Build.props` when the package is packed. `templates/smoke.sh` packs EasyEcs and the templates into a
local feed, creates `easyecs --tests` and an `easyecs-system` in its `Systems/` folder (checking the namespace),
then builds, runs and tests the result (CI runs it).
