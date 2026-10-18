# Samples

Complete programs, each a project in `EasyEcs.slnx` (folder `/samples/`). They reference `src/EasyEcs.Core` and the
generator directly, so they always build against this repository; in your own project the single line
`<PackageReference Include="EasyEcs" Version="..."/>` does the same (the package carries the generator and analyzer).

| Sample | What it shows | Run |
|---|---|---|
| [HelloWorld](HelloWorld) | components, a singleton, three `[ForEach]` systems, `world.Update()` | `dotnet run --project samples/HelloWorld` |
| [GameLoop](GameLoop) | a fixed-tick server: a mailbox from a network thread, system groups, an index of clients, prefabs, change-tracked replication | `dotnet run --project samples/GameLoop` |
| [SaveLoad](SaveLoad) | `Save` / `Load` of a world, `SaveEntities` / `LoadEntities` of a group, `MoveEntities` between worlds | `dotnet run --project samples/SaveLoad` |
| [MultiWorld](MultiWorld) | one world per thread (one zone each), no locks, entity-updates per second | `dotnet run -c Release --project samples/MultiWorld` |
| [HotReload](HotReload) | systems and components in a collectible plugin: unload, reload, keep the world's data | `dotnet run --project samples/HotReload/HotReload.Host` |

CI builds and runs every sample (`.github/workflows/ci.yml`). Smaller, one-feature programs are in
[`examples/`](../examples).
