# Godot

EasyEcs inside a Godot 4 .NET project: the scene's root node owns a world, steps it once per physics tick and draws
the entities with one multimesh. Read [HelloWorld](../HelloWorld) first.

## What you learn

- **The project targets net10.0.** Godot loads a C# project's assembly into the .NET runtime the project targets.
  A project made by an older editor says `net8.0`; change it to `net10.0` and install the .NET 10 SDK. Nothing else
  is needed: the package's generator and analyzer run in `dotnet build` and in the editor's build alike.
- **The node is the host, the systems are the game.** `Main` creates the world in `_Ready`, calls `world.Update()`
  in `_PhysicsProcess` (Godot's fixed tick) and disposes it in `_ExitTree`. Moving and bouncing are systems; the node
  holds no game state.
- **Godot types are component fields.** `Position` and `Velocity` hold a `Vector2`; the arena and the tick length
  are singletons.
- **Draw from the data in one call.** After the update the node copies every position into one float buffer and
  hands it to `RenderingServer.MultimeshSetBuffer`: one draw for every entity, no node per entity.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | `Position`, `Velocity`, and the `Arena` and `Step` singletons |
| [Systems.cs](Systems.cs) | `MoveSystem` and `BounceSystem` |
| [Main.cs](Main.cs) | the scene's root node: the world, the physics tick, the multimesh |
| [Main.tscn](Main.tscn), [project.godot](project.godot), [Godot.csproj](Godot.csproj) | the scene and the project (`Godot.csproj` targets net10.0) |
| [Support.cs](Support.cs) | plumbing that is not the lesson: `--ticks` and the report CI checks |

## Run it

Open the folder in the Godot 4 editor (the .NET build) and press Play, or run it without a window:

```bash
dotnet build samples/Godot
godot --headless --path samples/Godot -- --ticks 120
```

## Expected output

```text
120 ticks, 1000 entities, 1000 inside the arena
updates after the first allocated 0 B
```

The first update builds the queries' caches; from then on the world allocates nothing per tick, inside Godot as in a
server. CI runs this sample with Godot 4.7.2 on Linux. Godot's .NET exports to other platforms (Android, iOS) use the
runtime Godot ships for them and are not tested here.
