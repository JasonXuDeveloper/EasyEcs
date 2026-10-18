# Examples

One file per feature, each a complete [file-based app](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps)
(.NET 10): no project, no solution. The first line references the package, which carries the generator and the
analyzer, so `[ForEach]` systems and the generated members (`entity.Position`, `world.Clock`, `world.TeamIndex`) work
in a single file:

```csharp
#:package EasyEcs@4.0.1
```

```bash
dotnet run examples/hello.cs
```

| File | Feature |
|---|---|
| [hello.cs](hello.cs) | components, a tag, a singleton, two systems, the update loop |
| [queries-and-filters.cs](queries-and-filters.cs) | `[ForEach]` filters (`With`, `Without`, `WithAny`), `Query` / `Count` / `Any`, struct jobs |
| [change-tracking.cs](change-tracking.cs) | `ITrackChanges` and `[Changed<T>]`: visit only what was written |
| [removed.cs](removed.cs) | `[Removed<T>]`: visit entities that lost a component |
| [prefabs.cs](prefabs.cs) | `Prefab`, `Instantiate` one or a wave, variants, `Clone` |
| [dynamic-buffers.cs](dynamic-buffers.cs) | `DynamicBuffer<T>`: a list per entity |
| [indexes.cs](indexes.cs) | value indexes: `Find`, `Count`, `TryFind`, ordered `Range` |
| [relations.cs](relations.cs) | an indexed `EntityId` link, `IRelation` buffers, what happens when a target is destroyed |
| [hierarchy.cs](hierarchy.cs) | `ChildOf`, `Children`, cascading destroy |
| [snapshots.cs](snapshots.cs) | `Capture` / `Restore` (rollback) |
| [parallel.cs](parallel.cs) | parallel system stages, `[ForEach(Parallel = true)]` |
| [groups.cs](groups.cs) | `GroupBy` an `IGroup` component (system groups: [samples/GameLoop](../samples/GameLoop)) |
| [cleanup.cs](cleanup.cs) | `ICleanupComponent` and the `Destroyed` tag |
| [disable.cs](disable.cs) | `Disable` / `Enable` a component, `Disabled<T>` |

The `#:package` version is the released one, bumped by the release workflow. To run the examples against this
repository's sources instead (what CI does), use `examples/run.sh [file.cs ...]`: it copies each file to a temporary
directory, replaces the first line by `#:project src/EasyEcs.Core/EasyEcs.Core.csproj`, adds the generator as an
analyzer (a project reference does not carry it the way the package does) and runs it with warnings as errors.

Complete programs are in [`samples/`](../samples).
