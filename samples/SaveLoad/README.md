# SaveLoad

Persistence and migration of entities:

1. **A whole world**: `world.Save(stream)` to a file and `World.Load` back, checked value by value.
2. **A group of entities**: `SaveEntities` of a player with the items that are its children (`ChildOf`), then
   `LoadEntities` into another world: the links between them are kept, ids are remapped.
3. **Moving live entities**: `MoveEntities` hands a player from one world to another in one call.

Unmanaged components are saved as their bytes; a managed component (`Name`, a string) is a `[NinoType]`, serialized by
Nino (the project references `Nino.Generator`).

```bash
dotnet run --project samples/SaveLoad
```

It prints each world's contents after every step and a line per check, ending with `all checks passed` (exit code 0;
any failed check exits 1).
