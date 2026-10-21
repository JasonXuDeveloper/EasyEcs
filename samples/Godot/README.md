# Godot

EasyEcs inside a Godot 4 .NET project: the scene's root node owns a world, steps it once per physics tick and draws
the entities with one multimesh. Read [HelloWorld](../HelloWorld) first.

What each file teaches, with its code, is on the docs site: [Godot](https://ecs.xgamedev.net/en/docs/samples/godot)
([中文](https://ecs.xgamedev.net/zh/docs/samples/godot)).

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
