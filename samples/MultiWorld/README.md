# MultiWorld

Using every core with one world per thread, and seeing across the borders between them with ghost rows. Read
[SaveLoad](../SaveLoad) first.

What each file teaches, with its code, is on the docs site: [MultiWorld](https://ecs.xgamedev.net/en/docs/samples/multi-world)
([中文](https://ecs.xgamedev.net/zh/docs/samples/multi-world)).

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | `Position`, `Velocity`, the `Ghost` tag and the `Tally` singleton |
| [Systems.cs](Systems.cs) | `SightSystem` (a pair method over the zone's units and ghosts), `MoveSystem`, `BounceSystem` |
| [Zone.cs](Zone.cs) | `Map`: the zones side by side, where each unit starts; `Zone`: one zone's world and its update loop |
| [Border.cs](Border.cs) | the halo exchange between two updates: units that crossed move with `MoveEntities`, border rows are copied into the neighbor zone as ghosts |
| [Program.cs](Program.cs) | starts one thread per zone with a barrier that runs the exchange, prints the results, checks them against the map as one world |

## Run it

```bash
dotnet run -c Release --project samples/MultiWorld                    # 200 updates
dotnet run -c Release --project samples/MultiWorld -- --updates 50
```

## Expected output

The same on every run and every machine: the zones and the one world count the same sightings, and the process exits
with 1 if they do not.

```text
zone 0: 1,803 units,  79 ghosts,   357 arrivals,  3,377,596 sightings
zone 1: 1,999 units, 158 ghosts,   915 arrivals,  3,795,928 sightings
zone 2: 1,998 units, 161 ghosts,   914 arrivals,  3,806,712 sightings
zone 3: 2,200 units,  81 ghosts,   558 arrivals,  4,163,422 sightings
4 zones on 4 threads: 15,143,658 sightings in 200 updates
one world:            15,143,658 sightings
the zones saw what one world sees
```

This is a demonstration, not a measurement: for the speed of worlds side by side use the BenchmarkDotNet suite
(`MultiWorldBenchmark` in `benchmarks/EasyEcs.Benchmark`).

Next: [HotReload](../HotReload), replacing game logic while the world runs.
