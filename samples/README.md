# Samples

Complete programs that build on each other. Each is a project in `EasyEcs.slnx` (folder `/samples/`; the Godot
project is built by `dotnet build samples/Godot` and run by Godot) and reads
top-down: `Components.cs` (the data), then the systems (one job each), then a short `Program.cs` (`Main.cs` in Godot) that creates the
world, adds singletons and systems, runs and prints. Code that a real program needs but that is not the ECS lesson
(a mailbox between threads, fake clients, checks, printing) is in a file or folder of its own (`Host/`,
`Support.cs`) that says so.

The samples reference `src/EasyEcs.Core` and the generator directly, so they always build against this repository.
In your own project one line does the same, since the package carries the generator and the analyzer:
`<PackageReference Include="EasyEcs" Version="..."/>`.

## Learning path

Read them in this order:

| # | Sample | What it teaches | Run |
|---|---|---|---|
| 1 | [HelloWorld](HelloWorld) | components, a tag, a singleton, two `[ForEach]` systems and an `OnUpdate` one, `world.Update()` | `dotnet run --project samples/HelloWorld` |
| 2 | [GameLoop](GameLoop) | a fixed-tick server: input from another thread as data, system groups, an index of clients, prefabs, change-tracked replication | `dotnet run --project samples/GameLoop -- --ticks 30` |
| 3 | [SaveLoad](SaveLoad) | `Save` / `Load` of a world, `SaveEntities` / `LoadEntities` of a group, `MoveEntities` between worlds | `dotnet run --project samples/SaveLoad` |
| 4 | [MultiWorld](MultiWorld) | one world per thread (one zone each), no locks | `dotnet run -c Release --project samples/MultiWorld` |
| 5 | [HotReload](HotReload) | systems and components in a collectible plugin: unload, reload, keep the world's data | `dotnet run --project samples/HotReload/HotReload.Host` |
| 6 | [Actors](Actors) | actors: one type per kind as key component, index and system group (`ISystemGroupWith`), messages as `[EntityMethod]` methods, records loaded with `markWritten: false`, saving only what changed | `dotnet run --project samples/Actors` |
| 7 | [VoxelServer](VoxelServer) | a Minecraft-style server: chunks as entities saved and loaded around the players, an area of interest by chunk, water as a SIMD stencil, zombies by night | `dotnet run -c Release --project samples/VoxelServer` |
| 8 | [Godot](Godot) | a Godot 4 .NET project: the scene's root node owns the world, steps it per physics tick and draws the entities with one multimesh | open it in the Godot editor, or `godot --headless --path samples/Godot -- --ticks 120` |

Each sample's README lists its files in reading order and the output to expect; the
[Samples](https://ecs.xgamedev.net/en/docs/samples) section of the docs site walks through each one with its code
included from these files. CI builds and runs every sample (`.github/workflows/ci.yml`), the Godot one headless in
Godot 4.7.2, and checks that the deterministic ones print the output of their README and of their docs pages.

For one feature at a time, the guide pages of single features (queries, change tracking, prefabs, dynamic buffers,
indexes and relations, hierarchy, groups, cleanup and disable, snapshots, threading; start from the
[features list](https://ecs.xgamedev.net/en/docs/features)) each end their "How to use it" part with a complete,
runnable "Full example". To start a project of your own, use the `dotnet new easyecs` template
([templates](../templates)).
