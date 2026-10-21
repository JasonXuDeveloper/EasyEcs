# Samples

Complete programs that build on each other. Each is a project in `EasyEcs.slnx` (folder `/samples/`; the Godot
project is built by `dotnet build samples/Godot` and run by Godot) and reads
top-down: `Components.cs` (the data), then the systems (one job each), then a short `Program.cs` that creates the
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
| 1 | [HelloWorld](HelloWorld) | components, a tag, a singleton, three `[ForEach]` systems, `world.Update()` | `dotnet run --project samples/HelloWorld` |
| 2 | [GameLoop](GameLoop) | a fixed-tick server: input from another thread as data, system groups, an index of clients, prefabs, change-tracked replication | `dotnet run --project samples/GameLoop -- --ticks 30` |
| 3 | [SaveLoad](SaveLoad) | `Save` / `Load` of a world, `SaveEntities` / `LoadEntities` of a group, `MoveEntities` between worlds | `dotnet run --project samples/SaveLoad` |
| 4 | [MultiWorld](MultiWorld) | one world per thread (one zone each), no locks | `dotnet run -c Release --project samples/MultiWorld` |
| 5 | [HotReload](HotReload) | systems and components in a collectible plugin: unload, reload, keep the world's data | `dotnet run --project samples/HotReload/HotReload.Host` |
| 6 | [Actors](Actors) | actors: one type per kind as key component, index and system group (`ISystemGroupWith`), messages as `entity.Run` jobs, records loaded with `markWritten: false`, saving only what changed | `dotnet run --project samples/Actors` |
| 7 | [BlockWorld](BlockWorld) | a Minecraft-style server at 20 ticks a second: chunks as entities with a block buffer and a unique index, chunks loaded and unloaded around the players, dug blocks as items, zombies by night, `Mut<T>` and `[Changed<T>]` replication, `UpdateInterval`, `MeasureSystems` | `dotnet run -c Release --project samples/BlockWorld` |
| 8 | [Godot](Godot) | a Godot 4 .NET project: the scene's root node owns the world, steps it per physics tick and draws the entities with one multimesh | open it in the Godot editor, or `godot --headless --path samples/Godot -- --ticks 120` |

Each sample's README lists its files in reading order and the output to expect. CI builds and runs every sample
(`.github/workflows/ci.yml`), the Godot one headless in Godot 4.7.2.

For one feature at a time, the guide pages of single features (queries, change tracking, prefabs, dynamic buffers,
indexes and relations, hierarchy, groups, cleanup and disable, snapshots, threading; start from the
[features list](https://ecs.xgamedev.net/en/docs/features)) each end their "How to use it" part with a complete,
runnable "Full example". To start a project of your own, use the `dotnet new easyecs` template
([templates](../templates)).
