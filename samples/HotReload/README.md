# HotReload

Game logic in a plugin that is replaced while the world keeps running.

- **HotReload.Contracts**: what the host and the plugin share: the components the host owns (`Position`, `Velocity`)
  and `IGamePlugin` (`Install(World)`, `Status(World)`).
- **HotReload.Plugin**: its own component (`Score`, change-tracked), a singleton (`Bonus`) and two systems
  (`JoinScoring`, `ScoringSystem`). The build copies it into the host's `plugins/` folder.
- **HotReload.Host**: loads the plugin into a collectible `AssemblyLoadContext`, runs 3 updates, saves the world,
  removes the plugin's types from it (`world.RemoveTypes(assembly)`: its systems, components and singletons), unloads
  the context and checks that it was collected. It then loads the plugin again, `Load`s the saved world and runs 2 more
  updates: the scores carried over.

```bash
dotnet run --project samples/HotReload/HotReload.Host
```

It prints each step and a line per check, ending with `all checks passed` (exit code 0; a failed check exits 1); the final score is
`500 points` (3 + 2 updates of 100 units).
