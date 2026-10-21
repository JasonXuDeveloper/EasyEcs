# HotReload

Game logic in a plugin that is replaced while the world keeps running. Read [MultiWorld](../MultiWorld) first.

## What you learn

- **Logic in a collectible plugin.** The plugin's systems, components and singletons live in an assembly the host
  loads into a collectible `AssemblyLoadContext`.
- **Unloading cleanly.** `world.RemoveTypes(assembly)` takes the plugin's systems, components and singletons out
  of the world, so nothing in it roots the plugin and the context can be collected. The host's own entities,
  components and systems stay.
- **Keeping the data.** Saving before the unload and loading after the reload carries the plugin's component
  values over: a save names types by their type id (a hash of the full name), not by the assembly that loaded them.
- **Bulk changes.** `World.SetAll` gives a whole table of units a component in one call.

## Projects and files, in reading order

| File | What it holds |
|---|---|
| [HotReload.Contracts/Components.cs](HotReload.Contracts/Components.cs) | what the host owns: `Position`, `Velocity` |
| [HotReload.Contracts/IGamePlugin.cs](HotReload.Contracts/IGamePlugin.cs) | the only thing the host knows about a plugin |
| [HotReload.Plugin/Components.cs](HotReload.Plugin/Components.cs) | the plugin's data: `Score`, the `Bonus` singleton |
| [HotReload.Plugin/Systems.cs](HotReload.Plugin/Systems.cs) | `JoinScoringSystem` gives the units without a score one (`World.SetAll`), `ScoringSystem` adds the bonus |
| [HotReload.Plugin/ScoringPlugin.cs](HotReload.Plugin/ScoringPlugin.cs) | the `IGamePlugin`: installs the systems and the bonus |
| [HotReload.Host/Systems.cs](HotReload.Host/Systems.cs) | the host's `MoveSystem`, which runs through reloads |
| [HotReload.Host/Program.cs](HotReload.Host/Program.cs) | version 1: load, 3 updates, save, unload; version 2: load, load the save, 2 updates |
| [HotReload.Host/Host/Plugins.cs](HotReload.Host/Host/Plugins.cs) | plumbing that is not the ECS lesson: the collectible `PluginContext`, finding the `IGamePlugin`, waiting for collection |
| [HotReload.Host/Support.cs](HotReload.Host/Support.cs) | `Checks`: a line per check |

The contracts assembly loads once into the default context; the plugin resolves it from there, so its `Position`
is the host's. The plugin project is built with the host and copied into the host's `plugins/` folder.

## Run it

```bash
dotnet run --project samples/HotReload/HotReload.Host
```

## Expected output

The process exits with 0 when every check passes and with 1 otherwise.

```text
plugin loaded, 3 updates: 300 points, 3 systems
  ok   the plugin's context was collected: nothing in the world roots it
  ok   its systems left, the host's stayed
  ok   the host's entities and components stayed
plugin reloaded, 2 more updates: 500 points, 3 systems
  ok   the scores carried over the reload (3 + 2 updates of 100 units)
all checks passed
```

That is the end of the learning path. The [docs](https://ecs.xgamedev.net) cover every feature with a complete
example on its page.
